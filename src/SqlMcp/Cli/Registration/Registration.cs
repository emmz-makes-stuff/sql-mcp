using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlMcp.Cli.Registration;

internal enum RegistrationScope
{
    User,
    Project,
}

internal enum Launcher
{
    Tool,
    Dnx,
}

/// <summary>What to register: the launch command (program and arguments), where, and whether to replace an existing entry.</summary>
internal sealed record RegistrationRequest(
    RegistrationScope Scope,
    IReadOnlyList<string> Launch,
    bool Force,
    string ProjectDirectory,
    string HomeDirectory)
{
    public const string ServerName = "sql-mcp";

    public string Command => Launch[0];

    public IReadOnlyList<string> Arguments => [.. Launch.Skip(1)];

    public string ScopeName => Scope == RegistrationScope.User ? "user" : "project";
}

internal enum RegistrationStatus
{
    Registered,
    AlreadyRegistered,
    Failed,
}

/// <summary>
/// The result of one strategy. <see cref="Detail"/> is the client's message. On failure, <see cref="ManualFile"/> and
/// <see cref="ManualSnippet"/> say what to add by hand.
/// </summary>
internal sealed record RegistrationOutcome(RegistrationStatus Status, string Detail, string? ManualFile = null, string? ManualSnippet = null)
{
    public static RegistrationOutcome Registered(string detail) => new(RegistrationStatus.Registered, detail);

    public static RegistrationOutcome AlreadyRegistered(string detail) => new(RegistrationStatus.AlreadyRegistered, detail);
}

/// <summary>One coding agent's way of adding an MCP server.</summary>
internal interface IRegistrationStrategy
{
    string Client { get; }

    Task<RegistrationOutcome> RegisterAsync(RegistrationRequest request, CancellationToken cancellationToken);
}

/// <summary>Shared by the strategies: the JSON snippet a person can paste when the client's CLI can't do it.</summary>
internal static class ManualSnippet
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary><c>{ "mcpServers": { "sql-mcp": { ...server } } }</c>, the shape Claude Code and Gemini share.</summary>
    public static string McpServers(JsonObject server) =>
        new JsonObject { ["mcpServers"] = new JsonObject { [RegistrationRequest.ServerName] = server } }.ToJsonString(Options);

    public static JsonArray Array(IEnumerable<string> values) => [.. values.Select(v => (JsonNode?)JsonValue.Create(v))];

    public static RegistrationOutcome Failed(string detail, string file, string snippet) =>
        new(RegistrationStatus.Failed, detail, file, snippet);
}
