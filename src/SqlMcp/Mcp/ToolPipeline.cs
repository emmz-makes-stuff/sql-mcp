using System.Diagnostics;
using SqlMcp.Audit;
using SqlMcp.Classification;
using SqlMcp.Configuration;
using SqlMcp.Execution;
using SqlMcp.Introspection;
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
    public const string ListObjects = "list_objects";
    public const string DescribeObject = "describe_object";
    public const string ScriptDatabase = "script_database";
}

/// <summary>
/// A tool that runs SQL: the one place that ties its name, statement category, permission group, batch mode and
/// method together, and says whether it can return rows (so whether paging is offered with it).
/// </summary>
internal sealed record SqlTool(string Name, StatementCategory Category, ToolGroup Group, BatchMode Mode, bool ReturnsRows, string Method);

internal static class SqlTools
{
    public static readonly SqlTool Query = new(ToolNames.Query, StatementCategory.Query, ToolGroup.Query, BatchMode.ReadOnly, ReturnsRows: true, nameof(SqlMcpTools.Query));
    public static readonly SqlTool Command = new(ToolNames.Command, StatementCategory.Command, ToolGroup.Command, BatchMode.Write, ReturnsRows: true, nameof(SqlMcpTools.Command));
    public static readonly SqlTool Ddl = new(ToolNames.Ddl, StatementCategory.Ddl, ToolGroup.Ddl, BatchMode.Write, ReturnsRows: false, nameof(SqlMcpTools.Ddl));
    public static readonly SqlTool Execute = new(ToolNames.Execute, StatementCategory.Execute, ToolGroup.Execute, BatchMode.Write, ReturnsRows: true, nameof(SqlMcpTools.Execute));

    /// <summary>In the order the catalog lists them.</summary>
    public static readonly IReadOnlyList<SqlTool> All = [Query, Command, Ddl, Execute];
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
        RunAsync(SqlTools.Query, database, sql, cancellationToken);

    public Task<ToolResult> CommandAsync(string database, string sql, CancellationToken cancellationToken) =>
        RunAsync(SqlTools.Command, database, sql, cancellationToken);

    public Task<ToolResult> DdlAsync(string database, string sql, CancellationToken cancellationToken) =>
        RunAsync(SqlTools.Ddl, database, sql, cancellationToken);

    public Task<ToolResult> ExecuteAsync(string database, string sql, CancellationToken cancellationToken) =>
        RunAsync(SqlTools.Execute, database, sql, cancellationToken);

    public Task<ToolResult> ListObjectsAsync(string database, string? schema, IReadOnlyList<string>? types, string? pattern, CancellationToken cancellationToken)
    {
        if (!ObjectTypes.TryParseAll(types, out var parsed, out var error))
        {
            return Task.FromResult(ToolResult.Error(error!));
        }

        var filter = new ObjectFilter(Blank(schema), parsed, Blank(pattern));
        return RunIntrospectionAsync(
            ToolNames.ListObjects,
            database,
            filter.Describe(),
            (engine, db, token) => engine.CatalogReader.ListAsync(db.Entry.ConnectionString!, filter, db.Limits, token),
            list => ToolResult.Ok(ObjectListResponse.From(list)),
            cancellationToken);
    }

    public Task<ToolResult> DescribeObjectAsync(string database, string name, CancellationToken cancellationToken)
    {
        if (!ObjectName.TryParse(name, out var parsed, out var error))
        {
            return Task.FromResult(ToolResult.Error(error));
        }

        return RunIntrospectionAsync(
            ToolNames.DescribeObject,
            database,
            [parsed.ToString()],
            (engine, db, token) => engine.CatalogReader.DescribeAsync(db.Entry.ConnectionString!, parsed, db.Limits, token),
            details => ToolResult.Ok(ObjectDetailsResponse.From(details)),
            cancellationToken);
    }

    /// <summary>
    /// Scripts the whole database when <paramref name="objects"/> is left out, or the objects named. An empty list is
    /// refused rather than read as "everything" (Emmz's call, 2026-10-28). Every name is parsed here and resolved by
    /// the scripter before any file is created.
    /// </summary>
    public Task<ToolResult> ScriptDatabaseAsync(string database, IReadOnlyList<string>? objects, CancellationToken cancellationToken)
    {
        List<ObjectName>? names = null;
        if (objects is { Count: 0 })
        {
            return Task.FromResult(ToolResult.Error("objects is empty. Name at least one object, or leave objects out to script the whole database."));
        }

        if (objects is not null)
        {
            names = [];
            foreach (var text in objects)
            {
                if (!ObjectName.TryParse(text, out var parsed, out var error))
                {
                    return Task.FromResult(ToolResult.Error(error));
                }

                names.Add(parsed);
            }
        }

        return RunIntrospectionAsync(
            ToolNames.ScriptDatabase,
            database,
            names?.Select(n => n.ToString()).ToList() ?? [],
            (engine, db, token) => DatabaseScripter.ScriptAsync(
                engine.CatalogReader,
                engine.ScriptGenerator,
                db.Entry.ConnectionString!,
                names,
                new ScriptTarget(session.ProjectDirectory, session.ScriptsFallbackDirectory, db.Identifier, session.Version, session.Time),
                db.Limits,
                token),
            script => ToolResult.Ok(ScriptResponse.From(script)),
            cancellationToken,
            (script, auditEvent) => auditEvent with { ScriptPath = script.Path, Counts = script.Counts, OmittedCount = script.Omitted.Count },
            script => DeleteUnlogged(script.Path, session.Diagnostics));
    }

