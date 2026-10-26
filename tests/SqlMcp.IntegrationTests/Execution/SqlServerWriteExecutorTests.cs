using System.Diagnostics;
using SqlMcp.Audit;
using SqlMcp.Execution;
using SqlMcp.Execution.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Execution;

// The executor in Write mode (design W1–W4), called directly like SqlServerQueryExecutorTests. Every check of what
// was committed uses the fixture's own connection, so it sees only what another session would.
public sealed class SqlServerWriteExecutorTests(SqlServerFixture db)
{
    private readonly SqlServerQueryExecutor _executor = new(TimeProvider.System);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Successful_command()
    {
        await db.ScalarAsync("CREATE TABLE dbo.w_ok (id int PRIMARY KEY, active bit); INSERT INTO dbo.w_ok VALUES (7, 1), (8, 1), (9, 1)");

        var outcome = await Write("UPDATE dbo.w_ok SET active = 0 WHERE id = 7; DELETE FROM dbo.w_ok WHERE id IN (8, 9)");

        var succeeded = Assert.IsType<QuerySucceeded>(outcome);
        Assert.Equal([1, 2], succeeded.RowsAffected);
        Assert.Empty(succeeded.Result.ResultSets);
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_ok"));
        Assert.Equal(false, await db.ScalarAsync("SELECT active FROM dbo.w_ok WHERE id = 7"));
    }

    [Fact]
    public async Task A_read_reports_no_rows_affected()
    {
        var succeeded = Assert.IsType<QuerySucceeded>(await _executor.ExecuteAsync(db.ConnectionString, "SELECT 1 AS n", BatchMode.ReadOnly, EffectiveLimits.Default, Cancel));

        Assert.Empty(succeeded.RowsAffected);
    }

