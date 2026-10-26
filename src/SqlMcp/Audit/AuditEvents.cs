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

/// <summary>A batch that ran. <see cref="Rows"/> is a count of rows returned or affected, never their values.</summary>
internal sealed record ExecutedEvent(
    string Tool,
    string Database,
    string Sql,
    IReadOnlyList<string> Classification,
    long DurationMs,
    long Rows,
    bool Truncated) : AuditEvent("executed");

/// <summary>A call refused before anything ran. <see cref="Source"/> says where a permission denial came from.</summary>
internal sealed record DeniedEvent(string Tool, string? Database, string? Sql, string Reason, string? Source = null) : AuditEvent("denied");

internal enum FailureKind
{
    Sql,
    Timeout,
    Connection,

    /// <summary>A write passed the database's affected-rows cap and was rolled back (design W4).</summary>
    AffectedRowsCap,
    Other,
}

/// <summary>
/// An execution error. Only the error's number, severity and state are kept: SQL Server messages can echo row
/// values, so the message text is never logged (2026-10-25 devlog decision).
/// </summary>
internal sealed record FailedEvent(
    string Tool,
    string Database,
    string Sql,
    FailureKind Kind,
    int? ErrorNumber,
    int? Severity,
    int? State,
    long DurationMs) : AuditEvent("failed");

/// <summary>An introspection call (used by the introspection change). <see cref="Target"/> is an object name.</summary>
internal sealed record IntrospectionEvent(string Tool, string Database, string? Target) : AuditEvent("introspection");
