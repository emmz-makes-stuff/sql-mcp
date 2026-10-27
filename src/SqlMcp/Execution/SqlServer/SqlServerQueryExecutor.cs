using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlMcp.Audit;
using SqlMcp.Permissions;

namespace SqlMcp.Execution.SqlServer;

/// <summary>
/// Runs a batch on SQL Server (design D7, W1). The batch runs in a ReadCommitted transaction. A read-only batch is
/// always rolled back; a write batch runs with <c>XACT_ABORT</c> on and is committed only if every statement
/// succeeds and the affected-rows cap holds. Rows from every result set are buffered up to the row and byte caps,
/// and the connection is closed before the outcome is returned, so no locks, sessions or uncommitted changes outlive
/// the call.
/// </summary>
internal sealed class SqlServerQueryExecutor(TimeProvider time) : IQueryExecutor
{
    public const string ApplicationName = "sql-mcp";

    /// <summary>SqlClient reports a command timeout as error number -2.</summary>
    private const int TimeoutErrorNumber = -2;

    private const string NothingCommitted = WriteMessages.NothingCommitted;

    private const string CheckBeforeRerun = WriteMessages.CheckBeforeRerun;

    private const string EndedTransaction =
        "a procedure ended sql-mcp's transaction with its own COMMIT or ROLLBACK, so some of the batch may have been committed, and sql-mcp can't undo it";

    /// <summary>
    /// 266: the transaction count changed across an EXEC. 3902 and 3903: a COMMIT or ROLLBACK with no transaction
    /// left. Each means the batch's own statements ended sql-mcp's transaction (design Risks).
    /// </summary>
    private static readonly HashSet<int> TransactionEndedErrors = [266, 3902, 3903];

    /// <summary>
    /// The configured connection string as sql-mcp connects with it, for the executor and the catalog reader alike.
    /// Throws <see cref="ArgumentException"/> if it can't be parsed.
    /// </summary>
    public static string EffectiveConnectionString(string connectionString) => new SqlConnectionStringBuilder(connectionString)
    {
        ApplicationName = ApplicationName,

        // Without pooling, closing the connection ends the server session, so nothing outlives the call
        // (spec: connection released before paging), even if a rollback below fails.
        Pooling = false,
    }.ConnectionString;

