using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlMcp.Audit;
using SqlMcp.Permissions;

namespace SqlMcp.Execution.SqlServer;

/// <summary>
/// Runs a query batch on SQL Server (design D7). The batch runs in a ReadCommitted transaction that is always rolled
/// back. Rows from every result set are buffered up to the row and byte caps, and the connection is closed before
/// the outcome is returned, so no locks, sessions or changes outlive the call.
/// </summary>
internal sealed class SqlServerQueryExecutor(TimeProvider time) : IQueryExecutor
{
    public const string ApplicationName = "sql-mcp";

    /// <summary>SqlClient reports a command timeout as error number -2.</summary>
    private const int TimeoutErrorNumber = -2;

    public async Task<QueryOutcome> ExecuteAsync(string connectionString, string sql, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        TimeSpan Elapsed() => time.GetElapsedTime(started);

        string effectiveConnectionString;
        try
        {
            effectiveConnectionString = new SqlConnectionStringBuilder(connectionString)
            {
                ApplicationName = ApplicationName,

                // Without pooling, closing the connection ends the server session, so nothing outlives the call
                // (spec: connection released before paging), even if a rollback below fails.
                Pooling = false,
            }.ConnectionString;
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

        SqlTransaction? transaction = null;
        try
        {
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = limits.TimeoutSeconds };
            var result = await BufferAsync(command, limits, cancellationToken).ConfigureAwait(false);
            return new QuerySucceeded(result, Elapsed());
        }
        catch (SqlException ex) when (ex.Number == TimeoutErrorNumber)
        {
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"query timed out after {limits.TimeoutSeconds} seconds (this database's command timeout), so it was cancelled and rolled back. Narrow the query: add filters, select fewer columns, or use TOP.");
            return Failed(FailureKind.Timeout, message, ex, Elapsed());
        }
        catch (SqlException ex)
        {
            return Failed(FailureKind.Sql, ex.Message, ex, Elapsed());
        }
        finally
        {
            if (transaction is not null)
            {
                await RollBackAsync(transaction).ConfigureAwait(false);
            }
        }
    }

    private static async Task<BufferedResult> BufferAsync(SqlCommand command, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        var sets = new List<ResultSet>();
        var rowCount = 0;
        var byteCount = 0L;
        string? truncation = null;

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }

                var columns = SqlServerValues.Describe(reader);
                var rows = new List<JsonNode?[]>();
                sets.Add(new ResultSet(columns.Select(c => c.Column).ToArray(), rows));

                while (truncation is null && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (rowCount >= limits.MaxRows)
                    {
                        truncation = TruncationMessage($"the {limits.MaxRows.ToString(CultureInfo.InvariantCulture)}-row cap", rowCount, sets.Count - 1);
                        break;
                    }

                    var row = SqlServerValues.ReadRow(reader, columns);
                    var rowBytes = SizeOf(row);
                    if (byteCount + rowBytes > limits.MaxBytes)
                    {
                        truncation = TruncationMessage($"the {ByteSize.Format(limits.MaxBytes)} cap", rowCount, sets.Count - 1);
                        break;
                    }

                    rows.Add(row);
                    rowCount++;
                    byteCount += rowBytes;
                }
            }
            while (truncation is null && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await CloseReaderAsync(command, reader, cancelled: truncation is not null).ConfigureAwait(false);
        }

        return new BufferedResult(sets, truncation is not null, truncation);
    }

    /// <summary>
    /// Closes the reader. After a cap, the command is cancelled first, so the server stops sending rows and later
    /// result sets are never read (2026-10-25 decision); the cancellation error that closing then raises is expected.
    /// </summary>
    private static async Task CloseReaderAsync(SqlCommand command, SqlDataReader reader, bool cancelled)
    {
        if (!cancelled)
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            return;
        }

        command.Cancel();
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // The server acknowledges the cancel with "operation cancelled by user".
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

    /// <summary>The UTF-8 size of the row's values as JSON. This is what the byte cap counts.</summary>
    private static long SizeOf(JsonNode?[] row)
    {
        var size = 0L;
        foreach (var value in row)
        {
            size += value is null ? 4 : Encoding.UTF8.GetByteCount(value.ToJsonString());
        }

        return size;
    }

    private static string TruncationMessage(string cap, int rows, int resultSet) => string.Create(
        CultureInfo.InvariantCulture,
        $"result truncated: {cap} was reached after {rows} rows, in result set {resultSet}, and nothing after it was read. Narrow the query: add filters, select fewer columns, or use TOP.");

    private static QueryFailed Failed(FailureKind kind, string message, SqlException ex, TimeSpan duration) =>
        new(kind, message, ex.Number, ex.Class, ex.State, duration);
}
