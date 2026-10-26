using System.CommandLine;

namespace SqlMcp.Cli.Registration;

/// <summary>
/// <c>sql-mcp register &lt;client&gt; [--scope user|project] [--launcher tool|dnx] [--force]</c> (design D10). Scope
/// defaults to user. When the client can't do it, the command fails and prints what to add by hand.
/// </summary>
internal static class RegisterCommand
{
    public static Command Create(CliContext context, string version)
    {
        IRegistrationStrategy[] strategies =
        [
            new ClaudeRegistration(context.ProcessRunner),
            new CodexRegistration(context.ProcessRunner),
            new GeminiRegistration(context.ProcessRunner),
            new OpenCodeRegistration(context.ProcessRunner),
        ];

        var client = new Argument<string>("client") { Description = "The coding agent to register with." };
        client.AcceptOnlyFromAmong([.. strategies.Select(s => s.Client)]);
        var scope = new Option<RegistrationScope>("--scope")
        {
            Description = "user (every project; the default) or project (the client's shared project config).",
            DefaultValueFactory = _ => RegistrationScope.User,
        };
        var launcher = new Option<Launcher?>("--launcher")
        {
            Description = "How the client starts sql-mcp: tool (sql-mcp on PATH) or dnx. Defaults to tool if sql-mcp is on PATH.",
        };
        var force = new Option<bool>("--force") { Description = "Replace an existing sql-mcp registration." };

        var command = new Command("register", "Add sql-mcp as an MCP server to a coding agent.") { client, scope, launcher, force };
        CliAction.Set(command, async (result, cancellationToken) =>
        {
            var strategy = strategies.Single(s => s.Client == result.GetValue(client));
            var chosenScope = result.GetValue(scope);
            var launch = LaunchCommand.Build(LaunchCommand.Choose(result.GetValue(launcher), context), version, chosenScope, context.CurrentDirectory);
            var request = new RegistrationRequest(chosenScope, launch, result.GetValue(force), context.CurrentDirectory, context.HomeDirectory);

            var outcome = await strategy.RegisterAsync(request, cancellationToken).ConfigureAwait(false);
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            switch (outcome.Status)
            {
                case RegistrationStatus.AlreadyRegistered:
                    output.WriteLine(outcome.Detail);
                    return 0;

                case RegistrationStatus.Registered:
                    output.WriteLine($"Registered sql-mcp with {strategy.Client} at {request.ScopeName} scope: {string.Join(' ', launch)}");
                    if (outcome.Detail.Length > 0)
                    {
                        output.WriteLine(outcome.Detail);
                    }

                    if (chosenScope == RegistrationScope.Project)
                    {
                        output.WriteLine(
                            $"Note: the launch command names this checkout ({context.CurrentDirectory}). Teammates whose checkout is elsewhere need to run `sql-mcp register {strategy.Client} --scope project` themselves.");
                    }

                    return 0;

                default:
                    await error.WriteLineAsync(outcome.Detail).ConfigureAwait(false);
                    await error.WriteLineAsync($"To register by hand, add this to {outcome.ManualFile}:").ConfigureAwait(false);
                    await error.WriteLineAsync(outcome.ManualSnippet).ConfigureAwait(false);
                    return CliAction.Failure;
            }
        });
        return command;
    }
}
