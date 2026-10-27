using SqlMcp.Introspection;
using SqlMcp.Introspection.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Introspection;

// list_objects against seed.sql (design I4), on the default collation and on a case-sensitive one.
public sealed class SqlServerCatalogReaderListTests(SeededDatabases seeded) : IClassFixture<SeededDatabases>
{
    /// <summary>Every user object seed.sql creates, as "schema.name:type" ("-" for no schema).</summary>
    private static readonly string[] AllSeeded =
    [
        "-.audit_ddl:trigger",
        "archive.orders:table",
        "dbo.archive_orders:procedure",
        "dbo.customers:synonym",
        "dbo.events:table",
        "dbo.id_list:table_type",
        "dbo.order_count:function",
        "dbo.Order_Archive:table",
        "dbo.order_lines:table",
        "dbo.order_numbers:sequence",
        "dbo.orders:table",
        "dbo.orders_for:function",
        "dbo.orders_status:trigger",
        "dbo.phone:type",
        "dbo.prices:table",
        "dbo.prices_history:table",
        "dbo.secret_report:procedure",
        "dbo.users:table",
        "sales.open_orders:view",
    ];

    private readonly SqlServerCatalogReader _reader = new(TimeProvider.System);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static TheoryData<bool> Collations => new(false, true);

    [Theory]
    [MemberData(nameof(Collations))]
    public async Task List_a_database(bool caseSensitive)
    {
        var list = await List(caseSensitive, new ObjectFilter(null, [], null));

        Assert.Equal(AllSeeded.Order(StringComparer.Ordinal), list.Objects.Select(Key).Order(StringComparer.Ordinal));
        Assert.Equal(AllSeeded.Length, list.Total);
        Assert.False(list.Truncated);
        Assert.Equal(3, list.Objects.Single(o => o is { Schema: "dbo", Name: "users" }).ApproxRows);
        Assert.Equal(0, list.Objects.Single(o => o is { Schema: "dbo", Name: "orders" }).ApproxRows);
        Assert.Null(list.Objects.Single(o => o is { Schema: "sales", Name: "open_orders" }).ApproxRows);
    }

    [Fact]
    public async Task System_objects_are_left_out()
    {
        var list = await List(caseSensitive: false, new ObjectFilter(null, [], null));

        // The exact match in List_a_database already proves it; this names the rule.
        Assert.DoesNotContain(list.Objects, o => o.Schema is "sys" or "INFORMATION_SCHEMA");
        Assert.DoesNotContain(list.Objects, o => o.Name.StartsWith("sys", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Collations))]
    public async Task Filter_by_type_and_pattern(bool caseSensitive)
    {
        var list = await List(caseSensitive, new ObjectFilter(null, [ObjectType.Table], "ORDER*"));

        Assert.Equal(
            ["archive.orders:table", "dbo.Order_Archive:table", "dbo.order_lines:table", "dbo.orders:table"],
            list.Objects.Select(Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Collations))]
    public async Task Filter_by_schema_ignores_case(bool caseSensitive)
    {
        var list = await List(caseSensitive, new ObjectFilter("SALES", [], null));

        Assert.Equal(["sales.open_orders:view"], list.Objects.Select(Key));
    }

    [Fact]
    public async Task Filter_by_several_types()
    {
        var list = await List(caseSensitive: false, new ObjectFilter(null, [ObjectType.Type, ObjectType.TableType, ObjectType.Synonym], null));

        Assert.Equal(["dbo.customers:synonym", "dbo.id_list:table_type", "dbo.phone:type"], list.Objects.Select(Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Pattern_wildcards_of_like_match_literally()
    {
        // "_" is a single-character wildcard in LIKE; escaped, it matches only an underscore.
        var list = await List(caseSensitive: false, new ObjectFilter("dbo", [], "order_*"));

        Assert.Equal(["dbo.Order_Archive:table", "dbo.order_count:function", "dbo.order_lines:table", "dbo.order_numbers:sequence"], list.Objects.Select(Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Question_mark_matches_one_character()
    {
        var list = await List(caseSensitive: false, new ObjectFilter(null, [], "use?s"));

        Assert.Equal(["dbo.users:table"], list.Objects.Select(Key));
    }

    [Fact]
    public async Task More_objects_than_the_cap()
    {
        var list = await _reader.ListAsync(seeded.ConnectionString, new ObjectFilter(null, [], null), EffectiveLimits.Default with { MaxRows = 3 }, Cancel);

        var value = Assert.IsType<CatalogSucceeded<ObjectList>>(list).Value;
        Assert.Equal(3, value.Objects.Count);
        Assert.Equal(AllSeeded.Length, value.Total);
        Assert.True(value.Truncated);
    }

    [Fact]
    public async Task Unreachable_server_is_a_connection_failure()
    {
        var outcome = await _reader.ListAsync("Server=tcp:127.0.0.1,1;Database=x;Connect Timeout=2", new ObjectFilter(null, [], null), EffectiveLimits.Default, Cancel);

        var failed = Assert.IsType<CatalogFailed<ObjectList>>(outcome);
        Assert.Equal(Audit.FailureKind.Connection, failed.Kind);
        Assert.StartsWith("could not connect:", failed.Message, StringComparison.Ordinal);
    }

    private async Task<ObjectList> List(bool caseSensitive, ObjectFilter filter)
    {
        var connectionString = caseSensitive ? seeded.CaseSensitiveConnectionString : seeded.ConnectionString;
        var outcome = await _reader.ListAsync(connectionString, filter, EffectiveLimits.Default, Cancel);
        return Assert.IsType<CatalogSucceeded<ObjectList>>(outcome).Value;
    }

    private static string Key(ObjectSummary o) => $"{o.Schema ?? "-"}.{o.Name}:{o.Type.ToWireName()}";
}
