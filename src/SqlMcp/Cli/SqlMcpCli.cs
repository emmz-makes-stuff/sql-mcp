using System.CommandLine;
using SqlMcp.Cli.Registration;

namespace SqlMcp.Cli;

/// <summary>The <c>sql-mcp</c> root command (design D10).</summary>
internal static class SqlMcpCli
{
    public static RootCommand Build(CliContext context) =>
        new("sql-mcp: controlled, audited SQL access for coding agents.")
        {
            ServeCommand.Create(),
            InitCommand.Create(context),
            ApproveCommand.Create(context),
            DbCommand.Create(context),
            GrantCommands.CreateAllow(context),
            GrantCommands.CreateDeny(context),
            ConfigShowCommand.Create(context),
            RegisterCommand.Create(context, ServeCommand.Version),
        };
}
