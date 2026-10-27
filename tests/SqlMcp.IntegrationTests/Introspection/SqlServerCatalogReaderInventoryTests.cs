using SqlMcp.Introspection;
using SqlMcp.Introspection.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Introspection;

// The inventory the completeness check compares a whole-database script with (design I5), against seed.sql.
public sealed class SqlServerCatalogReaderInventoryTests(SqlServerFixture server, SeededDatabases seeded) : IClassFixture<SeededDatabases>
{
    private readonly SqlServerCatalogReader _reader = new(TimeProvider.System);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static TheoryData<bool> Collations => new(false, true);

    [Theory]
    [MemberData(nameof(Collations))]
    public async Task Inventory_holds_every_seeded_object_schema_and_partitioning_object(bool caseSensitive)
    {
        var inventory = await InventoryAsync(caseSensitive);
        string[] expected =
        [
            .. SqlServerCatalogReaderListTests.AllSeeded,
            "-.archive:schema",
            "-.sales:schema",
            "-.pf_events:partition_function",
            "-.ps_events:partition_scheme",
        ];

        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            inventory.Where(i => i.Class == InventoryClass.Scriptable).Select(i => Key(i.Item)).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Database_user_is_a_security_principal()
    {
        var inventory = await InventoryAsync(caseSensitive: false);

        Assert.Equal(
            [new InventoryItem(new CatalogItem(null, "app_reader", CatalogItemTypes.User), InventoryClass.SecurityPrincipal)],
            inventory.Where(i => i.Class != InventoryClass.Scriptable));
    }

    [Fact]
    public async Task Encrypted_procedure_is_marked_encrypted()
    {
        var inventory = await InventoryAsync(caseSensitive: false);

        Assert.Equal([new CatalogItem("dbo", "secret_report", "procedure")], inventory.Where(i => i.Encrypted).Select(i => i.Item));
    }

    [Fact]
    public async Task Kinds_the_seed_lacks()
    {
        var connectionString = await server.CreateDatabaseAsync($"inventory_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(connectionString, """
            CREATE RULE dbo.positive AS @value > 0;
            GO
            CREATE XML SCHEMA COLLECTION dbo.order_xml AS N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="order"/></xs:schema>';
            GO
            CREATE ROLE reporting;
            GO
            CREATE CERTIFICATE signing ENCRYPTION BY PASSWORD = 'Inventory-test-1' WITH SUBJECT = 'signing';
            GO
            CREATE TABLE dbo.totals (id int NOT NULL);
            GO
            GRANT SELECT ON dbo.totals TO reporting;
            DENY DELETE ON dbo.totals TO reporting;
            GO
            """);

        var outcome = await _reader.InventoryAsync(connectionString, EffectiveLimits.Default, Cancel);

        Assert.Equal(
            [
                new InventoryItem(new CatalogItem("dbo", "positive", "rule"), InventoryClass.Scriptable),
                new InventoryItem(new CatalogItem("dbo", "totals", "table"), InventoryClass.Scriptable),
                new InventoryItem(new CatalogItem("dbo", "order_xml", CatalogItemTypes.XmlSchemaCollection), InventoryClass.Scriptable),
                new InventoryItem(new CatalogItem(null, "reporting", CatalogItemTypes.Permissions), InventoryClass.SecurityPrincipal),
                new InventoryItem(new CatalogItem(null, "reporting", CatalogItemTypes.Role), InventoryClass.SecurityPrincipal),
                new InventoryItem(new CatalogItem(null, "signing", CatalogItemTypes.Certificate), InventoryClass.SecurityObject),
            ],
            Assert.IsType<CatalogSucceeded<IReadOnlyList<InventoryItem>>>(outcome).Value);
    }

    private async Task<IReadOnlyList<InventoryItem>> InventoryAsync(bool caseSensitive)
    {
        var connectionString = caseSensitive ? seeded.CaseSensitiveConnectionString : seeded.ConnectionString;
        var outcome = await _reader.InventoryAsync(connectionString, EffectiveLimits.Default, Cancel);
        return Assert.IsType<CatalogSucceeded<IReadOnlyList<InventoryItem>>>(outcome).Value;
    }

    private static string Key(CatalogItem item) => $"{item.Schema ?? "-"}.{item.Name}:{item.Type}";
}
