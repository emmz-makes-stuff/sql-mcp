using System.Diagnostics;
using SqlMcp.Audit;
using SqlMcp.Classification;
using SqlMcp.Configuration;
using SqlMcp.Execution;
using SqlMcp.Permissions;

namespace SqlMcp.Mcp;

/// <summary>The MCP tool names. Group tools are offered only where their group is allowed somewhere (design D8).</summary>
internal static class ToolNames
{
    public const string ListDatabases = "list_databases";
    public const string Query = "query";
    public const string NextPage = "next_page";
    public const string Abandon = "abandon";
}

/// <summary>
/// The checks every tool call goes through, in design D8's order:
/// <c>audit ok? → resolve db → group allowed? → available? → classify → execute</c>.
/// Permission and classification denials, executions and failures are audited. If an audit write fails, the call
/// returns "audit log unavailable" instead of its result: nothing is returned that wasn't logged.
/// </summary>
internal sealed class ToolPipeline(SqlMcpSession session)
{
    public ToolResult ListDatabases()
    {
        if (Refusal() is { } refusal)
        {
            return refusal;
        }

        var databases = session.Databases.Select(DatabaseInfo.From).ToList();
        return ToolResult.Ok(new DatabaseListing(databases, databases.Count == 0 ? session.NoDatabasesMessage : null));
    }

    public async Task<ToolResult> QueryAsync(string database, string sql, CancellationToken cancellationToken)
    {
        if (Refusal() is { } refusal)
        {
            return refusal;
        }

        if (Find(database) is not { } db)
        {
            return ToolResult.Error(DatabaseIdentity.UnknownIdentifierMessage(database, session.Databases.Select(d => d.Identifier)));
        }

        var grant = db.Groups[ToolGroup.Query];
        if (!grant.Allowed)
        {
            var reason = DenialReason(db, grant);
            return Audited(new DeniedEvent(ToolNames.Query, db.Identifier, sql, reason, grant.Source.ToDisplayName()), ToolResult.Error(reason));
        }

        if (!db.Entry.IsAvailable)
        {
            return ToolResult.Error($"{db.Identifier} is unavailable: {db.Entry.UnavailableReason}");
        }

        var engine = session.Engines[db.Entry.Engine];
        var classification = engine.Classifier.Classify(sql);
        var verdict = classification.ForTool(StatementCategory.Query, ToolCatalog.Categories);
        if (!verdict.Accepted)
        {
            var reason = $"Rejected, nothing was sent to the database: {verdict.Reason}";
            return Audited(new DeniedEvent(ToolNames.Query, db.Identifier, sql, reason), ToolResult.Error(reason));
        }

        QueryOutcome outcome;
        var started = Stopwatch.GetTimestamp();
        try
        {
            outcome = await engine.Executor.ExecuteAsync(db.Entry.ConnectionString!, sql, db.Limits, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The client cancelled after the batch was sent: it still goes in the log.
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            session.Audit.Write(new FailedEvent(ToolNames.Query, db.Identifier, sql, FailureKind.Other, null, null, null, elapsed));
            throw;
        }
        catch (Exception ex)
        {
            // Executors report failures as outcomes, but anything that escapes one (a value no converter can read,
            // a connection option the platform can't use) must still be audited before the agent hears of it.
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var failure = new FailedEvent(ToolNames.Query, db.Identifier, sql, FailureKind.Other, null, null, null, elapsed);
            return Audited(failure, ToolResult.Error($"query failed: {ex.Message}"));
        }

        var durationMs = (long)outcome.Duration.TotalMilliseconds;
        switch (outcome)
        {
            case QuerySucceeded succeeded:
                var categories = classification.Statements.Select(s => s.Category.ToName()).ToList();
                var executed = new ExecutedEvent(ToolNames.Query, db.Identifier, sql, categories, durationMs, succeeded.Result.RowCount, succeeded.Result.Truncated);
                if (!session.Audit.Write(executed))
                {
                    return Unavailable;
                }

                var page = session.Results.Open(succeeded.Result, db.Limits.PageSize);
                return ToolResult.Ok(PageResponse.First(page, succeeded.Result));

            case QueryFailed failed:
                var failure = new FailedEvent(ToolNames.Query, db.Identifier, sql, failed.Kind, failed.ErrorNumber, failed.Severity, failed.State, durationMs);
                return Audited(failure, ToolResult.Error(failed.Message));

            default:
                throw new InvalidOperationException($"Unexpected outcome {outcome.GetType().Name}.");
        }
    }

    public ToolResult NextPage(string handle)
    {
        if (!session.Audit.IsAvailable)
        {
            return Unavailable;
        }

        return session.Results.NextPage(handle) is { } page
            ? ToolResult.Ok(PageResponse.From(page))
            : ToolResult.Error(ResultStore.UnknownHandleMessage);
    }

    public ToolResult Abandon(string handle)
    {
        if (!session.Audit.IsAvailable)
        {
            return Unavailable;
        }

        return session.Results.Abandon(handle)
            ? ToolResult.Ok(new AbandonResponse(true))
            : ToolResult.Error(ResultStore.UnknownHandleMessage);
    }

    private static ToolResult Unavailable => ToolResult.Error(AuditLog.UnavailableMessage);

    /// <summary>
    /// "query is denied on X (source: user config).", plus the approval wording when an unapproved project grant
    /// was ignored. That wording comes from <see cref="ApprovalMessages"/>, so it asks the user and never the agent.
    /// </summary>
    private static string DenialReason(EffectiveDatabase db, GroupGrant grant)
    {
        var reason = $"{grant.Group.ToConfigName()} is denied on {db.Identifier} (source: {grant.Source.ToDisplayName()}).";
        return grant.Reason is null ? reason : $"{reason} {grant.Reason}";
    }

    /// <summary>The refusal every database tool starts with: no audit log, or no usable config.</summary>
    private ToolResult? Refusal()
    {
        if (!session.Audit.IsAvailable)
        {
            return Unavailable;
        }

        return session.ConfigError is { } error ? ToolResult.Error(error) : null;
    }

    private EffectiveDatabase? Find(string identifier)
    {
        var wanted = DatabaseIdentity.Normalise(identifier);
        return session.Databases.FirstOrDefault(d => string.Equals(d.Identifier, wanted, StringComparison.Ordinal));
    }

    private ToolResult Audited(AuditEvent auditEvent, ToolResult result) => session.Audit.Write(auditEvent) ? result : Unavailable;
}
