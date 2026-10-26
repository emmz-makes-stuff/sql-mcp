using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlMcp.Classification.SqlServer;

/// <summary>
/// Checks for nodes whose meaning depends on more than their type (design D6): SELECT INTO, table hint kinds, and
/// dynamic or indirect EXEC. Returns a rejection reason, or null.
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

    public static string? Check(TSqlFragment node, StatementCategory category) => node switch
    {
        SelectStatement { Into: not null } =>
            $"SELECT ... INTO {At(node)} creates a table, so it is not a query",
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
        _ => null,
    };

    private static bool IsSpExecuteSql(ExecutableProcedureReference reference) =>
        string.Equals(
            reference.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value,
            "sp_executesql",
            StringComparison.OrdinalIgnoreCase);

    private static string HintName(TableHint hint) => hint.HintKind.ToString().ToUpperInvariant();

    internal static string At(TSqlFragment node) =>
        $"at line {node.StartLine.ToString(CultureInfo.InvariantCulture)}, column {node.StartColumn.ToString(CultureInfo.InvariantCulture)}";
}
