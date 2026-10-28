using SqlMcp.Mcp;

namespace SqlMcp.Tests.Mcp;

public sealed class ToolCatalogTests : IDisposable
{
    private readonly SessionBuilder _builder = new();

    public void Dispose() => _builder.Dispose();

    [Fact]
    public void Agent_reads_the_tool_description()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        var query = ToolCatalog.Build(session).Single(t => t.ProtocolTool.Name == ToolNames.Query).ProtocolTool;

        Assert.Contains("explicit column list", query.Description, StringComparison.Ordinal);
        Assert.Contains("filter with WHERE", query.Description, StringComparison.Ordinal);
        Assert.Contains("TOP", query.Description, StringComparison.Ordinal);
        Assert.EndsWith("Default caps: 1000 rows or 5 MB per result, 100 rows per page, 30 s timeout.", query.Description, StringComparison.Ordinal);
        Assert.True(query.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public void A_database_with_other_limits_is_named_in_the_description()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "limits": { "maxRows": 200, "maxBytes": 1048576 } } ] }""");
        using var session = _builder.Build();

        var query = ToolCatalog.Build(session).Single(t => t.ProtocolTool.Name == ToolNames.Query).ProtocolTool;

        Assert.EndsWith(
            "Databases with other limits: sqlhost:1433/sales: 200 rows or 1 MB per result, 100 rows per page, 30 s timeout.",
            query.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_with_only_another_affected_rows_cap_is_not_named_in_the_query_description()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "limits": { "maxAffectedRows": 50 } } ] }""");
        using var session = _builder.Build();

        var query = ToolCatalog.Build(session).Single(t => t.ProtocolTool.Name == ToolNames.Query).ProtocolTool;

        Assert.DoesNotContain("Databases with other limits", query.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Query_tools_and_listing_are_offered_by_default()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        Assert.Equal(["list_databases", "query", "next_page", "abandon", "list_objects", "describe_object", "script_database"], Names(session));
    }

    [Fact]
    public void No_database_allows_command()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" }, { "connectionString": "Server=other;Database=Hr" } ] }""");
        using var session = _builder.Build();

        Assert.DoesNotContain("command", Names(session));
    }

    [Fact]
    public void No_database_allows_query()
    {
        // list_databases is always offered (2026-10-25 decision); the query and introspection tools are not.
        _builder.User("""{ "defaults": { "deny": ["query", "introspection"] }, "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        Assert.Equal(["list_databases"], Names(session));
    }

    [Fact]
    public void Only_the_listing_is_offered_when_the_config_is_invalid()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["admin"] } ] }""");
        using var session = _builder.Build();

        Assert.Equal(["list_databases"], Names(session));
    }

    [Fact]
    public void Client_sees_a_destructive_tool()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command"] } ] }""");
        using var session = _builder.Build();

        var command = Tool(session, ToolNames.Command);

        Assert.False(command.Annotations?.ReadOnlyHint);
        Assert.True(command.Annotations?.DestructiveHint);
        Assert.False(command.Annotations?.IdempotentHint);
        Assert.False(command.Annotations?.OpenWorldHint);
        Assert.Contains("commits all or nothing", command.Description, StringComparison.Ordinal);
        Assert.Contains("Make each change as narrow as you can", command.Description, StringComparison.Ordinal);
        Assert.Contains("runs in full before it is undone", command.Description, StringComparison.Ordinal);
        Assert.Contains("count the matching rows with query first", command.Description, StringComparison.Ordinal);
        Assert.Contains("need a WHERE clause", command.Description, StringComparison.Ordinal);
        Assert.EndsWith("Default affected-rows cap: 1000 rows per batch.", command.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_write_tool_is_destructive_and_states_the_cap()
    {
        _builder.User(AllWrites);
        using var session = _builder.Build();

        foreach (var name in new[] { ToolNames.Command, ToolNames.Ddl, ToolNames.Execute })
        {
            var tool = Tool(session, name);
            Assert.False(tool.Annotations?.ReadOnlyHint, name);
            Assert.True(tool.Annotations?.DestructiveHint, name);
            Assert.Contains("commits all or nothing", tool.Description, StringComparison.Ordinal);
            Assert.Contains("affected-rows cap: 1000", tool.Description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Execute_says_what_the_cap_counts()
    {
        _builder.User(AllWrites);
        using var session = _builder.Build();

        var execute = Tool(session, ToolNames.Execute);

        Assert.Contains("counts every row count the server reports, including rows a procedure's SELECT returns", execute.Description, StringComparison.Ordinal);
        Assert.Contains("NOCOUNT ON", execute.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_with_another_cap_is_named_where_the_tool_runs()
    {
        // Sales allows command with a cap of 50; Hr has a cap of 10 but doesn't allow command.
        _builder.User("""
            { "databases": [
                { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command"], "limits": { "maxAffectedRows": 50 } },
                { "connectionString": "Server=sqlhost;Database=Hr", "limits": { "maxAffectedRows": 10 } } ] }
            """);
        using var session = _builder.Build();

        Assert.EndsWith(
            "Default affected-rows cap: 1000 rows per batch. Databases with another cap: sqlhost:1433/sales: 50.",
            Tool(session, ToolNames.Command).Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_tool_when_every_group_is_allowed()
    {
        _builder.User(AllWrites);
        using var session = _builder.Build();

        Assert.Equal(["list_databases", "query", "command", "ddl", "execute", "next_page", "abandon", "list_objects", "describe_object", "script_database"], Names(session));
    }

    [Fact]
    public void Introspection_alone_offers_its_tools_without_paging()
    {
        _builder.User("""{ "defaults": { "deny": ["query"] }, "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        Assert.Equal(["list_databases", "list_objects", "describe_object", "script_database"], Names(session));
    }

    [Fact]
    public void Introspection_tools_are_not_offered_when_introspection_is_denied_everywhere()
    {
        _builder.User("""{ "defaults": { "deny": ["introspection"] }, "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" }, { "connectionString": "Server=other;Database=Hr" } ] }""");
        using var session = _builder.Build();

        Assert.DoesNotContain(ToolNames.ListObjects, Names(session));
        Assert.DoesNotContain(ToolNames.DescribeObject, Names(session));
        Assert.DoesNotContain(ToolNames.ScriptDatabase, Names(session));
    }

    [Fact]
    public void Introspection_tools_are_offered_when_one_database_allows_introspection()
    {
        _builder.User("""{ "defaults": { "deny": ["introspection"] }, "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["introspection"] }, { "connectionString": "Server=other;Database=Hr" } ] }""");
        using var session = _builder.Build();

        Assert.Contains(ToolNames.ListObjects, Names(session));
        Assert.Contains(ToolNames.DescribeObject, Names(session));
        Assert.Contains(ToolNames.ScriptDatabase, Names(session));
    }

    [Fact]
    public void Agent_reads_the_list_objects_description()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        var listObjects = Tool(session, ToolNames.ListObjects);

        Assert.True(listObjects.Annotations?.ReadOnlyHint);
        Assert.False(listObjects.Annotations?.DestructiveHint);
        Assert.False(listObjects.Annotations?.OpenWorldHint);
        Assert.Contains("a name pattern where * matches any characters and ? one character", listObjects.Description, StringComparison.Ordinal);
        Assert.Contains("Only objects the database login can see are shown", listObjects.Description, StringComparison.Ordinal);
        Assert.Contains("filter further", listObjects.Description, StringComparison.Ordinal);
        Assert.Contains("then describe_object for an object's details", listObjects.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_reads_the_describe_object_description()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        var describeObject = Tool(session, ToolNames.DescribeObject);

        Assert.True(describeObject.Annotations?.ReadOnlyHint);
        Assert.False(describeObject.Annotations?.DestructiveHint);
        Assert.False(describeObject.Annotations?.OpenWorldHint);
        Assert.Contains("Use list_objects to find names", describeObject.Description, StringComparison.Ordinal);
        Assert.Contains("Only objects the database login can see are shown", describeObject.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_reads_the_script_database_description()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        var scriptDatabase = Tool(session, ToolNames.ScriptDatabase);

        // Read-only by Emmz's 2026-10-26 decision: it never changes the database, and writes only a new, git-ignored file.
        Assert.True(scriptDatabase.Annotations?.ReadOnlyHint);
        Assert.False(scriptDatabase.Annotations?.DestructiveHint);
        Assert.False(scriptDatabase.Annotations?.OpenWorldHint);
        Assert.Contains("schema only: no data", scriptDatabase.Description, StringComparison.Ordinal);
        Assert.Contains("written to a new file, not returned: read it from the path in the result", scriptDatabase.Description, StringComparison.Ordinal);
        Assert.Contains("lists every object left out and why", scriptDatabase.Description, StringComparison.Ordinal);
        Assert.Contains("separated by lines holding only GO", scriptDatabase.Description, StringComparison.Ordinal);
        Assert.Contains("Only objects the database login can see are shown", scriptDatabase.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_database_objects_are_names_or_names_with_a_type()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        var schema = System.Text.Json.Nodes.JsonNode.Parse(Tool(session, ToolNames.ScriptDatabase).InputSchema.GetRawText())!;
        var describeSchema = System.Text.Json.Nodes.JsonNode.Parse(Tool(session, ToolNames.DescribeObject).InputSchema.GetRawText())!;

        Assert.Contains("\"array\"", schema["properties"]!["objects"]!["type"]!.ToJsonString(), StringComparison.Ordinal);
        var oneOf = schema["properties"]!["objects"]!["items"]!["oneOf"]!.AsArray();
        Assert.Equal("string", oneOf[0]!["type"]!.GetValue<string>());
        Assert.Equal("""["name"]""", oneOf[1]!["required"]!.ToJsonString());
        Assert.False(oneOf[1]!["additionalProperties"]!.GetValue<bool>());
        Assert.Contains("table_type", oneOf[1]!["properties"]!["type"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["database"], schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.NotNull(describeSchema["properties"]!["type"]);
        Assert.Equal(["database", "name"], describeSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Paging_is_offered_with_command_alone()
    {
        _builder.User("""{ "defaults": { "deny": ["query", "introspection"] }, "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command"] } ] }""");
        using var session = _builder.Build();

        Assert.Equal(["list_databases", "command", "next_page", "abandon"], Names(session));
    }

    [Fact]
    public void Ddl_alone_returns_no_rows_so_paging_is_not_offered()
    {
        _builder.User("""{ "defaults": { "deny": ["query", "introspection"] }, "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["ddl"] } ] }""");
        using var session = _builder.Build();

        Assert.Equal(["list_databases", "ddl"], Names(session));
    }

    [Fact]
    public void The_ddl_description_sends_views_and_schemas_alone_and_promises_no_rows()
    {
        _builder.User(AllWrites);
        using var session = _builder.Build();

        var ddl = Tool(session, ToolNames.Ddl).Description;
        var command = Tool(session, ToolNames.Command).Description;

        Assert.Contains("CREATE VIEW, ALTER VIEW, CREATE OR ALTER VIEW and CREATE SCHEMA must each be sent in a call of their own.", ddl, StringComparison.Ordinal);
        Assert.Contains("Returns committed: true. Default affected-rows cap", ddl, StringComparison.Ordinal);
        Assert.Contains("TRUNCATE TABLE and DROP report no row count, so the affected-rows cap doesn't limit them.", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("result sets", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("rows each statement affected", ddl, StringComparison.Ordinal);
        Assert.Contains("any result sets are buffered and paged as for query", command, StringComparison.Ordinal);
        Assert.Contains("any result sets are buffered and paged as for query", Tool(session, ToolNames.Execute).Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_query_tool_keeps_its_read_only_annotations()
    {
        _builder.User(AllWrites);
        using var session = _builder.Build();

        var query = Tool(session, ToolNames.Query);

        Assert.True(query.Annotations?.ReadOnlyHint);
        Assert.False(query.Annotations?.DestructiveHint);
    }

    private const string AllWrites = """{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command", "ddl", "execute"] } ] }""";

    private static ModelContextProtocol.Protocol.Tool Tool(SqlMcpSession session, string name) =>
        ToolCatalog.Build(session).Single(t => t.ProtocolTool.Name == name).ProtocolTool;

    private static List<string> Names(SqlMcpSession session) => [.. ToolCatalog.Build(session).Select(t => t.ProtocolTool.Name)];
}
