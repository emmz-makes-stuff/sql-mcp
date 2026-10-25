using SqlMcp.Audit;
using SqlMcp.Permissions;

namespace SqlMcp.Execution;

/// <summary>
/// Runs a batch that the classifier has already accepted, for one engine (design D2, D7). The batch always runs in
/// a transaction that is rolled back, and the connection is closed before the outcome is returned.
/// </summary>
internal interface IQueryExecutor
{
    Task<QueryOutcome> ExecuteAsync(string connectionString, string sql, EffectiveLimits limits, CancellationToken cancellationToken);
}

internal abstract record QueryOutcome(TimeSpan Duration);

internal sealed record QuerySucceeded(BufferedResult Result, TimeSpan Duration) : QueryOutcome(Duration);

/// <summary>
/// A batch that failed. <see cref="Message"/> is for the agent only: it can echo row values, so it is never audited
/// (2026-10-25 audit decision). The number, severity and state are safe to log.
/// </summary>
internal sealed record QueryFailed(FailureKind Kind, string Message, int? ErrorNumber, int? Severity, int? State, TimeSpan Duration)
    : QueryOutcome(Duration);
