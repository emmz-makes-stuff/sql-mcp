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

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
