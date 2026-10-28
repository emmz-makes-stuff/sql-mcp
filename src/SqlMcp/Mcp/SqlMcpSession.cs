using SqlMcp.Audit;
using SqlMcp.Classification;
using SqlMcp.Configuration;
using SqlMcp.Execution;
using SqlMcp.Introspection;
using SqlMcp.Permissions;

namespace SqlMcp.Mcp;

/// <summary>The engine-specific parts behind one <see cref="Engine"/> (design D2, introspection I2).</summary>
internal sealed record EngineServices(IStatementClassifier Classifier, IQueryExecutor Executor, ICatalogReader CatalogReader, IScriptGenerator ScriptGenerator);

/// <summary>
/// Everything one <c>serve</c> process works from: the effective databases, or the config error that stopped them
/// loading, plus the audit log, the result store and the engines. Config is read once at start-up, so an approval
/// or an edit applies from the next start.
/// </summary>
internal sealed class SqlMcpSession(
    string version,
    IReadOnlyList<string> configFiles,
    IReadOnlyList<EffectiveDatabase> databases,
    string? configError,
    string noDatabasesMessage,
    AuditLog audit,
    ResultStore results,
    IReadOnlyDictionary<Engine, EngineServices> engines,
    string projectDirectory,
    string scriptsFallbackDirectory,
    TimeProvider time,
    TextWriter diagnostics) : IDisposable
{
    public string Version { get; } = version;

    /// <summary>The config files that exist, user file first. Recorded in <c>session_start</c> and <c>session_end</c>.</summary>
    public IReadOnlyList<string> ConfigFiles { get; } = configFiles;

    public IReadOnlyList<EffectiveDatabase> Databases { get; } = databases;

    /// <summary>
    /// Why the config couldn't be loaded, naming the file and JSON path. While set, there are no databases and every
    /// tool returns this (2026-10-25 decision: start anyway, and let the tools report it).
    /// </summary>
    public string? ConfigError { get; } = configError;

    public string NoDatabasesMessage { get; } = noDatabasesMessage;

    public AuditLog Audit { get; } = audit;

    public ResultStore Results { get; } = results;

    public IReadOnlyDictionary<Engine, EngineServices> Engines { get; } = engines;

    /// <summary>Where <c>script_database</c> writes: this project's <c>.sql-mcp/scripts/</c>, or the fallback (design I6).</summary>
    public string ProjectDirectory { get; } = projectDirectory;

    public string ScriptsFallbackDirectory { get; } = scriptsFallbackDirectory;

    public TimeProvider Time { get; } = time;

    /// <summary>The server's stderr, for problems a person must see that no tool result can carry.</summary>
    public TextWriter Diagnostics { get; } = diagnostics;

    /// <summary>
    /// Discovers and layers the config files. An invalid file doesn't throw: it becomes <see cref="ConfigError"/>.
    /// The project file's widenings, and each project-only database, count as approved only as <c>approvals.json</c> records them.
    /// </summary>
    public static SqlMcpSession Load(
        ConfigPaths paths,
        Func<string, string?> getVariable,
        AuditLog audit,
        TimeProvider time,
        IReadOnlyDictionary<Engine, EngineServices> engines,
        string version,
        string scriptsFallbackDirectory,
        TextWriter? diagnostics = null)
    {
        var files = new[] { paths.UserConfig, paths.ProjectConfig }.Where(File.Exists).ToList();
        IReadOnlyList<EffectiveDatabase> databases = [];
        string? error = null;
        var noDatabases = new DiscoveredConfig(paths, null, null).NoDatabasesMessage;
        try
        {
            var config = ConfigDiscovery.Discover(paths, getVariable);
            var approval = new ApprovalStore(paths.Approvals, time).Read(config.Project);
            databases = PermissionResolver.Resolve(config.User, config.Project, approval);
        }
        catch (ConfigException ex)
        {
            error = $"sql-mcp can't use its configuration, so no database is available. Ask the user to fix it, then restart the server. {ex.Message}";
        }

        return new SqlMcpSession(version, files, databases, error, noDatabases, audit, new ResultStore(time), engines, paths.ProjectDirectory, scriptsFallbackDirectory, time, diagnostics ?? Console.Error);
    }

    public void Dispose()
    {
        Results.Dispose();
        Audit.Dispose();
    }
}
