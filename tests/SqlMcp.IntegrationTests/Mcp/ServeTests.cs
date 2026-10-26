using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlMcp.Configuration;
using SqlMcp.Execution.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Mcp;

/// <summary>
/// End to end: <c>sql-mcp serve</c> as a child process, driven through the MCP SDK client (design D11). HOME points
/// at a temp directory, so the developer's own user config and approvals are never read.
/// </summary>
public sealed class ServeTests : IAsyncLifetime
{
    private readonly SqlServerFixture _db;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sql-mcp-itests", Guid.NewGuid().ToString("N"));
    private readonly string _table = $"users_{Guid.NewGuid():N}";
    private McpClient? _client;

    public ServeTests(SqlServerFixture db) => _db = db;

    private string Home => Path.Combine(_root, "home");

    private string Project => Path.Combine(_root, "project");

    private string Database => DatabaseIdentity.FromConnectionString(_db.ConnectionString).Identifier;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string ServerDll => typeof(SqlServerQueryExecutor).Assembly.Location;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Path.Combine(Project, ".sql-mcp"));
        var config = new JsonObject { ["databases"] = new JsonArray(new JsonObject { ["connectionString"] = _db.ConnectionString }) };
        await File.WriteAllTextAsync(Path.Combine(Project, ".sql-mcp", "config.json"), config.ToJsonString());

        // A project-only database is unavailable until a person approves it; stand in for them here.
        var paths = ConfigPaths.Create(isWindows: false, Home, Home, Project, projectDirectory: null);
        new ApprovalStore(paths.Approvals, TimeProvider.System).Approve(ConfigLoader.Load(paths.ProjectConfig, _ => null));

        await _db.ScalarAsync($"CREATE TABLE dbo.{_table} (id int, name nvarchar(50)); INSERT INTO dbo.{_table} VALUES (1, N'alice'), (2, N'bob'), (3, N'carol')");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory doesn't affect other tests.
        }
    }

    [Fact]
    public async Task Started_by_a_client()
    {
        var client = await Connect();

        var tools = await client.ListToolsAsync(cancellationToken: Cancel);

        Assert.Equal(["abandon", "list_databases", "next_page", "query"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal("sql-mcp", client.ServerInfo?.Name);
    }

    [Fact]
    public async Task Successful_query()
    {
        var client = await Connect();

        var result = await Call(client, "query", new() { ["database"] = Database, ["sql"] = $"SELECT id, name FROM dbo.{_table} WHERE id < 10 ORDER BY id" });

        Assert.NotEqual(true, result.IsError);
        var page = JsonNode.Parse(Text(result))!;
        Assert.Equal("""[{"name":"id","type":"int"},{"name":"name","type":"nvarchar(50)"}]""", page["columns"]!.ToJsonString());
        Assert.Equal("""[[1,"alice"],[2,"bob"],[3,"carol"]]""", page["rows"]!.ToJsonString());
        Assert.Equal(3, page["rows_buffered"]!.GetValue<int>());
        Assert.False(page["truncated"]!.GetValue<bool>());
        Assert.True(page["last_page"]!.GetValue<bool>());
        Assert.Null(page["handle"]);

        var events = await AuditEvents();
        Assert.Equal("session_start", events[0]["event"]!.GetValue<string>());
        var executed = events.Single(e => e["event"]!.GetValue<string>() == "executed");
        Assert.Equal(Database, executed["database"]!.GetValue<string>());
        Assert.Equal(3, executed["rows"]!.GetValue<long>());

        var log = await File.ReadAllTextAsync(AuditPath(), Cancel);
        Assert.DoesNotContain("alice", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejected_statement()
    {
        var client = await Connect();

        var result = await Call(client, "query", new() { ["database"] = Database, ["sql"] = $"DELETE FROM dbo.{_table} WHERE 1 = 1" });

        Assert.True(result.IsError);
        Assert.Equal("Rejected, nothing was sent to the database: classified as command; the command tool is denied on this database", Text(result));
        Assert.Equal(3, await _db.ScalarAsync($"SELECT COUNT(*) FROM dbo.{_table}"));

        var events = await AuditEvents();
        Assert.DoesNotContain(events, e => e["event"]!.GetValue<string>() == "executed");
        var denied = events.Single(e => e["event"]!.GetValue<string>() == "denied");
        Assert.Equal("query", denied["tool"]!.GetValue<string>());
        Assert.Equal($"DELETE FROM dbo.{_table} WHERE 1 = 1", denied["sql"]!.GetValue<string>());
        Assert.Contains("classified as command", denied["reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stdout_carries_only_protocol_messages_and_the_session_ends_cleanly()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", [ServerDll, "serve", "--project", Project])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Project,
            Environment = { ["HOME"] = Home },
        })!;
        var stderr = process.StandardError.ReadToEndAsync(Cancel);

        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"itest","version":"1"}}}""");
        await process.StandardInput.FlushAsync(Cancel);
        var response = await process.StandardOutput.ReadLineAsync(Cancel);

        // Closing stdin ends the session; anything else on stdout must still be JSON-RPC.
        process.StandardInput.Close();
        var rest = await process.StandardOutput.ReadToEndAsync(Cancel);
        await process.WaitForExitAsync(Cancel);

        var reply = JsonNode.Parse(response!)!;
        Assert.Equal(1, reply["id"]!.GetValue<int>());
        Assert.Equal("sql-mcp", reply["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        foreach (var line in rest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assert.Equal("2.0", JsonNode.Parse(line)!["jsonrpc"]!.GetValue<string>());
        }

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Audit log:", await stderr, StringComparison.Ordinal);
        var events = await AuditEvents();
        Assert.Equal(["session_start", "session_end"], events.Select(e => e["event"]!.GetValue<string>()));
        Assert.Equal(Path.Combine(Project, ".sql-mcp", "config.json"), events[1]["config_files"]![0]!.GetValue<string>());
    }

    private async Task<McpClient> Connect()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sql-mcp",
            Command = "dotnet",
            Arguments = [ServerDll, "serve", "--project", Project],
            WorkingDirectory = Project,
            EnvironmentVariables = new Dictionary<string, string?> { ["HOME"] = Home },
        });
        _client = await McpClient.CreateAsync(transport, cancellationToken: Cancel);
        return _client;
    }

    private static Task<CallToolResult> Call(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        client.CallToolAsync(tool, arguments, cancellationToken: Cancel).AsTask();

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private string AuditPath() => Assert.Single(Directory.GetFiles(Path.Combine(Project, ".sql-mcp", "logs"), "*.jsonl"));

    private async Task<List<JsonObject>> AuditEvents()
    {
        // The server holds the file open; share it for reading.
        await using var stream = new FileStream(AuditPath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = (await reader.ReadToEndAsync(Cancel)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return [.. lines.Select(line => JsonSerializer.Deserialize<JsonObject>(line)!)];
    }
}
