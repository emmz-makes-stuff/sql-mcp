using System.CommandLine;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SqlMcp.Audit;
using SqlMcp.Classification.SqlServer;
using SqlMcp.Configuration;
using SqlMcp.Execution.SqlServer;
using SqlMcp.Introspection;
using SqlMcp.Introspection.SqlServer;
using SqlMcp.Mcp;

namespace SqlMcp.Cli;

/// <summary>
/// <c>sql-mcp serve [--project &lt;dir&gt;]</c>: the MCP server over stdio (design D8). Stdout carries only protocol
/// messages; every log goes to stderr.
/// </summary>
internal static class ServeCommand
{
    public static Command Create()
    {
        var project = new Option<string?>("--project")
        {
            Description = "The project directory to read .sql-mcp/ from. Defaults to the current directory.",
        };
        var command = new Command("serve", "Run the MCP server over stdio. MCP clients start this; you don't run it by hand.") { project };
        command.SetAction((result, cancellationToken) => RunAsync(result.GetValue(project), cancellationToken));
        return command;
    }

    public static string Version { get; } =
        typeof(ServeCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private static async Task<int> RunAsync(string? projectDirectory, CancellationToken cancellationToken)
    {
        var paths = ConfigPaths.ForCurrentUser(projectDirectory);
        var time = TimeProvider.System;
        var audit = AuditLog.Open(paths.ProjectDirectory, AuditLog.FallbackDirectoryForCurrentUser(), time, Environment.ProcessId);
        var engines = new Dictionary<Engine, EngineServices>
        {
            [Engine.SqlServer] = new(new SqlServerStatementClassifier(), new SqlServerQueryExecutor(time), new SqlServerCatalogReader(time), new SmoScriptGenerator(time)),
        };
        using var session = SqlMcpSession.Load(paths, Environment.GetEnvironmentVariable, audit, time, engines, Version, ScriptFiles.FallbackDirectoryForCurrentUser());

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "sql-mcp", Version = Version })
            .WithStdioServerTransport()
            .WithTools(ToolCatalog.Build(session));
        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("sql-mcp");
        Report(logger, session);

        audit.WriteSessionStart(Version, session.ConfigFiles);
        try
        {
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            audit.Write(new SessionEndEvent(Version, session.ConfigFiles));
        }

        return 0;
    }

    /// <summary>Start-up problems go to stderr for whoever reads the client's server log. Tools report them too.</summary>
    private static void Report(ILogger logger, SqlMcpSession session)
    {
        if (session.Audit.UnavailableReason is { } auditProblem)
        {
            Log.AuditUnavailable(logger, auditProblem);
        }
        else
        {
            Log.AuditPath(logger, session.Audit.FilePath!);
        }

        if (session.ConfigError is { } configProblem)
        {
            Log.ConfigError(logger, configProblem);
        }
    }
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Audit log: {Path}")]
    public static partial void AuditPath(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit log unavailable, so every tool call will be refused: {Reason}")]
    public static partial void AuditUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Problem}")]
    public static partial void ConfigError(ILogger logger, string problem);
}
