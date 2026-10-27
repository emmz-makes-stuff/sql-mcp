using SqlMcp.Introspection;

namespace SqlMcp.Tests.Introspection;

// Name parsing and the resolution messages (design I4, I8).
public sealed class ObjectNameTests
{
    [Theory]
    [InlineData("dbo.orders", "dbo", "orders")]
    [InlineData(" dbo.orders ", "dbo", "orders")]
    [InlineData("[dbo].[orders]", "dbo", "orders")]
    [InlineData("\"dbo\".\"orders\"", "dbo", "orders")]
    [InlineData("[sales].[open orders]", "sales", "open orders")]
    [InlineData("[dbo].[a]]b]", "dbo", "a]b")]
    [InlineData("orders", null, "orders")]
    [InlineData("[order lines]", null, "order lines")]
    public void Names_parse(string text, string? schema, string name)
    {
        Assert.True(ObjectName.TryParse(text, out var parsed, out var error), error);

        Assert.Equal(new ObjectName(schema, name), parsed);
    }

    [Theory]
    [InlineData("app.dbo.orders")]
    [InlineData("app..orders")]
    [InlineData("srv.app.dbo.orders")]
    public void Three_and_four_part_names_are_refused(string text)
    {
        Assert.False(ObjectName.TryParse(text, out _, out var error));

        Assert.Equal($"'{text}' names another database or server. Give schema.name in this database, such as dbo.orders.", error);
    }

    [Theory]
    [InlineData("dbo.orders; DROP TABLE dbo.users")]
    [InlineData("dbo.")]
    [InlineData("[dbo")]
    [InlineData("a.b.c.d.e")]
    public void Malformed_names_are_refused(string text)
    {
        Assert.False(ObjectName.TryParse(text, out _, out var error));

        Assert.StartsWith($"'{text}' is not an object name. Give schema.name", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_is_required(string? text)
    {
        Assert.False(ObjectName.TryParse(text, out _, out var error));

        Assert.Equal("Give an object name, such as dbo.orders.", error);
    }

    [Fact]
    public void Names_show_as_schema_dot_name()
    {
        Assert.Equal("dbo.orders", new ObjectName("dbo", "orders").ToString());
        Assert.Equal("audit_ddl", new ObjectName(null, "audit_ddl").ToString());
    }

    [Fact]
    public void One_candidate_resolves()
    {
        Assert.Null(NameResolution.Problem(new ObjectName(null, "users"), [new ObjectSummary("dbo", "users", ObjectType.Table, null)]));
    }

    [Fact]
    public void Ambiguous_unqualified_name()
    {
        var problem = NameResolution.Problem(
            new ObjectName(null, "orders"),
            [new ObjectSummary("archive", "orders", ObjectType.Table, null), new ObjectSummary("dbo", "orders", ObjectType.Table, null)]);

        Assert.Equal(
            "'orders' is ambiguous: it matches archive.orders (table), dbo.orders (table). Give a schema-qualified name, such as archive.orders.",
            problem);
    }

    [Fact]
    public void Ambiguous_with_a_database_trigger()
    {
        var problem = NameResolution.Problem(
            new ObjectName(null, "audit"),
            [new ObjectSummary(null, "audit", ObjectType.Trigger, null), new ObjectSummary("dbo", "audit", ObjectType.Table, null)]);

        Assert.Equal(
            "'audit' is ambiguous: it matches audit (trigger), dbo.audit (table). Give a schema-qualified name, such as dbo.audit.",
            problem);
    }

    [Fact]
    public void Unknown_object()
    {
        var problem = NameResolution.Problem(new ObjectName("dbo", "nothing_here"), []);

        Assert.Equal(
            "No object named 'dbo.nothing_here' was found. Use list_objects to find it; only objects this database login can see are found.",
            problem);
    }
}
