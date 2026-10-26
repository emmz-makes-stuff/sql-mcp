using SqlMcp.Configuration;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace SqlMcp.Cli.Registration;

/// <summary>
/// Codex. User scope uses <c>codex mcp add sql-mcp -- &lt;launch&gt;</c>, which always writes
/// <c>~/.codex/config.toml</c>. Project scope has no CLI, so <c>.codex/config.toml</c> is edited with Tomlyn's
/// syntax tree, which keeps comments, adding <c>[mcp_servers.sql-mcp]</c>. Codex loads a project config only for
/// trusted projects, so project scope warns about that.
/// </summary>
internal sealed class CodexRegistration(IProcessRunner runner) : IRegistrationStrategy
{
    public const string TrustWarning =
        "Codex only loads a project's .codex/config.toml when the project is trusted. If Codex hasn't trusted this project yet, it ignores this entry.";

    private const string Program = "codex";

    private static readonly string[] TableKey = ["mcp_servers", RegistrationRequest.ServerName];

    public string Client => "codex";

    public Task<RegistrationOutcome> RegisterAsync(RegistrationRequest request, CancellationToken cancellationToken) =>
        request.Scope == RegistrationScope.User
            ? RegisterUserAsync(request, cancellationToken)
            : Task.FromResult(RegisterProject(request));

    private async Task<RegistrationOutcome> RegisterUserAsync(RegistrationRequest request, CancellationToken cancellationToken)
    {
        var name = RegistrationRequest.ServerName;
        var existing = await RunAsync(request, ["mcp", "get", name], cancellationToken).ConfigureAwait(false);
        if (existing.NotFound)
        {
            return Manual(request, "The codex CLI wasn't found on PATH.");
        }

        if (existing.Succeeded)
        {
            if (!request.Force)
            {
                return RegistrationOutcome.AlreadyRegistered($"Codex already has a server named {name}. Use --force to replace it.");
            }

            await RunAsync(request, ["mcp", "remove", name], cancellationToken).ConfigureAwait(false);
        }

        var add = await RunAsync(request, ["mcp", "add", name, "--", .. request.Launch], cancellationToken).ConfigureAwait(false);
        return add.Succeeded
            ? RegistrationOutcome.Registered(add.Message)
            : Manual(request, $"codex mcp add failed: {add.Message}");
    }

    private static RegistrationOutcome RegisterProject(RegistrationRequest request)
    {
        var path = ConfigFile(request);
        DocumentSyntax document;
        if (File.Exists(path))
        {
            document = SyntaxParser.Parse(File.ReadAllText(path), path);
            if (document.HasErrors)
            {
                return Manual(request, $"{path} isn't valid TOML, so it was left unchanged: {string.Join("; ", document.Diagnostics)}");
            }
        }
        else
        {
            document = new DocumentSyntax();
        }

        var existing = Enumerable.Range(0, document.Tables.ChildrenCount)
            .Where(i => IsServerTable(document.Tables.GetChild(i)!))
            .ToList();
        if (existing.Count > 0 && !request.Force)
        {
            return RegistrationOutcome.AlreadyRegistered($"{path} already has [mcp_servers.{RegistrationRequest.ServerName}]. Use --force to replace it.");
        }

        // Remove the server's table and any sub-tables (such as its env), last first.
        foreach (var index in existing.OrderDescending())
        {
            document.Tables.RemoveChildAt(index);
        }

        // A blank line before the new table; two line breaks if the file doesn't end with one.
        var before = document.ToString();
        var newLines = before.Trim().Length == 0 ? 0 : before.EndsWith('\n') ? 1 : 2;
        document.Tables.Add(ServerTable(request, newLines));
        AtomicFile.WriteAllText(path, document.ToString());
        return RegistrationOutcome.Registered($"Wrote [mcp_servers.{RegistrationRequest.ServerName}] to {path}. {TrustWarning}");
    }

    /// <summary><c>[mcp_servers.sql-mcp]</c> itself, or a table under it.</summary>
    private static bool IsServerTable(TableSyntaxBase table)
    {
        var key = KeyParts(table.Name);
        return key.Count >= TableKey.Length && key.Take(TableKey.Length).SequenceEqual(TableKey);
    }

    private static List<string> KeyParts(KeySyntax? key)
    {
        if (key is null)
        {
            return [];
        }

        var parts = new List<string> { KeyText(key.Key) };
        foreach (var item in key.DotKeys)
        {
            parts.Add(KeyText(item.Key));
        }

        return parts;
    }

    private static string KeyText(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bare => bare.Key?.Text ?? "",
        StringValueSyntax quoted => quoted.Value ?? "",
        _ => "",
    };

    private static TableSyntax ServerTable(RegistrationRequest request, int leadingNewLines)
    {
        var table = new TableSyntax(new KeySyntax(TableKey[0], TableKey[1]));
        table.Items.Add("command", request.Command);
        table.Items.Add("args", request.Arguments.ToArray());
        if (leadingNewLines > 0)
        {
            table.OpenBracket!.LeadingTrivia = [new SyntaxTrivia(TokenKind.NewLine, new string('\n', leadingNewLines))];
        }

        return table;
    }

    private static string ConfigFile(RegistrationRequest request) => request.Scope == RegistrationScope.Project
        ? Path.Combine(request.ProjectDirectory, ".codex", "config.toml")
        : Path.Combine(request.HomeDirectory, ".codex", "config.toml");

    private Task<ProcessResult> RunAsync(RegistrationRequest request, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        runner.RunAsync(Program, arguments, request.ProjectDirectory, cancellationToken);

    private static RegistrationOutcome Manual(RegistrationRequest request, string detail)
    {
        var document = new DocumentSyntax();
        document.Tables.Add(ServerTable(request, leadingNewLines: 0));
        return ManualSnippet.Failed(detail, ConfigFile(request), document.ToString().TrimEnd());
    }
}
