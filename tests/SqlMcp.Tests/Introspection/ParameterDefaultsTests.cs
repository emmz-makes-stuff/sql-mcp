using SqlMcp.Introspection.SqlServer;

namespace SqlMcp.Tests.Introspection;

// sys.parameters doesn't record T-SQL defaults, so they come from the definition.
public sealed class ParameterDefaultsTests
{
    [Fact]
    public void Procedure_parameters()
    {
        var defaults = SqlServerCatalogReader.ParameterDefaults("""
            CREATE PROCEDURE dbo.p @before date, @Limit int = 10, @note nvarchar(20) = NULL OUTPUT AS
            BEGIN
                DECLARE @local int = 1;
                SELECT @local;
            END
            """);

        Assert.Equal(new Dictionary<string, bool> { ["@BEFORE"] = false, ["@LIMIT"] = true, ["@NOTE"] = true }, defaults);
    }

    [Fact]
    public void Function_parameters()
    {
        var defaults = SqlServerCatalogReader.ParameterDefaults("CREATE FUNCTION dbo.f (@user_id int = 0) RETURNS int AS BEGIN RETURN @user_id; END");

        Assert.Equal(new Dictionary<string, bool> { ["@USER_ID"] = true }, defaults);
    }

    [Fact]
    public void A_definition_that_does_not_parse_gives_no_answer()
    {
        Assert.Null(SqlServerCatalogReader.ParameterDefaults("CREATE PROCEDURE dbo.p @x int AS BEGIN"));
    }
}
