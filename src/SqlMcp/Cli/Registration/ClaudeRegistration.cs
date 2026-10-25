using System.Text.Json.Nodes;

namespace SqlMcp.Cli.Registration;

/// <summary>
/// Claude Code: <c>claude mcp add -s &lt;scope&gt; sql-mcp -- &lt;launch&gt;</c>. "Already registered" is
/// <c>claude mcp get sql-mcp</c> exiting 0. <c>--force</c> removes the entry at that scope first.
/// </summary>
internal sealed class ClaudeRegistration(IProcessRunner runner) : IRegistrationStrategy
{
    private const string Program = "claude";

    public string Client => "claude";

    public async Task<RegistrationOutcome> RegisterAsync(RegistrationRequest request, CancellationToken cancellationToken)
    {
        var name = RegistrationRequest.ServerName;
        var existing = await RunAsync(request, ["mcp", "get", name], cancellationToken).ConfigureAwait(false);
        if (existing.NotFound)
        {
            return Manual(request, "The claude CLI wasn't found on PATH.");
        }

        if (existing.Succeeded)
        {
            if (!request.Force)
            {
                return RegistrationOutcome.AlreadyRegistered($"Claude Code already has a server named {name}. Use --force to replace it.");
            }

            // It may be registered at another scope, in which case this fails and the add below reports it.
            await RunAsync(request, ["mcp", "remove", "-s", request.ScopeName, name], cancellationToken).ConfigureAwait(false);
        }

        var add = await RunAsync(request, ["mcp", "add", "-s", request.ScopeName, name, "--", .. request.Launch], cancellationToken).ConfigureAwait(false);
        return add.Succeeded
            ? RegistrationOutcome.Registered(add.Message)
            : Manual(request, $"claude mcp add failed: {add.Message}");
    }

    private Task<ProcessResult> RunAsync(RegistrationRequest request, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        runner.RunAsync(Program, arguments, request.ProjectDirectory, cancellationToken);

    private static RegistrationOutcome Manual(RegistrationRequest request, string detail)
    {
        var server = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = request.Command,
            ["args"] = ManualSnippet.Array(request.Arguments),
        };
        var file = request.Scope == RegistrationScope.Project
            ? Path.Combine(request.ProjectDirectory, ".mcp.json")
            : $"the top-level \"mcpServers\" of {Path.Combine(request.HomeDirectory, ".claude.json")}";
        return ManualSnippet.Failed(detail, file, ManualSnippet.McpServers(server));
    }
}
