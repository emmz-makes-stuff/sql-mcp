using SqlMcp.Audit;
using SqlMcp.Permissions;

namespace SqlMcp.Execution;

/// <summary>
/// Runs a batch that the classifier has already accepted, for one engine (design D2, D7, W1). The batch runs in a
/// transaction: a <see cref="BatchMode.ReadOnly"/> batch is always rolled back, and a <see cref="BatchMode.Write"/>
/// batch is committed only if all of it succeeds. The connection is closed before the outcome is returned.
/// </summary>
internal interface IQueryExecutor
{
    Task<QueryOutcome> ExecuteAsync(string connectionString, string sql, BatchMode mode, EffectiveLimits limits, CancellationToken cancellationToken);
}

/// <summary>Whether a batch's changes are kept (design W1).</summary>
internal enum BatchMode
{
    /// <summary>A query: rolled back however it ends, and a capped result cancels the rest of the batch.</summary>
    ReadOnly,

    /// <summary>
    /// A command, ddl or execute batch: committed all or nothing, bounded by the affected-rows cap, and a capped
    /// result is drained so the batch still runs to completion (design W2–W4).
    /// </summary>
    Write,
}

/// <summary>Wording every write failure shares, whichever layer reports it.</summary>
internal static class WriteMessages
{
    public const string NothingCommitted = "The batch was rolled back, so nothing was committed.";

    /// <summary>Ends every message where sql-mcp can't tell whether a write was committed.</summary>
    public const string CheckBeforeRerun = "Check with query before running it again.";
}

internal abstract record QueryOutcome(TimeSpan Duration);

/// <summary>
/// A batch that ran. For a write it was committed, and <see cref="RowsAffected"/> holds the count each statement
/// reported, in order; for a read it is empty.
/// </summary>
internal sealed record QuerySucceeded(BufferedResult Result, TimeSpan Duration) : QueryOutcome(Duration)
{
    public IReadOnlyList<int> RowsAffected { get; init; } = [];
}

/// <summary>
/// A batch that failed. <see cref="Message"/> is for the agent only: it can echo row values, so it is never audited
/// (2026-10-25 audit decision). The number, severity and state are safe to log, as are <see cref="Cap"/> and
/// <see cref="Affected"/>, which are set only for <see cref="FailureKind.AffectedRowsCap"/>.
/// </summary>
internal sealed record QueryFailed(FailureKind Kind, string Message, int? ErrorNumber, int? Severity, int? State, TimeSpan Duration)
    : QueryOutcome(Duration)
{
    public int? Cap { get; init; }

    public long? Affected { get; init; }
}
