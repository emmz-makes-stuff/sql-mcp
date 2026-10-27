using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using SqlMcp.Audit;
using SqlMcp.Execution;
using SqlMcp.Execution.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Execution;

// These call the executor directly, below the classifier (design D11): a write or a WAITFOR can't reach it
// through the query tool, but the executor must still undo and bound whatever it is given.
// They run alone: two measure the whole process or server (allocated bytes, sql-mcp sessions on a reused spid), so
// another class's work in parallel, such as SMO scripting, would show up in them.
[Collection(nameof(SqlServerQueryExecutorTests))]
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
        // Each value is 1000 bytes, so a 10 KB cap holds 10 rows.
        var outcome = await Run(
            "SELECT TOP (100) REPLICATE('x', 1000) AS filler FROM sys.all_objects",
            EffectiveLimits.Default with { MaxBytes = 10 * 1024 });

        var result = Assert.IsType<QuerySucceeded>(outcome).Result;
        Assert.Equal(10, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Contains("the 10 KB cap", result.TruncationMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("varbinary(max)", "REPLICATE(CAST(0xAB AS varbinary(max)), 8388608)")]
    [InlineData("nvarchar(max)", "REPLICATE(CAST(N'é' AS nvarchar(max)), 8388608)")]
    [InlineData("xml", "N'<a>' + REPLICATE(CAST(N'é' AS nvarchar(max)), 8388608) + N'</a>'")]
    [InlineData("xml", "N'<a>' + REPLICATE(CAST(N'<b>é</b>' AS nvarchar(max)), 1048576) + N'</a>'")]
    [InlineData("json", "N'[\"' + REPLICATE(CAST(N'é' AS nvarchar(max)), 8388608) + N'\"]'")]
    public async Task A_value_larger_than_the_cap_is_not_buffered(string type, string value)
    {
        // An 8 MB value under a 64 KB cap: the small row before it fits, and the large one truncates the result. Reading
        // the value in whole would allocate well over 8 MB; streaming stops at the cap. (The count is process-wide, so
        // the bound leaves room for tests running alongside.)
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var outcome = await Run(
            $"SELECT n, big FROM (SELECT 1 AS n, CAST(NULL AS {type}) AS big UNION ALL SELECT 2, CAST({value} AS {type})) AS t ORDER BY n",
            EffectiveLimits.Default with { MaxBytes = 64 * 1024 });
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        Assert.InRange(allocated, 0, 8 * 1024 * 1024);
        var result = Assert.IsType<QuerySucceeded>(outcome).Result;
        Assert.Equal(1, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Contains("the 64 KB cap", result.TruncationMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("varbinary(max)", "REPLICATE(CAST(0xAB AS varbinary(max)), 3000)", 4000)]
    [InlineData("nvarchar(max)", "REPLICATE(CAST(N'é' AS nvarchar(max)), 2000)", 4000)]
    [InlineData("xml", "N'<a>' + REPLICATE(CAST(N'é' AS nvarchar(max)), 2000) + N'</a>'", 4007)]
    public async Task A_large_value_counts_its_unescaped_size(string type, string value, int size)
    {
        // 3000 bytes are 4000 base64 characters; 2000 'é' are 4000 UTF-8 bytes, plus 7 for the xml's tags. Each fits a
        // cap of exactly its size, and fails one a byte smaller.
        var sql = $"SELECT CAST({value} AS {type}) AS big";

        var fits = Assert.IsType<QuerySucceeded>(await Run(sql, EffectiveLimits.Default with { MaxBytes = size })).Result;
        var over = Assert.IsType<QuerySucceeded>(await Run(sql, EffectiveLimits.Default with { MaxBytes = size - 1 })).Result;

        Assert.Equal(1, fits.RowCount);
        Assert.False(fits.Truncated);
        Assert.Equal(0, over.RowCount);
        Assert.True(over.Truncated);
    }

    [Fact]
    public async Task An_xml_value_matches_SQL_Servers_own_text()
    {
        // xml is rebuilt from a reader, so it must come out as SQL Server serialises it. One leading 'x' then 5000
        // emoji puts surrogate pairs across the 8192-character chunk boundary.
        var result = Assert.IsType<QuerySucceeded>(await Run("""
            DECLARE @x xml = N'<p:a xmlns:p="urn:x" xmlns="urn:d" t="1 &amp; &lt;2&gt; &quot;q&quot; ''s'' &#xA;&#x9;&#xD;é">'
                + N'<b/><c></c><d>x &amp; y &lt; z &gt; w " '' &#xD; &#x9;</d><!-- note & < --><?pi data & < ?><?empty?>'
                + N'<![CDATA[<raw> & ]]><r xml:space="preserve"><e> </e><e>  </e><e>&#x9;&#xA;</e><e>&#xD;</e><e>a  </e>'
                + N'<e> &#xA;a&#xA;b </e></r><s> <t/> x <t/>  </s><u>&#x20;</u>'
                + N'<f>x' + REPLICATE(CAST(N'😀' AS nvarchar(max)), 5000) + N'</f></p:a><g/>tail &amp;';
            SELECT @x AS doc, CAST(@x AS nvarchar(max)) AS text;
            """)).Result;

        var row = result.ResultSets.Single().Rows.Single();
        Assert.Equal(row[1]!.GetValue<string>(), row[0]!.GetValue<string>());
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

        // The session that ran the query is gone, and with it any transaction or lock. The client has closed it by
        // now; the server ends the session asynchronously, which takes a moment when the container is busy.
        var sql = $"SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE session_id = {spid} AND program_name = '{SqlServerQueryExecutor.ApplicationName}'";
        var remaining = await db.ScalarAsync(sql);
        for (var attempt = 0; attempt < 20 && !Equals(remaining, 0); attempt++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            remaining = await db.ScalarAsync(sql);
        }

        Assert.Equal(0, remaining);
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
            BatchMode.ReadOnly,
            EffectiveLimits.Default,
            Cancel);

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.Connection, failed.Kind);
        Assert.StartsWith("could not connect", failed.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_cancelled_query_returns_promptly()
    {
        // Billions of rows, and caps that never bite: only the cancel can stop it, and it must stop it promptly. (SqlClient
        // cancels the command itself when the token fires; the executor's own cancel-before-dispose covers exceptions
        // that aren't cancellations, which SQL alone can't provoke.)
        const string sql = "SELECT a.object_id FROM sys.all_columns a CROSS JOIN sys.all_columns b CROSS JOIN sys.all_columns c";
        var limits = EffectiveLimits.Default with { MaxRows = int.MaxValue, MaxBytes = long.MaxValue, TimeoutSeconds = 600 };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        var started = Stopwatch.GetTimestamp();

        try
        {
            await _executor.ExecuteAsync(db.ConnectionString, sql, BatchMode.ReadOnly, limits, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected; SqlClient may instead report the cancel as a failed outcome.
        }

        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    private Task<QueryOutcome> Run(string sql, EffectiveLimits? limits = null) =>
        _executor.ExecuteAsync(db.ConnectionString, sql, BatchMode.ReadOnly, limits ?? EffectiveLimits.Default, Cancel);
}

[CollectionDefinition(nameof(SqlServerQueryExecutorTests), DisableParallelization = true)]
public sealed class RunsAlone;
