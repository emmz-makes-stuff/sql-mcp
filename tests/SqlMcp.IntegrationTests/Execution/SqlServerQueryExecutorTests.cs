using System.Text.Encodings.Web;
using System.Text.Json;
using SqlMcp.Audit;
using SqlMcp.Execution;
using SqlMcp.Execution.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Execution;

// These call the executor directly, below the classifier (design D11): a write or a WAITFOR can't reach it
// through the query tool, but the executor must still undo and bound whatever it is given.
public sealed class SqlServerQueryExecutorTests(SqlServerFixture db)
{
    // The JSON text as written, without the default encoder's escaping of characters such as '+'.
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SqlServerQueryExecutor _executor = new(TimeProvider.System);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_is_rolled_back_after_a_successful_call()
    {
        await db.ScalarAsync("CREATE TABLE dbo.rollback_ok (id int)");

        var outcome = await Run("INSERT INTO dbo.rollback_ok VALUES (1); SELECT COUNT(*) AS n FROM dbo.rollback_ok");

        // Inside the call the row exists; afterwards nobody can see it.
        var result = Assert.IsType<QuerySucceeded>(outcome).Result;
        Assert.Equal(1, result.ResultSets.Single().Rows.Single()[0]!.GetValue<int>());
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.rollback_ok"));
    }

    [Fact]
    public async Task A_write_is_rolled_back_after_a_failed_call()
    {
        await db.ScalarAsync("CREATE TABLE dbo.rollback_failed (id int)");

        var outcome = await Run("INSERT INTO dbo.rollback_failed VALUES (1); SELECT 1 / 0 AS boom");

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.Sql, failed.Kind);
        Assert.Equal(8134, failed.ErrorNumber);
        Assert.Contains("divide by zero", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.rollback_failed"));
    }

