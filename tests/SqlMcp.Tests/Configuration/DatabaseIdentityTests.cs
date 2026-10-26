using SqlMcp.Configuration;

namespace SqlMcp.Tests.Configuration;

public class DatabaseIdentityTests
{
    [Theory]
    [InlineData("Server=sqlhost;Database=Sales", "sqlhost:1433/sales")]
    [InlineData("Data Source=SQLHOST;Initial Catalog=Sales", "sqlhost:1433/sales")]
    [InlineData("Server=tcp:sqlhost;Database=Sales", "sqlhost:1433/sales")]
    [InlineData("Server=tcp:sqlhost,1450;Database=Sales", "sqlhost:1450/sales")]
    [InlineData("Server=sqlhost, 1450;Database=Sales", "sqlhost:1450/sales")]
    [InlineData(@"Server=sqlhost\SQLEXPRESS,1500;Database=Sales", "sqlhost:1500/sales")]
    [InlineData("Server=db.example.com;Database=App", "db.example.com:1433/app")]
    public void Identity_is_lower_case_with_the_port(string connectionString, string expected)
    {
        var result = DatabaseIdentity.FromConnectionString(connectionString);

        Assert.Equal(expected, result.Identifier);
        Assert.Null(result.UnavailableReason);
    }

    [Fact]
    public void Port_is_always_included()
    {
        Assert.Equal("sqlhost:1433/sales", DatabaseIdentity.FromConnectionString("Server=sqlhost;Database=Sales").Identifier);
    }

    [Fact]
    public void Named_instance_without_a_port_is_unavailable()
    {
        var result = DatabaseIdentity.FromConnectionString(@"Server=sqlhost\SQLEXPRESS;Database=Sales");

        Assert.Equal("a named instance needs an explicit port", result.UnavailableReason);
        Assert.Equal(@"sqlhost\sqlexpress/sales", result.Identifier);
    }

    [Fact]
    public void Host_names_are_not_resolved()
    {
        var localhost = DatabaseIdentity.FromConnectionString("Server=localhost,1433;Database=app");
        var loopback = DatabaseIdentity.FromConnectionString("Server=127.0.0.1,1433;Database=app");

        Assert.Equal("localhost:1433/app", localhost.Identifier);
        Assert.Equal("127.0.0.1:1433/app", loopback.Identifier);
    }

    [Theory]
    [InlineData(@"Server=np:\\sqlhost\pipe\sql\query;Database=Sales")]
    [InlineData("Server=lpc:sqlhost;Database=Sales")]
    [InlineData("Server=admin:sqlhost;Database=Sales")]
    [InlineData("Server=.;Database=Sales")]
    [InlineData("Server=(local);Database=Sales")]
    [InlineData(@"Server=.\SQLEXPRESS;Database=Sales")]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=Sales")]
    public void Non_tcp_sources_are_unavailable(string connectionString)
    {
        var result = DatabaseIdentity.FromConnectionString(connectionString);

        Assert.Equal("only TCP connections are supported", result.UnavailableReason);
        Assert.DoesNotContain(":1433", result.Identifier, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_database_is_unavailable()
    {
        var result = DatabaseIdentity.FromConnectionString("Server=sqlhost");

        Assert.Equal("connection string names no database", result.UnavailableReason);
    }

    [Theory]
    [InlineData("Server=sqlhost,abc;Database=Sales")]
    [InlineData("Server=sqlhost,0;Database=Sales")]
    [InlineData("Server=sqlhost,70000;Database=Sales")]
    public void Invalid_port_is_unavailable(string connectionString)
    {
        var result = DatabaseIdentity.FromConnectionString(connectionString);

        Assert.StartsWith("port ", result.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_connection_string_is_unavailable_without_its_values()
    {
        var result = DatabaseIdentity.FromConnectionString("Server=sqlhost;Database=Sales;NotAKeyword=hunter2");

        Assert.NotNull(result.UnavailableReason);
        Assert.DoesNotContain("hunter2", result.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_identity_names_both_entries()
    {
        const string text = """
            { "databases": [
                { "connectionString": "Server=sqlhost;Database=Sales" },
                { "connectionString": "Server=localhost;Database=App" },
                { "connectionString": "Server=tcp:SQLHOST,1433;Database=sales;Password=hunter2" }
            ] }
            """;

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("config.json", text, _ => null));

        Assert.Contains("$.databases[0]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("$.databases[2]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sqlhost:1433/sales", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_malformed_entries_are_not_duplicates()
    {
        const string text = """
            { "databases": [ { "connectionString": "Server=a;Bogus=1" }, { "connectionString": "Server=b;Bogus=2" } ] }
            """;

        var file = ConfigLoader.Parse("config.json", text, _ => null);

        Assert.All(file.Databases, entry => Assert.False(entry.IsAvailable));
    }

    [Fact]
    public void Same_identity_in_different_files_is_not_a_duplicate()
    {
        const string text = """{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""";

        ConfigLoader.Parse("user.json", text, _ => null);
        ConfigLoader.Parse("project.json", text, _ => null);
    }

    [Fact]
    public void Unknown_identifier_lists_the_known_ones()
    {
        var message = DatabaseIdentity.UnknownIdentifierMessage(
            "otherhost:1433/sales", ["sqlhost:1433/sales", "localhost:1433/app"]);

        Assert.Contains("otherhost:1433/sales", message, StringComparison.Ordinal);
        Assert.Contains("localhost:1433/app, sqlhost:1433/sales", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_call_identifiers_are_normalised()
    {
        Assert.Equal("sqlhost:1433/sales", DatabaseIdentity.Normalise(" SQLHost:1433/Sales "));
    }
}
