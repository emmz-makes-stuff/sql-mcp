using System.Text.Json.Serialization;

namespace SqlMcp.Audit;

/// <summary>
/// An audit event. Every subtype is deliberately limited to fields that can't carry result values, connection
/// strings or secrets (spec: no result data or secrets in the log). <c>AuditEventShapeTests</c> enforces this.
/// <see cref="EventType"/> is a constructor argument, not an override: System.Text.Json doesn't apply
/// <c>[JsonIgnore]</c> on an abstract property to its overrides, so they were logged again as <c>event_type</c>.
/// </summary>
internal abstract record AuditEvent([property: JsonIgnore] string EventType);

/// <summary>The first event in every log. <see cref="LogPath"/> says where the log went, fallback or not.</summary>
internal sealed record SessionStartEvent(string Version, IReadOnlyList<string> ConfigFiles, string LogPath, int Pid) : AuditEvent("session_start");

internal sealed record SessionEndEvent(string Version, IReadOnlyList<string> ConfigFiles) : AuditEvent("session_end");

/// <summary>
/// A batch that ran, and for a write, was committed. <see cref="Rows"/> counts the rows returned, and
/// <see cref="RowsAffected"/> each statement's reported count for a write (null for a read). Counts only, never values.
/// </summary>
internal sealed record ExecutedEvent(
    string Tool,
    string Database,
    string Sql,
    IReadOnlyList<string> Classification,
    long DurationMs,
    long Rows,
    bool Truncated,
    IReadOnlyList<int>? RowsAffected = null) : AuditEvent("executed");

/// <summary>A call refused before anything ran. <see cref="Source"/> says where a permission denial came from.</summary>
internal sealed record DeniedEvent(string Tool, string? Database, string? Sql, string Reason, string? Source = null) : AuditEvent("denied");

internal enum FailureKind
{
    Sql,
    Timeout,
    Connection,

    /// <summary>A write passed the database's affected-rows cap and was rolled back (design W4).</summary>
    AffectedRowsCap,

    /// <summary>
    /// A write whose outcome sql-mcp can't know: a procedure ended its transaction, or the commit itself failed. Some
    /// of the batch may have been committed.
    /// </summary>
    OutcomeUnknown,

    /// <summary>An introspection name matched no object, or more than one (design I4).</summary>
    UnresolvedName,
    Other,
}

/// <summary>
/// An execution error. Only the error's number, severity and state are kept: SQL Server messages can echo row
/// values, so the message text is never logged (2026-10-25 devlog decision).
/// For a write, a failure means it was rolled back; <see cref="Cap"/> and <see cref="Affected"/> are set when the
/// affected-rows cap rolled it back.
/// </summary>
internal sealed record FailedEvent(
    string Tool,
    string Database,
    string Sql,
    FailureKind Kind,
    int? ErrorNumber,
    int? Severity,
    int? State,
    long DurationMs) : AuditEvent("failed")
{
    public int? Cap { get; init; }

    public long? Affected { get; init; }
}

/// <summary>
/// An introspection call that ran (introspection design I7). <see cref="Request"/> is what was asked for: the
/// filters as <c>name=value</c> entries, or object names. Never the objects, columns or definitions returned.
/// A failure keeps only its kind and error number, as <see cref="FailedEvent"/> does, because the message text can
/// echo values. A script adds where it was written, how many objects of each type it holds, and how many it omitted,
/// never the script text.
/// </summary>
internal sealed record IntrospectionEvent(
    string Tool,
    string Database,
    IReadOnlyList<string> Request,
    long DurationMs,
    bool Succeeded) : AuditEvent("introspection")
{
    public FailureKind? Kind { get; init; }

    public int? ErrorNumber { get; init; }

    public string? ScriptPath { get; init; }

    /// <summary>Scripted objects by type wire name, such as <c>table</c>.</summary>
    public IReadOnlyDictionary<string, int>? Counts { get; init; }

    public int? OmittedCount { get; init; }
}
