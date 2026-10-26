namespace SqlMcp.Permissions;

/// <summary>
/// The one wording for "the project config isn't approved", used by tools, <c>list_databases</c> and the CLI.
/// It is addressed to a person through the agent: it tells the agent to ask the user, and never offers
/// <c>sql-mcp approve</c> as a command for the agent to run (design D4).
/// </summary>
internal static class ApprovalMessages
{
    public const string AskTheUser =
        "Ask the user to run `sql-mcp approve` in their own terminal. Agents cannot approve.";

    /// <summary>E.g. "project config grants ddl but is not approved. Ask the user to …".</summary>
    public static string NotApproved(string widening) => $"project config {widening} but is not approved. {AskTheUser}";

    /// <summary>The widening of a database that only the project file defines, until it is approved.</summary>
    public const string AddsDatabase = "adds this database";

    public static string Grants(ToolGroup group) => $"grants {group.ToConfigName()}";

    public static string Raises(string limit) => $"raises {limit}";
}
