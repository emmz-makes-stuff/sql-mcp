using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlMcp.Classification;
using SqlMcp.Classification.SqlServer;

namespace SqlMcp.Tests.Classification;

/// <summary>Types that must never be on the query allowlist, whatever a future corpus case seems to need.</summary>
public class AllowlistGuardTests
{
    public static TheoryData<string> NeverInQueries => new(
        nameof(OpenQueryTableReference),
        nameof(OpenRowsetTableReference),
        nameof(BulkOpenRowset),
        nameof(InternalOpenRowset),
        nameof(OpenRowsetCosmos),
        nameof(AdHocTableReference),
        nameof(NextValueForExpression),
        nameof(ExecutableStringList),
        nameof(ExecuteStatement),
        nameof(SelectSetVariable),
        nameof(VariableReference),
        nameof(InsertStatement),
        nameof(UpdateStatement),
        nameof(DeleteStatement),
        nameof(MergeStatement),
        nameof(OutputClause),
        nameof(OutputIntoClause));

    [Theory]
    [MemberData(nameof(NeverInQueries))]
    public void Query_allowlist_excludes(string typeName)
    {
        Assert.DoesNotContain(Allowlists.For(StatementCategory.Query), type => type.Name == typeName);
    }

    /// <summary>Rowset functions reach outside the database, so no category may accept them (design W5).</summary>
    public static TheoryData<string> NeverAnywhere => new(
        nameof(OpenQueryTableReference),
        nameof(OpenRowsetTableReference),
        nameof(BulkOpenRowset),
        nameof(InternalOpenRowset),
        nameof(OpenRowsetCosmos),
        nameof(AdHocTableReference));

    [Theory]
    [MemberData(nameof(NeverAnywhere))]
    public void No_allowlist_includes(string typeName)
    {
        var categories = new[] { StatementCategory.Query, StatementCategory.Command, StatementCategory.Ddl, StatementCategory.Execute };

        Assert.All(categories, category => Assert.DoesNotContain(Allowlists.For(category), type => type.Name == typeName));
    }

    [Fact]
    public void Every_allowlisted_type_is_a_scriptdom_node()
    {
        var categories = new[] { StatementCategory.Query, StatementCategory.Command, StatementCategory.Ddl, StatementCategory.Execute };

        Assert.All(categories.SelectMany(Allowlists.For), type => Assert.True(type.IsAssignableTo(typeof(TSqlFragment)), type.Name));
    }

    [Fact]
    public void Every_statement_type_is_on_its_own_allowlist()
    {
        Assert.All(Allowlists.StatementCategories, pair => Assert.Contains(pair.Key, Allowlists.For(pair.Value)));
    }
}
