using SqlMcp.Configuration;

namespace SqlMcp.Tests.Cli;

/// <summary>Spec: cli, "init".</summary>
public sealed class InitCommandTests : IDisposable
{
    private readonly CliRunner _cli = new();

    private string ConfigPath => Path.Combine(_cli.ProjectDirectory, ".sql-mcp", "config.json");

    private string GitIgnorePath => Path.Combine(_cli.ProjectDirectory, ".sql-mcp", ".gitignore");

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Fresh_project_creates_both_files_and_exits_0()
    {
        var result = await _cli.RunAsync("init");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal("logs/\n", File.ReadAllText(GitIgnorePath));
        Assert.Contains($"Created {ConfigPath}.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fresh_config_has_no_databases_and_references_the_schema()
    {
        await _cli.RunAsync("init");

        var config = ConfigLoader.Load(ConfigPath, _ => null);

        Assert.Empty(config.Databases);
        Assert.Contains($"\"$schema\": \"{ProjectLayout.SchemaUrl}\"", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_id_matches_the_url_init_writes()
    {
        using var schema = System.Text.Json.JsonDocument.Parse(ConfigSchema.Text);

        Assert.Equal(ProjectLayout.SchemaUrl, schema.RootElement.GetProperty("$id").GetString());
    }

    [Fact]
    public async Task Already_initialised_leaves_the_config_unchanged_and_exits_0()
    {
        const string existing = "// mine\n{ \"databases\": [], }\n";
        _cli.Project.Write(".sql-mcp/config.json", existing);
        _cli.Project.Write(".sql-mcp/.gitignore", "custom\n");

        var result = await _cli.RunAsync("init");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(existing, File.ReadAllText(ConfigPath));
        Assert.Equal("custom\n", File.ReadAllText(GitIgnorePath));
        Assert.Contains("already exists", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Already_initialised_without_a_gitignore_adds_only_the_gitignore()
    {
        const string existing = "{ \"databases\": [] }";
        _cli.Project.Write(".sql-mcp/config.json", existing);

        var result = await _cli.RunAsync("init");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(existing, File.ReadAllText(ConfigPath));
        Assert.Equal("logs/\n", File.ReadAllText(GitIgnorePath));
    }
}
