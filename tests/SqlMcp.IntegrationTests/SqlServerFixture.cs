using Microsoft.Data.SqlClient;
using SqlMcp.IntegrationTests;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]

namespace SqlMcp.IntegrationTests;

/// <summary>One SQL Server 2025 container for the whole test assembly (design D11), with an empty test database.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2025-latest";
    private const string Database = "itest";

    private readonly MsSqlContainer _container = new MsSqlBuilder(Image).Build();

    /// <summary>A connection string for the test database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using (var connection = new SqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{Database}]", connection);
            await create.ExecuteNonQueryAsync();
        }

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = Database }.ConnectionString;
    }

    /// <summary>Runs SQL outside the executor, for setup and for checking what the executor left behind.</summary>
    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>
    /// Creates another empty database on the container and returns its connection string, for tests that read or
    /// script a whole database and so can't share <c>itest</c> with the other tests' objects.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name, string? collation = null)
    {
        await using (var connection = new SqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            var collate = collation is null ? string.Empty : $" COLLATE {collation}";
            await using var create = new SqlCommand($"CREATE DATABASE [{name}]{collate}", connection);
            await create.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>Runs a script whose batches are separated by <c>GO</c> lines, as sqlcmd would.</summary>
    public static async Task RunScriptAsync(string connectionString, string script)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in Batches(script))
        {
            await using var command = new SqlCommand(batch, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> Batches(string script)
    {
        var batch = new System.Text.StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            if (string.Equals(line.Trim(), "GO", StringComparison.OrdinalIgnoreCase))
            {
                if (batch.ToString().Trim().Length > 0)
                {
                    yield return batch.ToString();
                }

                batch.Clear();
                continue;
            }

            batch.Append(line).Append('\n');
        }

        if (batch.ToString().Trim().Length > 0)
        {
            yield return batch.ToString();
        }
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
