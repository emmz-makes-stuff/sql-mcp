using SqlMcp.Audit;
using SqlMcp.Introspection;
using SqlMcp.Introspection.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Introspection;

// SMO script generation (design I5) against seed.sql: what is scripted, in what order, and how it squares with the
// inventory. The round trip that runs the script is §5.4's.
public sealed class SmoScriptGeneratorTests(SqlServerFixture server, SeededDatabases seeded) : IClassFixture<SeededDatabases>
{
    private readonly SmoScriptGenerator _generator = new(TimeProvider.System);
    private readonly SqlServerCatalogReader _reader = new(TimeProvider.System);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Whole_database_script_holds_every_object_but_the_omitted_ones()
    {
        var (script, scripted) = await GenerateAsync(null);
        var inventory = Assert.IsType<CatalogSucceeded<IReadOnlyList<InventoryItem>>>(
            await _reader.InventoryAsync(seeded.ConnectionString, EffectiveLimits.Default, Cancel)).Value;

        Assert.Equal(
            [
                new OmittedItem(new CatalogItem("dbo", "secret_report", "procedure"), Completeness.Encrypted),
                new OmittedItem(new CatalogItem(null, "app_reader", CatalogItemTypes.User), Completeness.SecurityPrincipal),
            ],
            Completeness.Omitted(inventory, scripted).OrderBy(o => o.Reason, StringComparer.Ordinal));
        Assert.Empty(scripted.Except(inventory.Select(i => i.Item)));
        Assert.Equal(scripted.Count, scripted.Distinct().Count());

        // SMO writes tables and types bracketed; views and routines keep the text they were created with.
        foreach (var item in scripted.Where(i => i.Schema is not null && i.Type != "trigger"))
        {
            Assert.True(
                script.Contains($"[{item.Schema}].[{item.Name}]", StringComparison.Ordinal) || script.Contains($"{item.Schema}.{item.Name}", StringComparison.Ordinal),
                $"The script doesn't name {item.Schema}.{item.Name}.");
        }

        Assert.Contains("CREATE SCHEMA [sales]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE PARTITION SCHEME [ps_events]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER [dbo].[orders_status]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER audit_ddl ON DATABASE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_report", script, StringComparison.Ordinal);
        Assert.DoesNotContain("app_reader", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE DATABASE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("USE [", script, StringComparison.Ordinal);
        Assert.DoesNotContain($"GO{Environment.NewLine}INSERT", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Foreign_keys_come_after_both_their_tables()
    {
        var (script, _) = await GenerateAsync(null);

        // SMO creates every table, then adds the foreign keys, so a key's target always exists when it is added.
        foreach (var (table, key, target) in new[] { ("[dbo].[orders]", "[fk_orders_users]", "[dbo].[users]"), ("[dbo].[order_lines]", "[fk_order_lines_orders]", "[dbo].[orders]") })
        {
            var added = Index(script, $"ALTER TABLE {table}  WITH CHECK ADD  CONSTRAINT {key} FOREIGN KEY");
            Assert.True(Index(script, $"CREATE TABLE {table}") < added, $"{key} must come after {table}");
            Assert.True(Index(script, $"CREATE TABLE {target}") < added, $"{key} must come after {target}");
        }

        Assert.True(Index(script, "CREATE TYPE [dbo].[phone]") < Index(script, "CREATE TABLE [dbo].[users]"), "a type must precede the table that uses it");
        Assert.True(Index(script, "CREATE TABLE [dbo].[orders]") < Index(script, "CREATE VIEW sales.open_orders"), "a table must precede the view on it");
        Assert.True(Index(script, "CREATE SCHEMA [sales]") < Index(script, "CREATE VIEW sales.open_orders"), "schemas must come first");
        Assert.True(Index(script, "CREATE TABLE [dbo].[orders]") < Index(script, "CREATE TRIGGER [dbo].[orders_status]"), "triggers come after their table");
    }

    [Fact]
    public async Task Every_batch_ends_with_GO()
    {
        var (script, _) = await GenerateAsync(null);

        Assert.EndsWith($"{Environment.NewLine}GO{Environment.NewLine}", script, StringComparison.Ordinal);
        Assert.DoesNotContain($"GO{Environment.NewLine}GO{Environment.NewLine}", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_tables()
    {
        var (script, scripted) = await GenerateAsync(
            [new ObjectSummary("dbo", "order_lines", ObjectType.Table, null), new ObjectSummary("dbo", "orders", ObjectType.Table, null)]);

        Assert.Equal(
            [new CatalogItem("dbo", "orders", "table"), new CatalogItem("dbo", "order_lines", "table"), new CatalogItem("dbo", "orders_status", "trigger")],
            scripted);
        Assert.True(Index(script, "CREATE TABLE [dbo].[orders]") < Index(script, "CREATE TABLE [dbo].[order_lines]"), "orders must precede order_lines");
        Assert.Contains("CONSTRAINT [fk_order_lines_orders] FOREIGN KEY", script, StringComparison.Ordinal);
        Assert.Contains("CREATE NONCLUSTERED INDEX [ix_orders_user]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER [dbo].[orders_status]", script, StringComparison.Ordinal);
        Assert.Equal(2, script.Split("CREATE TABLE").Length - 1);
        Assert.DoesNotContain("CREATE TYPE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE SEQUENCE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE SCHEMA", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chosen_encrypted_procedure_is_left_out()
    {
        var (script, scripted) = await GenerateAsync([new ObjectSummary("dbo", "secret_report", ObjectType.Procedure, null)]);

        Assert.Empty(scripted);
        Assert.Empty(script);
    }

    [Fact]
    public async Task Encrypted_trigger_is_left_out_without_failing_its_table_and_other_kinds_match_the_inventory()
    {
        var connectionString = await server.CreateDatabaseAsync($"encrypted_trigger_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(connectionString, """
            CREATE TABLE dbo.accounts (id int NOT NULL PRIMARY KEY);
            GO
            CREATE TRIGGER dbo.accounts_audit ON dbo.accounts WITH ENCRYPTION AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
            GO
            CREATE TRIGGER dbo.accounts_plain ON dbo.accounts AFTER UPDATE AS BEGIN SET NOCOUNT ON; END;
            GO
            CREATE RULE dbo.positive AS @value > 0;
            GO
            CREATE XML SCHEMA COLLECTION dbo.order_xml AS N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="order"/></xs:schema>';
            GO
            """);
        var writer = new StringWriter();

        var outcome = await _generator.GenerateAsync(connectionString, null, writer, EffectiveLimits.Default, Cancel);
        var inventory = Assert.IsType<CatalogSucceeded<IReadOnlyList<InventoryItem>>>(
            await _reader.InventoryAsync(connectionString, EffectiveLimits.Default, Cancel)).Value;

        var scripted = Assert.IsType<CatalogSucceeded<IReadOnlyList<CatalogItem>>>(outcome).Value;
        Assert.Equal(
            [new OmittedItem(new CatalogItem("dbo", "accounts_audit", "trigger"), Completeness.Encrypted)],
            Completeness.Omitted(inventory, scripted));
        Assert.Contains("CREATE TRIGGER [dbo].[accounts_plain]", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encrypted_trigger_on_a_chosen_table_is_named_as_omitted()
    {
        var connectionString = await server.CreateDatabaseAsync($"chosen_trigger_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(connectionString, """
            CREATE TABLE dbo.accounts (id int NOT NULL PRIMARY KEY);
            GO
            CREATE TRIGGER dbo.accounts_audit ON dbo.accounts WITH ENCRYPTION AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
            GO
            CREATE TRIGGER dbo.accounts_plain ON dbo.accounts AFTER UPDATE AS BEGIN SET NOCOUNT ON; END;
            GO
            CREATE TABLE dbo.users (id int NOT NULL PRIMARY KEY);
            GO
            CREATE TRIGGER dbo.users_audit ON dbo.users WITH ENCRYPTION AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
            GO
            """);
        var resolution = await ResolveAsync(connectionString, new ObjectName("dbo", "accounts"));

        var outcome = await _generator.GenerateAsync(connectionString, resolution.Objects, new StringWriter(), EffectiveLimits.Default, Cancel);

        var scripted = Assert.IsType<CatalogSucceeded<IReadOnlyList<CatalogItem>>>(outcome).Value;
        Assert.Equal([new CatalogItem("dbo", "accounts", "table"), new CatalogItem("dbo", "accounts_plain", "trigger")], scripted);
        Assert.Equal(
            [new OmittedItem(new CatalogItem("dbo", "accounts_audit", "trigger"), Completeness.Encrypted)],
            Completeness.Omitted(resolution.Inventory, scripted));
    }

    [Fact]
    public async Task A_chosen_trigger_is_found_through_its_table()
    {
        var resolution = await ResolveAsync(seeded.ConnectionString, new ObjectName("dbo", "orders_status"));
        var (script, scripted) = await GenerateAsync(resolution.Objects);

        Assert.Equal(new TriggerParent("dbo", "orders", ObjectType.Table), Assert.Single(resolution.Objects).Parent);
        Assert.Equal([new CatalogItem("dbo", "orders_status", "trigger")], scripted);
        Assert.Contains("CREATE TRIGGER [dbo].[orders_status]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", script, StringComparison.Ordinal);
        Assert.Empty(Completeness.Omitted(resolution.Inventory, scripted));
    }

    [Fact]
    public async Task A_dml_trigger_without_its_parent_is_an_error()
    {
        var outcome = await _generator.GenerateAsync(
            seeded.ConnectionString, [new ObjectSummary("dbo", "orders_status", ObjectType.Trigger, null)], new StringWriter(), EffectiveLimits.Default, Cancel);

        var failed = Assert.IsType<CatalogFailed<IReadOnlyList<CatalogItem>>>(outcome);
        Assert.Equal(FailureKind.Other, failed.Kind);
        Assert.Equal(
            "scripting failed: 'dbo.orders_status' (trigger) is on a table or view that sql-mcp doesn't list, such as one marked as shipped by Microsoft, so it can't be scripted.",
            failed.Message);
    }

    [Fact]
    public async Task A_trigger_chosen_with_its_table_is_scripted_once()
    {
        var connectionString = await server.CreateDatabaseAsync($"trigger_twice_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(connectionString, """
            CREATE TABLE dbo.accounts (id int NOT NULL PRIMARY KEY);
            GO
            CREATE TRIGGER dbo.accounts_audit ON dbo.accounts AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
            GO
            """);
        var resolution = await ResolveAsync(connectionString, new ObjectName("dbo", "accounts"), new ObjectName("dbo", "accounts_audit"));
        var writer = new StringWriter();

        var outcome = await _generator.GenerateAsync(connectionString, resolution.Objects, writer, EffectiveLimits.Default, Cancel);

        Assert.Equal(
            [new CatalogItem("dbo", "accounts", "table"), new CatalogItem("dbo", "accounts_audit", "trigger")],
            Assert.IsType<CatalogSucceeded<IReadOnlyList<CatalogItem>>>(outcome).Value);
        Assert.Equal(1, writer.ToString().Split("CREATE TRIGGER").Length - 1);
        var copy = await server.CreateDatabaseAsync($"trigger_twice_copy_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(copy, writer.ToString());
    }

    [Fact]
    public async Task A_chosen_trigger_on_an_encrypted_view_goes_with_the_view()
    {
        var connectionString = await server.CreateDatabaseAsync($"view_trigger_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(connectionString, """
            CREATE TABLE dbo.accounts (id int NOT NULL PRIMARY KEY);
            GO
            CREATE VIEW dbo.secret_accounts WITH ENCRYPTION AS SELECT id FROM dbo.accounts;
            GO
            CREATE TRIGGER dbo.secret_accounts_insert ON dbo.secret_accounts INSTEAD OF INSERT AS BEGIN SET NOCOUNT ON; END;
            GO
            """);
        var resolution = await ResolveAsync(connectionString, new ObjectName("dbo", "secret_accounts_insert"));
        var writer = new StringWriter();

        var outcome = await _generator.GenerateAsync(connectionString, resolution.Objects, writer, EffectiveLimits.Default, Cancel);

        var scripted = Assert.IsType<CatalogSucceeded<IReadOnlyList<CatalogItem>>>(outcome).Value;
        Assert.Empty(scripted);
        Assert.Empty(writer.ToString());
        Assert.Equal(
            [new OmittedItem(new CatalogItem("dbo", "secret_accounts_insert", "trigger"), Completeness.OnEncryptedView)],
            Completeness.Omitted(resolution.Inventory, scripted));
    }

    [Fact]
    public async Task A_chosen_encrypted_view_takes_its_triggers_with_it()
    {
        var connectionString = await server.CreateDatabaseAsync($"chosen_view_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(connectionString, """
            CREATE TABLE dbo.accounts (id int NOT NULL PRIMARY KEY);
            GO
            CREATE VIEW dbo.secret_accounts WITH ENCRYPTION AS SELECT id FROM dbo.accounts;
            GO
            CREATE TRIGGER dbo.secret_accounts_insert ON dbo.secret_accounts INSTEAD OF INSERT AS BEGIN SET NOCOUNT ON; END;
            GO
            """);
        var resolution = await ResolveAsync(connectionString, new ObjectName("dbo", "secret_accounts"));

        var outcome = await _generator.GenerateAsync(connectionString, resolution.Objects, new StringWriter(), EffectiveLimits.Default, Cancel);

        Assert.Equal(
            [
                new OmittedItem(new CatalogItem("dbo", "secret_accounts_insert", "trigger"), Completeness.OnEncryptedView),
                new OmittedItem(new CatalogItem("dbo", "secret_accounts", "view"), Completeness.Encrypted),
            ],
            Completeness.Omitted(resolution.Inventory, Assert.IsType<CatalogSucceeded<IReadOnlyList<CatalogItem>>>(outcome).Value));
    }

    [Fact]
    public async Task Triggers_of_an_encrypted_view_go_with_it()
    {
        var connectionString = await server.CreateDatabaseAsync($"encrypted_view_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(connectionString, """
            CREATE TABLE dbo.accounts (id int NOT NULL PRIMARY KEY);
            GO
            CREATE VIEW dbo.secret_accounts WITH ENCRYPTION AS SELECT id FROM dbo.accounts;
            GO
            CREATE TRIGGER dbo.secret_accounts_insert ON dbo.secret_accounts INSTEAD OF INSERT AS BEGIN SET NOCOUNT ON; END;
            GO
            """);
        var writer = new StringWriter();

        var outcome = await _generator.GenerateAsync(connectionString, null, writer, EffectiveLimits.Default, Cancel);
        var inventory = Assert.IsType<CatalogSucceeded<IReadOnlyList<InventoryItem>>>(
            await _reader.InventoryAsync(connectionString, EffectiveLimits.Default, Cancel)).Value;

        var scripted = Assert.IsType<CatalogSucceeded<IReadOnlyList<CatalogItem>>>(outcome).Value;
        Assert.Equal(
            [
                new OmittedItem(new CatalogItem("dbo", "secret_accounts", "view"), Completeness.Encrypted),
                new OmittedItem(new CatalogItem("dbo", "secret_accounts_insert", "trigger"), Completeness.OnEncryptedView),
            ],
            Completeness.Omitted(inventory, scripted).OrderBy(o => o.Item.Type, StringComparer.Ordinal).Reverse());
        Assert.DoesNotContain("secret_accounts", writer.ToString(), StringComparison.Ordinal);
        var copy = await server.CreateDatabaseAsync($"encrypted_view_copy_{Guid.NewGuid().ToString("N")[..8]}");
        await SqlServerFixture.RunScriptAsync(copy, writer.ToString());
    }

    [Fact]
    public async Task A_chosen_table_type_that_shares_a_tables_name()
    {
        var (script, scripted) = await GenerateAsync([new ObjectSummary("crm", "address", ObjectType.TableType, null)]);

        Assert.Equal([new CatalogItem("crm", "address", "table_type")], scripted);
        Assert.Contains("CREATE TYPE [crm].[address] AS TABLE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_part_way_through()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var writer = new CancellingWriter(cancel, afterLines: 5);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _generator.GenerateAsync(seeded.ConnectionString, null, writer, EffectiveLimits.Default, cancel.Token));
    }

    [Fact]
    public async Task Unreachable_server_is_a_connection_failure()
    {
        var outcome = await _generator.GenerateAsync(
            "Server=tcp:127.0.0.1,1;Database=x;Connect Timeout=2", null, new StringWriter(), EffectiveLimits.Default, Cancel);

        Assert.Equal(FailureKind.Connection, Assert.IsType<CatalogFailed<IReadOnlyList<CatalogItem>>>(outcome).Kind);
    }

    private async Task<(string Script, IReadOnlyList<CatalogItem> Scripted)> GenerateAsync(IReadOnlyList<ObjectSummary>? objects)
    {
        var writer = new StringWriter();
        var outcome = await _generator.GenerateAsync(seeded.ConnectionString, objects, writer, EffectiveLimits.Default, Cancel);
        return (writer.ToString(), Assert.IsType<CatalogSucceeded<IReadOnlyList<CatalogItem>>>(outcome).Value);
    }

    private async Task<Resolution> ResolveAsync(string connectionString, params ObjectName[] names) =>
        Assert.IsType<CatalogSucceeded<Resolution>>(await _reader.ResolveAsync(connectionString, names, EffectiveLimits.Default, Cancel)).Value;

    private static int Index(string script, string text)
    {
        var at = script.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The script has no '{text}'.");
        return at;
    }

    /// <summary>Cancels the call once the script reaches a given line, as a client giving up part way would.</summary>
    private sealed class CancellingWriter(CancellationTokenSource cancel, int afterLines) : StringWriter
    {
        private int _lines;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (++_lines == afterLines)
            {
                cancel.Cancel();
            }
        }
    }
}
