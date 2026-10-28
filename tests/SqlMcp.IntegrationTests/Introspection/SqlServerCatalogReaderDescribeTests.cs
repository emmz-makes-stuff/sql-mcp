using SqlMcp.Audit;
using SqlMcp.Introspection;
using SqlMcp.Introspection.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Introspection;

// Name resolution and describe_object against seed.sql (design I3, I4).
public sealed class SqlServerCatalogReaderDescribeTests(SeededDatabases seeded) : IClassFixture<SeededDatabases>
{
    private readonly SqlServerCatalogReader _reader = new(TimeProvider.System);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("dbo.users", "dbo", "users", "table")]
    [InlineData("[sales].[open_orders]", "sales", "open_orders", "view")]
    [InlineData("\"dbo\".\"archive_orders\"", "dbo", "archive_orders", "procedure")]
    [InlineData("users", "dbo", "users", "table")]
    [InlineData("audit_ddl", null, "audit_ddl", "trigger")]
    [InlineData("id_list", "dbo", "id_list", "table_type")]
    public async Task Names_resolve(string text, string? schema, string name, string type)
    {
        var resolved = await Resolve(seeded.ConnectionString, text);

        Assert.Equal([(schema, name, type)], resolved.Select(o => (o.Schema, o.Name, o.Type.ToWireName())));
    }

    [Fact]
    public async Task Ambiguous_unqualified_name()
    {
        var failed = await ResolveFails(seeded.ConnectionString, "orders");

        Assert.Equal(FailureKind.UnresolvedName, failed.Kind);
        Assert.Equal(
            "'orders' is ambiguous: it matches archive.orders (table), dbo.orders (table). Give a schema-qualified name, such as archive.orders.",
            failed.Message);
    }

    [Fact]
    public async Task Unknown_object()
    {
        var failed = await ResolveFails(seeded.ConnectionString, "dbo.nothing_here");

        Assert.Equal(FailureKind.UnresolvedName, failed.Kind);
        Assert.Equal(
            "No object named 'dbo.nothing_here' was found. Use list_objects to find it; only objects this database login can see are found.",
            failed.Message);
    }

    [Fact]
    public async Task Every_name_that_fails_is_reported()
    {
        var failed = await ResolveFails(seeded.ConnectionString, "dbo.users", "orders", "dbo.nothing_here");

        Assert.Equal(2, failed.Message.Split(Environment.NewLine).Length);
        Assert.StartsWith("'orders' is ambiguous", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith("No object named 'dbo.nothing_here' was found. Use list_objects to find it; only objects this database login can see are found.", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_compare_as_the_database_compares_them()
    {
        Assert.Equal("Order_Archive", (await Resolve(seeded.ConnectionString, "dbo.order_archive")).Single().Name);

        var failed = await ResolveFails(seeded.CaseSensitiveConnectionString, "dbo.order_archive");
        Assert.StartsWith("No object named 'dbo.order_archive'", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Order_Archive", (await Resolve(seeded.CaseSensitiveConnectionString, "dbo.Order_Archive")).Single().Name);
    }

    [Fact]
    public async Task The_same_object_twice_resolves_once()
    {
        Assert.Single(await Resolve(seeded.ConnectionString, "dbo.users", "[dbo].[users]", "users"));
    }

    [Fact]
    public async Task Table_with_keys()
    {
        var orders = await Describe("dbo.orders");

        Assert.Equal(ObjectType.Table, orders.Type);
        Assert.Equal(
            [
                new ColumnDetails("id", "int", false, null, true, null),
                new ColumnDetails("user_id", "int", false, null, null, null),
                new ColumnDetails("number", "bigint", false, "(NEXT VALUE FOR [dbo].[order_numbers])", null, null),
                new ColumnDetails("placed_at", "date", false, null, null, null),
                new ColumnDetails("status", "varchar(10)", false, null, null, null),
            ],
            orders.Columns);
        Assert.Equal(("pk_orders", true), (orders.PrimaryKey!.Name, orders.PrimaryKey.Clustered));
        Assert.Equal(["id"], orders.PrimaryKey.Columns);
        var index = Assert.Single(orders.Indexes!);
        Assert.Equal(("ix_orders_user", "nonclustered", false, null as string), (index.Name, index.Kind, index.Unique, index.Filter));
        Assert.Equal(["user_id"], index.Columns);
        Assert.Equal(["placed_at"], index.Included!);
        Assert.Equal(["orders_status"], orders.Triggers!);

        var declared = Assert.Single(orders.ForeignKeys!);
        Assert.Equal(("fk_orders_users", "dbo.orders", "dbo.users", null as string, null as string), (declared.Name, declared.Table, declared.ReferencedTable, declared.OnDelete, declared.OnUpdate));
        Assert.Equal(["user_id"], declared.Columns);
        Assert.Equal(["id"], declared.ReferencedColumns);

        var referencing = Assert.Single(orders.ReferencedBy!);
        Assert.Equal(("fk_order_lines_orders", "dbo.order_lines", "dbo.orders"), (referencing.Name, referencing.Table, referencing.ReferencedTable));
        Assert.Equal(["order_id"], referencing.Columns);
        Assert.Equal(["id"], referencing.ReferencedColumns);

        Assert.Null(orders.UniqueConstraints);
        Assert.Null(orders.CheckConstraints);
        Assert.Null(orders.Temporal);
        Assert.Null(orders.Definition);
    }

    [Fact]
    public async Task Table_with_constraints_defaults_and_computed_columns()
    {
        var users = await Describe("dbo.users");

        Assert.Equal(
            [
                new ColumnDetails("id", "int", false, null, true, null),
                new ColumnDetails("email", "nvarchar(200)", false, null, null, null),
                new ColumnDetails("phone", "dbo.phone", true, null, null, null),
                new ColumnDetails("created_at", "datetime2(3)", false, "(sysutcdatetime())", null, null),
                new ColumnDetails("display_name", "nvarchar(200)", true, null, null, "(lower([email]))"),
            ],
            users.Columns);
        var unique = Assert.Single(users.UniqueConstraints!);
        Assert.Equal(("uq_users_email", false), (unique.Name, unique.Clustered));
        Assert.Equal(["email"], unique.Columns);
        Assert.Equal([new CheckDetails("ck_users_email", "([email] like '%@%')")], users.CheckConstraints);
        Assert.Equal(["fk_orders_users"], users.ReferencedBy!.Select(k => k.Name));
        Assert.Null(users.ForeignKeys);
        Assert.Null(users.Indexes);
    }

    [Fact]
    public async Task Composite_key_and_partitioned_table()
    {
        var lines = await Describe("dbo.order_lines");
        Assert.Equal(["order_id", "line"], lines.PrimaryKey!.Columns);

        var events = await Describe("dbo.events");
        Assert.Equal(["id"], events.PrimaryKey!.Columns);
        Assert.Null(events.Indexes);
    }

    [Fact]
    public async Task Temporal_table()
    {
        var prices = await Describe("dbo.prices");

        Assert.Equal(new TemporalDetails("dbo.prices_history", "valid_from", "valid_to"), prices.Temporal);
        Assert.Equal(["id", "amount", "valid_from", "valid_to"], prices.Columns!.Select(c => c.Name));
    }

    [Fact]
    public async Task View()
    {
        var view = await Describe("sales.open_orders");

        Assert.Equal(["id", "user_id", "placed_at"], view.Columns!.Select(c => c.Name));
        var definition = Assert.IsType<Definition.Text>(view.Definition);
        Assert.StartsWith("CREATE VIEW sales.open_orders AS", definition.Value, StringComparison.Ordinal);
        Assert.Null(view.PrimaryKey);
        Assert.Null(view.ForeignKeys);
        Assert.Null(view.Indexes);
    }

    [Fact]
    public async Task Procedure()
    {
        var procedure = await Describe("dbo.archive_orders");

        Assert.Equal([new ParameterDetails("@before", "date", "input", false)], procedure.Parameters);
        var definition = Assert.IsType<Definition.Text>(procedure.Definition);
        Assert.StartsWith("CREATE PROCEDURE dbo.archive_orders @before date AS", definition.Value, StringComparison.Ordinal);
        Assert.Null(procedure.Kind);
        Assert.Null(procedure.ReturnType);
    }

    [Fact]
    public async Task Encrypted_procedure()
    {
        var procedure = await Describe("dbo.secret_report");

        Assert.Equal(new Definition.Unavailable(Definition.Encrypted), procedure.Definition);
        Assert.Null(procedure.Parameters);
    }

    [Fact]
    public async Task Scalar_function()
    {
        var function = await Describe("dbo.order_count");

        Assert.Equal("scalar", function.Kind);
        Assert.Equal("int", function.ReturnType);
        Assert.Equal([new ParameterDetails("@user_id", "int", "input", false)], function.Parameters);
        Assert.IsType<Definition.Text>(function.Definition);
        Assert.Null(function.Columns);
    }

    [Fact]
    public async Task Inline_table_valued_function()
    {
        var function = await Describe("dbo.orders_for");

        Assert.Equal("inline_table_valued", function.Kind);
        Assert.Equal("table", function.ReturnType);
        Assert.Equal(["id", "placed_at"], function.Columns!.Select(c => c.Name));
        Assert.Equal([new ParameterDetails("@user_id", "int", "input", false)], function.Parameters);
    }

    [Fact]
    public async Task Triggers()
    {
        var trigger = await Describe("dbo.orders_status");
        Assert.StartsWith("CREATE TRIGGER dbo.orders_status ON dbo.orders", Assert.IsType<Definition.Text>(trigger.Definition).Value, StringComparison.Ordinal);

        var database = await Describe("audit_ddl");
        Assert.Null(database.Schema);
        Assert.StartsWith("CREATE TRIGGER audit_ddl ON DATABASE", Assert.IsType<Definition.Text>(database.Definition).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synonym()
    {
        var synonym = await Describe("dbo.customers");

        Assert.Equal("dbo.users", synonym.Target);
    }

    [Fact]
    public async Task Sequence()
    {
        var sequence = await Describe("dbo.order_numbers");

        Assert.Equal(new SequenceDetails("bigint", "1000", "10", "1000", "999999", Cycles: false), sequence.Sequence);
    }

    [Fact]
    public async Task User_defined_type()
    {
        var type = await Describe("dbo.phone");

        Assert.Equal("varchar(20)", type.BaseType);
        Assert.False(type.Nullable);
    }

    [Fact]
    public async Task Table_type()
    {
        var type = await Describe("dbo.id_list");

        Assert.Equal([new ColumnDetails("id", "int", false, null, null, null)], type.Columns);
        Assert.Equal(["id"], type.PrimaryKey!.Columns);
        Assert.Null(type.ReferencedBy);
    }

    [Fact]
    public async Task A_table_and_a_table_type_with_the_same_name()
    {
        var failed = await ResolveFails(seeded.ConnectionString, "crm.address");
        var table = await _reader.DescribeAsync(seeded.ConnectionString, new ObjectName("crm", "address") { Type = ObjectType.Table }, EffectiveLimits.Default, Cancel);
        var type = await _reader.DescribeAsync(seeded.ConnectionString, new ObjectName("crm", "address") { Type = ObjectType.TableType }, EffectiveLimits.Default, Cancel);

        Assert.Equal(
            "'crm.address' is ambiguous: it matches crm.address (table), crm.address (table_type), an object and a type with the same name. Give its type too, such as table_type.",
            failed.Message);
        var tableDetails = Assert.IsType<CatalogSucceeded<ObjectDetails>>(table).Value;
        Assert.Equal((ObjectType.Table, "pk_crm_address"), (tableDetails.Type, tableDetails.PrimaryKey!.Name));
        var typeDetails = Assert.IsType<CatalogSucceeded<ObjectDetails>>(type).Value;
        Assert.Equal(ObjectType.TableType, typeDetails.Type);
        Assert.Equal(["line"], typeDetails.Columns!.Select(c => c.Name));
    }

    [Fact]
    public async Task Foreign_keys_with_the_same_name_in_two_schemas()
    {
        var orders = await Describe("archive.orders");

        Assert.Equal(
            [("fk_returns_orders", "archive.returns", "order_id"), ("fk_returns_orders", "crm.returns", "order_id")],
            orders.ReferencedBy!.Select(k => (k.Name, k.Table, Assert.Single(k.Columns))).Order());
        Assert.All(orders.ReferencedBy!, k => Assert.Equal(["id"], k.ReferencedColumns));
    }

    [Fact]
    public async Task Describe_an_unknown_object()
    {
        var outcome = await _reader.DescribeAsync(seeded.ConnectionString, new ObjectName("dbo", "nothing_here"), EffectiveLimits.Default, Cancel);

        Assert.Equal(FailureKind.UnresolvedName, Assert.IsType<CatalogFailed<ObjectDetails>>(outcome).Kind);
    }

    private async Task<ObjectDetails> Describe(string text)
    {
        Assert.True(ObjectName.TryParse(text, out var name, out var error), error);
        var outcome = await _reader.DescribeAsync(seeded.ConnectionString, name, EffectiveLimits.Default, Cancel);
        return Assert.IsType<CatalogSucceeded<ObjectDetails>>(outcome).Value;
    }

    private async Task<IReadOnlyList<ObjectSummary>> Resolve(string connectionString, params string[] texts)
    {
        var outcome = await _reader.ResolveAsync(connectionString, Names(texts), EffectiveLimits.Default, Cancel);
        return Assert.IsType<CatalogSucceeded<IReadOnlyList<ObjectSummary>>>(outcome).Value;
    }

    private async Task<CatalogFailed<IReadOnlyList<ObjectSummary>>> ResolveFails(string connectionString, params string[] texts)
    {
        var outcome = await _reader.ResolveAsync(connectionString, Names(texts), EffectiveLimits.Default, Cancel);
        return Assert.IsType<CatalogFailed<IReadOnlyList<ObjectSummary>>>(outcome);
    }

    private static List<ObjectName> Names(string[] texts) =>
        [.. texts.Select(t => ObjectName.TryParse(t, out var name, out var error) ? name : throw new ArgumentException(error))];
}
