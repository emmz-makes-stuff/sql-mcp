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

/// <summary>A catalog reader that records what it was asked for and returns a canned outcome.</summary>
internal sealed class FakeCatalogReader : ICatalogReader
{
    public List<ObjectFilter> Lists { get; } = [];

    public CatalogOutcome<ObjectList> ListOutcome { get; set; } =
        new CatalogSucceeded<ObjectList>(new ObjectList([], 0, false), TimeSpan.FromMilliseconds(4));

    public List<ObjectName> Describes { get; } = [];

    public CatalogOutcome<ObjectDetails> DescribeOutcome { get; set; } =
        new CatalogSucceeded<ObjectDetails>(new ObjectDetails("dbo", "orders", ObjectType.Table), TimeSpan.FromMilliseconds(4));

    public List<IReadOnlyList<ObjectName>> Resolves { get; } = [];

    /// <summary>
    /// What names resolve to. By default each name resolves to a table of that name in <c>dbo</c>, and the inventory
    /// holds just those tables.
    /// </summary>
    public CatalogOutcome<Resolution>? ResolveOutcome { get; set; }

    public int Inventories { get; private set; }

    public CatalogOutcome<IReadOnlyList<InventoryItem>> InventoryOutcome { get; set; } =
        new CatalogSucceeded<IReadOnlyList<InventoryItem>>([], TimeSpan.FromMilliseconds(4));

    public CatalogOutcome<string> ServerVersionOutcome { get; set; } = new CatalogSucceeded<string>("17.0.1000.7", TimeSpan.FromMilliseconds(1));

    /// <summary>When set, the call is cancelled as if the client gave up while the catalog was read.</summary>
    public bool Cancels { get; set; }

    /// <summary>When set, the call throws this, as a reader bug would.</summary>
    public Exception? Throws { get; set; }

    public Task<CatalogOutcome<ObjectList>> ListAsync(string connectionString, ObjectFilter filter, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        Lists.Add(filter);
        return Respond(ListOutcome);
    }

    public Task<CatalogOutcome<Resolution>> ResolveAsync(string connectionString, IReadOnlyList<ObjectName> names, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        Resolves.Add(names);
        if (ResolveOutcome is not null)
        {
            return Respond(ResolveOutcome);
        }

        List<ObjectSummary> objects = [.. names.Select(n => new ObjectSummary(n.Schema ?? "dbo", n.Name, ObjectType.Table, null))];
        return Respond<Resolution>(new CatalogSucceeded<Resolution>(
            new Resolution(objects, [.. objects.Select(o => new InventoryItem(o.ToItem(), InventoryClass.Scriptable))]), TimeSpan.FromMilliseconds(2)));
    }

    public Task<CatalogOutcome<ObjectDetails>> DescribeAsync(string connectionString, ObjectName name, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        Describes.Add(name);
        return Respond(DescribeOutcome);
    }

    public Task<CatalogOutcome<IReadOnlyList<InventoryItem>>> InventoryAsync(string connectionString, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        Inventories++;
        return Respond(InventoryOutcome);
    }

    public Task<CatalogOutcome<string>> ServerVersionAsync(string connectionString, EffectiveLimits limits, CancellationToken cancellationToken) =>
        Respond(ServerVersionOutcome);

    private Task<CatalogOutcome<T>> Respond<T>(CatalogOutcome<T> outcome)
    {
        if (Throws is not null)
        {
            return Task.FromException<CatalogOutcome<T>>(Throws);
        }

        return Cancels ? Task.FromCanceled<CatalogOutcome<T>>(new CancellationToken(canceled: true)) : Task.FromResult(outcome);
    }
}

/// <summary>A script generator that records what it was asked to script, writes <see cref="Text"/>, and returns a canned outcome.</summary>
internal sealed class FakeScriptGenerator : IScriptGenerator
{
    /// <summary>The objects each call was given: null for the whole database.</summary>
    public List<IReadOnlyList<ObjectSummary>?> Calls { get; } = [];

    public string Text { get; set; } = "CREATE TABLE dbo.orders (id int);\nGO\n";

    public CatalogOutcome<IReadOnlyList<CatalogItem>> Outcome { get; set; } =
        new CatalogSucceeded<IReadOnlyList<CatalogItem>>([new CatalogItem("dbo", "orders", "table")], TimeSpan.FromMilliseconds(6));

    /// <summary>When set, the call is cancelled part way through, after writing <see cref="Text"/>.</summary>
    public bool Cancels { get; set; }

    /// <summary>Runs after <see cref="Text"/> is written, e.g. to take the audit log away before the outcome is logged.</summary>
    public Action? During { get; set; }

    public async Task<CatalogOutcome<IReadOnlyList<CatalogItem>>> GenerateAsync(
        string connectionString, IReadOnlyList<ObjectSummary>? objects, TextWriter writer, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        Calls.Add(objects);
        await writer.WriteAsync(Text);
        During?.Invoke();
        if (Cancels)
        {
            throw new OperationCanceledException();
        }

        return Outcome;
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

    public FakeScriptGenerator Scripts { get; } = new();

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
        var engines = new Dictionary<Engine, EngineServices> { [Engine.SqlServer] = new(new SqlServerStatementClassifier(), Executor, Catalog, Scripts) };
        return SqlMcpSession.Load(Paths, name => _environment.GetValueOrDefault(name), audit, Time, engines, "1.2.3-test", Path.Combine(_temp.Path, "scripts-fallback"));
    }

    /// <summary>The audit log's events, one JSON object per line.</summary>
    public static List<JsonObject> Events(SqlMcpSession session) =>
        [.. File.ReadAllLines(session.Audit.FilePath!).Select(line => JsonNode.Parse(line)!.AsObject())];

    public void Dispose() => _temp.Dispose();
}
