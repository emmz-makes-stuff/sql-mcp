namespace SqlMcp.IntegrationTests.Introspection;

/// <summary>
/// Two databases of their own, each holding <c>seed.sql</c>: one with the server's default collation and one with a
/// case-sensitive collation. Tests that list or script a whole database use these, never the shared <c>itest</c>
/// database, which holds the other tests' objects.
/// </summary>
public sealed class SeededDatabases(SqlServerFixture server) : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public string CaseSensitiveConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Each test class gets its own pair, so classes running in parallel never share or clash.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var seed = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Introspection", "seed.sql"));
        ConnectionString = await server.CreateDatabaseAsync($"seeded_{suffix}");
        await SqlServerFixture.RunScriptAsync(ConnectionString, seed);
        CaseSensitiveConnectionString = await server.CreateDatabaseAsync($"seeded_cs_{suffix}", "Latin1_General_100_CS_AS");
        await SqlServerFixture.RunScriptAsync(CaseSensitiveConnectionString, seed);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
