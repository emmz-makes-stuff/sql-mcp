using SqlMcp.Permissions;

namespace SqlMcp.Introspection;

/// <summary>What <c>script_database</c> made: the file, its size, what it scripted by type, and what it left out.</summary>
internal sealed record ScriptResult(string Path, long Bytes, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<OmittedItem> Omitted);

/// <summary>Where a script goes and what its header says, apart from the server version, which is read per call.</summary>
internal sealed record ScriptTarget(string ProjectDirectory, string FallbackDirectory, string Identifier, string SqlMcpVersion, TimeProvider Time);

/// <summary>
/// <c>script_database</c>'s steps (design I5–I6): resolve the chosen names, take the inventory, read the server
/// version, write the script file, then compare what was scripted with the inventory. Every step before the file
/// can fail the call, so an unknown name never leaves a file behind.
/// </summary>
internal static class DatabaseScripter
{
    /// <summary>
    /// Scripts <paramref name="names"/>, or the whole database when it is null. For chosen objects the inventory is
    /// narrowed to them, so the omitted list names a chosen object the generator couldn't script (such as an
    /// encrypted procedure) and nothing that wasn't asked for.
    /// </summary>
    public static async Task<CatalogOutcome<ScriptResult>> ScriptAsync(
        ICatalogReader reader,
        IScriptGenerator generator,
        string connectionString,
        IReadOnlyList<ObjectName>? names,
        ScriptTarget target,
        EffectiveLimits limits,
        CancellationToken cancellationToken)
    {
        var started = target.Time.GetTimestamp();

        IReadOnlyList<ObjectSummary>? chosen = null;
        if (names is not null)
        {
            var resolved = await reader.ResolveAsync(connectionString, names, limits, cancellationToken).ConfigureAwait(false);
            if (resolved is not CatalogSucceeded<IReadOnlyList<ObjectSummary>> { Value: var objects })
            {
                return Failed(resolved, target.Time.GetElapsedTime(started));
            }

            chosen = objects;
        }

        var inventoryOutcome = await reader.InventoryAsync(connectionString, limits, cancellationToken).ConfigureAwait(false);
        if (inventoryOutcome is not CatalogSucceeded<IReadOnlyList<InventoryItem>> { Value: var inventory })
        {
            return Failed(inventoryOutcome, target.Time.GetElapsedTime(started));
        }

        var versionOutcome = await reader.ServerVersionAsync(connectionString, limits, cancellationToken).ConfigureAwait(false);
        if (versionOutcome is not CatalogSucceeded<string> { Value: var serverVersion })
        {
            return Failed(versionOutcome, target.Time.GetElapsedTime(started));
        }

        var header = new ScriptHeader(target.Identifier, serverVersion, target.Time.GetUtcNow(), target.SqlMcpVersion);
        var written = await ScriptFiles.WriteAsync(
            target.ProjectDirectory,
            target.FallbackDirectory,
            header,
            writer => generator.GenerateAsync(connectionString, chosen, writer, limits, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (written is not CatalogSucceeded<WrittenScript<IReadOnlyList<CatalogItem>>> { Value: var script })
        {
            return Failed(written, target.Time.GetElapsedTime(started));
        }

        if (chosen is not null)
        {
            var wanted = chosen.Select(o => new CatalogItem(o.Schema, o.Name, o.Type.ToWireName())).ToHashSet();
            inventory = [.. inventory.Where(i => wanted.Contains(i.Item))];
        }

        var counts = script.Value
            .GroupBy(item => item.Type, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var omitted = Completeness.Omitted(inventory, script.Value);
        return new CatalogSucceeded<ScriptResult>(new ScriptResult(script.Path, script.Bytes, counts, omitted), target.Time.GetElapsedTime(started));
    }

    /// <summary>A step's failure as the call's, timed from the start of the call.</summary>
    private static CatalogFailed<ScriptResult> Failed<T>(CatalogOutcome<T> outcome, TimeSpan elapsed) => outcome is CatalogFailed<T> failed
        ? new CatalogFailed<ScriptResult>(failed.Kind, failed.Message, failed.ErrorNumber, elapsed)
        : throw new InvalidOperationException($"Unexpected outcome {outcome.GetType().Name}.");
}
