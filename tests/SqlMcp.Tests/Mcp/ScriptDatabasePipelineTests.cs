using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using SqlMcp.Audit;
using SqlMcp.Introspection;
using SqlMcp.Mcp;

namespace SqlMcp.Tests.Mcp;

/// <summary>
/// Spec: creation-script, and audit-log "introspection" events for <c>script_database</c> (design I7), with a fake
/// catalog reader and generator writing real files under the project's <c>.sql-mcp/scripts/</c>.
/// </summary>
public sealed class ScriptDatabasePipelineTests : IDisposable
{
    private const string Sales = "sqlhost:1433/sales";
    private const string OneDatabase = """{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;User ID=svc;Password=s3cret" } ] }""";

    private readonly SessionBuilder _builder = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => _builder.Dispose();

    [Fact]
    public async Task Script_denied()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "deny": ["introspection"] } ] }""");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ScriptDatabaseAsync(Sales, null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal($"introspection is denied on {Sales} (source: user config).", result.Text);
        Assert.Empty(_builder.Scripts.Calls);
        Assert.Equal(0, _builder.Catalog.Inventories);
        Assert.Empty(ScriptFilesIn(session));
        Assert.Equal("script_database", Single(session, "denied")["tool"]!.GetValue<string>());
    }

    [Fact]
    public async Task Result()
    {
        _builder.User(OneDatabase);
        IEnumerable<InventoryItem> scriptable =
        [
            .. Enumerable.Range(1, 12).Select(i => Scriptable("dbo", $"t{i}", "table")),
            .. Enumerable.Range(1, 3).Select(i => Scriptable("dbo", $"v{i}", "view")),
            Scriptable("dbo", "p1", "procedure"),
            Scriptable("dbo", "p2", "procedure"),
        ];
        _builder.Catalog.InventoryOutcome = Inventory(scriptable, [new InventoryItem(new CatalogItem(null, "app_reader", CatalogItemTypes.User), InventoryClass.SecurityPrincipal)]);
        _builder.Scripts.Outcome = new CatalogSucceeded<IReadOnlyList<CatalogItem>>([.. scriptable.Select(i => i.Item)], TimeSpan.FromMilliseconds(6));
        using var session = _builder.Build();

        var json = Json(await new ToolPipeline(session).ScriptDatabaseAsync(Sales, null, Cancel));

        var path = json["path"]!.GetValue<string>();
        Assert.Equal(Path.Combine(session.ProjectDirectory, ".sql-mcp", "scripts", "sqlhost_1433_sales-20261025T184000Z.sql"), path);
        Assert.Equal(new FileInfo(path).Length, json["bytes"]!.GetValue<long>());
        Assert.Equal("""{"procedure":2,"table":12,"view":3}""", json["counts"]!.ToJsonString());
        var omitted = Assert.Single(json["omitted"]!.AsArray())!;
        Assert.Equal("""{"name":"app_reader","type":"user","reason":"security principal"}""", omitted.ToJsonString());
        Assert.Null(json["script"]);
        Assert.DoesNotContain("CREATE TABLE", json.ToJsonString(), StringComparison.Ordinal);

        var text = File.ReadAllText(path);
        Assert.StartsWith(ScriptFiles.Header(new ScriptHeader(Sales, "17.0.1000.7", _builder.Time.Now, "1.2.3-test")), text, StringComparison.Ordinal);
        Assert.EndsWith(_builder.Scripts.Text, text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("svc", text, StringComparison.Ordinal);
        Assert.Null(Assert.Single(_builder.Scripts.Calls));
        Assert.Empty(_builder.Catalog.Resolves);
    }

    [Fact]
    public async Task Script_call_is_audited_with_path_counts_and_omitted_count_but_no_script()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.InventoryOutcome = Inventory(
            [Scriptable("dbo", "orders", "table")],
            [new InventoryItem(new CatalogItem(null, "app_reader", CatalogItemTypes.User), InventoryClass.SecurityPrincipal)]);
        using var session = _builder.Build();

        var json = Json(await new ToolPipeline(session).ScriptDatabaseAsync(Sales, null, Cancel));

        var introspection = Single(session, "introspection");
        Assert.Equal("script_database", introspection["tool"]!.GetValue<string>());
        Assert.Equal(Sales, introspection["database"]!.GetValue<string>());
        Assert.Empty(introspection["request"]!.AsArray());
        Assert.True(introspection["succeeded"]!.GetValue<bool>());
        Assert.Equal(json["path"]!.GetValue<string>(), introspection["script_path"]!.GetValue<string>());
        Assert.Equal("""{"table":1}""", introspection["counts"]!.ToJsonString());
        Assert.Equal(1, introspection["omitted_count"]!.GetValue<int>());
        var log = File.ReadAllText(session.Audit.FilePath!);
        Assert.DoesNotContain("CREATE TABLE", log, StringComparison.Ordinal);
        Assert.DoesNotContain("app_reader", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chosen_objects_are_resolved_and_the_inventory_narrowed_to_them()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.InventoryOutcome = Inventory(
            [Scriptable("dbo", "orders", "table"), Scriptable("dbo", "customers", "table")],
            [new InventoryItem(new CatalogItem("dbo", "pricing", "procedure"), InventoryClass.Scriptable, Encrypted: true)],
            [new InventoryItem(new CatalogItem(null, "app_reader", CatalogItemTypes.User), InventoryClass.SecurityPrincipal)]);
        _builder.Catalog.ResolveOutcome = new CatalogSucceeded<IReadOnlyList<ObjectSummary>>(
            [new ObjectSummary("dbo", "orders", ObjectType.Table, null), new ObjectSummary("dbo", "pricing", ObjectType.Procedure, null)],
            TimeSpan.FromMilliseconds(2));
        using var session = _builder.Build();

        var json = Json(await new ToolPipeline(session).ScriptDatabaseAsync(Sales, ["orders", "[dbo].[pricing]"], Cancel));

        Assert.Equal(["orders", "dbo.pricing"], Assert.Single(_builder.Catalog.Resolves).Select(n => n.ToString()));
        Assert.Equal(["orders", "pricing"], Assert.Single(_builder.Scripts.Calls)!.Select(o => o.Name));
        var omitted = Assert.Single(json["omitted"]!.AsArray())!;
        Assert.Equal("""{"schema":"dbo","name":"pricing","type":"procedure","reason":"encrypted"}""", omitted.ToJsonString());
        Assert.Equal(["orders", "dbo.pricing"], Single(session, "introspection")["request"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task An_empty_object_list_is_refused_before_anything_is_read()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ScriptDatabaseAsync(Sales, [], Cancel);

        Assert.True(result.IsError);
        Assert.Equal("objects is empty. Name at least one object, or leave objects out to script the whole database.", result.Text);
        Assert.Empty(_builder.Catalog.Resolves);
        Assert.Equal(0, _builder.Catalog.Inventories);
        Assert.Empty(_builder.Scripts.Calls);
        Assert.Empty(ScriptFilesIn(session));
    }

    [Fact]
    public async Task Unknown_name_in_the_list()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.ResolveOutcome = new CatalogFailed<IReadOnlyList<ObjectSummary>>(
            FailureKind.UnresolvedName, "No object is named dbo.nope.", null, TimeSpan.FromMilliseconds(2));
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ScriptDatabaseAsync(Sales, ["dbo.orders", "dbo.nope"], Cancel);

        Assert.True(result.IsError);
        Assert.Equal("No object is named dbo.nope.", result.Text);
        Assert.Empty(_builder.Scripts.Calls);
        Assert.Equal(0, _builder.Catalog.Inventories);
        Assert.Empty(ScriptFilesIn(session));
        var introspection = Single(session, "introspection");
        Assert.False(introspection["succeeded"]!.GetValue<bool>());
        Assert.Equal("unresolved_name", introspection["kind"]!.GetValue<string>());
        Assert.Null(introspection["script_path"]);
    }

    [Fact]
    public async Task A_bad_name_is_refused_before_anything_is_read()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ScriptDatabaseAsync(Sales, ["dbo.orders", "a.b.c"], Cancel);

        Assert.True(result.IsError);
        Assert.Empty(_builder.Catalog.Resolves);
        Assert.Empty(_builder.Scripts.Calls);
        Assert.DoesNotContain(Events(session), e => e["event"]!.GetValue<string>() == "introspection");
    }

    [Fact]
    public async Task Failed_inventory_writes_no_file()
    {
        _builder.User(OneDatabase);
        _builder.Catalog.InventoryOutcome = new CatalogFailed<IReadOnlyList<InventoryItem>>(FailureKind.Timeout, "reading the catalog timed out.", -2, TimeSpan.FromSeconds(30));
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ScriptDatabaseAsync(Sales, null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal("reading the catalog timed out.", result.Text);
        Assert.Empty(_builder.Scripts.Calls);
        Assert.Empty(ScriptFilesIn(session));
        Assert.Equal("timeout", Single(session, "introspection")["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_script_leaves_no_file()
    {
        _builder.User(OneDatabase);
        _builder.Scripts.Outcome = new CatalogFailed<IReadOnlyList<CatalogItem>>(FailureKind.Other, "Script failed for Table 'dbo.orders'.", null, TimeSpan.FromMilliseconds(3));
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).ScriptDatabaseAsync(Sales, null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal("Script failed for Table 'dbo.orders'.", result.Text);
        Assert.Empty(ScriptFilesIn(session));
        var introspection = Single(session, "introspection");
        Assert.False(introspection["succeeded"]!.GetValue<bool>());
        Assert.Null(introspection["script_path"]);
    }

    [Fact]
    public async Task Cancelled_script_leaves_no_file_and_is_audited()
    {
        _builder.User(OneDatabase);
        _builder.Scripts.Cancels = true;
        using var session = _builder.Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ToolPipeline(session).ScriptDatabaseAsync(Sales, null, Cancel));

        Assert.Empty(ScriptFilesIn(session));
        Assert.False(Single(session, "introspection")["succeeded"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Audit_failure_deletes_the_script()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();
        _builder.Scripts.During = session.Audit.Dispose;

        var result = await new ToolPipeline(session).ScriptDatabaseAsync(Sales, null, Cancel);

        Assert.True(result.IsError);
        Assert.Equal(AuditLog.UnavailableMessage, result.Text);
        Assert.Single(_builder.Scripts.Calls);
        Assert.Empty(ScriptFilesIn(session));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void A_script_that_cannot_be_deleted_is_named_on_stderr()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Relies on a read-only directory blocking deletes, as on Unix.");
        using var temp = new TempDirectory();
        var script = temp.Write("scripts/sales.sql", "CREATE TABLE t (id int);");
        var directory = Path.GetDirectoryName(script)!;
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var diagnostics = new StringWriter();
        try
        {
            ToolPipeline.DeleteUnlogged(script, diagnostics);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.True(File.Exists(script));
        Assert.Contains($"Delete {script} by hand.", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_log_unavailable()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();
        session.Audit.Dispose();

        var result = await new ToolPipeline(session).ScriptDatabaseAsync(Sales, null, Cancel);

        Assert.Equal(AuditLog.UnavailableMessage, result.Text);
        Assert.Empty(_builder.Scripts.Calls);
        Assert.Empty(ScriptFilesIn(session));
    }

    private static InventoryItem Scriptable(string schema, string name, string type) => new(new CatalogItem(schema, name, type), InventoryClass.Scriptable);

    private static CatalogSucceeded<IReadOnlyList<InventoryItem>> Inventory(params IEnumerable<InventoryItem>[] parts) =>
        new([.. parts.SelectMany(p => p)], TimeSpan.FromMilliseconds(4));

    /// <summary>Every file, finished or temp, in the project's scripts directory and the fallback.</summary>
    private static List<string> ScriptFilesIn(SqlMcpSession session) =>
        [.. new[] { Path.Combine(session.ProjectDirectory, ".sql-mcp", "scripts"), session.ScriptsFallbackDirectory }
            .Where(Directory.Exists)
            .SelectMany(Directory.GetFiles)];

    private static JsonObject Json(ToolResult result)
    {
        Assert.False(result.IsError, result.Text);
        return JsonNode.Parse(result.Text)!.AsObject();
    }

    private static List<JsonObject> Events(SqlMcpSession session) => SessionBuilder.Events(session);

    private static JsonObject Single(SqlMcpSession session, string eventType) =>
        Events(session).Single(e => e["event"]!.GetValue<string>() == eventType);
}
