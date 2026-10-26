using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlMcp.Cli.Registration;

/// <summary>
/// Gemini CLI: <c>gemini mcp add -s &lt;scope&gt; sql-mcp &lt;launch&gt;</c>. Gemini rejects a <c>--</c> separator but
/// passes the arguments after the command through unchanged. <c>add</c> upserts, so <c>--force</c> needs no remove.
/// "Already registered" is a <c>sql-mcp:</c> line in <c>gemini mcp list</c>, which covers every scope.
/// </summary>
internal sealed partial class GeminiRegistration(IProcessRunner runner) : IRegistrationStrategy
{
    private const string Program = "gemini";

    public string Client => "gemini";

    public async Task<RegistrationOutcome> RegisterAsync(RegistrationRequest request, CancellationToken cancellationToken)
    {
        var name = RegistrationRequest.ServerName;
        if (!request.Force)
        {
            var list = await RunAsync(request, ["mcp", "list"], cancellationToken).ConfigureAwait(false);
            if (list.NotFound)
            {
                return Manual(request, "The gemini CLI wasn't found on PATH.");
            }

            if (list.Succeeded && ListedServer().IsMatch(list.StandardOutput))
            {
                return RegistrationOutcome.AlreadyRegistered($"Gemini CLI already has a server named {name}. Use --force to replace it.");
            }
        }

        // Always pass -s: Gemini's default scope is project.
        var add = await RunAsync(request, ["mcp", "add", "-s", request.ScopeName, name, .. request.Launch], cancellationToken).ConfigureAwait(false);
        if (add.NotFound)
        {
            return Manual(request, "The gemini CLI wasn't found on PATH.");
        }

        return add.Succeeded
            ? RegistrationOutcome.Registered(add.Message)
            : Manual(request, $"gemini mcp add failed: {add.Message}");
    }

    private Task<ProcessResult> RunAsync(RegistrationRequest request, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        runner.RunAsync(Program, arguments, request.ProjectDirectory, cancellationToken);

    private static RegistrationOutcome Manual(RegistrationRequest request, string detail)
    {
        var server = new JsonObject
        {
            ["command"] = request.Command,
            ["args"] = ManualSnippet.Array(request.Arguments),
        };
        var file = request.Scope == RegistrationScope.Project
            ? Path.Combine(request.ProjectDirectory, ".gemini", "settings.json")
            : Path.Combine(request.HomeDirectory, ".gemini", "settings.json");
        return ManualSnippet.Failed(detail, file, ManualSnippet.McpServers(server));
    }

    /// <summary>A <c>gemini mcp list</c> line such as <c>✓ sql-mcp: sql-mcp serve (stdio) - Connected</c>.</summary>
    [GeneratedRegex(@"^\s*\S+\s+sql-mcp:", RegexOptions.Multiline)]
    private static partial Regex ListedServer();
}
