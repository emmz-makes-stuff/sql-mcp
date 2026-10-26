using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlMcp.Configuration;

namespace SqlMcp.Cli.Registration;

/// <summary>
/// OpenCode. Its v1 <c>mcp add</c> is an interactive wizard, so the config file is edited with the JSONC editor
/// (design D5): <c>~/.config/opencode/opencode.json(c)</c> at user scope, <c>opencode.json(c)</c> at project scope.
/// The shape depends on the major version from <c>opencode --version</c>: v1 writes
/// <c>mcp.sql-mcp = { type: "local", command: [...], enabled: true }</c>; v2 writes <c>mcp.servers.sql-mcp</c>
/// without the default <c>disabled: false</c>.
/// </summary>
internal sealed class OpenCodeRegistration(IProcessRunner runner) : IRegistrationStrategy
{
    private const string Program = "opencode";

    public string Client => "opencode";

    public async Task<RegistrationOutcome> RegisterAsync(RegistrationRequest request, CancellationToken cancellationToken)
    {
        var version = await runner.RunAsync(Program, ["--version"], request.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        var path = ConfigFile(request);
        if (!version.Succeeded)
        {
            var detail = version.NotFound ? "The opencode CLI wasn't found on PATH." : $"opencode --version failed: {version.Message}";
            return Manual(request, 1, path, detail);
        }

        var major = MajorVersion(version.StandardOutput);
        if (major is not (1 or 2))
        {
            return Manual(request, 1, path, $"OpenCode version \"{version.StandardOutput.Trim()}\" isn't one sql-mcp knows how to configure (v1 or v2).");
        }

        JsoncEditor editor;
        try
        {
            editor = new JsoncEditor(File.Exists(path) ? File.ReadAllText(path) : "{}");
        }
        catch (JsonException ex)
        {
            return Manual(request, major.Value, path, $"{path} isn't valid JSON, so it was left unchanged: {ex.Message}");
        }

        JsoncSegment[] key = Key(major.Value);
        if (editor.Exists(key) && !request.Force)
        {
            return RegistrationOutcome.AlreadyRegistered($"{path} already has a server named {RegistrationRequest.ServerName}. Use --force to replace it.");
        }

        editor.Set(key, Server(request, major.Value));
        AtomicFile.WriteAllText(path, editor.Text);
        return RegistrationOutcome.Registered($"Wrote {string.Join('.', key)} to {path}.");
    }

    /// <summary>The first number in the output, e.g. 1 from <c>1.14.33</c> or <c>opencode v2.0.1</c>.</summary>
    public static int? MajorVersion(string output)
    {
        var start = output.AsSpan().IndexOfAnyInRange('0', '9');
        if (start < 0)
        {
            return null;
        }

        var digits = output.AsSpan(start);
        var end = digits.IndexOfAnyExceptInRange('0', '9');
        return int.TryParse(end < 0 ? digits : digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : null;
    }

    private static JsoncSegment[] Key(int major) => major == 1
        ? ["mcp", RegistrationRequest.ServerName]
        : ["mcp", "servers", RegistrationRequest.ServerName];

    private static JsonObject Server(RegistrationRequest request, int major)
    {
        var server = new JsonObject { ["type"] = "local", ["command"] = ManualSnippet.Array(request.Launch) };
        if (major == 1)
        {
            server["enabled"] = true;
        }

        return server;
    }

    /// <summary>An existing <c>opencode.jsonc</c> is used if there is one; otherwise <c>opencode.json</c>.</summary>
    private static string ConfigFile(RegistrationRequest request)
    {
        var directory = request.Scope == RegistrationScope.Project
            ? request.ProjectDirectory
            : Path.Combine(request.HomeDirectory, ".config", "opencode");
        var jsonc = Path.Combine(directory, "opencode.jsonc");
        return File.Exists(jsonc) ? jsonc : Path.Combine(directory, "opencode.json");
    }

    private static RegistrationOutcome Manual(RegistrationRequest request, int major, string path, string detail)
    {
        var editor = new JsoncEditor("{}");
        editor.Set(Key(major), Server(request, major));
        return ManualSnippet.Failed(detail, path, editor.Text);
    }
}
