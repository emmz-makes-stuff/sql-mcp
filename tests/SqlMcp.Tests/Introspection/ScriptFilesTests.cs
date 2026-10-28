using SqlMcp.Audit;
using SqlMcp.Configuration;
using SqlMcp.Introspection;

namespace SqlMcp.Tests.Introspection;

/// <summary>Spec: creation-script, "Script is written to a file" and "Script file header" (design I6).</summary>
public sealed class ScriptFilesTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 14, 5, 9, TimeSpan.Zero);

    private static readonly ScriptHeader AppHeader = new("localhost:1433/app", "17.0.1000.7", At, "1.2.3");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string ProjectScripts => Path.Combine(_temp.Path, ".sql-mcp", "scripts");

    private string Fallback => Path.Combine(_temp.Path, "fallback");

    [Fact]
    public void Script_file_name()
    {
        Assert.Equal("localhost_1433_app-20261026T140509Z.sql", ScriptFiles.FileName("localhost:1433/app", At));
        Assert.Equal("localhost_1433_app-20261026T140509Z-2.sql", ScriptFiles.FileName("localhost:1433/app", At, 2));
        Assert.Equal("a_b_c.d-e-20261026T140509Z.sql", ScriptFiles.FileName("a\\b c.d-e", At));
    }

    [Fact]
    public void File_name_uses_the_utc_time()
    {
        Assert.Equal("x-20261026T140509Z.sql", ScriptFiles.FileName("x", At.ToOffset(TimeSpan.FromHours(10))));
    }

    [Fact]
    public void Fallback_directory_sits_beside_the_audit_logs()
    {
        Assert.Equal(Path.Combine("/home/u", ".local", "state", "sql-mcp", "scripts"), ScriptFiles.FallbackDirectory(false, "/home/u", "/unused"));
        Assert.Equal(Path.Combine("C:\\L", "sql-mcp", "scripts"), ScriptFiles.FallbackDirectory(true, "/unused", "C:\\L"));
    }

    [Fact]
    public async Task Script_goes_to_the_project_scripts_directory_with_the_header_first()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".sql-mcp"));

        var written = Succeeded(await WriteAsync("CREATE TABLE t (id int);\nGO\n"));

        Assert.Equal(Path.Combine(ProjectScripts, "localhost_1433_app-20261026T140509Z.sql"), written.Path);
        var text = File.ReadAllText(written.Path);
        Assert.Equal(ScriptFiles.Header(AppHeader) + "CREATE TABLE t (id int);\nGO\n", text);
        Assert.Equal(new FileInfo(written.Path).Length, written.Bytes);
        Assert.Equal(42, written.Value);
        Assert.Equal(["localhost_1433_app-20261026T140509Z.sql"], Files(ProjectScripts));
    }

    [Fact]
    public void Header_names_database_server_version_time_and_sql_mcp_version()
    {
        var lines = ScriptFiles.Header(AppHeader).Split('\n');

        Assert.All(lines.Where(line => line.Length > 0), line => Assert.StartsWith("-- ", line, StringComparison.Ordinal));
        Assert.Contains("localhost:1433/app", lines[0], StringComparison.Ordinal);
        Assert.Contains("1.2.3", lines[0], StringComparison.Ordinal);
        Assert.Equal("-- Server version: 17.0.1000.7", lines[1]);
        Assert.Equal("-- Written at: 2026-10-26T14:05:09Z", lines[2]);
        Assert.EndsWith("\n\n", ScriptFiles.Header(AppHeader), StringComparison.Ordinal);
    }

    [Fact]
    public void Header_keeps_each_value_on_its_comment_line()
    {
        var header = ScriptFiles.Header(AppHeader with { Identifier = "host/db\nDROP TABLE x" });

        Assert.DoesNotContain("\nDROP", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_holds_no_connection_string_fragment()
    {
        const string connectionString = "Server=tcp:db.example.com,1433;Database=app;User ID=svc_writer;Password=Hunter2-secret;Encrypt=True;Application Name=billing-api";
        var identifier = DatabaseIdentity.FromConnectionString(connectionString).Identifier;

        var written = Succeeded(await WriteAsync("SELECT 1;\nGO\n", AppHeader with { Identifier = identifier }));

        var text = File.ReadAllText(written.Path);
        Assert.Contains(identifier, text, StringComparison.Ordinal);
        foreach (var fragment in new[] { "svc_writer", "Hunter2-secret", "Password", "User ID", "Encrypt", "billing-api", "tcp:" })
        {
            Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fragment, written.Path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Collision_adds_a_number_and_overwrites_nothing()
    {
        Directory.CreateDirectory(ProjectScripts);
        File.WriteAllText(Path.Combine(ProjectScripts, "localhost_1433_app-20261026T140509Z.sql"), "first");
        File.WriteAllText(Path.Combine(ProjectScripts, "localhost_1433_app-20261026T140509Z-2.sql.tmp"), "in progress");

        var written = Succeeded(await WriteAsync("x"));

        Assert.Equal(Path.Combine(ProjectScripts, "localhost_1433_app-20261026T140509Z-3.sql"), written.Path);
        Assert.Equal("first", File.ReadAllText(Path.Combine(ProjectScripts, "localhost_1433_app-20261026T140509Z.sql")));
        Assert.Equal("in progress", File.ReadAllText(Path.Combine(ProjectScripts, "localhost_1433_app-20261026T140509Z-2.sql.tmp")));
    }

    [Fact]
    public async Task Concurrent_scripts_in_the_same_second_get_different_files()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".sql-mcp"));
        var release = new TaskCompletionSource();

        var first = WriteAsync(async writer =>
        {
            await release.Task;
            await writer.WriteAsync("first");
            return new CatalogSucceeded<int>(1, TimeSpan.Zero);
        });
        var second = Succeeded(await WriteAsync("second"));
        release.SetResult();
        var firstWritten = Succeeded(await first);

        Assert.NotEqual(firstWritten.Path, second.Path);
        Assert.EndsWith("first", File.ReadAllText(firstWritten.Path), StringComparison.Ordinal);
        Assert.EndsWith("second", File.ReadAllText(second.Path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_write_leaves_no_file()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".sql-mcp"));

        var outcome = await WriteAsync(async writer =>
        {
            await writer.WriteAsync("CREATE TABLE half");
            return new CatalogFailed<int>(FailureKind.Sql, "Invalid object name 'x'.", 208, TimeSpan.FromSeconds(1));
        });

        var failed = Assert.IsType<CatalogFailed<WrittenScript<int>>>(outcome);
        Assert.Equal((FailureKind.Sql, "Invalid object name 'x'.", (int?)208), (failed.Kind, failed.Message, failed.ErrorNumber));
        Assert.Empty(Files(ProjectScripts));
    }

    [Fact]
    public async Task Cancelled_write_leaves_no_file()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".sql-mcp"));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteAsync(async writer =>
        {
            await writer.WriteAsync("CREATE TABLE half");
            await cancellation.CancelAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            return new CatalogSucceeded<int>(1, TimeSpan.Zero);
        }, cancellation.Token));

        Assert.Empty(Files(ProjectScripts));
    }

    [Fact]
    public async Task Thrown_write_failure_leaves_no_file_and_says_what_failed()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".sql-mcp"));

        var outcome = await WriteAsync(async writer =>
        {
            await writer.WriteAsync("CREATE TABLE half");
            throw new IOException("disk full");
        });

        var failed = Assert.IsType<CatalogFailed<WrittenScript<int>>>(outcome);
        Assert.Equal(FailureKind.Other, failed.Kind);
        Assert.Contains("disk full", failed.Message, StringComparison.Ordinal);
        Assert.Empty(Files(ProjectScripts));
    }

    [Fact]
    public async Task Without_a_project_directory_the_script_goes_to_the_fallback()
    {
        var written = Succeeded(await WriteAsync("x"));

        Assert.Equal(Fallback, Path.GetDirectoryName(written.Path));
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, ".sql-mcp")));
    }

    [Fact]
    public async Task Unwritable_project_scripts_directory_falls_back()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".sql-mcp"));
        File.WriteAllText(ProjectScripts, "a file where the directory should be");

        var written = Succeeded(await WriteAsync("x"));

        Assert.Equal(Fallback, Path.GetDirectoryName(written.Path));
    }

    [Fact]
    public async Task Neither_directory_writable_fails_with_both_reasons()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".sql-mcp"));
        File.WriteAllText(ProjectScripts, "not a directory");
        File.WriteAllText(Fallback, "not a directory");

        var failed = Assert.IsType<CatalogFailed<WrittenScript<int>>>(await WriteAsync("x"));

        Assert.Equal(FailureKind.Other, failed.Kind);
        Assert.Contains(ProjectScripts, failed.Message, StringComparison.Ordinal);
        Assert.Contains(Fallback, failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_to_the_project_adds_scripts_to_an_existing_gitignore()
    {
        _temp.Write(".sql-mcp/.gitignore", "logs/\n");

        Succeeded(await WriteAsync("x"));

        Assert.Equal("logs/\nscripts/\n", File.ReadAllText(Path.Combine(_temp.Path, ".sql-mcp", ".gitignore")));
    }

    private Task<CatalogOutcome<WrittenScript<int>>> WriteAsync(string body, ScriptHeader? header = null) =>
        ScriptFiles.WriteAsync<int>(_temp.Path, Fallback, header ?? AppHeader, async writer =>
        {
            await writer.WriteAsync(body);
            return new CatalogSucceeded<int>(42, TimeSpan.Zero);
        }, TestContext.Current.CancellationToken);

    private Task<CatalogOutcome<WrittenScript<int>>> WriteAsync(Func<TextWriter, Task<CatalogOutcome<int>>> write, CancellationToken? cancellationToken = null) =>
        ScriptFiles.WriteAsync(_temp.Path, Fallback, AppHeader, write, cancellationToken ?? TestContext.Current.CancellationToken);

    private static WrittenScript<int> Succeeded(CatalogOutcome<WrittenScript<int>> outcome) =>
        Assert.IsType<CatalogSucceeded<WrittenScript<int>>>(outcome).Value;

    private static string[] Files(string directory) => Directory.Exists(directory)
        ? [.. Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
        : [];
}
