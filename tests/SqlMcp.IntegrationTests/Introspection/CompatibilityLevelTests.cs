using SqlMcp.Introspection;
using SqlMcp.Introspection.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Introspection;

// The catalog reads against a database at compatibility level 120 (SQL Server 2014), below the 130 that functions
// such as STRING_SPLIT need. The server is 2025, but a migrated database keeps its old level.
public sealed class CompatibilityLevelTests(SqlServerFixture server)
{
    private readonly SqlServerCatalogReader _reader = new(TimeProvider.System);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Catalog_reads_work_at_compatibility_level_120()
    {
        var name = $"compat120_{Guid.NewGuid().ToString("N")[..8]}";
        var connectionString = await server.CreateDatabaseAsync(name);
        await SqlServerFixture.RunScriptAsync(connectionString, $"""
            ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL = 120;
            GO
            CREATE TABLE dbo.orders (id int PRIMARY KEY);
            GO
            CREATE VIEW dbo.open_orders AS SELECT id FROM dbo.orders;
            GO
            CREATE SEQUENCE dbo.order_numbers AS int START WITH 1;
            """);

        var list = await _reader.ListAsync(connectionString, new ObjectFilter(null, [ObjectType.Table, ObjectType.View], null), EffectiveLimits.Default, Cancel);
        var inventory = await _reader.InventoryAsync(connectionString, EffectiveLimits.Default, Cancel);
        var describe = await _reader.DescribeAsync(connectionString, new ObjectName("dbo", "orders"), EffectiveLimits.Default, Cancel);
        var resolve = await _reader.ResolveAsync(connectionString, [new ObjectName("dbo", "orders"), new ObjectName(null, "open_orders")], EffectiveLimits.Default, Cancel);

        Assert.Equal(
            ["dbo.open_orders:view", "dbo.orders:table"],
            Assert.IsType<CatalogSucceeded<ObjectList>>(list).Value.Objects.Select(o => $"{o.Schema}.{o.Name}:{o.Type.ToWireName()}"));
        Assert.Contains(Assert.IsType<CatalogSucceeded<IReadOnlyList<InventoryItem>>>(inventory).Value, i => i.Item == new CatalogItem("dbo", "order_numbers", "sequence"));
        Assert.Equal("id", Assert.Single(Assert.IsType<CatalogSucceeded<ObjectDetails>>(describe).Value.PrimaryKey!.Columns));
        Assert.Equal(["orders", "open_orders"], Assert.IsType<CatalogSucceeded<Resolution>>(resolve).Value.Objects.Select(o => o.Name));
    }
}
