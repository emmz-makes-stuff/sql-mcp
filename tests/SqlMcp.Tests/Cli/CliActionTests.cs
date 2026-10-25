using System.CommandLine;
using SqlMcp.Cli;
using SqlMcp.Configuration;

namespace SqlMcp.Tests.Cli;

/// <summary>
/// Spec: cli, "Exit codes". "Invalid config" runs through the shared handler here; section 8.3 adds the same
/// scenario against <c>config show</c>.
/// </summary>
public sealed class CliActionTests
{
    [Fact]
    public async Task Invalid_config_exits_non_zero_and_prints_the_validation_error()
    {
        var result = await RunAsync(() => throw new ConfigException("/p/.sql-mcp/config.json: $.databases[0].allow[0]: admin is not a tool group."));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal("/p/.sql-mcp/config.json: $.databases[0].allow[0]: admin is not a tool group.", result.Error.TrimEnd());
    }

    [Fact]
    public async Task Invalid_config_loaded_from_disk_exits_non_zero_with_file_and_location()
    {
        using var cli = new CliRunner();
        var path = cli.Project.Write(".sql-mcp/config.json", """{ "databases": [ { "connectionString": "Server=x;Database=y", "allow": ["admin"] } ] }""");

        var result = await RunAsync(() => ConfigDiscovery.Discover(cli.Context.Paths(), _ => null));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(path, result.Error, StringComparison.Ordinal);
        Assert.Contains("$.databases[0].allow[0]", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_exception_prints_its_message_only()
    {
        var result = await RunAsync(() => throw new CliException("Nope."));

        Assert.Equal(CliAction.Failure, result.ExitCode);
        Assert.Equal("Nope.", result.Error.TrimEnd());
    }

    [Fact]
    public async Task Success_exits_0()
    {
        var result = await RunAsync(() => { });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
    }

    [Fact]
    public async Task Unknown_option_exits_non_zero()
    {
        using var cli = new CliRunner();

        var result = await cli.RunAsync("init", "--yes");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cli.ProjectDirectory, ".sql-mcp")));
    }

    private static Task<CliResult> RunAsync(Action body)
    {
        var command = new Command("probe");
        CliAction.Set(command, _ =>
        {
            body();
            return 0;
        });
        return CliRunner.RunAsync(new RootCommand { command }, "probe");
    }
}
