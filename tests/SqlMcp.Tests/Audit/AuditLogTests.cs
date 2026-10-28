using System.Runtime.Versioning;
using System.Text.Json;
using SqlMcp.Audit;

namespace SqlMcp.Tests.Audit;

// Directory permissions are set with Unix modes; CI and development run on Linux and macOS.
[UnsupportedOSPlatform("windows")]
public sealed class AuditLogTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 18, 5, 12, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTime _time = new(Start);

    private string Project => Path.Combine(_temp.Path, "project");

    private string Fallback => Path.Combine(_temp.Path, "state", "sql-mcp", "logs");

    public AuditLogTests() => Directory.CreateDirectory(Project);

    public void Dispose()
    {
        // Undo any read-only directories so the temp tree can be deleted.
        foreach (var directory in Directory.EnumerateDirectories(_temp.Path, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _temp.Dispose();
    }

    [Fact]
    public void Project_directory_present()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".sql-mcp"));

        using var log = Open();

        Assert.True(log.IsAvailable);
        Assert.Equal(Path.Combine(Project, ".sql-mcp", "logs", "20261025T180512Z-4242.jsonl"), log.FilePath);
        Assert.True(File.Exists(log.FilePath));
        Assert.Equal("logs/\nscripts/\n", File.ReadAllText(Path.Combine(Project, ".sql-mcp", ".gitignore")));
    }

    [Fact]
    public void An_existing_gitignore_keeps_its_lines_and_gains_the_missing_ones()
    {
        _temp.Write("project/.sql-mcp/.gitignore", "logs/\n*.local.json\n");

        using var log = Open();

        Assert.Equal("logs/\n*.local.json\nscripts/\n", File.ReadAllText(Path.Combine(Project, ".sql-mcp", ".gitignore")));
    }

    [Fact]
    public void No_project_directory()
    {
        using var log = Open();

        Assert.Equal(Path.Combine(Fallback, "20261025T180512Z-4242.jsonl"), log.FilePath);
        Assert.False(Directory.Exists(Path.Combine(Project, ".sql-mcp")));

        log.WriteSessionStart("1.2.3", ["/home/emmz/.config/sql-mcp/config.json"]);

        var first = Lines(log).First();
        Assert.Equal("session_start", first.GetProperty("event").GetString());
        Assert.Equal(log.FilePath, first.GetProperty("log_path").GetString());
        Assert.Equal(4242, first.GetProperty("pid").GetInt32());
        Assert.Equal("1.2.3", first.GetProperty("version").GetString());
        Assert.Equal("/home/emmz/.config/sql-mcp/config.json", first.GetProperty("config_files")[0].GetString());
    }

    [Fact]
    public void Unwritable_project_directory_falls_back()
    {
        var sqlMcp = Directory.CreateDirectory(Path.Combine(Project, ".sql-mcp")).FullName;
        File.SetUnixFileMode(sqlMcp, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        using var log = Open();

        Assert.True(log.IsAvailable);
        Assert.StartsWith(Fallback, log.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_directory_not_writable()
    {
        var sqlMcp = Directory.CreateDirectory(Path.Combine(Project, ".sql-mcp")).FullName;
        File.SetUnixFileMode(sqlMcp, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var state = Directory.CreateDirectory(Path.Combine(_temp.Path, "state")).FullName;
        File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        using var log = Open();

        Assert.False(log.IsAvailable);
        Assert.Null(log.FilePath);
        Assert.Contains(sqlMcp, log.UnavailableReason, StringComparison.Ordinal);
        Assert.False(log.Write(new DeniedEvent("query", "a:1433/b", "SELECT 1", "x")));
        Assert.StartsWith("audit log unavailable", AuditLog.UnavailableMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_log_is_unavailable()
    {
        var log = Open();
        log.Dispose();

        Assert.False(log.IsAvailable);
        Assert.False(log.Write(new DeniedEvent("query", null, null, "x")));
    }

    [Fact]
    public void Readable_while_running()
    {
        using var log = Open();

        log.Write(new ExecutedEvent("query", "sqlhost:1433/sales", "SELECT a FROM t", ["query"], 12, 3, false));

        var line = Assert.Single(Lines(log));
        Assert.Equal("executed", line.GetProperty("event").GetString());
    }

    [Fact]
    public void Every_line_has_a_timestamp_event_and_session()
    {
        using var log = Open();
        log.WriteSessionStart("1.0.0", []);
        _time.Advance(TimeSpan.FromSeconds(2));
        log.Write(new SessionEndEvent("1.0.0", []));

        var lines = Lines(log);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Equal(log.SessionId.ToString(), line.GetProperty("session_id").GetString());
            Assert.Equal(TimeSpan.Zero, line.GetProperty("ts").GetDateTimeOffset().Offset);
        });
        Assert.Equal(Start.AddSeconds(2), lines[1].GetProperty("ts").GetDateTimeOffset());
        Assert.Equal(7, log.SessionId.Version);
    }

    [Fact]
    public void Denied_attempt()
    {
        using var log = Open();

        log.Write(new DeniedEvent("query", "sqlhost:1433/sales", "DELETE FROM t", "classified as command; use the command tool"));

        var line = Assert.Single(Lines(log));
        Assert.Equal("denied", line.GetProperty("event").GetString());
        Assert.Equal("query", line.GetProperty("tool").GetString());
        Assert.Equal("sqlhost:1433/sales", line.GetProperty("database").GetString());
        Assert.Equal("DELETE FROM t", line.GetProperty("sql").GetString());
        Assert.Equal("classified as command; use the command tool", line.GetProperty("reason").GetString());
    }

    [Fact]
    public void Permission_denial()
    {
        using var log = Open();

        log.Write(new DeniedEvent("query", "sqlhost:1433/sales", "SELECT 1", "query is denied on sqlhost:1433/sales", "user config"));

        var line = Assert.Single(Lines(log));
        Assert.Equal("query is denied on sqlhost:1433/sales", line.GetProperty("reason").GetString());
        Assert.Equal("user config", line.GetProperty("source").GetString());
    }

    [Fact]
    public void Query_with_results_records_only_the_count()
    {
        using var log = Open();

        log.Write(new ExecutedEvent("query", "sqlhost:1433/sales", "SELECT email FROM users", ["query"], 40, 250, true));

        var line = Assert.Single(Lines(log));
        Assert.Equal(250, line.GetProperty("rows").GetInt64());
        Assert.True(line.GetProperty("truncated").GetBoolean());
        Assert.Equal(40, line.GetProperty("duration_ms").GetInt64());
    }

    [Fact]
    public void Failed_event_has_no_message()
    {
        using var log = Open();

        log.Write(new FailedEvent("query", "sqlhost:1433/sales", "SELECT CAST(email AS int) FROM users", FailureKind.Sql, 245, 16, 1, 5));

        var line = Assert.Single(Lines(log));
        Assert.Equal("sql", line.GetProperty("kind").GetString());
        Assert.Equal(245, line.GetProperty("error_number").GetInt32());
        Assert.False(line.TryGetProperty("message", out _));
    }

    [Fact]
    public void Concurrent_writes_stay_one_line_each()
    {
        using var log = Open();

        Parallel.For(0, 200, i => log.Write(new IntrospectionEvent("list_objects", "sqlhost:1433/sales", [$"pattern=t{i}"], i, Succeeded: true)));

        Assert.Equal(200, Lines(log).Count);
    }

    private AuditLog Open() => AuditLog.Open(Project, Fallback, _time, 4242);

    private static List<JsonElement> Lines(AuditLog log)
    {
        // The log holds the file open for writing; share it to read, as a tail would.
        using var stream = new FileStream(log.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }
}
