using SqlMcp.Introspection.SqlServer;

namespace SqlMcp.Tests.Introspection;

public sealed class SqlServerTypeNamesTests
{
    [Theory]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("nvarchar", 400, 0, 0, "nvarchar(200)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("nchar", 20, 0, 0, "nchar(10)")]
    [InlineData("varchar", 10, 0, 0, "varchar(10)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("binary", 16, 0, 0, "binary(16)")]
    [InlineData("decimal", 9, 10, 2, "decimal(10,2)")]
    [InlineData("numeric", 17, 38, 0, "numeric(38,0)")]
    [InlineData("datetime2", 7, 23, 3, "datetime2(3)")]
    [InlineData("datetimeoffset", 10, 34, 7, "datetimeoffset(7)")]
    [InlineData("time", 5, 16, 7, "time(7)")]
    [InlineData("vector", 20, 0, 0, "vector(3)")]
    [InlineData("float", 8, 53, 0, "float")]
    [InlineData("xml", -1, 0, 0, "xml")]
    public void System_types(string name, short maxLength, byte precision, byte scale, string expected)
    {
        Assert.Equal(expected, SqlServerTypeNames.Format(name, "sys", userDefined: false, maxLength, precision, scale));
    }

    [Fact]
    public void User_defined_types_are_schema_qualified_without_a_length()
    {
        Assert.Equal("dbo.phone", SqlServerTypeNames.Format("phone", "dbo", userDefined: true, 20, 0, 0));
    }
}
