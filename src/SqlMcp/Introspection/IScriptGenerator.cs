using SqlMcp.Permissions;

namespace SqlMcp.Introspection;

/// <summary>
/// Writes a T-SQL script that creates a database's schema, with no data and no <c>CREATE DATABASE</c> (design I5). Each
/// call opens and closes its own connection, and changes nothing in the database.
/// </summary>
internal interface IScriptGenerator
{
    /// <summary>
    /// Scripts <paramref name="objects"/>, or the whole database when it is null, into <paramref name="writer"/>, each
    /// object after the ones it depends on. Chosen objects are scripted without the objects they depend on. The
    /// outcome lists what was scripted, for <see cref="Completeness"/> to compare with the inventory.
    /// </summary>
    Task<CatalogOutcome<IReadOnlyList<CatalogItem>>> GenerateAsync(
        string connectionString, IReadOnlyList<ObjectSummary>? objects, TextWriter writer, EffectiveLimits limits, CancellationToken cancellationToken);
}
