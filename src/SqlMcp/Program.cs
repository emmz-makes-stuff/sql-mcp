using SqlMcp.Cli;

return await SqlMcpCli.Build(CliContext.ForCurrentProcess()).Parse(args).InvokeAsync().ConfigureAwait(false);
