using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlMcp.Audit;
using SqlMcp.Execution.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.Introspection.SqlServer;

/// <summary>
/// Reads SQL Server's catalog views (design I4). Every query is fixed and parameterised, runs on its own unpooled
/// connection with the database's command timeout, and outside any transaction: nothing here writes.
/// </summary>
internal sealed class SqlServerCatalogReader(TimeProvider time) : ICatalogReader
{
    /// <summary>SqlClient reports a command timeout as error number -2.</summary>
    private const int TimeoutErrorNumber = -2;

    /// <summary>
    /// Every user object as one row set: schema-scoped objects from <c>sys.objects</c>, user types from
    /// <c>sys.types</c>, and database triggers (no schema) from <c>sys.triggers</c>. The type codes map to the
    /// engine-neutral names; CLR procedures, functions, aggregates and triggers count as their T-SQL kind.
    /// </summary>
    private const string ObjectsCte = """
        WITH objects AS (
            SELECT s.name AS schema_name, o.name,
                CASE o.type
                    WHEN 'U' THEN 'table' WHEN 'V' THEN 'view'
                    WHEN 'P' THEN 'procedure' WHEN 'PC' THEN 'procedure'
                    WHEN 'TR' THEN 'trigger' WHEN 'TA' THEN 'trigger'
                    WHEN 'SO' THEN 'sequence' WHEN 'SN' THEN 'synonym'
                    ELSE 'function'
                END AS type,
                CASE WHEN o.type = 'U'
                    THEN (SELECT SUM(p.rows) FROM sys.partitions AS p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1))
                END AS approx_rows
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF', 'TR', 'TA', 'SO', 'SN')
              AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
            UNION ALL
            SELECT s.name, t.name, CASE WHEN t.is_table_type = 1 THEN 'table_type' ELSE 'type' END, NULL
            FROM sys.types AS t
            JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE t.is_user_defined = 1
            UNION ALL
            SELECT NULL, tr.name, 'trigger', NULL
            FROM sys.triggers AS tr
            WHERE tr.parent_class = 0 AND tr.is_ms_shipped = 0
        )
        """;

    /// <summary>
    /// The filters compare through <c>LOWER()</c>, so a case-sensitive database collation still matches without regard
    /// to case. <c>COUNT(*) OVER ()</c> is computed before <c>TOP</c>, so it counts every match.
    /// </summary>
    private const string ListSql = ObjectsCte + """
        SELECT TOP (@cap) schema_name, name, type, approx_rows, COUNT(*) OVER () AS total
        FROM objects
        WHERE (@schema IS NULL OR LOWER(schema_name) = LOWER(@schema))
          AND (@pattern IS NULL OR LOWER(name) LIKE LOWER(@pattern) ESCAPE '\')
          AND (@types IS NULL OR type IN (SELECT value FROM STRING_SPLIT(@types, ',')))
        ORDER BY schema_name, name;
        """;

    public Task<CatalogOutcome<ObjectList>> ListAsync(string connectionString, ObjectFilter filter, EffectiveLimits limits, CancellationToken cancellationToken) =>
        ReadAsync(connectionString, limits, async connection =>
        {
            await using var command = Command(connection, ListSql, limits);
            command.Parameters.Add(new SqlParameter("@cap", System.Data.SqlDbType.Int) { Value = limits.MaxRows });
            command.Parameters.Add(Text("@schema", filter.Schema));
            command.Parameters.Add(Text("@pattern", filter.Pattern is null ? null : Glob.ToLike(filter.Pattern)));
            command.Parameters.Add(Text("@types", filter.Types.Count == 0 ? null : string.Join(",", filter.Types.Select(t => t.ToWireName()))));

            var objects = new List<ObjectSummary>();
            var total = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!ObjectTypes.TryParse(reader.GetString(2), out var type))
                {
                    throw new InvalidOperationException($"The catalog query returned an unknown object type '{reader.GetString(2)}'.");
                }

                objects.Add(new ObjectSummary(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetString(1),
                    type,
                    reader.IsDBNull(3) ? null : reader.GetInt64(3)));
                total = reader.GetInt32(4);
            }

            return new ObjectList(objects, total, total > objects.Count);
        }, cancellationToken);

    /// <summary>
    /// Opens a connection, runs <paramref name="read"/>, and turns SqlClient's failures into outcomes, as the
    /// executor does: an unparseable connection string or a failed open is a connection failure, error -2 a timeout.
    /// </summary>
    private async Task<CatalogOutcome<T>> ReadAsync<T>(
        string connectionString, EffectiveLimits limits, Func<SqlConnection, Task<T>> read, CancellationToken cancellationToken)
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
            var value = await read(connection).ConfigureAwait(false);
            return new CatalogSucceeded<T>(value, Elapsed());
        }
        catch (SqlException ex) when (ex.Number == TimeoutErrorNumber)
        {
            return new CatalogFailed<T>(
                FailureKind.Timeout,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"reading the catalog timed out after {limits.TimeoutSeconds} seconds (this database's command timeout), so it was cancelled. Ask for less: filter by schema, type or name."),
                ex.Number,
                Elapsed());
        }
        catch (SqlException ex)
        {
            return new CatalogFailed<T>(FailureKind.Sql, $"reading the catalog failed: {ex.Message}", ex.Number, Elapsed());
        }
    }

    private static SqlCommand Command(SqlConnection connection, string sql, EffectiveLimits limits) =>
        new(sql, connection) { CommandTimeout = limits.TimeoutSeconds };

    private static SqlParameter Text(string name, string? value) =>
        new(name, System.Data.SqlDbType.NVarChar, 4000) { Value = (object?)value ?? DBNull.Value };
}
