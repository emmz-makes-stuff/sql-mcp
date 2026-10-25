using System.CommandLine;
using SqlMcp.Cli;

// The remaining commands (init, approve, db, allow/deny, config show, register) land in section 8.
var root = new RootCommand("sql-mcp: controlled, audited SQL access for coding agents.") { ServeCommand.Create() };
return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
