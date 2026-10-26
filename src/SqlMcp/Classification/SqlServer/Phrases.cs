using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlMcp.Classification.SqlServer;

/// <summary>Readable names for ScriptDom node types in rejection reasons. Unknown types get a generated phrase.</summary>
internal static class Phrases
{
    private static readonly Dictionary<Type, string> Known = new()
    {
        [typeof(OpenQueryTableReference)] = "OPENQUERY (a linked-server rowset)",
        [typeof(OpenRowsetTableReference)] = "OPENROWSET",
        [typeof(BulkOpenRowset)] = "OPENROWSET(BULK ...)",
        [typeof(InternalOpenRowset)] = "OPENROWSET",
        [typeof(OpenRowsetCosmos)] = "OPENROWSET",
        [typeof(AdHocTableReference)] = "OPENDATASOURCE",
        [typeof(NextValueForExpression)] = "NEXT VALUE FOR (it advances a sequence)",
        [typeof(GrantStatement)] = "GRANT",
        [typeof(DenyStatement)] = "DENY",
        [typeof(RevokeStatement)] = "REVOKE",
        [typeof(BackupDatabaseStatement)] = "BACKUP DATABASE",
        [typeof(UseStatement)] = "USE",
        [typeof(SetVariableStatement)] = "SET @variable",
        [typeof(PredicateSetStatement)] = "SET option",
        [typeof(DeclareVariableStatement)] = "DECLARE",
        [typeof(BeginTransactionStatement)] = "BEGIN TRANSACTION",
        [typeof(WaitForStatement)] = "WAITFOR",
        [typeof(VariableReference)] = "a @variable",
        [typeof(SelectSetVariable)] = "assigning to a @variable in SELECT",
    };

    /// <summary>E.g. "GRANT", or "create procedure statement (CreateProcedureStatement)" when no phrase is known.</summary>
    public static string For(TSqlFragment node) => For(node.GetType());

    public static string For(Type type)
    {
        if (Known.TryGetValue(type, out var phrase))
        {
            return phrase;
        }

        var words = new StringBuilder();
        foreach (var c in type.Name)
        {
            if (char.IsUpper(c) && words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(char.ToLowerInvariant(c));
        }

        return $"{words} ({type.Name})";
    }
}
