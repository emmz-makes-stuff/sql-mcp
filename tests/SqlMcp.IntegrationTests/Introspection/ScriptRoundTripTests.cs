using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlMcp.Configuration;
using SqlMcp.Introspection;
using SqlMcp.Introspection.SqlServer;
using SqlMcp.Mcp;
using SqlMcp.Permissions;

namespace SqlMcp.IntegrationTests.Introspection;

/// <summary>
/// Spec: creation-script, end to end against seed.sql (design I8): <c>script_database</c>'s steps write a real file,
/// the file is run in an empty database, and the two databases must read back the same.
/// </summary>
public sealed partial class ScriptRoundTripTests(SqlServerFixture server, SeededDatabases seeded) : IClassFixture<SeededDatabases>, IDisposable
{
    private readonly SqlServerCatalogReader _reader = new(TimeProvider.System);
    private readonly SmoScriptGenerator _generator = new(TimeProvider.System);
    private readonly string _project = Path.Combine(Path.GetTempPath(), "sql-mcp-itests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private string ScriptsDirectory => Path.Combine(_project, ".sql-mcp", "scripts");

    private string Fallback => Path.Combine(_project, "fallback");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task Script_rebuilds_the_schema()
    {
        var script = await ScriptSeededAsync();
        var copy = await server.CreateDatabaseAsync($"rebuilt_{Guid.NewGuid():N}"[..20]);

        await SqlServerFixture.RunScriptAsync(copy, await File.ReadAllTextAsync(script.Path, Cancel));

        var omitted = script.Omitted.Select(o => (o.Item.Schema, o.Item.Name)).ToHashSet();
        var original = await ListAsync(seeded.ConnectionString);
        var rebuilt = await ListAsync(copy);
        Assert.Equal(original.Where(o => !omitted.Contains((o.Schema, o.Name))), rebuilt);
        Assert.Contains(rebuilt, o => o.Type == ObjectType.Table);
        foreach (var item in rebuilt)
        {
            var name = new ObjectName(item.Schema, item.Name);
            Assert.Equal(await DescribeAsync(seeded.ConnectionString, name), await DescribeAsync(copy, name));
        }
    }

    [Fact]
    public async Task Encrypted_procedure_is_omitted_and_named()
    {
        var script = await ScriptSeededAsync();

        Assert.Contains(new OmittedItem(new CatalogItem("dbo", "secret_report", "procedure"), Completeness.Encrypted), script.Omitted);
        Assert.DoesNotContain("secret_report", await File.ReadAllTextAsync(script.Path, Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_users_are_omitted_and_named()
    {
        var script = await ScriptSeededAsync();

        Assert.Contains(new OmittedItem(new CatalogItem(null, "app_reader", CatalogItemTypes.User), Completeness.SecurityPrincipal), script.Omitted);
        var text = await File.ReadAllTextAsync(script.Path, Cancel);
        Assert.DoesNotContain("app_reader", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE USER", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_file_has_the_header_and_no_connection_string()
    {
        var script = await ScriptSeededAsync();

        var text = await File.ReadAllTextAsync(script.Path, Cancel);
        var identifier = DatabaseIdentity.FromConnectionString(seeded.ConnectionString).Identifier;
        Assert.StartsWith($"-- Schema script for {identifier}, written by sql-mcp 9.9.9-itest.\n-- Server version: 17.", text, StringComparison.Ordinal);
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(seeded.ConnectionString);
        Assert.DoesNotContain(builder.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain($"User ID={builder.UserID}", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new FileInfo(script.Path).Length, script.Bytes);
        Assert.Equal("logs/\nscripts/\n", await File.ReadAllTextAsync(Path.Combine(_project, ".sql-mcp", ".gitignore"), Cancel));
    }

    [Fact]
    public async Task Failed_script_leaves_no_file()
    {
        Directory.CreateDirectory(Path.Combine(_project, ".sql-mcp"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var header = new ScriptHeader("seeded", "17.0", DateTimeOffset.UtcNow, "9.9.9-itest");

        CatalogOutcome<WrittenScript<IReadOnlyList<CatalogItem>>>? outcome = null;
        try
        {
            outcome = await ScriptFiles.WriteAsync(
                _project,
                Fallback,
                header,
                writer => _generator.GenerateAsync(seeded.ConnectionString, null, new CancelAfterFirstBatch(writer, cancellation), EffectiveLimits.Default, cancellation.Token),
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled part way through, as the client would.
        }

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(outcome is null or CatalogFailed<WrittenScript<IReadOnlyList<CatalogItem>>>, $"Scripting went on after it was cancelled: {outcome}");
        Assert.Empty(Directory.Exists(ScriptsDirectory) ? Directory.GetFiles(ScriptsDirectory) : []);
        Assert.False(Directory.Exists(Fallback));
    }

    private async Task<ScriptResult> ScriptSeededAsync()
    {
        Directory.CreateDirectory(Path.Combine(_project, ".sql-mcp"));
        var identifier = DatabaseIdentity.FromConnectionString(seeded.ConnectionString).Identifier;
        var outcome = await DatabaseScripter.ScriptAsync(
            _reader,
            _generator,
            seeded.ConnectionString,
            null,
            new ScriptTarget(_project, Fallback, identifier, "9.9.9-itest", TimeProvider.System),
            EffectiveLimits.Default,
            Cancel);
        var script = Assert.IsType<CatalogSucceeded<ScriptResult>>(outcome).Value;
        Assert.Equal(ScriptsDirectory, Path.GetDirectoryName(script.Path));
        return script;
    }

    /// <summary>Every object's identity, without row counts, which differ between the two databases.</summary>
    private async Task<List<ObjectSummary>> ListAsync(string connectionString)
    {
        var list = Assert.IsType<CatalogSucceeded<ObjectList>>(
            await _reader.ListAsync(connectionString, new ObjectFilter(null, [], null), EffectiveLimits.Default, Cancel)).Value;
        Assert.False(list.Truncated);
        return [.. list.Objects.Select(o => o with { ApproxRows = null })];
    }

    /// <summary>
    /// The details as <c>describe_object</c> returns them, so nested lists compare by value. Two differences no script
    /// can avoid are evened out (Emmz's call, 2026-10-27):
    /// <list type="bullet">
    /// <item>SQL Server doesn't let a table type's constraints be named, so each database generates its own names
    /// for them (such as <c>PK__TT_id_li__3213E83F837891E8</c>). Those names are blanked for table types.</item>
    /// <item>SMO rebuilds a DML trigger's <c>CREATE TRIGGER … AS</c> header from the catalog (bracketed and
    /// schema-qualified) and keeps only the body as written. The header, up to its <c>AS</c>, is dropped.</item>
    /// </list>
    /// </summary>
    private async Task<string> DescribeAsync(string connectionString, ObjectName name)
    {
        var details = Assert.IsType<CatalogSucceeded<ObjectDetails>>(
            await _reader.DescribeAsync(connectionString, name, EffectiveLimits.Default, Cancel)).Value;
        if (details is { Type: ObjectType.Trigger, Schema: not null, Definition: Definition.Text text })
        {
            details = details with { Definition = new Definition.Text(TriggerHeader().Replace(text.Value, "CREATE TRIGGER … AS", 1)) };
        }

        var json = JsonSerializer.Serialize(ObjectDetailsResponse.From(details), ToolResult.Json);
        return details.Type == ObjectType.TableType ? SystemNamed().Replace(json, "\"(system named)\"") : json;
    }

    [GeneratedRegex("\"(PK|UQ|CK|DF)__[^\"]*__[0-9A-F]{16}\"")]
    private static partial Regex SystemNamed();

    [GeneratedRegex(@"\A\s*CREATE\s+TRIGGER\b.*?\bAS\b", RegexOptions.Singleline)]
    private static partial Regex TriggerHeader();

    /// <summary>Passes the script through, and cancels once the first batch has ended.</summary>
    private sealed class CancelAfterFirstBatch(TextWriter inner, CancellationTokenSource cancellation) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            if (value is not null && value.Contains("GO", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        }

        public override Task WriteAsync(string? value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            Write(value);
            Write(inner.NewLine);
            return Task.CompletedTask;
        }
    }
}
