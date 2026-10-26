using SqlMcp.Classification;
using SqlMcp.Classification.SqlServer;

namespace SqlMcp.Tests.Classification;

public class ClassifierTests
{
    /// <summary>Every category with a tool, so verdicts name the tool that would run a statement.</summary>
    private static readonly IReadOnlySet<StatementCategory> AllTools = new HashSet<StatementCategory>
    {
        StatementCategory.Query, StatementCategory.Command, StatementCategory.Ddl, StatementCategory.Execute,
    };

    private static readonly IReadOnlySet<StatementCategory> QueryOnly = new HashSet<StatementCategory> { StatementCategory.Query };

    private readonly SqlServerStatementClassifier _classifier = new();

    [Theory]
    [MemberData(nameof(ClassifierTable.Accepted), MemberType = typeof(ClassifierTable))]
    public void Statement_is_classified(string sql, string expectedName)
    {
        var expected = Enum.GetValues<StatementCategory>().Single(c => c.ToName() == expectedName);
        var result = _classifier.Classify(sql);

        Assert.Null(result.BatchError);
        var statement = Assert.Single(result.Statements);
        Assert.True(statement.Category == expected, $"Expected {expected}, got {statement.Category}: {statement.Reason}");
        Assert.True(result.ForTool(expected, AllTools).Accepted);
    }

    [Theory]
    [MemberData(nameof(ClassifierTable.Rejected), MemberType = typeof(ClassifierTable))]
    public void Statement_is_rejected_with_a_reason(string sql, string reasonFragment)
    {
        var result = _classifier.Classify(sql);

        Assert.Null(result.BatchError);
        var statement = Assert.Single(result.Statements);
        Assert.Equal(StatementCategory.Rejected, statement.Category);
        Assert.Contains(reasonFragment, statement.Reason, StringComparison.Ordinal);
        Assert.All(
            [StatementCategory.Query, StatementCategory.Command, StatementCategory.Ddl, StatementCategory.Execute],
            tool => Assert.False(result.ForTool(tool, AllTools).Accepted));
    }

    [Fact]
    public void Syntax_error_is_rejected_with_the_parser_position_and_message()
    {
        var result = _classifier.Classify("SELEC * FROM t");

        Assert.Empty(result.Statements);
        Assert.StartsWith("syntax error at line 1, column ", result.BatchError, StringComparison.Ordinal);
        Assert.Contains("Incorrect syntax", result.BatchError, StringComparison.Ordinal);
        Assert.False(result.ForTool(StatementCategory.Query, AllTools).Accepted);
    }

    [Theory]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    [InlineData("SELECT 1\nGO")]
    public void Go_separators_are_rejected(string sql)
    {
        var verdict = _classifier.Classify(sql).ForTool(StatementCategory.Query, AllTools);

        Assert.False(verdict.Accepted);
        Assert.Contains("GO", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   -- just a comment")]
    public void Empty_batch_is_rejected(string sql)
    {
        Assert.Equal("there is no statement to run", _classifier.Classify(sql).ForTool(StatementCategory.Query, AllTools).Reason);
    }

    [Fact]
    public void Rejection_gives_the_position_of_the_element()
    {
        var statement = Assert.Single(_classifier.Classify("SELECT a\nFROM t WITH (UPDLOCK)").Statements);

        Assert.Contains("at line 2, column", statement.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_batch()
    {
        var verdict = _classifier.Classify("SELECT 1; DROP TABLE t").ForTool(StatementCategory.Query, AllTools);

        Assert.False(verdict.Accepted);
        Assert.Contains("statement 2", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("classified as ddl", verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("statement 1", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_batch_names_a_rejected_statement_and_its_reason()
    {
        var verdict = _classifier.Classify("SELECT 1;\nSELECT * INTO t2 FROM t").ForTool(StatementCategory.Query, AllTools);

        Assert.False(verdict.Accepted);
        Assert.Contains("statement 2 (line 2) is rejected", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("creates a table", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Homogeneous_batch()
    {
        var result = _classifier.Classify("SELECT 1; SELECT 2");

        Assert.Equal(2, result.Statements.Count);
        Assert.True(result.ForTool(StatementCategory.Query, AllTools).Accepted);
        Assert.False(result.ForTool(StatementCategory.Command, AllTools).Accepted);
    }

    [Fact]
    public void Wrong_tool()
    {
        var verdict = _classifier.Classify("DELETE FROM t WHERE id = 1").ForTool(StatementCategory.Query, AllTools);

        Assert.Equal(BatchVerdict.Reject("classified as command; use the command tool"), verdict);
    }

    [Fact]
    public void Wrong_tool_that_is_denied_on_the_database()
    {
        var verdict = _classifier.Classify("DELETE FROM t WHERE id = 1").ForTool(StatementCategory.Query, QueryOnly);

        Assert.Equal(BatchVerdict.Reject("classified as command; the command tool is denied on this database"), verdict);
    }

    [Fact]
    public void Mixed_batch_says_which_tools_are_denied_on_the_database()
    {
        var verdict = _classifier.Classify("SELECT 1; DROP TABLE t").ForTool(StatementCategory.Query, QueryOnly);

        Assert.Equal(
            BatchVerdict.Reject(
                "statement 2 (line 1) classified as ddl; the ddl tool is denied on this database. The whole batch is rejected, so nothing in it runs."),
            verdict);
    }

    [Fact]
    public void Dynamic_sql_in_a_query()
    {
        var verdict = _classifier.Classify("EXEC('SELECT 1')").ForTool(StatementCategory.Query, AllTools);

        Assert.False(verdict.Accepted);
        Assert.Contains("dynamic SQL, which can't be classified", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_tool_category_is_a_programming_error()
    {
        var result = _classifier.Classify("SELECT 1");

        Assert.Throws<ArgumentOutOfRangeException>(() => result.ForTool(StatementCategory.Rejected, AllTools));
    }
}
