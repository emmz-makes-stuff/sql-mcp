using SqlMcp.Audit;
using SqlMcp.Permissions;

namespace SqlMcp.Introspection;

/// <summary>
/// Reads one engine's catalog into the engine-neutral model (design I2, I4). It runs only fixed catalog queries,
/// never SQL from the agent, and changes nothing. Each call opens and closes its own connection.
/// </summary>
internal interface ICatalogReader
{
    /// <summary>The user objects matching <paramref name="filter"/>, at most the database's row cap of them.</summary>
    Task<CatalogOutcome<ObjectList>> ListAsync(string connectionString, ObjectFilter filter, EffectiveLimits limits, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the one object each name means (design I4): a schema-qualified name must match exactly, an unqualified one
    /// must match in exactly one schema. Names compare as the database compares them. If any name fails, the outcome
    /// is a <see cref="FailureKind.UnresolvedName"/> failure whose message covers every name that failed.
    /// </summary>
    Task<CatalogOutcome<IReadOnlyList<ObjectSummary>>> ResolveAsync(string connectionString, IReadOnlyList<ObjectName> names, EffectiveLimits limits, CancellationToken cancellationToken);

    /// <summary>Resolves <paramref name="name"/> as <see cref="ResolveAsync"/> does, then reads its details.</summary>
    Task<CatalogOutcome<ObjectDetails>> DescribeAsync(string connectionString, ObjectName name, EffectiveLimits limits, CancellationToken cancellationToken);
}

/// <summary>How a catalog read ended. Failures carry the same kinds as an executor's, so they audit the same way.</summary>
internal abstract record CatalogOutcome<T>(TimeSpan Duration);

internal sealed record CatalogSucceeded<T>(T Value, TimeSpan Duration) : CatalogOutcome<T>(Duration);

/// <summary><see cref="Message"/> is for the agent and is never logged; the audit log keeps the kind and number.</summary>
internal sealed record CatalogFailed<T>(FailureKind Kind, string Message, int? ErrorNumber, TimeSpan Duration) : CatalogOutcome<T>(Duration);