    [Fact]
    public async Task Timeout()
    {
        await db.ScalarAsync("CREATE TABLE dbo.rollback_timeout (id int)");

        var outcome = await Run(
            "INSERT INTO dbo.rollback_timeout VALUES (1); WAITFOR DELAY '00:00:10'; SELECT 1 AS n",
            EffectiveLimits.Default with { TimeoutSeconds = 1 });

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.Timeout, failed.Kind);
        Assert.Contains("timed out after 1 seconds", failed.Message, StringComparison.Ordinal);
        Assert.True(failed.Duration < TimeSpan.FromSeconds(8), $"took {failed.Duration}");
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.rollback_timeout"));
    }

    [Fact]
    public async Task Result_over_the_cap()
    {
        var outcome = await Run(
            "SELECT TOP (50000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b");

        var result = Assert.IsType<QuerySucceeded>(outcome).Result;
        Assert.Equal(1000, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Contains("1000-row cap", result.TruncationMessage, StringComparison.Ordinal);
        Assert.Contains("add filters, select fewer columns, or use TOP", result.TruncationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Byte_cap()
    {
        // Each value is 1002 bytes as JSON, so a 10 KB cap holds 10 rows.
        var outcome = await Run(
            "SELECT TOP (100) REPLICATE('x', 1000) AS filler FROM sys.all_objects",
            EffectiveLimits.Default with { MaxBytes = 10 * 1024 });

        var result = Assert.IsType<QuerySucceeded>(outcome).Result;
        Assert.Equal(10, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Contains("the 10 KB cap", result.TruncationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_under_the_cap_is_not_truncated()
    {
        var result = Assert.IsType<QuerySucceeded>(await Run("SELECT TOP (1000) object_id FROM sys.all_objects ORDER BY object_id")).Result;

        Assert.False(result.Truncated);
        Assert.Null(result.TruncationMessage);
    }

    [Fact]
    public async Task Multiple_result_sets_share_the_caps()
    {
        var outcome = await Run(
            "SELECT 1 AS a UNION ALL SELECT 2; SELECT 'x' AS b, 3 AS c UNION ALL SELECT 'y', 4 UNION ALL SELECT 'z', 5; SELECT 6 AS d",
            EffectiveLimits.Default with { MaxRows = 3 });

        var result = Assert.IsType<QuerySucceeded>(outcome).Result;

        // Two rows from set 0, one from set 1, then everything stops: set 2 is never read.
        Assert.Equal(2, result.ResultSets.Count);
        Assert.Equal([new ResultColumn("a", "int")], result.ResultSets[0].Columns);
        Assert.Equal(2, result.ResultSets[0].Rows.Count);
        Assert.Equal(["b", "c"], result.ResultSets[1].Columns.Select(c => c.Name));
        Assert.Equal("x", result.ResultSets[1].Rows.Single()[0]!.GetValue<string>());
        Assert.True(result.Truncated);
        Assert.Contains("in result set 1", result.TruncationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_result_sets_are_buffered_in_order()
    {
        var result = Assert.IsType<QuerySucceeded>(await Run("SELECT 1 AS a; SELECT 2 AS b WHERE 1 = 0; SELECT 3 AS c")).Result;

        Assert.Equal(["a", "b", "c"], result.ResultSets.Select(s => s.Columns.Single().Name));
        Assert.Equal([1, 0, 1], result.ResultSets.Select(s => s.Rows.Count));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Connection_released_before_paging()
    {
        // 250 rows: more than one page at the default page size.
        var outcome = await Run(
            "SELECT @@SPID AS spid; SELECT TOP (250) object_id FROM sys.all_objects");

        var result = Assert.IsType<QuerySucceeded>(outcome).Result;
        Assert.Equal(250, result.ResultSets[1].Rows.Count);
        var spid = result.ResultSets[0].Rows.Single()[0]!.GetValue<short>();

        // The session that ran the query is gone, and with it any transaction or lock.
        Assert.Equal(0, await db.ScalarAsync(
            $"SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE session_id = {spid} AND program_name = '{SqlServerQueryExecutor.ApplicationName}'"));
    }

    [Fact]
    public async Task The_session_is_named_sql_mcp()
    {
        var result = Assert.IsType<QuerySucceeded>(await Run("SELECT APP_NAME() AS app")).Result;

        Assert.Equal("sql-mcp", result.ResultSets.Single().Rows.Single()[0]!.GetValue<string>());
    }

    [Fact]
    public async Task Types_round_trip()
    {
        var result = Assert.IsType<QuerySucceeded>(await Run("""
            SELECT
                CAST(NULL AS nvarchar(10)) AS null_text,
                CAST(N'' AS nvarchar(10)) AS empty_text,
                CAST(1234567890123456789012345678.0123456789 AS decimal(38,10)) AS big_decimal,
                CAST('2026-10-25T18:20:01.1234567' AS datetime2(7)) AS precise_time,
                CAST(0xDEADBEEF AS varbinary(max)) AS bytes,
                CAST(9007199254740993 AS bigint) AS big_integer,
                CAST(42 AS int) AS small_integer,
                CAST(1 AS bit) AS flag,
                CAST(0.1 AS float) AS approximate,
                CAST(12.3456 AS money) AS cash,
                CAST('2026-10-25' AS date) AS day,
                CAST('18:20:01.1234567' AS time(7)) AS clock,
                CAST('2026-10-25T18:20:01.1234567+01:00' AS datetimeoffset(7)) AS zoned,
                CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier) AS id,
                CAST(N'héllo' AS nvarchar(50)) AS unicode,
                CAST('<a>1</a>' AS xml) AS doc,
                CAST(CAST(5 AS bigint) AS sql_variant) AS variant,
                geography::Point(51.5, -0.12, 4326) AS place,
                CAST('{"a":1}' AS json) AS payload,
                CAST('[1.5,2,3]' AS vector(3)) AS embedding
            """)).Result;

        var set = result.ResultSets.Single();
        var row = set.Rows.Single();
        var types = set.Columns.ToDictionary(c => c.Name, c => c.SqlType);
        object? Value(string name)
        {
            var node = row[set.Columns.ToList().FindIndex(c => c.Name == name)];
            return node?.ToJsonString(Unescaped);
        }

        Assert.Null(Value("null_text"));
        Assert.Equal("\"\"", Value("empty_text"));
        Assert.Equal("\"1234567890123456789012345678.0123456789\"", Value("big_decimal"));
        Assert.Equal("decimal(38,10)", types["big_decimal"]);
        Assert.Equal("\"2026-10-25T18:20:01.1234567\"", Value("precise_time"));
        Assert.Equal("datetime2(7)", types["precise_time"]);
        Assert.Equal("\"3q2+7w==\"", Value("bytes"));
        Assert.Equal("varbinary(max)", types["bytes"]);
        Assert.Equal("\"9007199254740993\"", Value("big_integer"));
        Assert.Equal("42", Value("small_integer"));
        Assert.Equal("true", Value("flag"));
        Assert.Equal("0.1", Value("approximate"));
        Assert.Equal("\"12.3456\"", Value("cash"));
        Assert.Equal("\"2026-10-25\"", Value("day"));
        Assert.Equal("\"18:20:01.1234567\"", Value("clock"));
        Assert.Equal("\"2026-10-25T18:20:01.1234567+01:00\"", Value("zoned"));
        Assert.Equal("\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"", Value("id"));
        Assert.Equal("héllo", row[set.Columns.ToList().FindIndex(c => c.Name == "unicode")]!.GetValue<string>());
        Assert.Equal("nvarchar(50)", types["unicode"]);
        Assert.Equal("\"<a>1</a>\"", Value("doc"));
        Assert.Equal("\"5\"", Value("variant"));
        Assert.StartsWith("geography (CLR type", types["place"], StringComparison.Ordinal);
        Assert.NotEmpty(Convert.FromBase64String(row[set.Columns.ToList().FindIndex(c => c.Name == "place")]!.GetValue<string>()));
        Assert.Equal("{\"a\":1}", row[set.Columns.ToList().FindIndex(c => c.Name == "payload")]!.GetValue<string>());
        Assert.Equal("[1.5,2,3]", Value("embedding"));
    }

    [Fact]
    public async Task An_unreachable_server_is_a_connection_failure()
    {
        var outcome = await _executor.ExecuteAsync(
            "Server=tcp:127.0.0.1,1;Connect Timeout=2;User ID=sa;Password=x;TrustServerCertificate=true",
            "SELECT 1",
            EffectiveLimits.Default,
            Cancel);

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.Connection, failed.Kind);
        Assert.StartsWith("could not connect", failed.Message, StringComparison.Ordinal);
    }

    private Task<QueryOutcome> Run(string sql, EffectiveLimits? limits = null) =>
        _executor.ExecuteAsync(db.ConnectionString, sql, limits ?? EffectiveLimits.Default, Cancel);
}
