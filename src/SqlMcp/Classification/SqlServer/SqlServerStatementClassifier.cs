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
            var syntax = string.Join("; ", errors.Select(e =>
                $"syntax error at line {e.Line.ToString(CultureInfo.InvariantCulture)}, column {e.Column.ToString(CultureInfo.InvariantCulture)}: {e.Message}"));
            return new ClassificationResult([], OwnCallHint(sql, errors) is { } hint ? $"{hint}. The parser reported: {syntax}" : syntax);
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

    /// <summary>
    /// SQL Server runs a view or schema statement only alone in its batch (Msg 111), and ScriptDom enforces that as a
    /// bare "Incorrect syntax near 'CREATE'". This says why, when the batch has one that isn't alone: something comes
    /// before it, or the parser failed at a later statement after it.
    /// </summary>
    private static string? OwnCallHint(string sql, IList<ParseError> errors)
    {
        IList<TSqlParserToken> tokens;
        using (var reader = new StringReader(sql))
        {
            tokens = new TSql170Parser(initialQuotedIdentifiers: true).GetTokenStream(reader, out _);
        }

        var words = tokens.Where(t => t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment
            or TSqlTokenType.MultilineComment or TSqlTokenType.Semicolon or TSqlTokenType.EndOfFile)).ToList();
        var statementStarts = words
            .Where(t => t.TokenType is TSqlTokenType.Create or TSqlTokenType.Alter or TSqlTokenType.Drop or TSqlTokenType.Truncate)
            .Select(t => t.Offset)
            .ToHashSet();
        for (var i = 0; i < words.Count; i++)
        {
            // After GRANT, DENY, REVOKE or a comma, CREATE VIEW names a permission, not a statement.
            var permission = i > 0 && words[i - 1].TokenType is TSqlTokenType.Grant or TSqlTokenType.Deny or TSqlTokenType.Revoke or TSqlTokenType.Comma;
            if (!permission
                && OwnCallStatement(words, i) is { } name
                && (i > 0 || errors.Any(e => e.Offset > words[i].Offset && statementStarts.Contains(e.Offset))))
            {
                return $"{name} must be the only statement in its call: send it on its own";
            }
        }

        return null;
    }

    private static string? OwnCallStatement(List<TSqlParserToken> words, int i)
    {
        TSqlTokenType At(int offset) => i + offset < words.Count ? words[i + offset].TokenType : TSqlTokenType.None;
        return (At(0), At(1), At(2), At(3)) switch
        {
            (TSqlTokenType.Create, TSqlTokenType.View, _, _) => "CREATE VIEW",
            (TSqlTokenType.Alter, TSqlTokenType.View, _, _) => "ALTER VIEW",
            (TSqlTokenType.Create, TSqlTokenType.Or, TSqlTokenType.Alter, TSqlTokenType.View) => "CREATE OR ALTER VIEW",
            (TSqlTokenType.Create, TSqlTokenType.Schema, _, _) => "CREATE SCHEMA",
            _ => null,
        };
    }

    private static StatementClassification Classify(TSqlStatement statement, int number)
    {
        if (!Allowlists.StatementCategories.TryGetValue(statement.GetType(), out var category))
        {
            return Rejected(number, statement, Phrases.Unsupported(statement));
        }

        var visitor = new AllowlistVisitor(category, targetDatabaseOnly: category is not StatementCategory.Query);
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
    private sealed class AllowlistVisitor(StatementCategory category, bool targetDatabaseOnly) : TSqlFragmentVisitor
    {
        public string? Rejection { get; private set; }

        public override void Visit(TSqlFragment node)
        {
            if (Rejection is not null)
            {
                return;
            }

            Rejection = SideEffectChecks.Check(node, category, targetDatabaseOnly);
            if (Rejection is null && !Allowlists.For(category).Contains(node.GetType()))
            {
                Rejection = $"{Phrases.For(node)} {SideEffectChecks.At(node)} is not allowed in {category.ToName()} statements";
            }
        }

        /// <summary>
        /// A view's SELECT is checked as a query (design W5), so a view can't hide what a query couldn't do. Creating a
        /// view is a write, so its SELECT also keeps to the target database, which a query need not (code-review fix
        /// 10.2). In DDL a SELECT statement only appears as a view's body.
        /// </summary>
        public override void ExplicitVisit(SelectStatement node)
        {
            if (category != StatementCategory.Ddl)
            {
                base.ExplicitVisit(node);
                return;
            }

            if (Rejection is not null)
            {
                return;
            }

            var query = new AllowlistVisitor(StatementCategory.Query, targetDatabaseOnly: false);
            node.Accept(query);
            if (query.Rejection is { } reason)
            {
                Rejection = $"the view's SELECT is not a valid query: {reason}";
                return;
            }

            var names = new AllowlistVisitor(StatementCategory.Query, targetDatabaseOnly: true);
            node.Accept(names);
            Rejection = names.Rejection;
        }
    }
}
