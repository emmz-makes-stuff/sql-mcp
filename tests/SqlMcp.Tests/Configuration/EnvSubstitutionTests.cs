using Microsoft.Data.SqlClient;
using SqlMcp.Configuration;

namespace SqlMcp.Tests.Configuration;

public class EnvSubstitutionTests
{
    private const string Sales = """
        { "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;User ID=reader;Password=${env:SALES_PW}" } ] }
        """;

    [Fact]
    public void Variable_is_set()
    {
        var entry = Load(Sales, new() { ["SALES_PW"] = "s3cret" });

        Assert.True(entry.IsAvailable);
        Assert.Equal("s3cret", new SqlConnectionStringBuilder(entry.ConnectionString).Password);
    }

    [Fact]
    public void Variable_is_missing()
    {
        var entry = Load(Sales, []);

        Assert.False(entry.IsAvailable);
        Assert.Equal("environment variable SALES_PW is not set", entry.UnavailableReason);
        Assert.Null(entry.ConnectionString);
        Assert.Equal("sqlhost:1433/sales", entry.Identifier);
    }

    [Fact]
    public void Every_missing_variable_is_named()
    {
        var result = EnvSubstitution.Substitute("Server=a;Database=b;User ID=${env:U};Password=${env:P}", _ => null);

        Assert.Equal(["U", "P"], result.MissingVariables);
        Assert.Equal("environment variable U is not set; environment variable P is not set", result.UnavailableReason);
    }

    [Fact]
    public void Empty_variable_is_set_not_missing()
    {
        var result = EnvSubstitution.Substitute("Server=a;Database=b;Password=${env:P}", _ => "");

        Assert.Empty(result.MissingVariables);
        Assert.NotNull(result.ConnectionString);
    }

    [Fact]
    public void A_value_cannot_inject_keywords()
    {
        var result = EnvSubstitution.Substitute(
            "Server=sqlhost;Database=Sales;Password=${env:P}", _ => "x;Server=evil;Database=other");

        var builder = new SqlConnectionStringBuilder(result.ConnectionString);
        Assert.Equal("sqlhost", builder.DataSource);
        Assert.Equal("Sales", builder.InitialCatalog);
        Assert.Equal("x;Server=evil;Database=other", builder.Password);
    }

    [Fact]
    public void Secrets_are_not_exposed()
    {
        var literal = Load("""
            { "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;User ID=reader;Password=hunter2" } ] }
            """, []);
        var fromEnv = Load(Sales, new() { ["SALES_PW"] = "s3cret" });

        Assert.DoesNotContain("hunter2", literal.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", fromEnv.ToString(), StringComparison.Ordinal);

        var masked = ConnectionStringMasker.MaskRaw(literal.RawConnectionString);
        Assert.DoesNotContain("hunter2", masked, StringComparison.Ordinal);
        Assert.Contains("***", masked, StringComparison.Ordinal);

        Assert.Contains("${env:SALES_PW}", ConnectionStringMasker.MaskRaw(fromEnv.RawConnectionString), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server=a;Database=b;PWD=hunter2")]
    [InlineData("Server=a;Database=b;password=hunter2")]
    [InlineData("Server=a;Database=b;Password=${env:P}hunter2")]
    public void Masking_covers_every_password_form(string raw)
    {
        Assert.DoesNotContain("hunter2", ConnectionStringMasker.MaskRaw(raw), StringComparison.Ordinal);
    }

    private static DatabaseEntry Load(string text, Dictionary<string, string> variables) =>
        Assert.Single(ConfigLoader.Parse("config.json", text, name => variables.GetValueOrDefault(name)).Databases);
}
