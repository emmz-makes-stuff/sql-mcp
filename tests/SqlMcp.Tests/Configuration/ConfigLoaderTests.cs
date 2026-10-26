using SqlMcp.Configuration;
using SqlMcp.Permissions;

namespace SqlMcp.Tests.Configuration;

public class ConfigLoaderTests
{
    private static readonly Func<string, string?> NoVariables = _ => null;

    [Fact]
    public void Comments_are_accepted()
    {
        const string text = """
            {
              // Global settings
              "defaults": { "allow": ["command",], },
              "databases": [
                /* the sales database */
                { "connectionString": "Server=sqlhost;Database=Sales", "deny": ["ddl"], },
              ],
            }
            """;

        var file = ConfigLoader.Parse("config.json", text, NoVariables);

        Assert.Equal([ToolGroup.Command], file.Defaults.Allow);
        var entry = Assert.Single(file.Databases);
        Assert.Equal("sqlhost:1433/sales", entry.Identifier);
        Assert.Equal([ToolGroup.Ddl], entry.Grants.Deny);
    }

    [Fact]
    public void Invalid_file_names_the_file_the_path_and_the_unknown_name()
    {
        const string text = """
            { "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["query", "admin"] } ] }
            """;

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("/home/emmz/.config/sql-mcp/config.json", text, NoVariables));

        Assert.Contains("/home/emmz/.config/sql-mcp/config.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("$.databases[0].allow[1]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"admin\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_group_in_defaults_is_rejected()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            ConfigLoader.Parse("config.json", """{ "defaults": { "deny": ["admin"] } }""", NoVariables));

        Assert.Contains("$.defaults.deny[0]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("admin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_property_is_named_without_its_value()
    {
        const string text = """
            { "databases": [ { "connectionString": "Server=a;Database=b", "conectionString": "Password=hunter2" } ] }
            """;

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("config.json", text, NoVariables));

        Assert.Contains("$.databases[0].conectionString", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown property", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_error_is_reported_once_at_its_own_path()
    {
        const string text = """
            { "defaults": { "limits": { "maxRows": 0 } },
              "databases": [ { "connectionString": "Server=a;Database=b", "allow": ["query", "admin"], "engine": "pg", "oops": 1 } ] }
            """;

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("/p/config.json", text, NoVariables));

        Assert.Equal(
            [
                "/p/config.json: $.databases[0].allow[1]: \"admin\" is not a known value",
                "/p/config.json: $.databases[0].engine: \"pg\" is not a known value",
                "/p/config.json: $.databases[0].oops: unknown property \"oops\"",
                "/p/config.json: $.defaults.limits.maxRows: 0 should be at least 1",
            ],
            ex.Message.Split(Environment.NewLine));
    }

    [Fact]
    public void Missing_connection_string_is_rejected()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            ConfigLoader.Parse("config.json", """{ "databases": [ { "allow": ["query"] } ] }""", NoVariables));

        Assert.Contains("$.databases[0]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("connectionString", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_reports_line_and_column()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            ConfigLoader.Parse("config.json", "{\n  \"databases\": [ }", NoVariables));

        Assert.StartsWith("config.json: line 2, column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Limits_are_read()
    {
        const string text = """
            { "databases": [ { "connectionString": "Server=a;Database=b",
                               "limits": { "maxRows": 50, "maxBytes": 1048576, "pageSize": 10, "timeoutSeconds": 5 } } ] }
            """;

        var entry = Assert.Single(ConfigLoader.Parse("config.json", text, NoVariables).Databases);

        Assert.Equal(new LimitSettings(50, 1048576, 10, 5), entry.Grants.Limits);
    }

    [Fact]
    public void Whole_number_limits_written_as_decimals_are_read_as_integers()
    {
        const string text = """{ "defaults": { "limits": { "maxRows": 100.0, "maxBytes": 1e6, "pageSize": 10.00 } } }""";

        var file = ConfigLoader.Parse("config.json", text, NoVariables);

        Assert.Equal(new LimitSettings(100, 1_000_000, 10, null), file.Defaults.Limits);
    }

    [Fact]
    public void A_limit_too_large_to_hold_is_a_config_error_with_its_path()
    {
        const string text = """{ "defaults": { "limits": { "maxRows": 3000000000 } } }""";

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("/p/config.json", text, NoVariables));

        Assert.StartsWith("/p/config.json: $.defaults.limits.maxRows: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_byte_cap_over_one_gibibyte_is_a_config_error_with_its_path()
    {
        const string text = """{ "defaults": { "limits": { "maxBytes": 1073741825 } } }""";

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("/p/config.json", text, NoVariables));

        Assert.StartsWith("/p/config.json: $.defaults.limits.maxBytes: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fractional_limit_is_a_config_error_with_its_path()
    {
        const string text = """{ "defaults": { "limits": { "pageSize": 10.5 } } }""";

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("/p/config.json", text, NoVariables));

        Assert.StartsWith("/p/config.json: $.defaults.limits.pageSize: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_schema_is_the_published_file()
    {
        var published = File.ReadAllText(Path.Combine(RepoRoot(), "src", "SqlMcp", "schema", "config.schema.json"));

        Assert.Equal(published, ConfigSchema.Text);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "sql-mcp.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
