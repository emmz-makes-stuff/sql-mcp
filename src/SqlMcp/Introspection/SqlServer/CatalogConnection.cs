using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlMcp.Audit;
using SqlMcp.Execution.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.Introspection.SqlServer;

/// <summary>
/// How the catalog reader and the script generator connect (code review, 2026-10-28): each call opens its own
/// connection as the executor would, runs its work, and gets SqlClient's failures back as outcomes. An unparseable
/// connection string or a failed open is a connection failure, error -2 a timeout, and any other SQL error a SQL failure.
/// </summary>
/// <param name="time">Times the call, from before the connection opens.</param>
/// <param name="activity">What the work is, for the messages: <c>reading the catalog</c> or <c>scripting</c>.</param>
internal sealed class CatalogConnection(TimeProvider time, string activity)
{
    /// <summary>
    /// Opens a connection and runs <paramref name="work"/> on it. A <see cref="SqlException"/> anywhere in the chain of
    /// what it throws is a timeout or a SQL failure; SMO wraps them. <paramref name="classify"/> turns the caller's own
    /// exceptions into a failure, and anything else is rethrown.
    /// </summary>
    public async Task<CatalogOutcome<T>> RunAsync<T>(
        string connectionString,
        EffectiveLimits limits,
        Func<SqlConnection, Task<T>> work,
        CancellationToken cancellationToken,
        string timeoutAdvice = "",
        Func<Exception, (FailureKind Kind, string Message)?>? classify = null)
    {
        var started = time.GetTimestamp();
        TimeSpan Elapsed() => time.GetElapsedTime(started);

        string effective;
        try
        {
            effective = SqlServerQueryExecutor.EffectiveConnectionString(connectionString);
        }
        catch (ArgumentException ex)
        {
            return new CatalogFailed<T>(FailureKind.Connection, $"invalid connection string: {ex.Message}", null, Elapsed());
        }

        await using var connection = new SqlConnection(effective);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            return new CatalogFailed<T>(FailureKind.Connection, $"could not connect: {ex.Message}", ex.Number, Elapsed());
        }

        try
        {
            var value = await work(connection).ConfigureAwait(false);
            return new CatalogSucceeded<T>(value, Elapsed());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (Innermost<SqlException>(ex) is { Number: SqlServerQueryExecutor.TimeoutErrorNumber } timeout)
        {
            return new CatalogFailed<T>(
                FailureKind.Timeout,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{activity} timed out after {limits.TimeoutSeconds} seconds (this database's command timeout), so it was cancelled.{timeoutAdvice}"),
                timeout.Number,
                Elapsed());
        }
        catch (Exception ex) when (Innermost<SqlException>(ex) is { } sql)
        {
            return new CatalogFailed<T>(FailureKind.Sql, $"{activity} failed: {sql.Message}", sql.Number, Elapsed());
        }
        catch (Exception ex) when (classify?.Invoke(ex) is { } failure)
        {
            return new CatalogFailed<T>(failure.Kind, failure.Message, null, Elapsed());
        }
    }

    private static T? Innermost<T>(Exception ex)
        where T : Exception
    {
        T? found = null;
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                found = match;
            }
        }

        return found;
    }
}