    /// <summary>
    /// Deletes a script whose audit event couldn't be written. If that fails too, the agent still isn't told the path,
    /// and <paramref name="diagnostics"/> (the server's stderr) names it so a person can remove it (Emmz's call, 2026-10-28).
    /// </summary>
    internal static void DeleteUnlogged(string path, TextWriter diagnostics)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.WriteLine($"sql-mcp: a script was written but its audit event wasn't, and the script couldn't be deleted. Delete {path} by hand. {ex.Message}");
        }
    }

    /// <summary>
    /// The flow every introspection tool shares, in the order <see cref="RunAsync"/> uses (design I7):
    /// <c>audit ok? → resolve db → introspection allowed? → available? → read → audit → respond</c>. There's no
    /// classifier step: the reader runs fixed catalog queries, never the agent's SQL. A call that reads the catalog
    /// is logged as one <c>introspection</c> event, succeeded or not. <paramref name="describe"/> adds a success's
    /// details to its event; if that event can't be logged, <paramref name="discard"/> undoes what the call left
    /// behind (a script file), so nothing is handed out unlogged.
    /// </summary>
    private async Task<ToolResult> RunIntrospectionAsync<T>(
        string tool,
        string database,
        IReadOnlyList<string> request,
        Func<EngineServices, EffectiveDatabase, CancellationToken, Task<CatalogOutcome<T>>> read,
        Func<T, ToolResult> respond,
        CancellationToken cancellationToken,
        Func<T, IntrospectionEvent, IntrospectionEvent>? describe = null,
        Action<T>? discard = null)
    {
        if (Refusal() is { } refusal)
        {
            return refusal;
        }

        if (Find(database) is not { } db)
        {
            return ToolResult.Error(DatabaseIdentity.UnknownIdentifierMessage(database, session.Databases.Select(d => d.Identifier)));
        }

        var grant = db.Groups[ToolGroup.Introspection];
        if (!grant.Allowed)
        {
            var reason = DenialReason(db, grant);
            return Audited(new DeniedEvent(tool, db.Identifier, null, reason, grant.Source.ToDisplayName()), ToolResult.Error(reason));
        }

        if (!db.Entry.IsAvailable)
        {
            return ToolResult.Error($"{db.Identifier} is unavailable: {db.Entry.UnavailableReason}");
        }

        var engine = session.Engines[db.Entry.Engine];
        CatalogOutcome<T> outcome;
        var started = Stopwatch.GetTimestamp();
        try
        {
            outcome = await read(engine, db, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session.Audit.Write(Failed(FailureKind.Other, null));
            throw;
        }
        catch (Exception ex)
        {
            // As in RunAsync: anything that escapes the reader is still audited before the agent hears of it.
            return Audited(Failed(FailureKind.Other, null), ToolResult.Error($"{tool} failed: {ex.Message}"));
        }

        if (outcome is CatalogSucceeded<T> succeeded)
        {
            var succeededEvent = new IntrospectionEvent(tool, db.Identifier, request, (long)succeeded.Duration.TotalMilliseconds, Succeeded: true);
            if (session.Audit.Write(describe?.Invoke(succeeded.Value, succeededEvent) ?? succeededEvent))
            {
                return respond(succeeded.Value);
            }

            discard?.Invoke(succeeded.Value);
            return Unavailable;
        }

        return outcome switch
        {
            CatalogFailed<T> failed => Audited(
                new IntrospectionEvent(tool, db.Identifier, request, (long)failed.Duration.TotalMilliseconds, Succeeded: false)
                {
                    Kind = failed.Kind,
                    ErrorNumber = failed.ErrorNumber,
                },
                ToolResult.Error(failed.Message)),
            _ => throw new InvalidOperationException($"Unexpected outcome {outcome.GetType().Name}."),
        };

        IntrospectionEvent Failed(FailureKind kind, int? errorNumber) =>
            new(tool, db.Identifier, request, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, Succeeded: false)
            {
                Kind = kind,
                ErrorNumber = errorNumber,
            };
    }

    /// <summary>An optional text argument: blank or whitespace means "not given".</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// One flow for every tool that runs SQL, so each audits in the same order (design W7). A write is logged
    /// <c>executed</c> only once the executor has committed it.
    /// </summary>
    private async Task<ToolResult> RunAsync(SqlTool sqlTool, string database, string sql, CancellationToken cancellationToken)
    {
        var (tool, category, mode) = (sqlTool.Name, sqlTool.Category, sqlTool.Mode);
        if (Refusal() is { } refusal)
        {
            return refusal;
        }

        if (Find(database) is not { } db)
        {
            return ToolResult.Error(DatabaseIdentity.UnknownIdentifierMessage(database, session.Databases.Select(d => d.Identifier)));
        }

        var grant = db.Groups[sqlTool.Group];
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
            // executor commits last and reports a failed commit itself, so an exception that escapes it came before
            // the commit, and the write was rolled back.
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
        SqlTools.All.Where(t => db.IsAllowed(t.Group)).Select(t => t.Category).ToHashSet();

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