    [Fact]
    public async Task Error_part_way_through_a_batch()
    {
        // Without XACT_ABORT a duplicate key fails only its own statement and the batch carries on, so the WAITFOR
        // would run. With it, the batch stops at the error.
        await db.ScalarAsync("CREATE TABLE dbo.w_partway (id int PRIMARY KEY)");
        var started = Stopwatch.GetTimestamp();

        var outcome = await Write("INSERT INTO dbo.w_partway VALUES (1); INSERT INTO dbo.w_partway VALUES (1); WAITFOR DELAY '00:00:05'; INSERT INTO dbo.w_partway VALUES (2)");

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.Sql, failed.Kind);
        Assert.Equal(2627, failed.ErrorNumber);
        Assert.EndsWith("The batch was rolled back, so nothing was committed.", failed.Message, StringComparison.Ordinal);
        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(4));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_partway"));
    }

    [Fact]
    public async Task A_conversion_error_rolls_back_the_earlier_insert()
    {
        await db.ScalarAsync("CREATE TABLE dbo.w_convert (id int)");

        var outcome = await Write("INSERT INTO dbo.w_convert VALUES (1); INSERT INTO dbo.w_convert VALUES ('not a number')");

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.Sql, failed.Kind);
        Assert.Contains("nothing was committed", failed.Message, StringComparison.Ordinal);
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_convert"));
    }

    [Fact]
    public async Task Timeout()
    {
        await db.ScalarAsync("CREATE TABLE dbo.w_timeout (id int)");

        var outcome = await Write(
            "INSERT INTO dbo.w_timeout VALUES (1); WAITFOR DELAY '00:00:10'; INSERT INTO dbo.w_timeout VALUES (2)",
            EffectiveLimits.Default with { TimeoutSeconds = 1 });

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.Timeout, failed.Kind);
        Assert.Contains("timed out after 1 seconds", failed.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was committed", failed.Message, StringComparison.Ordinal);
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_timeout"));
    }

    [Fact]
    public async Task Large_output()
    {
        // 1500 rows through OUTPUT under a 1000-row cap: the result stops at 1000, but the insert runs in full.
        await db.ScalarAsync("CREATE TABLE dbo.w_output_large (n int)");

        var outcome = await Write(
            "INSERT INTO dbo.w_output_large OUTPUT inserted.n SELECT TOP (1500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects a CROSS JOIN sys.all_objects b",
            EffectiveLimits.Default with { MaxAffectedRows = 2000 });

        var succeeded = Assert.IsType<QuerySucceeded>(outcome);
        Assert.Equal(1000, succeeded.Result.RowCount);
        Assert.True(succeeded.Result.Truncated);
        Assert.Contains("1000-row cap", succeeded.Result.TruncationMessage, StringComparison.Ordinal);
        Assert.Contains("the batch still ran to completion", succeeded.Result.TruncationMessage, StringComparison.Ordinal);
        Assert.Equal([1500], succeeded.RowsAffected);
        Assert.Equal(1500, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_output_large"));
    }

    [Fact]
    public async Task A_byte_capped_result_is_drained_through_later_statements()
    {
        // The byte cap is hit in the first OUTPUT; the second statement's result set is skipped, but it still runs.
        await db.ScalarAsync("CREATE TABLE dbo.w_output_bytes (filler varchar(1000))");

        var outcome = await Write(
            "INSERT INTO dbo.w_output_bytes OUTPUT inserted.filler SELECT TOP (100) REPLICATE('x', 1000) FROM sys.all_objects; "
            + "INSERT INTO dbo.w_output_bytes OUTPUT inserted.filler VALUES ('last')",
            EffectiveLimits.Default with { MaxBytes = 10 * 1024 });

        var succeeded = Assert.IsType<QuerySucceeded>(outcome);
        Assert.Equal(10, succeeded.Result.RowCount);
        Assert.Single(succeeded.Result.ResultSets);
        Assert.True(succeeded.Result.Truncated);
        Assert.Equal([100, 1], succeeded.RowsAffected);
        Assert.Equal(101, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_output_bytes"));
    }

    [Fact]
    public async Task Over_the_cap()
    {
        await db.ScalarAsync("CREATE TABLE dbo.w_over (n int, touched bit DEFAULT 0); INSERT INTO dbo.w_over (n) SELECT TOP (5000) 1 FROM sys.all_objects a CROSS JOIN sys.all_objects b");

        var outcome = await Write("UPDATE dbo.w_over SET touched = 1 WHERE n = 1");

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.AffectedRowsCap, failed.Kind);
        Assert.Equal(1000, failed.Cap);
        Assert.Equal(5000, failed.Affected);
        Assert.Equal(
            "over the affected-rows cap: the statements that completed affected 5000 rows, over this database's cap of 1000, so it was stopped. The batch was rolled back, so nothing was committed. Narrow the WHERE clause, or split the change into smaller batches.",
            failed.Message);
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_over WHERE touched = 1"));
    }

    [Fact]
    public async Task Within_the_cap()
    {
        await db.ScalarAsync("CREATE TABLE dbo.w_within (n int, touched bit DEFAULT 0); INSERT INTO dbo.w_within (n) SELECT TOP (999) 1 FROM sys.all_objects a CROSS JOIN sys.all_objects b");

        var outcome = await Write("UPDATE dbo.w_within SET touched = 1 WHERE n = 1");

        Assert.Equal([999], Assert.IsType<QuerySucceeded>(outcome).RowsAffected);
        Assert.Equal(999, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_within WHERE touched = 1"));
    }

    [Fact]
    public async Task The_statement_that_passes_the_cap_rolls_back_the_ones_before_it()
    {
        // 6 rows, then 6 more under a cap of 10: the second insert passes it, and the first is undone with it.
        await db.ScalarAsync("CREATE TABLE dbo.w_cap_batch (n int)");
        const string six = "SELECT TOP (6) 1 FROM sys.all_objects";

        var outcome = await Write(
            $"INSERT INTO dbo.w_cap_batch {six}; INSERT INTO dbo.w_cap_batch {six}",
            EffectiveLimits.Default with { MaxAffectedRows = 10 });

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.AffectedRowsCap, failed.Kind);
        Assert.Equal(10, failed.Cap);
        Assert.Equal(12, failed.Affected);
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_cap_batch"));
    }

    [Fact]
    public async Task Once_the_counts_reach_the_client_the_cap_stops_the_batch()
    {
        // The server holds small output back until a packet fills or the batch ends, so the counts usually arrive too
        // late to stop anything. RAISERROR WITH NOWAIT (below the classifier, as in any procedure) flushes them, and
        // the cancel then stops the batch before the WAITFOR and the last insert.
        await db.ScalarAsync("CREATE TABLE dbo.w_cap_stop (n int)");
        const string six = "SELECT TOP (6) 1 FROM sys.all_objects";
        var started = Stopwatch.GetTimestamp();

        var outcome = await Write(
            $"INSERT INTO dbo.w_cap_stop {six}; INSERT INTO dbo.w_cap_stop {six}; RAISERROR('flush', 0, 1) WITH NOWAIT; WAITFOR DELAY '00:00:05'; INSERT INTO dbo.w_cap_stop VALUES (2)",
            EffectiveLimits.Default with { MaxAffectedRows = 10 });

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.AffectedRowsCap, failed.Kind);
        Assert.Equal(12, failed.Affected);
        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(4));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_cap_stop"));
    }

    [Fact]
    public async Task Output_clause()
    {
        await db.ScalarAsync("CREATE TABLE dbo.w_jobs (id int, done bit); INSERT INTO dbo.w_jobs VALUES (1, 1), (2, 0), (3, 1), (4, 1)");

        var outcome = await Write("DELETE FROM dbo.w_jobs OUTPUT deleted.id WHERE done = 1");

        var succeeded = Assert.IsType<QuerySucceeded>(outcome);
        Assert.Equal([3], succeeded.RowsAffected);
        var set = Assert.Single(succeeded.Result.ResultSets);
        Assert.Equal([1, 3, 4], set.Rows.Select(r => r[0]!.GetValue<int>()).Order());
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_jobs"));
    }

    [Fact]
    public async Task Procedure_result_set()
    {
        // The procedure is set up here, outside the executor, as an administrator would create it.
        await db.ScalarAsync("CREATE TABLE dbo.w_orders (id int, placed date, archived bit DEFAULT 0); INSERT INTO dbo.w_orders (id, placed) VALUES (1, '2025-06-01'), (2, '2025-12-01'), (3, '2026-03-01')");
        await db.ScalarAsync("""
            CREATE PROCEDURE dbo.archive_orders @before date AS
            BEGIN
                UPDATE dbo.w_orders SET archived = 1 WHERE placed < @before;
                SELECT id FROM dbo.w_orders WHERE archived = 1 ORDER BY id;
            END
            """);

        var outcome = await Write("EXEC dbo.archive_orders @before = '2026-01-01'");

        var succeeded = Assert.IsType<QuerySucceeded>(outcome);
        var set = Assert.Single(succeeded.Result.ResultSets);
        Assert.Equal(["id"], set.Columns.Select(c => c.Name));
        Assert.Equal([1, 2], set.Rows.Select(r => r[0]!.GetValue<int>()));
        // The SELECT reports its count like the UPDATE does, and both count (Emmz's ruling, 2026-10-26).
        Assert.Equal([2, 2], succeeded.RowsAffected);
        Assert.Equal(2, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_orders WHERE archived = 1"));
    }

    [Fact]
    public async Task A_procedures_select_counts_towards_the_cap()
    {
        await db.ScalarAsync("CREATE TABLE dbo.w_select_cap (n int, touched bit DEFAULT 0); INSERT INTO dbo.w_select_cap (n) SELECT TOP (600) 1 FROM sys.all_objects a CROSS JOIN sys.all_objects b");
        await db.ScalarAsync("""
            CREATE PROCEDURE dbo.touch_and_list AS
            BEGIN
                UPDATE dbo.w_select_cap SET touched = 1;
                SELECT n FROM dbo.w_select_cap WHERE touched = 1;
            END
            """);

        var outcome = await Write("EXEC dbo.touch_and_list");

        var failed = Assert.IsType<QueryFailed>(outcome);
        Assert.Equal(FailureKind.AffectedRowsCap, failed.Kind);
        Assert.Equal(1200, failed.Affected);
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.w_select_cap WHERE touched = 1"));
    }

    private Task<QueryOutcome> Write(string sql, EffectiveLimits? limits = null) =>
        _executor.ExecuteAsync(db.ConnectionString, sql, BatchMode.Write, limits ?? EffectiveLimits.Default, Cancel);
}
