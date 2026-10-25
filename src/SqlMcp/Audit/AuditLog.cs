using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SqlMcp.Audit;

/// <summary>
/// The per-session JSON Lines audit log (design D9). One per process. Each event is written under a lock and
/// flushed at once. If the log can't be opened or written, <see cref="IsAvailable"/> turns false for good, and
/// tools must refuse to run: nothing runs that can't be logged.
/// </summary>
internal sealed class AuditLog : IDisposable
{
    public const string UnavailableMessage =
        "audit log unavailable: sql-mcp can't write its audit log, so it won't run anything. Ask the user to check the log directory.";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly int _processId;
    private FileStream? _stream;

    private AuditLog(Guid sessionId, TimeProvider time, int processId, FileStream? stream, string? unavailableReason)
    {
        SessionId = sessionId;
        _time = time;
        _processId = processId;
        _stream = stream;
        FilePath = stream?.Name;
        UnavailableReason = unavailableReason;
    }

    public Guid SessionId { get; }

    /// <summary>The log file, or null if no directory could be written.</summary>
    public string? FilePath { get; }

    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                return _stream is not null;
            }
        }
    }

    /// <summary>Why the log is unavailable, for stderr. Not shown to agents.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// Opens the log in <c>&lt;project&gt;/.sql-mcp/logs/</c> if <c>.sql-mcp/</c> exists and can be written to,
    /// creating <c>.sql-mcp/.gitignore</c> if it is missing. Otherwise it uses <paramref name="fallbackDirectory"/>.
    /// If neither works, the log is unavailable.
    /// </summary>
    public static AuditLog Open(string projectDirectory, string fallbackDirectory, TimeProvider time, int processId)
    {
        var sessionId = Guid.CreateVersion7(time.GetUtcNow());
        var name = $"{time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{processId.ToString(CultureInfo.InvariantCulture)}.jsonl";
        var failures = new List<string>();

        var sqlMcpDirectory = Path.Combine(projectDirectory, ".sql-mcp");
        if (Directory.Exists(sqlMcpDirectory))
        {
            var stream = TryOpen(Path.Combine(sqlMcpDirectory, "logs"), name, failures, () => EnsureGitIgnore(sqlMcpDirectory));
            if (stream is not null)
            {
                return new AuditLog(sessionId, time, processId, stream, null);
            }
        }

        var fallback = TryOpen(fallbackDirectory, name, failures, () => { });
        return fallback is not null
            ? new AuditLog(sessionId, time, processId, fallback, null)
            : new AuditLog(sessionId, time, processId, null, string.Join("; ", failures));
    }

    /// <summary>The user-level state directory: <c>~/.local/state/sql-mcp/logs</c>, or <c>%LOCALAPPDATA%\sql-mcp\logs</c>.</summary>
    public static string FallbackDirectory(bool isWindows, string homeDirectory, string localAppDataDirectory) => isWindows
        ? Path.Combine(localAppDataDirectory, "sql-mcp", "logs")
        : Path.Combine(homeDirectory, ".local", "state", "sql-mcp", "logs");

    public static string FallbackDirectoryForCurrentUser() => FallbackDirectory(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// Appends one event as a single JSON line and flushes it. Returns false, and makes the log unavailable, if
    /// it can't be written.
    /// </summary>
    public bool Write(AuditEvent auditEvent)
    {
        var line = Serialise(auditEvent);
        lock (_lock)
        {
            if (_stream is null)
            {
                return false;
            }

            try
            {
                _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
                _stream.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                UnavailableReason = $"{_stream.Name}: {ex.Message}";
                _stream.Dispose();
                _stream = null;
                return false;
            }
        }
    }

    /// <summary>The session start, with the log's own path, as the first event.</summary>
    public bool WriteSessionStart(string version, IReadOnlyList<string> configFiles) =>
        Write(new SessionStartEvent(version, configFiles, FilePath ?? "", _processId));

    internal string Serialise(AuditEvent auditEvent)
    {
        var line = new JsonObject
        {
            ["ts"] = _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["event"] = auditEvent.EventType,
            ["session_id"] = SessionId.ToString(),
        };

        var fields = JsonSerializer.SerializeToNode(auditEvent, auditEvent.GetType(), Options)!.AsObject();
        foreach (var (key, value) in fields.ToList())
        {
            fields.Remove(key);
            line[key] = value;
        }

        return line.ToJsonString();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
            UnavailableReason ??= "the audit log is closed";
        }
    }

    private static FileStream? TryOpen(string directory, string name, List<string> failures, Action prepare)
    {
        try
        {
            Directory.CreateDirectory(directory);
            prepare();
            return new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{directory}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Spec: the server creates <c>.sql-mcp/.gitignore</c> (containing <c>logs/</c>) when it is missing.</summary>
    private static void EnsureGitIgnore(string sqlMcpDirectory)
    {
        var path = Path.Combine(sqlMcpDirectory, ".gitignore");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "logs/\n");
        }
    }
}
