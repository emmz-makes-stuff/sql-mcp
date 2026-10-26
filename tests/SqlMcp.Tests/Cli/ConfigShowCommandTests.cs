namespace SqlMcp.Tests.Cli;

/// <summary>Spec: cli, "Show effective config" and "Invalid config".</summary>
public sealed class ConfigShowCommandTests : IDisposable
{
    private readonly CliRunner _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Show_effective_config_lists_permissions_with_sources_and_masks_secrets()
    {
        _cli.Project.Write(".sql-mcp/config.json", """
            {
              "defaults": { "deny": ["introspection"] },
              "databases": [
                { "connectionString": "Server=localhost,1433;Database=app;User Id=sa;Password=hunter2", "allow": ["ddl"] },
                { "connectionString": "Server=db2;Database=reports;Password=${env:REPORTS_PW}", "limits": { "maxRows": 10 } }
              ]
            }
            """);

        var result = await _cli.RunAsync("config", "show");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.DoesNotContain("hunter2", result.Output, StringComparison.Ordinal);
        // The masker rebuilds the string through DbConnectionStringBuilder, which lower-cases keys.
        Assert.Contains("password=***", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("password=${env:REPORTS_PW}", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(not approved)", result.Output, StringComparison.Ordinal);
        Assert.Contains("localhost:1433/app  (project config)", result.Output, StringComparison.Ordinal);
        Assert.Contains("allowed:    query (default)", result.Output, StringComparison.Ordinal);
        Assert.Contains("introspection (project config)", result.Output, StringComparison.Ordinal);
        Assert.Contains("ddl (project config not approved)", result.Output, StringComparison.Ordinal);
        Assert.Contains("available:  no (project config adds this database but is not approved.", result.Output, StringComparison.Ordinal);
        Assert.Contains("Also: environment variable REPORTS_PW is not set)", result.Output, StringComparison.Ordinal);
        Assert.Contains("limits:     10 rows,", result.Output, StringComparison.Ordinal);
        Assert.Contains("note:       project config adds this database, grants ddl but is not approved.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Available_database_from_the_user_file()
    {
        _cli.Variables["APP_PW"] = "secret-value";
        await _cli.RunAsync("db", "add", "--user", "Server=localhost;Database=app;Password=${env:APP_PW}");

        var result = await _cli.RunAsync("config", "show");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("localhost:1433/app  (user config)", result.Output, StringComparison.Ordinal);
        Assert.Contains("available:  yes", result.Output, StringComparison.Ordinal);
        Assert.Contains("1000 rows, 5 MB, 100 rows per page, 30 s timeout", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", result.Output, StringComparison.Ordinal);
        Assert.Contains("(not found)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_databases_says_where_to_add_them()
    {
        var result = await _cli.RunAsync("config", "show");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No databases are configured.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_config_exits_non_zero_and_prints_the_validation_error()
    {
        var path = _cli.Project.Write(".sql-mcp/config.json", """{ "databases": [ { "connectionString": "Server=x;Database=y", "allow": ["admin"] } ] }""");

        var result = await _cli.RunAsync("config", "show");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains(path, result.Error, StringComparison.Ordinal);
        Assert.Contains("$.databases[0].allow[0]", result.Error, StringComparison.Ordinal);
    }
}
