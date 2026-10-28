using Microsoft.Data.SqlClient;
using SqlMcp.Audit;
using SqlMcp.Introspection;
using SqlMcp.Introspection.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Introspection;

// Spec: catalog query times out. A transaction holding uncommitted DDL keeps a schema-modification lock on its table,
// so a catalog read of that table waits on it until the database's command timeout cancels the read.
public sealed class CatalogTimeoutTests(SqlServerFixture server)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Catalog_query_times_out()
    {
        var connectionString = await server.CreateDatabaseAsync($"timeout_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(connectionString, "CREATE TABLE dbo.orders (id int PRIMARY KEY);");

        await using var blocker = new SqlConnection(connectionString);
        await blocker.OpenAsync(Cancel);
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(Cancel);
        await using (var alter = new SqlCommand("ALTER TABLE dbo.orders ADD note int NULL;", blocker, transaction))
        {
            await alter.ExecuteNonQueryAsync(Cancel);
        }

        var outcome = await new SqlServerCatalogReader(TimeProvider.System).ListAsync(
            connectionString, new ObjectFilter(null, [], null), EffectiveLimits.Default with { TimeoutSeconds = 1 }, Cancel);

        var failed = Assert.IsType<CatalogFailed<ObjectList>>(outcome);
        Assert.Equal(FailureKind.Timeout, failed.Kind);
        Assert.Equal(-2, failed.ErrorNumber);
        Assert.Equal(
            "reading the catalog timed out after 1 seconds (this database's command timeout), so it was cancelled. Ask for less: filter by schema, type or name.",
            failed.Message);

        await transaction.RollbackAsync(Cancel);
    }
}
