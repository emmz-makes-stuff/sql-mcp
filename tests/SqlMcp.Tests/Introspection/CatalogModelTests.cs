using SqlMcp.Introspection;

namespace SqlMcp.Tests.Introspection;

public sealed class CatalogModelTests
{
    [Fact]
    public void Every_type_round_trips_through_its_wire_name()
    {
        foreach (var type in Enum.GetValues<ObjectType>())
        {
            Assert.True(ObjectTypes.TryParse(type.ToWireName(), out var parsed));
            Assert.Equal(type, parsed);
        }

        Assert.Equal(
            ["table", "view", "procedure", "function", "trigger", "sequence", "synonym", "type", "table_type"],
            ObjectTypes.WireNames);
    }

    [Fact]
    public void Type_names_ignore_case_and_surrounding_spaces()
    {
        Assert.True(ObjectTypes.TryParse(" Table_Type ", out var parsed));
        Assert.Equal(ObjectType.TableType, parsed);
    }

    [Fact]
    public void Unknown_object_type()
    {
        Assert.False(ObjectTypes.TryParseAll(["table", "package"], out var types, out var error));

        Assert.Empty(types);
        Assert.Equal(
            "'package' is not an object type. Use one of: table, view, procedure, function, trigger, sequence, synonym, type, table_type.",
            error);
    }

    [Fact]
    public void No_types_means_every_type()
    {
        Assert.True(ObjectTypes.TryParseAll(null, out var types, out _));
        Assert.Empty(types);
    }

    [Fact]
    public void Repeated_types_are_kept_once()
    {
        Assert.True(ObjectTypes.TryParseAll(["view", "VIEW"], out var types, out _));
        Assert.Equal([ObjectType.View], types);
    }

    [Fact]
    public void A_filter_describes_only_the_parts_given()
    {
        Assert.Empty(new ObjectFilter(null, [], null).Describe());
        Assert.Equal(
            ["schema=sales", "types=table,table_type", "pattern=order*"],
            new ObjectFilter("sales", [ObjectType.Table, ObjectType.TableType], "order*").Describe());
    }
}
