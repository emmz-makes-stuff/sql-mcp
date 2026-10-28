using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        "Returns the next page of a result, given the handle from query, command or execute. The last page is marked last_page and releases the handle. Handles also expire after 10 minutes idle, and only the 20 most recently used are kept; if one is unknown, run the query again (never rerun a write to see its rows: it has already been committed).";

    public const string Abandon =
        "Releases a result handle and its buffered rows, when you don't need the remaining pages.";

    /// <summary>What every introspection tool's description says about visibility (spec: introspection tool descriptions).</summary>
    private const string VisibilityGuidance =
        "Only objects the database login can see are shown, so a login without permission to view an object's definition may see less than the database holds.";

    public const string ListObjects =
        "Lists the user objects in one database: tables, views, procedures, functions, triggers, sequences, synonyms, types and table types, with each one's schema, name and type, and an approximate row count for tables. " +
        "Use it to find objects without writing catalog queries, then describe_object for an object's details. Narrow the list with a schema, one or more types, and a name pattern where * matches any characters and ? one character; matching ignores case. " +
        "The list is capped at the database's row cap; past it, the result is truncated and gives the total, and you should filter further. " +
        VisibilityGuidance;

    public const string DescribeObject =
        "Describes one object in one database, given its name as schema.name (brackets allowed, such as [dbo].[order lines]); a name without a schema is accepted only when exactly one object has it. Use list_objects to find names. " +
        "Types are named separately from other objects, so a table and a table type can share a name; then give type too, such as table_type. " +
        "For a table or view: its columns in order with types, nullability, defaults, identity and computed expressions; for a table, also its primary key, unique and check constraints, indexes, triggers, the foreign keys it declares and the ones that reference it, and a temporal table's history table and period columns. " +
        "For a view, procedure, function or trigger: its definition, or why it is unavailable (such as encrypted); for a procedure or function, its parameters in order, and a function's kind and return type. " +
        "For a sequence, synonym, type or table type: its settings, target, base type, or columns and constraints. Parts that don't apply or are empty are left out. " +
        VisibilityGuidance;

    public const string ScriptDatabase =
        "Writes a T-SQL script that creates one database's schema: schemas, tables with their keys, constraints, indexes and triggers, views, procedures, functions, sequences, synonyms, types and table types, each after the objects it depends on. " +
        "The script holds schema only: no data, and no CREATE DATABASE, so run it in an existing, empty database. Batches are separated by lines holding only GO. " +
        "The script is written to a new file, not returned: read it from the path in the result, which also gives its size and the number of objects scripted by type. " +
        "With no object list, the whole database is scripted, and the result lists every object left out and why: users, roles and permissions are never scripted, nor are encrypted definitions. " +
        "With a list of names (schema.name, or {\"name\": …, \"type\": …} when an object and a type share the name, resolved as describe_object resolves them), only those objects are scripted, without the objects they depend on; if any name isn't found, no file is written. " +
        "The database is never changed. " +
        VisibilityGuidance;

    /// <summary>What every write tool's description says about the transaction and the cap (spec: write tool descriptions).</summary>
    private const string WriteGuidance =
        "The batch runs in one transaction and commits all or nothing: if any statement fails, the batch times out, or its statements together affect more rows than the affected-rows cap, everything is rolled back. " +
        "Make each change as narrow as you can. A change over the cap runs in full before it is undone, holding locks while it runs and while it rolls back, so before a change that could touch many rows, count the matching rows with query first. " +
        "Name objects in this database only: a name with a database or server part is refused. ";

    /// <summary>What command and execute return; ddl returns no rows, so it says less (see <see cref="DdlGuidance"/>).</summary>
    private const string RowsGuidance =
        "Returns committed: true and the rows each statement affected; any result sets are buffered and paged as for query.";

    public const string CommandGuidance =
        "Changes data on one database with INSERT, UPDATE, DELETE or MERGE. " +
        "Every statement must be a command; anything else is refused before it reaches the database. " +
        "UPDATE and DELETE need a WHERE clause that picks the rows to change. OUTPUT returns the changed rows. " +
        WriteGuidance + RowsGuidance;

    public const string DdlGuidance =
        "Changes the schema of one database: tables, columns, constraints, indexes, views, schemas and sequences, and TRUNCATE TABLE. " +
        "Procedures, functions, triggers, and users, roles and permissions can't be created or changed. A view's SELECT must be a valid query. " +
        "Every statement must be ddl; anything else is refused before it reaches the database. " +
        "CREATE VIEW, ALTER VIEW, CREATE OR ALTER VIEW and CREATE SCHEMA must each be sent in a call of their own. " +
        "TRUNCATE TABLE and DROP report no row count, so the affected-rows cap doesn't limit them. " +
        WriteGuidance + "Returns committed: true.";

    public const string ExecuteGuidance =
        "Runs an existing stored procedure on one database with EXEC, passing each parameter as a literal or NULL. " +
        "System procedures (the sys schema, and names starting sp_ or xp_), dynamic SQL and EXEC of a string are refused. " +
        "The procedure does whatever its code does, so run only procedures you understand. " +
        "The affected-rows cap counts every row count the server reports, including rows a procedure's SELECT returns; a procedure that sets NOCOUNT ON reports none, so the cap can't see its changes. " +
        WriteGuidance + RowsGuidance;

    /// <summary>A write tool's guidance plus the affected-rows cap: the default, then any database it runs on whose cap differs.</summary>
    public static string Write(string guidance, ToolGroup group, IEnumerable<EffectiveDatabase> databases)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{guidance} Default affected-rows cap: {EffectiveLimits.Default.MaxAffectedRows} rows per batch.");
        var others = databases
            .Where(d => d.IsAllowed(group) && d.Limits.MaxAffectedRows != EffectiveLimits.Default.MaxAffectedRows)
            .Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Identifier}: {d.Limits.MaxAffectedRows}"))
            .ToList();
        return others.Count == 0 ? text : $"{text} Databases with another cap: {string.Join("; ", others)}.";
    }

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
/// Builds the server's tool list (design D8, W7). <c>list_databases</c> is always offered (2026-10-25 decision). Each
/// group's tool is offered only if that group is allowed on at least one database, and the paging tools whenever a
/// tool that can return rows is (query, command or execute; ddl returns none). Every call still checks permission.
/// </summary>
internal static class ToolCatalog
{
    public static IReadOnlyList<McpServerTool> Build(SqlMcpSession session)
    {
        var tools = new SqlMcpTools(new ToolPipeline(session));
        var list = new List<McpServerTool>
        {
            Create(tools, nameof(SqlMcpTools.ListDatabases), ToolNames.ListDatabases, ToolDescriptions.ListDatabases),
        };

        var offered = SqlTools.All.Where(t => session.Databases.Any(d => d.IsAllowed(t.Group))).ToList();
        foreach (var tool in offered)
        {
            list.Add(Create(tools, tool.Method, tool.Name, Describe(tool, session.Databases), readOnly: tool.Mode == BatchMode.ReadOnly));
        }

        if (offered.Any(t => t.ReturnsRows))
        {
            list.Add(Create(tools, nameof(SqlMcpTools.NextPage), ToolNames.NextPage, ToolDescriptions.NextPage));
            list.Add(Create(tools, nameof(SqlMcpTools.Abandon), ToolNames.Abandon, ToolDescriptions.Abandon));
        }

        // The introspection tools return no result handles, so they never bring the paging tools with them.
        if (session.Databases.Any(d => d.IsAllowed(ToolGroup.Introspection)))
        {
            list.Add(Create(tools, nameof(SqlMcpTools.ListObjects), ToolNames.ListObjects, ToolDescriptions.ListObjects));
            list.Add(Create(tools, nameof(SqlMcpTools.DescribeObject), ToolNames.DescribeObject, ToolDescriptions.DescribeObject));

            // Read-only too (2026-10-26 decision): it never changes the database, and only ever writes a new, git-ignored file.
            list.Add(WithObjectsItemsSchema(Create(tools, nameof(SqlMcpTools.ScriptDatabase), ToolNames.ScriptDatabase, ToolDescriptions.ScriptDatabase)));
        }

        return list;
    }

