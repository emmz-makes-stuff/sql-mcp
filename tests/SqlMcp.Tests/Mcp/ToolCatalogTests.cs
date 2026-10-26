using SqlMcp.Mcp;

namespace SqlMcp.Tests.Mcp;

public sealed class ToolCatalogTests : IDisposable
{
    private readonly SessionBuilder _builder = new();

    public void Dispose() => _builder.Dispose();

    [Fact]
    public void Agent_reads_the_tool_description()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        var query = ToolCatalog.Build(session).Single(t => t.ProtocolTool.Name == ToolNames.Query).ProtocolTool;

        Assert.Contains("explicit column list", query.Description, StringComparison.Ordinal);
        Assert.Contains("filter with WHERE", query.Description, StringComparison.Ordinal);
        Assert.Contains("TOP", query.Description, StringComparison.Ordinal);
        Assert.EndsWith("Default caps: 1000 rows or 5 MB per result, 100 rows per page, 30 s timeout.", query.Description, StringComparison.Ordinal);
        Assert.True(query.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public void A_database_with_other_limits_is_named_in_the_description()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "limits": { "maxRows": 200, "maxBytes": 1048576 } } ] }""");
        using var session = _builder.Build();

        var query = ToolCatalog.Build(session).Single(t => t.ProtocolTool.Name == ToolNames.Query).ProtocolTool;

        Assert.EndsWith(
            "Databases with other limits: sqlhost:1433/sales: 200 rows or 1 MB per result, 100 rows per page, 30 s timeout.",
            query.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Query_tools_and_listing_are_offered_by_default()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        Assert.Equal(["list_databases", "query", "next_page", "abandon"], Names(session));
    }

    [Fact]
    public void No_database_allows_command()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" }, { "connectionString": "Server=other;Database=Hr" } ] }""");
        using var session = _builder.Build();

        Assert.DoesNotContain("command", Names(session));
    }

    [Fact]
    public void No_database_allows_query()
    {
        // list_databases is always offered (2026-10-25 decision); the query tools are not.
        _builder.User("""{ "defaults": { "deny": ["query"] }, "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        using var session = _builder.Build();

        Assert.Equal(["list_databases"], Names(session));
    }

    [Fact]
    public void Only_the_listing_is_offered_when_the_config_is_invalid()
    {
        _builder.User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["admin"] } ] }""");
        using var session = _builder.Build();

        Assert.Equal(["list_databases"], Names(session));
    }

    private static List<string> Names(SqlMcpSession session) => [.. ToolCatalog.Build(session).Select(t => t.ProtocolTool.Name)];
}
