using SqlMcp.Introspection;

namespace SqlMcp.Tests.Introspection;

public sealed class CompletenessTests
{
    private static readonly CatalogItem Orders = new("dbo", "orders", "table");
    private static readonly CatalogItem OrderLines = new("dbo", "order_lines", "table");
    private static readonly CatalogItem Secret = new("dbo", "secret_proc", "procedure");
    private static readonly CatalogItem AppReader = new(null, "app_reader", CatalogItemTypes.User);
    private static readonly CatalogItem Signing = new(null, "signing", CatalogItemTypes.Certificate);
    private static readonly CatalogItem Catalog = new(null, "ft", CatalogItemTypes.FullTextCatalog);

    [Fact]
    public void Fully_scripted_inventory_omits_nothing()
    {
        InventoryItem[] inventory = [new(Orders, InventoryClass.Scriptable), new(OrderLines, InventoryClass.Scriptable)];

        Assert.Empty(Completeness.Omitted(inventory, [OrderLines, Orders]));
    }

    [Fact]
    public void Encrypted_module()
    {
        InventoryItem[] inventory = [new(Orders, InventoryClass.Scriptable), new(Secret, InventoryClass.Scriptable, Encrypted: true)];

        Assert.Equal([new OmittedItem(Secret, Completeness.Encrypted)], Completeness.Omitted(inventory, [Orders]));
    }

    [Fact]
    public void Security_principal()
    {
        InventoryItem[] inventory = [new(AppReader, InventoryClass.SecurityPrincipal)];

        Assert.Equal([new OmittedItem(AppReader, Completeness.SecurityPrincipal)], Completeness.Omitted(inventory, []));
    }

    [Fact]
    public void Security_object()
    {
        InventoryItem[] inventory = [new(Signing, InventoryClass.SecurityObject)];

        Assert.Equal([new OmittedItem(Signing, Completeness.SecurityObject)], Completeness.Omitted(inventory, []));
    }

    [Fact]
    public void Anything_else_the_generator_skipped()
    {
        InventoryItem[] inventory = [new(Orders, InventoryClass.Scriptable), new(Catalog, InventoryClass.Scriptable)];

        Assert.Equal([new OmittedItem(Catalog, Completeness.NotScripted)], Completeness.Omitted(inventory, [Orders]));
    }

    [Fact]
    public void Omitted_items_keep_inventory_order()
    {
        InventoryItem[] inventory =
        [
            new(Signing, InventoryClass.SecurityObject),
            new(Orders, InventoryClass.Scriptable),
            new(AppReader, InventoryClass.SecurityPrincipal),
            new(Secret, InventoryClass.Scriptable, Encrypted: true),
        ];

        Assert.Equal([Signing, AppReader, Secret], Completeness.Omitted(inventory, [Orders]).Select(o => o.Item));
    }

    [Fact]
    public void Items_match_on_schema_name_and_type()
    {
        var view = Orders with { Type = "view" };
        var otherSchema = Orders with { Schema = "sales" };
        InventoryItem[] inventory = [new(Orders, InventoryClass.Scriptable)];

        Assert.Single(Completeness.Omitted(inventory, [view, otherSchema]));
        Assert.Empty(Completeness.Omitted(inventory, [new CatalogItem("dbo", "orders", "table")]));
    }

    [Fact]
    public void Trigger_on_an_encrypted_view()
    {
        var view = new CatalogItem("dbo", "secret_view", "view");
        var trigger = new CatalogItem("dbo", "secret_view_insert", "trigger");
        InventoryItem[] inventory = [new(view, InventoryClass.Scriptable, Encrypted: true), new(trigger, InventoryClass.Scriptable) { Parent = view }];

        Assert.Equal(
            [new OmittedItem(view, Completeness.Encrypted), new OmittedItem(trigger, Completeness.OnEncryptedView)],
            Completeness.Omitted(inventory, []));
    }

    [Fact]
    public void Narrowed_keeps_the_chosen_objects_and_their_triggers()
    {
        var ordersTrigger = new CatalogItem("dbo", "orders_audit", "trigger");
        var linesTrigger = new CatalogItem("dbo", "order_lines_audit", "trigger");
        InventoryItem[] inventory =
        [
            new(Orders, InventoryClass.Scriptable),
            new(OrderLines, InventoryClass.Scriptable),
            new(ordersTrigger, InventoryClass.Scriptable) { Parent = Orders },
            new(linesTrigger, InventoryClass.Scriptable) { Parent = OrderLines },
            new(AppReader, InventoryClass.SecurityPrincipal),
        ];

        Assert.Equal([Orders, ordersTrigger], Completeness.Narrowed(inventory, new HashSet<CatalogItem> { Orders }).Select(i => i.Item));
    }

    [Fact]
    public void Scripted_items_outside_the_inventory_are_ignored()
    {
        InventoryItem[] inventory = [new(Orders, InventoryClass.Scriptable)];

        Assert.Empty(Completeness.Omitted(inventory, [Orders, OrderLines]));
    }
}
