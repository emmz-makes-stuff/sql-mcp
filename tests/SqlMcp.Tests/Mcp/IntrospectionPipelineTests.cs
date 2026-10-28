using System.Text.Json.Nodes;
using SqlMcp.Audit;
using SqlMcp.Introspection;
using SqlMcp.Mcp;

namespace SqlMcp.Tests.Mcp;

// The introspection tools' shared flow (design I7), with a fake catalog reader.
public sealed class IntrospectionPipelineTests : IDisposable
{
    private const string Sales = "sqlhost:1433/sales";
    private const string OneDatabase = """{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;Password=s3cret" } ] }""";

    private readonly SessionBuilder _builder = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => _builder.Dispose();

    [Fact]
    public async Task Introspection_denied()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "deny": ["introspection"] } ] }""");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ListObjectsAsync(Sales, null, null, null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal($"introspection is denied on {Sales} (source: user config).", result.Text);
        Assert.Empty(_builder.Catalog.Lists);
        var denied = Single(session, "denied");
        Assert.Equal("list_objects", denied["tool"]!.GetValue<string>());
        Assert.Equal("user config", denied["source"]!.GetValue<string>());
        Assert.Null(denied["sql"]);
        Assert.DoesNotContain(Events(session), e => e["event"]!.GetValue<string>() == "introspection");
    }

    [Fact]
    public async Task Successful_list_is_audited_without_the_objects()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.ListOutcome = new CatalogSucceeded<ObjectList>(
            new ObjectList([new ObjectSummary("dbo", "secret_salaries", ObjectType.Table, 12), new ObjectSummary(null, "audit_ddl", ObjectType.Trigger, null)], 2, false),
            TimeSpan.FromMilliseconds(7));
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ListObjectsAsync(Sales, " sales ", ["table", "view"], "sal*", Cancel);

        var json = Json(result);
        var filter = Assert.Single(_builder.Catalog.Lists);
        Assert.Equal("sales", filter.Schema);
        Assert.Equal([ObjectType.Table, ObjectType.View], filter.Types);
        Assert.Equal("sal*", filter.Pattern);
        Assert.Equal(2, json["total"]!.GetValue<int>());
        Assert.False(json["truncated"]!.GetValue<bool>());
        Assert.Null(json["message"]);
        var objects = json["objects"]!.AsArray();
        Assert.Equal("""{"schema":"dbo","name":"secret_salaries","type":"table","approx_rows":12}""", objects[0]!.ToJsonString());
        Assert.Equal("""{"name":"audit_ddl","type":"trigger"}""", objects[1]!.ToJsonString());

        var introspection = Single(session, "introspection");
        Assert.Equal("list_objects", introspection["tool"]!.GetValue<string>());
        Assert.Equal(Sales, introspection["database"]!.GetValue<string>());
        Assert.Equal(["schema=sales", "types=table,view", "pattern=sal*"], introspection["request"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(7, introspection["duration_ms"]!.GetValue<long>());
        Assert.True(introspection["succeeded"]!.GetValue<bool>());
        Assert.Null(introspection["kind"]);
        Assert.DoesNotContain("secret_salaries", File.ReadAllText(session.Audit.FilePath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blank_filters_are_not_given()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        Json(await new ToolPipeline(session).ListObjectsAsync(Sales, "  ", [], "", Cancel));

        var filter = Assert.Single(_builder.Catalog.Lists);
        Assert.Null(filter.Schema);
        Assert.Empty(filter.Types);
        Assert.Null(filter.Pattern);
        Assert.Empty(Single(session, "introspection")["request"]!.AsArray());
    }

    [Fact]
    public async Task Unknown_object_type_is_refused_before_the_catalog_is_read()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ListObjectsAsync(Sales, null, ["package"], null, Cancel);

        Assert.True(result.IsError);
        Assert.StartsWith("'package' is not an object type. Use one of: table, view,", result.Text, StringComparison.Ordinal);
        Assert.Empty(_builder.Catalog.Lists);
    }

    [Fact]
    public async Task More_objects_than_the_cap()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.ListOutcome = new CatalogSucceeded<ObjectList>(
            new ObjectList([.. Enumerable.Range(1, 1000).Select(i => new ObjectSummary("dbo", $"t{i}", ObjectType.Table, 0))], 3000, true),
            TimeSpan.FromMilliseconds(9));
        using var session = _builder.Build();

        var json = Json(await new ToolPipeline(session).ListObjectsAsync(Sales, null, null, null, Cancel));

        Assert.Equal(1000, json["objects"]!.AsArray().Count);
        Assert.Equal(3000, json["total"]!.GetValue<int>());
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Equal(
            "Showing 1000 of 3000 matching objects: the list is capped at this database's row cap. Filter by schema, type or name pattern to see the rest.",
            json["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_introspection()
    {
        const string message = "reading the catalog timed out after 30 seconds (this database's command timeout), so it was cancelled. Ask for less: filter by schema, type or name.";
        _builder.User(OneDatabase);
        _builder.Catalog.ListOutcome = new CatalogFailed<ObjectList>(FailureKind.Timeout, message, -2, TimeSpan.FromSeconds(30));
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ListObjectsAsync(Sales, null, null, null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal(message, result.Text);
        var introspection = Single(session, "introspection");
        Assert.False(introspection["succeeded"]!.GetValue<bool>());
        Assert.Equal("timeout", introspection["kind"]!.GetValue<string>());
        Assert.Equal(-2, introspection["error_number"]!.GetValue<int>());
        Assert.Equal(30000, introspection["duration_ms"]!.GetValue<long>());
        Assert.DoesNotContain("timed out", File.ReadAllText(session.Audit.FilePath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_read_is_still_audited()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.Cancels = true;
        using var session = _builder.Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ToolPipeline(session).ListObjectsAsync(Sales, null, null, null, Cancel));

        var introspection = Single(session, "introspection");
        Assert.False(introspection["succeeded"]!.GetValue<bool>());
        Assert.Equal("other", introspection["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_exception_from_the_reader_is_audited_and_reported()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.Throws = new InvalidOperationException("The catalog query returned an unknown object type 'x'.");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ListObjectsAsync(Sales, null, null, null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal("list_objects failed: The catalog query returned an unknown object type 'x'.", result.Text);
        Assert.Equal("other", Single(session, "introspection")["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_database_is_refused_without_reading()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ListObjectsAsync("nowhere:1433/x", null, null, null, Cancel);

        Assert.True(result.IsError);
        Assert.Empty(_builder.Catalog.Lists);
    }

    [Fact]
    public async Task Audit_log_unavailable()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();
        session.Audit.Dispose();

        var result = await new ToolPipeline(session).ListObjectsAsync(Sales, null, null, null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal(AuditLog.UnavailableMessage, result.Text);
        Assert.Empty(_builder.Catalog.Lists);
    }

    [Fact]
    public async Task Describe_call_is_audited_without_the_details()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.DescribeOutcome = new CatalogSucceeded<ObjectDetails>(
            new ObjectDetails("dbo", "orders", ObjectType.Table)
            {
                Columns = [new ColumnDetails("secret_column", "int", false, null, true, null)],
                Definition = new Definition.Text("CREATE TABLE secret_definition"),
            },
            TimeSpan.FromMilliseconds(6));
        using var session = _builder.Build();

        Json(await new ToolPipeline(session).DescribeObjectAsync(Sales, "[dbo].[orders]", null, Cancel));

        Assert.Equal(new ObjectName("dbo", "orders"), Assert.Single(_builder.Catalog.Describes));
        var introspection = Single(session, "introspection");
        Assert.Equal("describe_object", introspection["tool"]!.GetValue<string>());
        Assert.Equal(Sales, introspection["database"]!.GetValue<string>());
        Assert.Equal(["dbo.orders"], introspection["request"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(6, introspection["duration_ms"]!.GetValue<long>());
        Assert.True(introspection["succeeded"]!.GetValue<bool>());
        var log = File.ReadAllText(session.Audit.FilePath!);
        Assert.DoesNotContain("secret_column", log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_definition", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_view_gives_columns_and_a_definition_and_no_keys()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.DescribeOutcome = new CatalogSucceeded<ObjectDetails>(
            new ObjectDetails("sales", "open_orders", ObjectType.View)
            {
                Columns = [new ColumnDetails("id", "int", false, null, null, null), new ColumnDetails("total", "decimal(10,2)", true, null, null, "([a]+[b])")],
                UniqueConstraints = [],
                Definition = new Definition.Text("CREATE VIEW sales.open_orders AS SELECT 1"),
            },
            TimeSpan.FromMilliseconds(3));
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).DescribeObjectAsync(Sales, "sales.open_orders", null, Cancel);

        Assert.False(result.IsError, result.Text);
        Assert.Equal(
            """{"schema":"sales","name":"open_orders","type":"view","columns":[{"name":"id","type":"int","nullable":false},{"name":"total","type":"decimal(10,2)","nullable":true,"computed":"([a]+[b])"}],"definition":"CREATE VIEW sales.open_orders AS SELECT 1"}""",
            result.Text);
    }

    [Fact]
    public async Task An_unavailable_definition_says_why()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.DescribeOutcome = new CatalogSucceeded<ObjectDetails>(
            new ObjectDetails("dbo", "secret_report", ObjectType.Procedure)
            {
                Parameters = [new ParameterDetails("@before", "date", "input", null)],
                Definition = new Definition.Unavailable(Definition.Encrypted),
            },
            TimeSpan.FromMilliseconds(3));
        using var session = _builder.Build();

        var json = Json(await new ToolPipeline(session).DescribeObjectAsync(Sales, "dbo.secret_report", null, Cancel));

        Assert.Equal("""{"unavailable":"encrypted"}""", json["definition"]!.ToJsonString());
        Assert.Equal("""[{"name":"@before","type":"date","direction":"input"}]""", json["parameters"]!.ToJsonString());
    }

    [Fact]
    public async Task A_bad_name_is_refused_before_the_catalog_is_read()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).DescribeObjectAsync(Sales, "app.dbo.orders", null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal("'app.dbo.orders' names another database or server. Give schema.name in this database, such as dbo.orders.", result.Text);
        Assert.Empty(_builder.Catalog.Describes);
        Assert.DoesNotContain(Events(session), e => e["event"]!.GetValue<string>() == "introspection");
    }

    [Fact]
    public async Task An_unresolved_name_is_audited_as_failed()
    {
        const string message = "No object named 'dbo.nothing_here' was found. Use list_objects to find it; only objects this database login can see are found.";
        _builder.User(OneDatabase);
        _builder.Catalog.DescribeOutcome = new CatalogFailed<ObjectDetails>(FailureKind.UnresolvedName, message, null, TimeSpan.FromMilliseconds(2));
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).DescribeObjectAsync(Sales, "dbo.nothing_here", null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal(message, result.Text);
        var introspection = Single(session, "introspection");
        Assert.False(introspection["succeeded"]!.GetValue<bool>());
        Assert.Equal("unresolved_name", introspection["kind"]!.GetValue<string>());
        Assert.Equal(["dbo.nothing_here"], introspection["request"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Describe_denied()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "deny": ["introspection"] } ] }""");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).DescribeObjectAsync(Sales, "dbo.orders", null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal($"introspection is denied on {Sales} (source: user config).", result.Text);
        Assert.Empty(_builder.Catalog.Describes);
        Assert.Equal("describe_object", Single(session, "denied")["tool"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bad_argument_on_a_denied_database_is_audited_as_denied()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "deny": ["introspection"] } ] }""");
        using var session = _builder.Build();
        var pipeline = new ToolPipeline(session);

        var name = await pipeline.DescribeObjectAsync(Sales, "a.b.c.d", null, Cancel);
        var type = await pipeline.ListObjectsAsync(Sales, null, ["bogus"], null, Cancel);

        Assert.Equal($"introspection is denied on {Sales} (source: user config).", name.Text);
        Assert.Equal($"introspection is denied on {Sales} (source: user config).", type.Text);
        Assert.Equal(["describe_object", "list_objects"], Events(session).Where(e => e["event"]!.GetValue<string>() == "denied").Select(e => e["tool"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_bad_argument_with_the_audit_log_unavailable_gets_the_audit_refusal()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();
        session.Audit.Dispose();

        var result = await new ToolPipeline(session).DescribeObjectAsync(Sales, "a.b.c.d", null, Cancel);

        Assert.Equal(AuditLog.UnavailableMessage, result.Text);
    }

    [Fact]
    public async Task A_null_type_is_not_an_object_type()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ListObjectsAsync(Sales, null, ["table", null], null, Cancel);

        Assert.True(result.IsError);
        Assert.StartsWith("'null' is not an object type. Use one of: table, view,", result.Text, StringComparison.Ordinal);
        Assert.Empty(_builder.Catalog.Lists);
    }

    [Fact]
    public async Task Describe_with_a_type()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.DescribeOutcome = new CatalogSucceeded<ObjectDetails>(new ObjectDetails("dbo", "address", ObjectType.TableType), TimeSpan.Zero);
        using var session = _builder.Build();

        Json(await new ToolPipeline(session).DescribeObjectAsync(Sales, "dbo.address", " Table_Type ", Cancel));

        Assert.Equal(new ObjectName("dbo", "address") { Type = ObjectType.TableType }, Assert.Single(_builder.Catalog.Describes));
        Assert.Equal(["dbo.address (table_type)"], Single(session, "introspection")["request"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Describe_with_an_unknown_type()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).DescribeObjectAsync(Sales, "dbo.address", "package", Cancel);

        Assert.True(result.IsError);
        Assert.StartsWith("'package' is not an object type.", result.Text, StringComparison.Ordinal);
        Assert.Empty(_builder.Catalog.Describes);
    }

    private static JsonObject Json(ToolResult result)
    {
        Assert.False(result.IsError, result.Text);
        return JsonNode.Parse(result.Text)!.AsObject();
    }

    private static List<JsonObject> Events(SqlMcpSession session) => SessionBuilder.Events(session);

    private static JsonObject Single(SqlMcpSession session, string eventType) =>
        Events(session).Single(e => e["event"]!.GetValue<string>() == eventType);
}
