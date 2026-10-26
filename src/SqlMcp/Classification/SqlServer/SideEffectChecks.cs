using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlMcp.Classification.SqlServer;

/// <summary>
/// Checks for nodes whose meaning depends on more than their type (design D6, W5): SELECT INTO and rowset functions
/// everywhere; table hint kinds in a query; the WHERE rule and the names rule in writes (the names rule in a view's
/// SELECT too); and dynamic or
/// indirect EXEC. Returns a rejection reason, or null.
/// </summary>
internal static class SideEffectChecks
{
    /// <summary>Hints a query may use: none of them takes update or exclusive locks or escalates locking.</summary>
    private static readonly HashSet<TableHintKind> QueryHints =
    [
        TableHintKind.NoLock,
        TableHintKind.ReadUncommitted,
        TableHintKind.ReadCommitted,
        TableHintKind.ReadCommittedLock,
        TableHintKind.RepeatableRead,
        TableHintKind.Snapshot,
        TableHintKind.Rowlock,
        TableHintKind.NoExpand,
        TableHintKind.NoWait,
        TableHintKind.ReadPast,
        TableHintKind.ForceSeek,
        TableHintKind.ForceScan,
        TableHintKind.Index,
    ];

    private static readonly HashSet<TableHintKind> LockingHints =
    [
        TableHintKind.UpdLock,
        TableHintKind.XLock,
        TableHintKind.TabLock,
        TableHintKind.TabLockX,
        TableHintKind.HoldLock,
        TableHintKind.Serializable,
        TableHintKind.PagLock,
    ];

    /// <param name="targetDatabaseOnly">
    /// Whether the names rule applies: grants are per database, so a write, or a view's SELECT, may not reach another
    /// database or server through a three- or four-part name. A query may.
    /// </param>
    public static string? Check(TSqlFragment node, StatementCategory category, bool targetDatabaseOnly) => node switch
    {
        SelectStatement { Into: not null } =>
            $"SELECT ... INTO {At(node)} creates a table, so it is not a query",
        OpenQueryTableReference or OpenRowsetTableReference or BulkOpenRowset or InternalOpenRowset or OpenRowsetCosmos
            or AdHocTableReference =>
            $"{Phrases.For(node)} {At(node)} reaches outside the database, so no tool runs it",
        UpdateStatement { UpdateSpecification: { WhereClause: null } update } statement
            when !TargetsCte(update.Target, statement.WithCtesAndXmlNamespaces) =>
            $"UPDATE needs a WHERE clause that picks the rows to change ({At(node)})",
        DeleteStatement { DeleteSpecification: { WhereClause: null } delete } statement
            when !TargetsCte(delete.Target, statement.WithCtesAndXmlNamespaces) =>
            $"DELETE needs a WHERE clause that picks the rows to change ({At(node)})",
        CreateSchemaStatement { Owner: not null } =>
            $"CREATE SCHEMA ... AUTHORIZATION {At(node)} sets the schema's owner, which is a security change",
        CreateSchemaStatement { StatementList.Statements.Count: > 0 } =>
            $"CREATE SCHEMA with objects in it {At(node)} is not supported: create the schema, then each object in its own statement",
        SchemaObjectName name when targetDatabaseOnly && (name.DatabaseIdentifier is not null || name.ServerIdentifier is not null) =>
            $"{OtherDatabase} {Dotted(name.Identifiers)} {At(node)} has a database or server part",
        ColumnReferenceExpression { MultiPartIdentifier.Count: >= 4 } column when targetDatabaseOnly =>
            $"{OtherDatabase} column {Dotted(column.MultiPartIdentifier.Identifiers)} {At(node)} has a database part",
        TableHint hint when category == StatementCategory.Query && !QueryHints.Contains(hint.HintKind) =>
            LockingHints.Contains(hint.HintKind)
                ? $"table hint {HintName(hint)} {At(node)} takes update or exclusive locks or escalates locking, so it is not allowed in a query"
                : $"table hint {HintName(hint)} {At(node)} is not allowed in a query",
        ExecutableStringList =>
            $"EXEC of a string {At(node)} is dynamic SQL, which can't be classified",
        ExecuteSpecification { LinkedServer: not null } =>
            $"EXEC ... AT a linked server {At(node)} runs elsewhere, so it can't be classified",
        ExecutableProcedureReference { ProcedureReference.ProcedureVariable: not null } =>
            $"EXEC of a procedure named by a variable {At(node)} can't be classified",
        ExecutableProcedureReference { AdHocDataSource: not null } =>
            $"EXEC through OPENDATASOURCE {At(node)} runs elsewhere, so it can't be classified",
        ExecutableProcedureReference reference when IsSpExecuteSql(reference) =>
            $"sp_executesql {At(node)} runs dynamic SQL, which can't be classified",
        ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } procedure } when IsSystemProcedure(procedure) =>
            $"system and extended procedures can't be run: {Dotted(procedure.Identifiers)} {At(node)}",
        ExecuteSpecification { Variable: not null } =>
            $"assigning EXEC's return value to a @variable {At(node)} is not supported",
        ExecuteParameter { ParameterValue: { } value } when !IsPlainValue(value) =>
            $"a procedure parameter {At(node)} must be a literal, NULL or a negative number",
        _ => null,
    };

    private const string OtherDatabase = "writes may only name objects in the target database:";

    /// <summary>
    /// A target that is a CTE of the same statement needs no WHERE: the CTE's query does the filtering (2026-10-26
    /// decision). The affected-rows cap still applies.
    /// </summary>
    private static bool TargetsCte(TableReference target, WithCtesAndXmlNamespaces? with) =>
        target is NamedTableReference { SchemaObject: { SchemaIdentifier: null, DatabaseIdentifier: null, ServerIdentifier: null } name }
        && with is not null
        && with.CommonTableExpressions.Any(cte =>
            string.Equals(cte.ExpressionName.Value, name.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>sys</c> schema, or a name starting <c>sp_</c> or <c>xp_</c>, ignoring case (design W5).</summary>
    private static bool IsSystemProcedure(SchemaObjectName procedure) =>
        string.Equals(procedure.SchemaIdentifier?.Value, "sys", StringComparison.OrdinalIgnoreCase)
        || procedure.BaseIdentifier.Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase)
        || procedure.BaseIdentifier.Value.StartsWith("xp_", StringComparison.OrdinalIgnoreCase);

    /// <summary>A literal (including NULL), or a negated number such as <c>-5</c>.</summary>
    private static bool IsPlainValue(ScalarExpression value) => value is Literal
        or UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral or NumericLiteral or RealLiteral or MoneyLiteral };

    private static string Dotted(IEnumerable<Identifier> identifiers) => string.Join(".", identifiers.Select(i => i.Value));

    private static bool IsSpExecuteSql(ExecutableProcedureReference reference) =>
        string.Equals(
            reference.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value,
            "sp_executesql",
            StringComparison.OrdinalIgnoreCase);

    private static string HintName(TableHint hint) => hint.HintKind.ToString().ToUpperInvariant();

    internal static string At(TSqlFragment node) =>
        $"at line {node.StartLine.ToString(CultureInfo.InvariantCulture)}, column {node.StartColumn.ToString(CultureInfo.InvariantCulture)}";
}