    public async Task<QueryOutcome> ExecuteAsync(string connectionString, string sql, BatchMode mode, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        var write = mode == BatchMode.Write;
        var started = time.GetTimestamp();
        TimeSpan Elapsed() => time.GetElapsedTime(started);

        string effectiveConnectionString;
        try
        {
            effectiveConnectionString = EffectiveConnectionString(connectionString);
        }
        catch (ArgumentException ex)
        {
            return new QueryFailed(FailureKind.Connection, $"invalid connection string: {ex.Message}", null, null, null, Elapsed());
        }

        await using var connection = new SqlConnection(effectiveConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            return Failed(FailureKind.Connection, $"could not connect: {ex.Message}", ex, Elapsed());
        }

        var rowsAffected = new List<int>();
        var affected = 0L;
        var overCap = false;
        SqlTransaction? transaction = null;
        try
        {
            if (write)
            {
                // A separate command, so the batch can't turn it off (the classifier rejects SET anyway). With it on,
                // any run-time error dooms the transaction and stops the batch, instead of failing one statement (W2).
                await using var abort = new SqlCommand("SET XACT_ABORT ON", connection) { CommandTimeout = limits.TimeoutSeconds };
                await abort.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = limits.TimeoutSeconds };
            if (write)
            {
                // Each completed statement's count, including those inside procedures and triggers unless NOCOUNT
                // hides them. Past the cap, cancel at once so later statements don't run (W4). The cancel must be
                // sent from inside the handler: from another thread it waits on the parser until the batch ends.
                // Counts arrive only when the server flushes its output (a full packet, or the end of the batch), so
                // a batch with little output has usually run in full by then. The rollback undoes it either way.
                command.StatementCompleted += (_, e) =>
                {
                    rowsAffected.Add(e.RecordCount);
                    affected += e.RecordCount;
                    if (!overCap && affected > limits.MaxAffectedRows)
                    {
                        overCap = true;
                        command.Cancel();
                    }
                };
            }

            var result = await BufferAsync(command, mode, limits, cancellationToken).ConfigureAwait(false);

            // The cancel can land after the server has already finished, so the reader may end without an error.
            if (overCap)
            {
                return CapFailed(limits.MaxAffectedRows, affected, Elapsed());
            }

            if (write)
            {
                // Not cancellable: a cancel that landed mid-commit would leave it unknown whether the change was kept,
                // while the call is reported as failed. Once the commit returns, nothing can fail the call (the
                // connection's disposal cleans up the transaction object).
                try
                {
                    await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // SqlClient has seen the server end the transaction: a procedure committed or rolled back itself
                    // and then began another, so the counts matched and no error was raised.
                    return new QueryFailed(FailureKind.OutcomeUnknown, $"{EndedTransaction} ({ex.Message}) {CheckBeforeRerun}", null, null, null, Elapsed());
                }
                catch (SqlException ex)
                {
                    return Failed(FailureKind.OutcomeUnknown, $"the commit failed, so sql-mcp can't tell whether the batch was committed: {ex.Message} {CheckBeforeRerun}", ex, Elapsed());
                }

                transaction = null;
            }

            return new QuerySucceeded(result, Elapsed()) { RowsAffected = rowsAffected };
        }
        catch (SqlException ex) when (write && ex.Errors.Cast<SqlError>().Any(e => TransactionEndedErrors.Contains(e.Number)))
        {
            // Checked first: once the transaction has ended, neither the cap nor an error can roll the batch back.
            return Failed(FailureKind.OutcomeUnknown, $"{EndedTransaction}: {ex.Message} {CheckBeforeRerun}", ex, Elapsed());
        }
        catch (SqlException) when (overCap)
        {
            // The error the cancel raises ("a severe error occurred on the current command").
            return CapFailed(limits.MaxAffectedRows, affected, Elapsed());
        }
        catch (SqlException ex) when (ex.Number == TimeoutErrorNumber)
        {
            var message = write
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"the batch timed out after {limits.TimeoutSeconds} seconds (this database's command timeout), so it was cancelled. {NothingCommitted} Make the change narrower, or split it.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"query timed out after {limits.TimeoutSeconds} seconds (this database's command timeout), so it was cancelled and rolled back. Narrow the query: add filters, select fewer columns, or use TOP.");
            return Failed(FailureKind.Timeout, message, ex, Elapsed());
        }
        catch (SqlException ex)
        {
            return Failed(FailureKind.Sql, write ? $"{ex.Message} {NothingCommitted}" : ex.Message, ex, Elapsed());
        }
        finally
        {
            if (transaction is not null)
            {
                await RollBackAsync(transaction).ConfigureAwait(false);
            }
        }
    }

    private static async Task<BufferedResult> BufferAsync(SqlCommand command, BatchMode mode, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        // A read stops at a cap and cancels the rest. A write must run to completion to commit, so after a cap it keeps
        // reading, without fetching values, until the batch ends (W3).
        var drain = mode == BatchMode.Write;
        var sets = new List<ResultSet>();
        var rowCount = 0;
        var byteCount = 0L;
        string? truncation = null;

        // Sequential access, so a large value is streamed against the byte cap instead of being read in whole first,
        // and the columns of a drained row are never pulled into memory.
        var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        var completed = false;
        try
        {
            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }

                if (truncation is not null)
                {
                    await DrainAsync(reader, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var columns = SqlServerValues.Describe(reader);
                var rows = new List<JsonNode?[]>();
                sets.Add(new ResultSet(columns.Select(c => c.Column).ToArray(), rows));

                while (truncation is null && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (rowCount >= limits.MaxRows)
                    {
                        truncation = TruncationMessage($"the {limits.MaxRows.ToString(CultureInfo.InvariantCulture)}-row cap", rowCount, sets.Count - 1, drain);
                        break;
                    }

                    if (await SqlServerValues.ReadRowAsync(reader, columns, limits.MaxBytes - byteCount, cancellationToken).ConfigureAwait(false) is not { } read)
                    {
                        truncation = TruncationMessage($"the {ByteSize.Format(limits.MaxBytes)} cap", rowCount, sets.Count - 1, drain);
                        break;
                    }

                    rows.Add(read.Row);
                    rowCount++;
                    byteCount += read.Size;
                }

                if (truncation is not null && drain)
                {
                    await DrainAsync(reader, cancellationToken).ConfigureAwait(false);
                }
            }
            while ((truncation is null || drain) && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            completed = true;
        }
        finally
        {
            // After an exception part-way, or a read's cap, the rest of the stream is unread: cancel rather than drain
            // it.
            await CloseReaderAsync(command, reader, cancel: !completed || (truncation is not null && !drain)).ConfigureAwait(false);
        }

        return new BufferedResult(sets, truncation is not null, truncation);
    }

    /// <summary>Reads the rest of the current result set without fetching any values.</summary>
    private static async Task DrainAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }

    /// <summary>
    /// Closes the reader. After a cap or an exception, the command is cancelled first, so the server stops sending rows
    /// and later result sets are never read (2026-10-25 decision); without it, disposing the reader drains the whole
    /// stream. The cancellation error that closing then raises is expected.
    /// </summary>
    private static async Task CloseReaderAsync(SqlCommand command, SqlDataReader reader, bool cancel)
    {
        if (!cancel)
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            return;
        }

        command.Cancel();
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The server acknowledges the cancel with "operation cancelled by user". After an exception the transport
            // may also be gone. Either way the read is over, and an error from closing must not replace the one that
            // ended it.
        }
    }

    /// <summary>
    /// Rolls back unless the server already ended the transaction (some errors abort it). If the rollback itself
    /// fails, the connection is closed without pooling, which ends the session and rolls the transaction back.
    /// </summary>
    private static async Task RollBackAsync(SqlTransaction transaction)
    {
        if (transaction.Connection is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Closing the unpooled connection rolls back on the server.
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string TruncationMessage(string cap, int rows, int resultSet, bool drained) => drained
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"result truncated: {cap} was reached after {rows} rows, in result set {resultSet}. The rest of the results were discarded, and the batch still ran to completion.")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"result truncated: {cap} was reached after {rows} rows, in result set {resultSet}, and nothing after it was read. Narrow the query: add filters, select fewer columns, or use TOP.");

    private static QueryFailed CapFailed(int cap, long affected, TimeSpan duration) => new(
        FailureKind.AffectedRowsCap,
        string.Create(
            CultureInfo.InvariantCulture,
            $"over the affected-rows cap: the statements that completed affected {affected} rows, over this database's cap of {cap}, so it was stopped. {NothingCommitted} Narrow the WHERE clause, or split the change into smaller batches."),
        null,
        null,
        null,
        duration)
    {
        Cap = cap,
        Affected = affected,
    };

    private static QueryFailed Failed(FailureKind kind, string message, SqlException ex, TimeSpan duration) =>
        new(kind, message, ex.Number, ex.Class, ex.State, duration);
}
