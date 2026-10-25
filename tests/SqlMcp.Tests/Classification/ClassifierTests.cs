using SqlMcp.Classification;
using SqlMcp.Classification.SqlServer;

namespace SqlMcp.Tests.Classification;

public class ClassifierTests
{
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
        Assert.True(result.ForTool(expected).Accepted);
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
            tool => Assert.False(result.ForTool(tool).Accepted));
    }

    [Fact]
    public void Syntax_error_is_rejected_with_the_parser_position_and_message()
    {
        var result = _classifier.Classify("SELEC * FROM t");

        Assert.Empty(result.Statements);
        Assert.StartsWith("syntax error at line 1, column ", result.BatchError, StringComparison.Ordinal);
        Assert.Contains("Incorrect syntax", result.BatchError, StringComparison.Ordinal);
        Assert.False(result.ForTool(StatementCategory.Query).Accepted);
    }

    [Theory]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    [InlineData("SELECT 1\nGO")]
    public void Go_separators_are_rejected(string sql)
    {
        var verdict = _classifier.Classify(sql).ForTool(StatementCategory.Query);

        Assert.False(verdict.Accepted);
        Assert.Contains("GO", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   -- just a comment")]
    public void Empty_batch_is_rejected(string sql)
    {
        Assert.Equal("there is no statement to run", _classifier.Classify(sql).ForTool(StatementCategory.Query).Reason);
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
        var verdict = _classifier.Classify("SELECT 1; DROP TABLE t").ForTool(StatementCategory.Query);

        Assert.False(verdict.Accepted);
        Assert.Contains("statement 2", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("classified as ddl", verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("statement 1", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_batch_names_a_rejected_statement_and_its_reason()
    {
        var verdict = _classifier.Classify("SELECT 1;\nSELECT * INTO t2 FROM t").ForTool(StatementCategory.Query);

        Assert.False(verdict.Accepted);
        Assert.Contains("statement 2 (line 2) is rejected", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("creates a table", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Homogeneous_batch()
    {
        var result = _classifier.Classify("SELECT 1; SELECT 2");

        Assert.Equal(2, result.Statements.Count);
        Assert.True(result.ForTool(StatementCategory.Query).Accepted);
        Assert.False(result.ForTool(StatementCategory.Command).Accepted);
    }

    [Fact]
    public void Wrong_tool()
    {
        var verdict = _classifier.Classify("DELETE FROM t").ForTool(StatementCategory.Query);

        Assert.Equal(BatchVerdict.Reject("classified as command; use the command tool"), verdict);
    }

    [Fact]
    public void Dynamic_sql_in_a_query()
    {
        var verdict = _classifier.Classify("EXEC('SELECT 1')").ForTool(StatementCategory.Query);

        Assert.False(verdict.Accepted);
        Assert.Contains("dynamic SQL, which can't be classified", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_tool_category_is_a_programming_error()
    {
        var result = _classifier.Classify("SELECT 1");

        Assert.Throws<ArgumentOutOfRangeException>(() => result.ForTool(StatementCategory.Rejected));
    }
}