    private static string Describe(SqlTool tool, IReadOnlyList<EffectiveDatabase> databases) => tool.Category switch
    {
        StatementCategory.Query => ToolDescriptions.Query(databases),
        StatementCategory.Command => ToolDescriptions.Write(ToolDescriptions.CommandGuidance, tool.Group, databases),
        StatementCategory.Ddl => ToolDescriptions.Write(ToolDescriptions.DdlGuidance, tool.Group, databases),
        StatementCategory.Execute => ToolDescriptions.Write(ToolDescriptions.ExecuteGuidance, tool.Group, databases),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool.Category, "No tool runs this category."),
    };

    /// <summary>
    /// The generated schema gives <c>script_database</c>'s <c>objects</c> items no type at all, since they bind to
    /// <see cref="JsonElement"/>. This says what they are: a name, or a name with a type (Emmz's call, 2026-10-28).
    /// </summary>
    private static McpServerTool WithObjectsItemsSchema(McpServerTool tool)
    {
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
        var objects = schema["properties"]!["objects"]!;
        objects["items"] = new JsonObject
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string" },
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string" },
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Introspection.ObjectTypes.WireNames.Select(n => JsonValue.Create(n))]) },
                    },
                    ["required"] = new JsonArray("name"),
                    ["additionalProperties"] = false,
                }),
        };
        tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
        return tool;
    }

    /// <summary>
    /// A write tool is annotated as not read-only, destructive and not idempotent: running a batch twice applies it
    /// twice. No tool reaches outside the configured databases.
    /// </summary>
    private static McpServerTool Create(SqlMcpTools target, string method, string name, string description, bool readOnly = true) =>
        McpServerTool.Create(
            typeof(SqlMcpTools).GetMethod(method, BindingFlags.Instance | BindingFlags.Public)!,
            target,
            new McpServerToolCreateOptions
            {
                Name = name,
                Description = description,
                ReadOnly = readOnly,
                Destructive = !readOnly,
                Idempotent = readOnly ? null : false,
                OpenWorld = false,
            });
}

