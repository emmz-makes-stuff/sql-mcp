using SqlMcp.Cli;
using SqlMcp.Configuration;
using SqlMcp.Permissions;

namespace SqlMcp.Tests.Cli;

/// <summary>Spec: cli, "approve".</summary>
public sealed class ApproveCommandTests : IDisposable
{
    private const string DdlOnApp = """
        {
          "databases": [
            { "connectionString": "Server=localhost,1433;Database=app", "allow": ["ddl"], "limits": { "maxRows": 5000, "pageSize": 10, "maxAffectedRows": 5000 } }
          ]
        }
        """;

    private readonly CliRunner _cli = new();

    public void Dispose() => _cli.Dispose();

    private string DirectoryName => Path.GetFileName(_cli.ProjectDirectory);

    [Fact]
    public async Task Interactive_approval_stores_it_and_the_next_start_applies_the_ddl_grant()
    {
        _cli.Project.Write(".sql-mcp/config.json", DdlOnApp);
        _cli.Terminal.Input.Enqueue(DirectoryName);

        var result = await _cli.RunAsync("approve");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("localhost:1433/app: adds this database, allows ddl, maxRows 1000 → 5000, maxAffectedRows 1000 → 5000", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize", result.Output, StringComparison.Ordinal);
        Assert.Contains($"type the project directory's name ({DirectoryName})", result.Output, StringComparison.Ordinal);
        Assert.Contains("Approved.", result.Output, StringComparison.Ordinal);
        Assert.True(ResolveApp().IsAllowed(ToolGroup.Ddl));
    }

    [Fact]
    public async Task Surrounding_whitespace_in_the_typed_name_is_ignored()
    {
        _cli.Project.Write(".sql-mcp/config.json", DdlOnApp);
        _cli.Terminal.Input.Enqueue($"  {DirectoryName}  ");

        var result = await _cli.RunAsync("approve");

        Assert.Equal(0, result.ExitCode);
        Assert.True(ResolveApp().IsAllowed(ToolGroup.Ddl));
    }

    [Theory]
    [InlineData("y")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Wrong_confirmation_approves_nothing_and_exits_non_zero(string? typed)
    {
        _cli.Project.Write(".sql-mcp/config.json", DdlOnApp);
        if (typed is not null)
        {
            _cli.Terminal.Input.Enqueue(typed);
        }

        var result = await _cli.RunAsync("approve");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Not approved", result.Error, StringComparison.Ordinal);
        Assert.False(ResolveApp().IsAllowed(ToolGroup.Ddl));
    }

    [Fact]
    public async Task Wrong_case_is_a_wrong_confirmation()
    {
        _cli.Project.Write(".sql-mcp/config.json", DdlOnApp);
        _cli.Terminal.Input.Enqueue(DirectoryName.ToUpperInvariant());

        var result = await _cli.RunAsync("approve");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Nothing_to_approve_when_the_project_only_narrows()
    {
        _cli.Project.Write(".sql-mcp/config.json", """
            { "defaults": { "deny": ["introspection"], "limits": { "maxRows": 10 } } }
            """);

        var result = await _cli.RunAsync("approve");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("only narrows access. Nothing needs approval.", result.Output, StringComparison.Ordinal);
        Assert.Equal(0, _cli.Terminal.Reads);
    }

    [Fact]
    public async Task Nothing_to_approve_without_a_project_config()
    {
        var result = await _cli.RunAsync("approve");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Nothing to approve.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Already_approved_file_needs_nothing()
    {
        _cli.Project.Write(".sql-mcp/config.json", DdlOnApp);
        _cli.Terminal.Input.Enqueue(DirectoryName);
        await _cli.RunAsync("approve");

        var result = await _cli.RunAsync("approve");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already approved", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Agent_tries_to_approve_without_a_terminal(bool inputRedirected, bool outputRedirected)
    {
        _cli.Project.Write(".sql-mcp/config.json", DdlOnApp);
        _cli.Terminal.IsInputRedirected = inputRedirected;
        _cli.Terminal.IsOutputRedirected = outputRedirected;
        _cli.Terminal.Input.Enqueue(DirectoryName);

        var result = await _cli.RunAsync("approve");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(
            "Approval must be done by a person in an interactive terminal. Ask the user to run `sql-mcp approve`.",
            result.Error.TrimEnd());
        Assert.Equal(0, _cli.Terminal.Reads);
        Assert.False(ResolveApp().IsAllowed(ToolGroup.Ddl));
    }

    [Theory]
    [InlineData("--yes")]
    [InlineData("-y")]
    [InlineData("--force")]
    [InlineData("--no-prompt")]
    [InlineData("--confirm")]
    public async Task Skip_flags_are_not_accepted(string flag)
    {
        _cli.Project.Write(".sql-mcp/config.json", DdlOnApp);
        _cli.Terminal.Input.Enqueue(DirectoryName);

        var result = await _cli.RunAsync("approve", flag);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(flag, result.Error, StringComparison.Ordinal);
        Assert.Equal(0, _cli.Terminal.Reads);
        Assert.False(ResolveApp().IsAllowed(ToolGroup.Ddl));
    }

    [Fact]
    public async Task A_project_only_database_is_shown_with_the_variables_it_reads_and_never_their_values()
    {
        _cli.Variables["APP_PW"] = "hunter2";
        _cli.Project.Write(".sql-mcp/config.json", """
            { "databases": [ { "connectionString": "Server=evil.example,1433;Database=app;User Id=a;Password=${env:APP_PW}" } ] }
            """);
        _cli.Terminal.Input.Enqueue(DirectoryName);

        var result = await _cli.RunAsync("approve");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("evil.example:1433/app: adds this database, reading ${env:APP_PW}", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_database_stays_approved_through_other_edits()
    {
        _cli.Project.Write(".sql-mcp/config.json", DdlOnApp);
        _cli.Terminal.Input.Enqueue(DirectoryName);
        await _cli.RunAsync("approve");

        await _cli.RunAsync("deny", "introspection");

        var app = ResolveApp();
        Assert.True(app.Entry.IsAvailable);
        Assert.False(app.IsAllowed(ToolGroup.Ddl));
        var result = await _cli.RunAsync("approve");
        Assert.Contains("localhost:1433/app: allows ddl", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ApprovalMessages.AddsDatabase, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_unparseable_entries_do_not_stop_approval()
    {
        var userConfig = _cli.Context.Paths().UserConfig;
        Directory.CreateDirectory(Path.GetDirectoryName(userConfig)!);
        File.WriteAllText(userConfig, """{ "databases": [ { "connectionString": "Server=a;Bogus=1" } ] }""");
        _cli.Project.Write(".sql-mcp/config.json", """
            { "databases": [ { "connectionString": "Server=b;Bogus=2" }, { "connectionString": "Server=localhost,1433;Database=app" } ] }
            """);
        _cli.Terminal.Input.Enqueue(DirectoryName);

        var result = await _cli.RunAsync("approve");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("localhost:1433/app: adds this database", result.Output, StringComparison.Ordinal);
        Assert.Contains("Approved.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Approve_has_no_options_of_its_own()
    {
        var approve = SqlMcpCli.Build(_cli.Context).Subcommands.Single(c => c.Name == "approve");

        Assert.DoesNotContain(approve.Options, o => o.Name is not ("--help" or "-h" or "-?"));
        Assert.Empty(approve.Arguments);
    }

    private EffectiveDatabase ResolveApp()
    {
        var paths = _cli.Context.Paths();
        var config = ConfigDiscovery.Discover(paths, _ => null);
        var approval = new ApprovalStore(paths.Approvals, TimeProvider.System).Read(config.Project);
        return PermissionResolver.Resolve(config.User, config.Project, approval).Single(d => d.Identifier == "localhost:1433/app");
    }
}
