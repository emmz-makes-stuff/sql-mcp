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

    private const string Programmable =
        "procedures, functions and triggers can't be created through sql-mcp: their bodies run later, unseen by the classifier";

    /// <summary>Statements whose refusal needs its own reason rather than "… is not supported by any tool".</summary>
    private static readonly Dictionary<Type, string> Refusals = new()
    {
        [typeof(CreateProcedureStatement)] = $"CREATE PROCEDURE: {Programmable}",
        [typeof(AlterProcedureStatement)] = $"ALTER PROCEDURE: {Programmable}",
        [typeof(CreateOrAlterProcedureStatement)] = $"CREATE OR ALTER PROCEDURE: {Programmable}",
        [typeof(CreateFunctionStatement)] = $"CREATE FUNCTION: {Programmable}",
        [typeof(AlterFunctionStatement)] = $"ALTER FUNCTION: {Programmable}",
        [typeof(CreateOrAlterFunctionStatement)] = $"CREATE OR ALTER FUNCTION: {Programmable}",
        [typeof(CreateTriggerStatement)] = $"CREATE TRIGGER: {Programmable}",
        [typeof(AlterTriggerStatement)] = $"ALTER TRIGGER: {Programmable}",
        [typeof(CreateOrAlterTriggerStatement)] = $"CREATE OR ALTER TRIGGER: {Programmable}",
    };

    /// <summary>Why a statement no tool runs is refused.</summary>
    public static string Unsupported(TSqlStatement statement) =>
        Refusals.TryGetValue(statement.GetType(), out var reason) ? reason : $"{For(statement)} is not supported by any tool";

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
