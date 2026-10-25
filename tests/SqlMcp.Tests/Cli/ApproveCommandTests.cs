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
            { "connectionString": "Server=localhost,1433;Database=app", "allow": ["ddl"], "limits": { "maxRows": 5000, "pageSize": 10 } }
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
        Assert.Contains("localhost:1433/app: allows ddl, maxRows 1000 → 5000", result.Output, StringComparison.Ordinal);
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
            { "defaults": { "deny": ["introspection"], "limits": { "maxRows": 10 } }, "databases": [ { "connectionString": "Server=a;Database=b" } ] }
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
        var approved = new ApprovalStore(paths.Approvals, TimeProvider.System).IsApproved(config.Project!.Path, config.Project.Sha256);
        return PermissionResolver.Resolve(config.User, config.Project, approved).Single(d => d.Identifier == "localhost:1433/app");
    }
}
