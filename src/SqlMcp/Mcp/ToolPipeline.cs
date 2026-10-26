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
    public const string Command = "command";
    public const string Ddl = "ddl";
    public const string Execute = "execute";
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

    public Task<ToolResult> QueryAsync(string database, string sql, CancellationToken cancellationToken) =>
        RunAsync(ToolNames.Query, StatementCategory.Query, BatchMode.ReadOnly, database, sql, cancellationToken);

    public Task<ToolResult> CommandAsync(string database, string sql, CancellationToken cancellationToken) =>
        RunAsync(ToolNames.Command, StatementCategory.Command, BatchMode.Write, database, sql, cancellationToken);

    public Task<ToolResult> DdlAsync(string database, string sql, CancellationToken cancellationToken) =>
        RunAsync(ToolNames.Ddl, StatementCategory.Ddl, BatchMode.Write, database, sql, cancellationToken);

    public Task<ToolResult> ExecuteAsync(string database, string sql, CancellationToken cancellationToken) =>
        RunAsync(ToolNames.Execute, StatementCategory.Execute, BatchMode.Write, database, sql, cancellationToken);

    /// <summary>
    /// One flow for every tool that runs SQL, so each audits in the same order (design W7). The tool's group is its
    /// category's. A write is logged <c>executed</c> only once the executor has committed it.
    /// </summary>
    private async Task<ToolResult> RunAsync(
        string tool, StatementCategory category, BatchMode mode, string database, string sql, CancellationToken cancellationToken)
    {
        if (Refusal() is { } refusal)
        {
            return refusal;
        }

        if (Find(database) is not { } db)
        {
            return ToolResult.Error(DatabaseIdentity.UnknownIdentifierMessage(database, session.Databases.Select(d => d.Identifier)));
        }

        var grant = db.Groups[CategoryGroups[category]];
        if (!grant.Allowed)
        {
            var reason = DenialReason(db, grant);
            return Audited(new DeniedEvent(tool, db.Identifier, sql, reason, grant.Source.ToDisplayName()), ToolResult.Error(reason));
        }

        if (!db.Entry.IsAvailable)
        {
            return ToolResult.Error($"{db.Identifier} is unavailable: {db.Entry.UnavailableReason}");
        }

        var engine = session.Engines[db.Entry.Engine];
        var classification = engine.Classifier.Classify(sql);
        var verdict = classification.ForTool(category, AllowedCategories(db));
        if (!verdict.Accepted)
        {
            var reason = $"Rejected, nothing was sent to the database: {verdict.Reason}";
            return Audited(new DeniedEvent(tool, db.Identifier, sql, reason), ToolResult.Error(reason));
        }

        QueryOutcome outcome;
        var started = Stopwatch.GetTimestamp();
        try
        {
            outcome = await engine.Executor.ExecuteAsync(db.Entry.ConnectionString!, sql, mode, db.Limits, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The client cancelled after the batch was sent: it still goes in the log.
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            session.Audit.Write(new FailedEvent(tool, db.Identifier, sql, FailureKind.Other, null, null, null, elapsed));
            throw;
        }
        catch (Exception ex)
        {
            // Executors report failures as outcomes, but anything that escapes one (a value no converter can read,
            // a connection option the platform can't use) must still be audited before the agent hears of it. The
            // executor commits last, so an escaping write was rolled back.
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var failure = new FailedEvent(tool, db.Identifier, sql, FailureKind.Other, null, null, null, elapsed);
            var message = mode == BatchMode.Write ? $"{tool} failed: {ex.Message} {NothingCommitted}" : $"{tool} failed: {ex.Message}";
            return Audited(failure, ToolResult.Error(message));
        }

        var durationMs = (long)outcome.Duration.TotalMilliseconds;
        switch (outcome)
        {
            case QuerySucceeded succeeded:
                var categories = classification.Statements.Select(s => s.Category.ToName()).ToList();
                var write = mode == BatchMode.Write;
                var executed = new ExecutedEvent(
                    tool, db.Identifier, sql, categories, durationMs, succeeded.Result.RowCount, succeeded.Result.Truncated, write ? succeeded.RowsAffected : null);
                if (!session.Audit.Write(executed))
                {
                    return write ? ToolResult.Error(CommittedUnlogged) : Unavailable;
                }

                if (write && succeeded.Result.ResultSets.Count == 0)
                {
                    return ToolResult.Ok(new WriteResponse(true, succeeded.RowsAffected));
                }

                var page = session.Results.Open(succeeded.Result, db.Limits.PageSize);
                var first = PageResponse.First(page, succeeded.Result);
                return ToolResult.Ok(write ? first with { Committed = true, RowsAffected = succeeded.RowsAffected } : first);

            case QueryFailed failed:
                var failure = new FailedEvent(tool, db.Identifier, sql, failed.Kind, failed.ErrorNumber, failed.Severity, failed.State, durationMs)
                {
                    Cap = failed.Cap,
                    Affected = failed.Affected,
                };
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
    /// A committed write whose <c>executed</c> event couldn't be written. It can't be undone, so the agent is told it
    /// happened rather than left to retry it.
    /// </summary>
    private const string CommittedUnlogged =
        "audit log unavailable: the batch was committed, but sql-mcp couldn't log it, so it won't run anything more. Don't run it again. Ask the user to check the log directory.";

    private const string NothingCommitted = WriteMessages.NothingCommitted;

    /// <summary>
    /// "query is denied on X (source: user config).", plus the approval wording when an unapproved project grant
    /// was ignored. That wording comes from <see cref="ApprovalMessages"/>, so it asks the user and never the agent.
    /// </summary>
    private static string DenialReason(EffectiveDatabase db, GroupGrant grant)
    {
        var reason = $"{grant.Group.ToConfigName()} is denied on {db.Identifier} (source: {grant.Source.ToDisplayName()}).";
        return grant.Reason is null ? reason : $"{reason} {grant.Reason}";
    }

    /// <summary>
    /// The statement categories whose tool's group is allowed on <paramref name="db"/>, so a wrong-tool refusal can
    /// say whether the right tool would run there (design W6).
    /// </summary>
    private static HashSet<StatementCategory> AllowedCategories(EffectiveDatabase db) =>
        CategoryGroups.Where(pair => db.IsAllowed(pair.Value)).Select(pair => pair.Key).ToHashSet();

    private static readonly Dictionary<StatementCategory, ToolGroup> CategoryGroups = new()
    {
        [StatementCategory.Query] = ToolGroup.Query,
        [StatementCategory.Command] = ToolGroup.Command,
        [StatementCategory.Ddl] = ToolGroup.Ddl,
        [StatementCategory.Execute] = ToolGroup.Execute,
    };

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
