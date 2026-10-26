using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlMcp.Classification;
using SqlMcp.Execution;
using SqlMcp.Permissions;

namespace SqlMcp.Mcp;

/// <summary>The agent guidance in each tool's description (spec: tool descriptions steer towards constrained queries).</summary>
internal static class ToolDescriptions
{
    public const string ListDatabases =
        "Lists the databases this server can use. For each one: its identifier (pass it to the other tools), whether it is available and why not, the tool groups allowed and denied with where each grant comes from, and its limits. Call this first.";

    public const string QueryGuidance =
        "Runs a read-only T-SQL query on one database and returns the first page of rows, with column names and SQL types. " +
        "Every statement must be a query: anything that writes, or could, is refused before it reaches the database. " +
        "Ask only for what you need: use an explicit column list instead of SELECT *, filter with WHERE, and limit rows with TOP. " +
        "Results are buffered up to a row cap and a byte cap; past either, the result is truncated and you should narrow the query. " +
        "If more pages remain, the result includes a handle: read on with next_page, or release it with abandon.";

    public const string NextPage =
        "Returns the next page of a query result, given the handle from query. The last page is marked last_page and releases the handle. Handles also expire after 10 minutes idle, and only the 20 most recently used are kept; if one is unknown, run the query again.";

    public const string Abandon =
        "Releases a query result handle and its buffered rows, when you don't need the remaining pages.";

    /// <summary>The query guidance plus the caps: the defaults, then any database whose limits differ.</summary>
    public static string Query(IEnumerable<EffectiveDatabase> databases)
    {
        var text = $"{QueryGuidance} Default caps: {Describe(EffectiveLimits.Default)}.";
        var others = databases
            .Where(d => d.IsAllowed(ToolGroup.Query) && !SameQueryCaps(d.Limits, EffectiveLimits.Default))
            .Select(d => $"{d.Identifier}: {Describe(d.Limits)}")
            .ToList();
        return others.Count == 0 ? text : $"{text} Databases with other limits: {string.Join("; ", others)}.";
    }

    /// <summary>The affected-rows cap applies only to writes, so it doesn't make a query's caps differ.</summary>
    private static bool SameQueryCaps(EffectiveLimits a, EffectiveLimits b) => a with { MaxAffectedRows = b.MaxAffectedRows } == b;

    public static string Describe(EffectiveLimits limits) => string.Create(
        CultureInfo.InvariantCulture,
        $"{limits.MaxRows} rows or {ByteSize.Format(limits.MaxBytes)} per result, {limits.PageSize} rows per page, {limits.TimeoutSeconds} s timeout");
}

/// <summary>
/// Builds the server's tool list (design D8). <c>list_databases</c> is always offered (2026-10-25 decision); the
/// query tools only if <c>query</c> is allowed on at least one database. Every call still checks permission again.
/// </summary>
internal static class ToolCatalog
{
    /// <summary>
    /// The statement categories this release has a tool for, so a refusal can say whether another tool would run
    /// the statement. The write-tools change adds command, ddl and execute.
    /// </summary>
    public static readonly IReadOnlySet<StatementCategory> Categories = new HashSet<StatementCategory> { StatementCategory.Query };

    public static IReadOnlyList<McpServerTool> Build(SqlMcpSession session)
    {
        var tools = new SqlMcpTools(new ToolPipeline(session));
        var list = new List<McpServerTool>
        {
            Create(tools, nameof(SqlMcpTools.ListDatabases), ToolNames.ListDatabases, ToolDescriptions.ListDatabases),
        };

        if (session.Databases.Any(d => d.IsAllowed(ToolGroup.Query)))
        {
            list.Add(Create(tools, nameof(SqlMcpTools.Query), ToolNames.Query, ToolDescriptions.Query(session.Databases)));
            list.Add(Create(tools, nameof(SqlMcpTools.NextPage), ToolNames.NextPage, ToolDescriptions.NextPage));
            list.Add(Create(tools, nameof(SqlMcpTools.Abandon), ToolNames.Abandon, ToolDescriptions.Abandon));
        }

        return list;
    }

    private static McpServerTool Create(SqlMcpTools target, string method, string name, string description) =>
        McpServerTool.Create(
            typeof(SqlMcpTools).GetMethod(method, BindingFlags.Instance | BindingFlags.Public)!,
            target,
            new McpServerToolCreateOptions
            {
                Name = name,
                Description = description,
                ReadOnly = true,
                Destructive = false,
                OpenWorld = false,
            });
}

/// <summary>The tool methods: thin wrappers that turn a <see cref="ToolResult"/> into an MCP result.</summary>
internal sealed class SqlMcpTools(ToolPipeline pipeline)
{
    public CallToolResult ListDatabases() => ToCallResult(pipeline.ListDatabases());

    public async Task<CallToolResult> Query(
        [Description("The database identifier from list_databases, such as localhost:1433/app.")] string database,
        [Description("The T-SQL query. Name the columns you need, filter with WHERE, and use TOP.")] string sql,
        CancellationToken cancellationToken) =>
        ToCallResult(await pipeline.QueryAsync(database, sql, cancellationToken).ConfigureAwait(false));

    public CallToolResult NextPage([Description("The handle from query.")] string handle) => ToCallResult(pipeline.NextPage(handle));

    public CallToolResult Abandon([Description("The handle from query.")] string handle) => ToCallResult(pipeline.Abandon(handle));

    private static CallToolResult ToCallResult(ToolResult result) => new()
    {
        IsError = result.IsError,
        Content = [new TextContentBlock { Text = result.Text }],
    };
}