/// <summary>The tool methods: thin wrappers that turn a <see cref="ToolResult"/> into an MCP result.</summary>
internal sealed class SqlMcpTools(ToolPipeline pipeline)
{
    private const string DatabaseParameter = "The database identifier from list_databases, such as localhost:1433/app.";
    private const string HandleParameter = "The handle from query, command or execute.";

    private const string TypeParameter =
        "The object's type: table, view, procedure, function, trigger, sequence, synonym, type or table_type. Optional: needed only when an object and a type share the name, such as a table and a table type.";

    /// <summary>The objects parameter's description. <see cref="ToolCatalog"/> gives its items a string-or-object schema.</summary>
    internal const string ObjectsParameter =
        "Only these objects, each as schema.name, such as \"dbo.orders\", or as {\"name\": \"dbo.address\", \"type\": \"table_type\"} when an object and a type share the name. Optional: leave it out to script the whole database.";

    public CallToolResult ListDatabases() => ToCallResult(pipeline.ListDatabases());

    public async Task<CallToolResult> Query(
        [Description(DatabaseParameter)] string database,
        [Description("The T-SQL query. Name the columns you need, filter with WHERE, and use TOP.")] string sql,
        CancellationToken cancellationToken) =>
        ToCallResult(await pipeline.QueryAsync(database, sql, cancellationToken).ConfigureAwait(false));

    public async Task<CallToolResult> Command(
        [Description(DatabaseParameter)] string database,
        [Description("The T-SQL INSERT, UPDATE, DELETE or MERGE statements. Keep each change as narrow as you can.")] string sql,
        CancellationToken cancellationToken) =>
        ToCallResult(await pipeline.CommandAsync(database, sql, cancellationToken).ConfigureAwait(false));

    public async Task<CallToolResult> Ddl(
        [Description(DatabaseParameter)] string database,
        [Description("The T-SQL schema statements: CREATE, ALTER or DROP of tables, indexes, views, schemas or sequences, or TRUNCATE TABLE.")] string sql,
        CancellationToken cancellationToken) =>
        ToCallResult(await pipeline.DdlAsync(database, sql, cancellationToken).ConfigureAwait(false));

    public async Task<CallToolResult> Execute(
        [Description(DatabaseParameter)] string database,
        [Description("The EXEC statements, such as EXEC dbo.archive_orders @before = '2026-01-01'.")] string sql,
        CancellationToken cancellationToken) =>
        ToCallResult(await pipeline.ExecuteAsync(database, sql, cancellationToken).ConfigureAwait(false));

    public async Task<CallToolResult> ListObjects(
        [Description(DatabaseParameter)] string database,
        [Description("Only objects in this schema, such as dbo. Optional.")] string? schema = null,
        [Description("Only objects of these types: table, view, procedure, function, trigger, sequence, synonym, type, table_type. Optional.")] string?[]? types = null,
        [Description("Only objects whose names match this pattern: * matches any characters, ? one character, ignoring case, such as order*. Optional.")] string? pattern = null,
        CancellationToken cancellationToken = default) =>
        ToCallResult(await pipeline.ListObjectsAsync(database, schema, types, pattern, cancellationToken).ConfigureAwait(false));

    public async Task<CallToolResult> DescribeObject(
        [Description(DatabaseParameter)] string database,
        [Description("The object's name as schema.name, such as dbo.orders or [sales].[open orders]. A name without a schema works only when exactly one object has it.")] string name,
        [Description(TypeParameter)] string? type = null,
        CancellationToken cancellationToken = default) =>
        ToCallResult(await pipeline.DescribeObjectAsync(database, name, type, cancellationToken).ConfigureAwait(false));

    public async Task<CallToolResult> ScriptDatabase(
        [Description(DatabaseParameter)] string database,
        [Description(ObjectsParameter)] JsonElement[]? objects = null,
        CancellationToken cancellationToken = default) =>
        ToCallResult(await pipeline.ScriptDatabaseAsync(database, objects, cancellationToken).ConfigureAwait(false));

    public CallToolResult NextPage([Description(HandleParameter)] string handle) => ToCallResult(pipeline.NextPage(handle));

    public CallToolResult Abandon([Description(HandleParameter)] string handle) => ToCallResult(pipeline.Abandon(handle));

    private static CallToolResult ToCallResult(ToolResult result) => new()
    {
        IsError = result.IsError,
        Content = [new TextContentBlock { Text = result.Text }],
    };
}
