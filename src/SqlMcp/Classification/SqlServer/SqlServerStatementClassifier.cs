using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlMcp.Classification.SqlServer;

/// <summary>
/// Classifies T-SQL with ScriptDom (design D6, ADR-0002). The top-level statement type picks a candidate category,
/// then every node in the statement must be on that category's allowlist and pass the side-effect checks.
/// Anything else is rejected, so syntax nobody has seen yet fails closed.
/// </summary>
internal sealed class SqlServerStatementClassifier : IStatementClassifier
{
    public ClassificationResult Classify(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql))
        {
            fragment = parser.Parse(reader, out errors);
        }

        if (errors.Count > 0)
        {
            return new ClassificationResult([], string.Join("; ", errors.Select(e =>
                $"syntax error at line {e.Line.ToString(CultureInfo.InvariantCulture)}, column {e.Column.ToString(CultureInfo.InvariantCulture)}: {e.Message}")));
        }

        // SqlClient can't run GO, and splitting batches silently would hide what runs.
        if (fragment.ScriptTokenStream.Any(token => token.TokenType == TSqlTokenType.Go))
        {
            return new ClassificationResult([], "GO batch separators are not supported; send one batch per call");
        }

        var statements = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count == 0)
        {
            return new ClassificationResult([], "there is no statement to run");
        }

        return new ClassificationResult(statements.Select((s, i) => Classify(s, i + 1)).ToList());
    }

    private static StatementClassification Classify(TSqlStatement statement, int number)
    {
        if (!Allowlists.StatementCategories.TryGetValue(statement.GetType(), out var category))
        {
            return Rejected(number, statement, Phrases.Unsupported(statement));
        }

        var visitor = new AllowlistVisitor(category);
        statement.Accept(visitor);
        return visitor.Rejection is { } reason
            ? Rejected(number, statement, reason)
            : new StatementClassification(number, category, statement.StartLine, statement.StartColumn);
    }

    private static StatementClassification Rejected(int number, TSqlStatement statement, string reason) =>
        new(number, StatementCategory.Rejected, statement.StartLine, statement.StartColumn, reason);

    /// <summary>
    /// Visits every node: <see cref="TSqlFragmentVisitor"/> calls <see cref="Visit(TSqlFragment)"/> for each one,
    /// whatever its concrete type. The first failure is kept.
    /// </summary>
    private sealed class AllowlistVisitor(StatementCategory category) : TSqlFragmentVisitor
    {
        public string? Rejection { get; private set; }

        public override void Visit(TSqlFragment node)
        {
            if (Rejection is not null)
            {
                return;
            }

            Rejection = SideEffectChecks.Check(node, category);
            if (Rejection is null && !Allowlists.For(category).Contains(node.GetType()))
            {
                Rejection = $"{Phrases.For(node)} {SideEffectChecks.At(node)} is not allowed in {category.ToName()} statements";
            }
        }

        /// <summary>
        /// A view's SELECT is checked as a query (design W5), so a view can't hide what a query couldn't do. In DDL a
        /// SELECT statement only appears as a view's body.
        /// </summary>
        public override void ExplicitVisit(SelectStatement node)
        {
            if (category != StatementCategory.Ddl)
            {
                base.ExplicitVisit(node);
                return;
            }

            if (Rejection is null)
            {
                var query = new AllowlistVisitor(StatementCategory.Query);
                node.Accept(query);
                Rejection = query.Rejection is { } reason ? $"the view's SELECT is not a valid query: {reason}" : null;
            }
        }
    }
}
