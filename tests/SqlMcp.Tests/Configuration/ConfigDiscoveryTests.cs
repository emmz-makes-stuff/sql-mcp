using SqlMcp.Configuration;
using SqlMcp.Permissions;

namespace SqlMcp.Tests.Configuration;

public sealed class ConfigDiscoveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string Home => Path.Combine(_temp.Path, "home");

    private string AppData => Path.Combine(_temp.Path, "appdata");

    private string Project => Path.Combine(_temp.Path, "project");

    public ConfigDiscoveryTests() => Directory.CreateDirectory(Project);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void User_path_is_under_dot_config_on_unix()
    {
        var paths = ConfigPaths.Create(false, Home, AppData, Project, null);

        Assert.Equal(Path.Combine(Home, ".config", "sql-mcp", "config.json"), paths.UserConfig);
        Assert.Equal(Path.Combine(Home, ".config", "sql-mcp", "approvals.json"), paths.Approvals);
    }

    [Fact]
    public void User_path_is_under_appdata_on_windows()
    {
        var paths = ConfigPaths.Create(true, Home, AppData, Project, null);

        Assert.Equal(Path.Combine(AppData, "sql-mcp", "config.json"), paths.UserConfig);
    }

    [Fact]
    public void Project_config_is_under_the_current_directory_by_default()
    {
        var paths = ConfigPaths.Create(false, Home, AppData, Project, null);

        Assert.Equal(Path.Combine(Project, ".sql-mcp", "config.json"), paths.ProjectConfig);
    }

    [Fact]
    public void Project_option_is_resolved_against_the_current_directory()
    {
        var paths = ConfigPaths.Create(false, Home, AppData, _temp.Path, "project");

        Assert.Equal(Path.Combine(Project, ".sql-mcp", "config.json"), paths.ProjectConfig);
    }

    [Fact]
    public void Only_a_user_file_exists()
    {
        _temp.Write("home/.config/sql-mcp/config.json", """
            { "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command"] } ] }
            """);
        var paths = ConfigPaths.Create(false, Home, AppData, Project, null);

        var config = ConfigDiscovery.Discover(paths, _ => null);

        Assert.Null(config.Project);
        var entry = Assert.Single(config.User!.Databases);
        Assert.Equal("sqlhost:1433/sales", entry.Identifier);
        Assert.True(entry.IsAvailable);
        Assert.Equal([ToolGroup.Command], entry.Grants.Allow);
    }

    [Fact]
    public void Neither_file_exists()
    {
        var paths = ConfigPaths.Create(false, Home, AppData, Project, null);

        var config = ConfigDiscovery.Discover(paths, _ => null);

        Assert.Null(config.User);
        Assert.Null(config.Project);
        Assert.Contains(paths.UserConfig, config.NoDatabasesMessage, StringComparison.Ordinal);
        Assert.Contains(paths.ProjectConfig, config.NoDatabasesMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_file_is_found()
    {
        _temp.Write("project/.sql-mcp/config.json", """
            { "databases": [ { "connectionString": "Server=localhost;Database=App" } ] }
            """);
        var paths = ConfigPaths.Create(false, Home, AppData, Project, null);

        var config = ConfigDiscovery.Discover(paths, _ => null);

        Assert.Null(config.User);
        Assert.Equal("localhost:1433/app", Assert.Single(config.Project!.Databases).Identifier);
    }

    [Fact]
    public void An_invalid_file_fails_discovery()
    {
        var path = _temp.Write("project/.sql-mcp/config.json", """{ "databases": 42 }""");
        var paths = ConfigPaths.Create(false, Home, AppData, Project, null);

        var ex = Assert.Throws<ConfigException>(() => ConfigDiscovery.Discover(paths, _ => null));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }
}
