using SqlMcp.Cli;
using SqlMcp.Configuration;
using SqlMcp.Permissions;

namespace SqlMcp.Tests.Cli;

/// <summary>Spec: cli, "Config editing" (db add/remove, allow/deny).</summary>
public sealed class ConfigEditingCommandTests : IDisposable
{
    private const string AppConnection = "Server=localhost,1433;Database=app;User Id=sa;Password=${env:APP_PW}";

    private readonly CliRunner _cli = new();

    private string ProjectConfig => Path.Combine(_cli.ProjectDirectory, ".sql-mcp", "config.json");

    private string UserConfig => Path.Combine(_cli.HomeDirectory, ".config", "sql-mcp", "config.json");

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Add_a_database_prints_its_identifier()
    {
        await _cli.RunAsync("init");

        var result = await _cli.RunAsync("db", "add", AppConnection);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("localhost:1433/app", result.Output.Trim());
        var entry = Assert.Single(ConfigLoader.Load(ProjectConfig, _ => null).Databases);
        Assert.Equal("localhost:1433/app", entry.Identifier);
        Assert.Equal(AppConnection, entry.RawConnectionString);
    }

    [Fact]
    public async Task Add_to_the_project_file_warns_that_the_database_needs_approval()
    {
        await _cli.RunAsync("init");

        var result = await _cli.RunAsync("db", "add", AppConnection);

        Assert.Contains(
            "localhost:1433/app: project config adds this database but is not approved. Ask the user to run `sql-mcp approve`",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_keeps_comments_in_the_file()
    {
        _cli.Project.Write(".sql-mcp/config.json", "{\n  // team databases\n  \"databases\": [],\n}\n");

        await _cli.RunAsync("db", "add", AppConnection);

        Assert.Contains("// team databases", File.ReadAllText(ProjectConfig), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_without_init_creates_the_project_files()
    {
        var result = await _cli.RunAsync("db", "add", AppConnection);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(ProjectLayout.SchemaUrl, File.ReadAllText(ProjectConfig), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_cli.ProjectDirectory, ".sql-mcp", ".gitignore")));
    }

    [Fact]
    public async Task Add_to_the_user_file()
    {
        var result = await _cli.RunAsync("db", "add", "--user", AppConnection);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(ConfigLoader.Load(UserConfig, _ => null).Databases);
        Assert.False(File.Exists(ProjectConfig));
    }

    [Fact]
    public async Task Add_a_duplicate_fails_and_leaves_the_file()
    {
        await _cli.RunAsync("db", "add", AppConnection);
        var before = File.ReadAllText(ProjectConfig);

        var result = await _cli.RunAsync("db", "add", "Server=LOCALHOST;Database=APP");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("localhost:1433/app is already in the project config", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(ProjectConfig));
    }

    [Theory]
    [InlineData("Server=a;Database=b;User Id=sa;Password=hunter2")]
    [InlineData("Server=a;Database=b;PWD=hunter2")]
    [InlineData("Server=a;Database=b;Password=pre${env:PW}")]
    public async Task Add_a_literal_password_to_the_project_config_is_refused(string connectionString)
    {
        await _cli.RunAsync("init");
        var before = File.ReadAllText(ProjectConfig);

        var result = await _cli.RunAsync("db", "add", connectionString);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(DbCommand.LiteralPasswordMessage, result.Error.TrimEnd());
        Assert.DoesNotContain("hunter2", File.ReadAllText(ProjectConfig), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(ProjectConfig));
    }

    [Fact]
    public async Task Add_a_literal_password_to_the_user_config_is_allowed()
    {
        var result = await _cli.RunAsync("db", "add", "--user", "Server=a;Database=b;Password=hunter2");

        Assert.Equal(0, result.ExitCode);
        Assert.Single(ConfigLoader.Load(UserConfig, _ => null).Databases);
    }

    [Fact]
    public async Task Add_an_unparseable_connection_string_fails()
    {
        var result = await _cli.RunAsync("db", "add", "this is not=a;valid=string;;=");

        Assert.NotEqual(0, result.ExitCode);
        Assert.StartsWith("Not added: connection string is not valid", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_a_named_instance_without_port_warns_but_adds()
    {
        var result = await _cli.RunAsync("db", "add", @"Server=sqlhost\SQLEXPRESS;Database=app");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("a named instance needs an explicit port", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_a_database()
    {
        await _cli.RunAsync("db", "add", AppConnection);
        await _cli.RunAsync("db", "add", "Server=db2;Database=reports");

        var result = await _cli.RunAsync("db", "remove", "LOCALHOST:1433/APP");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("db2:1433/reports", Assert.Single(ConfigLoader.Load(ProjectConfig, _ => null).Databases).Identifier);
    }

    [Fact]
    public async Task Remove_a_database_only_in_the_user_file_points_at_user()
    {
        await _cli.RunAsync("db", "add", "--user", AppConnection);
        await _cli.RunAsync("init");

        var result = await _cli.RunAsync("db", "remove", "localhost:1433/app");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("isn't in the project config", result.Error, StringComparison.Ordinal);
        Assert.Contains("add --user", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allow_a_group_on_a_database_warns_until_approved()
    {
        await _cli.RunAsync("db", "add", AppConnection);

        var result = await _cli.RunAsync("allow", "command", "--db", "localhost:1433/app");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([ToolGroup.Command], ConfigLoader.Load(ProjectConfig, _ => null).Databases[0].Grants.Allow);
        Assert.Contains("not in effect until approved", result.Error, StringComparison.Ordinal);
        Assert.Contains("project config adds this database, grants command but is not approved", result.Error, StringComparison.Ordinal);
        Assert.Contains("sql-mcp approve", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allow_in_the_user_file_needs_no_approval()
    {
        await _cli.RunAsync("db", "add", "--user", AppConnection);

        var result = await _cli.RunAsync("allow", "ddl", "--db", "localhost:1433/app", "--user");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal([ToolGroup.Ddl], ConfigLoader.Load(UserConfig, _ => null).Databases[0].Grants.Allow);
    }

    [Fact]
    public async Task Allow_moves_the_group_off_the_same_level_deny_list()
    {
        _cli.Project.Write(".sql-mcp/config.json", """
            {
              "defaults": { "deny": ["command", "ddl"] },
              "databases": [],
            }
            """);

        var result = await _cli.RunAsync("allow", "command");

        Assert.Equal(0, result.ExitCode);
        var defaults = ConfigLoader.Load(ProjectConfig, _ => null).Defaults;
        Assert.Equal([ToolGroup.Command], defaults.Allow);
        Assert.Equal([ToolGroup.Ddl], defaults.Deny);
    }

    [Fact]
    public async Task Deny_moves_the_group_off_the_allow_list_and_needs_no_approval()
    {
        await _cli.RunAsync("db", "add", AppConnection);
        await _cli.RunAsync("allow", "query", "--db", "localhost:1433/app");
        var paths = _cli.Context.Paths();
        new ApprovalStore(paths.Approvals, TimeProvider.System).Approve(ConfigLoader.Load(paths.ProjectConfig, _ => null));

        var result = await _cli.RunAsync("deny", "query", "--db", "localhost:1433/app");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        var grants = ConfigLoader.Load(ProjectConfig, _ => null).Databases[0].Grants;
        Assert.Empty(grants.Allow);
        Assert.Equal([ToolGroup.Query], grants.Deny);
    }

    [Fact]
    public async Task Allow_twice_changes_nothing_the_second_time()
    {
        await _cli.RunAsync("allow", "command");
        var before = File.ReadAllText(ProjectConfig);

        var result = await _cli.RunAsync("allow", "command");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already allowed", result.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(ProjectConfig));
    }

    [Fact]
    public async Task Allow_on_a_database_not_in_any_file_suggests_db_add()
    {
        await _cli.RunAsync("init");

        var result = await _cli.RunAsync("allow", "command", "--db", "nowhere:1433/x");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sql-mcp db add", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_group_is_rejected_by_the_parser()
    {
        var result = await _cli.RunAsync("allow", "admin");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(ProjectConfig));
    }

    [Fact]
    public async Task Edit_of_an_approved_file_warns_that_the_approval_lapsed()
    {
        await _cli.RunAsync("db", "add", AppConnection);
        await _cli.RunAsync("allow", "ddl", "--db", "localhost:1433/app");
        var paths = _cli.Context.Paths();
        new ApprovalStore(paths.Approvals, TimeProvider.System).Approve(ConfigLoader.Load(paths.ProjectConfig, _ => null));

        var result = await _cli.RunAsync("deny", "execute");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("grants ddl but is not approved", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_of_an_invalid_file_fails_with_the_validation_error()
    {
        _cli.Project.Write(".sql-mcp/config.json", """{ "databases": [], "bogus": 1 }""");

        var result = await _cli.RunAsync("allow", "command");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(ProjectConfig, result.Error, StringComparison.Ordinal);
        Assert.Contains("bogus", result.Error, StringComparison.Ordinal);
    }
}
