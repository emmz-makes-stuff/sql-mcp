using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;

namespace SqlMcp.IntegrationTests.Introspection;

// The ADR-0003 gate (design I1): SMO's Scripter, on this machine and against SQL Server 2025, scripts the object
// types we were least sure of, ordered by DependencyWalker as the script generator will order them.
public sealed class SmoSpikeTests(SqlServerFixture db)
{
    [Fact]
    public async Task Scripter_handles_the_awkward_object_types()
    {
        await db.ScalarAsync("CREATE SCHEMA spike");
        await db.ScalarAsync("CREATE TYPE spike.phone FROM varchar(20) NOT NULL");
        await db.ScalarAsync("CREATE TYPE spike.id_list AS TABLE (id int PRIMARY KEY)");
        await db.ScalarAsync("""
            CREATE TABLE spike.temporal_orders (
                id int PRIMARY KEY,
                phone spike.phone,
                valid_from datetime2 GENERATED ALWAYS AS ROW START NOT NULL,
                valid_to datetime2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (valid_from, valid_to))
            WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = spike.temporal_orders_history))
            """);
        await db.ScalarAsync("CREATE PARTITION FUNCTION pf_spike (int) AS RANGE RIGHT FOR VALUES (100, 200)");
        await db.ScalarAsync("CREATE PARTITION SCHEME ps_spike AS PARTITION pf_spike ALL TO ([PRIMARY])");
        await db.ScalarAsync("CREATE TABLE spike.partitioned_events (id int NOT NULL, CONSTRAINT pk_events PRIMARY KEY (id)) ON ps_spike (id)");

        var script = Script(server =>
        {
            var database = server.Databases[new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog];
            return
            [
                database.UserDefinedDataTypes["phone", "spike"].Urn,
                database.UserDefinedTableTypes["id_list", "spike"].Urn,
                database.Tables["temporal_orders", "spike"].Urn,
                database.Tables["partitioned_events", "spike"].Urn,
            ];
        });

        Assert.Contains("CREATE TYPE [spike].[phone] FROM [varchar](20) NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE [spike].[id_list] AS TABLE", script, StringComparison.Ordinal);
        Assert.Contains("SYSTEM_VERSIONING = ON", script, StringComparison.Ordinal);
        Assert.Contains("CREATE PARTITION FUNCTION [pf_spike]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE PARTITION SCHEME [ps_spike]", script, StringComparison.Ordinal);
        Assert.Contains("ON [ps_spike]([id])", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("CREATE TYPE [spike].[phone]", StringComparison.Ordinal) < script.IndexOf("CREATE TABLE [spike].[temporal_orders]", StringComparison.Ordinal),
            "The UDT must be created before the table that uses it.");
        Assert.True(
            script.IndexOf("CREATE PARTITION SCHEME", StringComparison.Ordinal) < script.IndexOf("CREATE TABLE [spike].[partitioned_events]", StringComparison.Ordinal),
            "The partition scheme must be created before the table stored on it.");
    }

    private string Script(Func<Server, Urn[]> pick)
    {
        using var connection = new SqlConnection(db.ConnectionString);
        var server = new Server(new ServerConnection(connection));
        var scripter = new Scripter(server)
        {
            Options =
            {
                SchemaQualify = true,
                DriAll = true,
                Indexes = true,
                Triggers = true,
                ScriptBatchTerminator = true,
                WithDependencies = false,
            },
        };

        var tree = scripter.DiscoverDependencies(pick(server), DependencyType.Parents);
        var ordered = scripter.WalkDependencies(tree);
        return string.Join(Environment.NewLine, scripter.ScriptWithList(ordered).Cast<string>());
    }
}
