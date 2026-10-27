using System.Text.Json.Nodes;
using SqlMcp.Audit;
using SqlMcp.Classification.SqlServer;
using SqlMcp.Configuration;
using SqlMcp.Execution;
using SqlMcp.Introspection;
using SqlMcp.Mcp;
using SqlMcp.Permissions;

namespace SqlMcp.Tests.Mcp;

/// <summary>An executor that records what it was asked to run and returns a canned outcome.</summary>
internal sealed class FakeExecutor : IQueryExecutor
{
    public List<string> Calls { get; } = [];

    /// <summary>The mode of each call, in the same order as <see cref="Calls"/>.</summary>
    public List<BatchMode> Modes { get; } = [];

    public QueryOutcome Outcome { get; set; } = new QuerySucceeded(new BufferedResult([], false, null), TimeSpan.FromMilliseconds(5));

    /// <summary>When set, the call is cancelled as if the client gave up while the batch ran.</summary>
    public bool Cancels { get; set; }

    /// <summary>When set, the call throws this, as an executor bug or an unreadable value would.</summary>
    public Exception? Throws { get; set; }

    /// <summary>Runs while the batch "executes", e.g. to take the audit log away before the outcome is logged.</summary>
    public Action? During { get; set; }

    public Task<QueryOutcome> ExecuteAsync(string connectionString, string sql, BatchMode mode, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        Calls.Add(sql);
        Modes.Add(mode);
        During?.Invoke();
        if (Throws is not null)
        {
            return Task.FromException<QueryOutcome>(Throws);
        }

        return Cancels ? Task.FromCanceled<QueryOutcome>(new CancellationToken(canceled: true)) : Task.FromResult(Outcome);
    }
}

/// <summary>A catalog reader that records the filters it was given and returns a canned outcome.</summary>
internal sealed class FakeCatalogReader : ICatalogReader
{
    public List<ObjectFilter> Lists { get; } = [];

    public CatalogOutcome<ObjectList> ListOutcome { get; set; } =
        new CatalogSucceeded<ObjectList>(new ObjectList([], 0, false), TimeSpan.FromMilliseconds(4));

    /// <summary>When set, the call is cancelled as if the client gave up while the catalog was read.</summary>
    public bool Cancels { get; set; }

    /// <summary>When set, the call throws this, as a reader bug would.</summary>
    public Exception? Throws { get; set; }

    public Task<CatalogOutcome<ObjectList>> ListAsync(string connectionString, ObjectFilter filter, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        Lists.Add(filter);
        return Respond(ListOutcome);
    }

    private Task<CatalogOutcome<T>> Respond<T>(CatalogOutcome<T> outcome)
    {
        if (Throws is not null)
        {
            return Task.FromException<CatalogOutcome<T>>(Throws);
        }

        return Cancels ? Task.FromCanceled<CatalogOutcome<T>>(new CancellationToken(canceled: true)) : Task.FromResult(outcome);
    }
}

/// <summary>
/// A <see cref="SqlMcpSession"/> over temp user and project directories, the real classifier, a fake executor, a
/// fake catalog reader, and an audit log in the project's <c>.sql-mcp/logs/</c>.
/// </summary>
internal sealed class SessionBuilder : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public FakeExecutor Executor { get; } = new();

    public FakeCatalogReader Catalog { get; } = new();

    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 10, 25, 18, 40, 0, TimeSpan.Zero));

    public ConfigPaths Paths => new(Path.Combine(_temp.Path, "home", ".config", "sql-mcp", "config.json"), Path.Combine(_temp.Path, "project"));

    public SessionBuilder User(string json)
    {
        _temp.Write("home/.config/sql-mcp/config.json", json);
        return this;
    }

    public SessionBuilder Project(string json)
    {
        _temp.Write("project/.sql-mcp/config.json", json);
        return this;
    }

    public SessionBuilder Variable(string name, string value)
    {
        _environment[name] = value;
        return this;
    }

    public SqlMcpSession Build()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "project", ".sql-mcp"));
        var audit = AuditLog.Open(Paths.ProjectDirectory, Path.Combine(_temp.Path, "fallback"), Time, 4242);
        var engines = new Dictionary<Engine, EngineServices> { [Engine.SqlServer] = new(new SqlServerStatementClassifier(), Executor, Catalog) };
        return SqlMcpSession.Load(Paths, name => _environment.GetValueOrDefault(name), audit, Time, engines, "1.2.3-test");
    }

    /// <summary>The audit log's events, one JSON object per line.</summary>
    public static List<JsonObject> Events(SqlMcpSession session) =>
        [.. File.ReadAllLines(session.Audit.FilePath!).Select(line => JsonNode.Parse(line)!.AsObject())];

    public void Dispose() => _temp.Dispose();
}
