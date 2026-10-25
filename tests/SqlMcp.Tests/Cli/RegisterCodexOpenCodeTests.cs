using System.Text.Json.Nodes;
using SqlMcp.Cli.Registration;
using SqlMcp.Configuration;
using Tomlyn.Parsing;

namespace SqlMcp.Tests.Cli;

/// <summary>Spec: cli, "register" for Codex and OpenCode, on fake runners and temp config files.</summary>
public sealed class RegisterCodexOpenCodeTests : IDisposable
{
    private readonly CliRunner _cli = new();

    public RegisterCodexOpenCodeTests()
    {
        var bin = Path.Combine(_cli.HomeDirectory, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "sql-mcp"), "");
        _cli.Variables["PATH"] = bin;
    }

    private string CodexProjectFile => Path.Combine(_cli.ProjectDirectory, ".codex", "config.toml");

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Codex_user_scope_uses_the_cli()
    {
        _cli.Processes.On("codex", ["mcp", "get"], new ProcessResult(1, "", "not found"));

        var result = await _cli.RunAsync("register", "codex");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["mcp", "add", "sql-mcp", "--", "sql-mcp", "serve"], _cli.Processes.CallsTo("codex").Last());
    }

    [Fact]
    public async Task Codex_user_scope_already_registered_and_force()
    {
        var already = await _cli.RunAsync("register", "codex");
        var forced = await _cli.RunAsync("register", "codex", "--force");

        Assert.Contains("already has a server named sql-mcp", already.Output, StringComparison.Ordinal);
        Assert.Equal(0, forced.ExitCode);
        var calls = _cli.Processes.CallsTo("codex").ToList();
        Assert.Equal(["mcp", "remove", "sql-mcp"], calls[^2]);
        Assert.Equal("add", calls[^1][1]);
    }

    [Fact]
    public async Task Codex_user_scope_not_installed_prints_toml()
    {
        _cli.Processes.Missing.Add("codex");

        var result = await _cli.RunAsync("register", "codex");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(Path.Combine(_cli.HomeDirectory, ".codex", "config.toml"), result.Error, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.sql-mcp]", result.Error, StringComparison.Ordinal);
        Assert.Contains("command = \"sql-mcp\"", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codex_project_scope_creates_the_file_and_warns_about_trust()
    {
        var result = await _cli.RunAsync("register", "codex", "--scope", "project", "--launcher", "dnx");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_cli.Processes.Calls);
        Assert.Contains(CodexRegistration.TrustWarning, result.Output, StringComparison.Ordinal);
        var server = Server(File.ReadAllText(CodexProjectFile));
        Assert.Equal("dnx", server.Command);
        Assert.Equal(["serve", "--project", _cli.ProjectDirectory], server.Args.Skip(1));
    }

    [Fact]
    public async Task Codex_project_scope_keeps_comments_and_other_servers()
    {
        _cli.Project.Write(".codex/config.toml", """
            # Project settings for Codex
            model = "gpt-5" # pinned

            [mcp_servers.other]
            command = "other"
            """);

        var result = await _cli.RunAsync("register", "codex", "--scope", "project");

        Assert.Equal(0, result.ExitCode);
        var text = File.ReadAllText(CodexProjectFile);
        Assert.StartsWith("""
            # Project settings for Codex
            model = "gpt-5" # pinned

            [mcp_servers.other]
            command = "other"
            """, text, StringComparison.Ordinal);
        Assert.Contains("\n\n[mcp_servers.sql-mcp]\n", text, StringComparison.Ordinal);
        Assert.Equal("sql-mcp", Server(text).Command);
    }

    [Fact]
    public async Task Codex_project_scope_already_registered_unless_forced()
    {
        const string original = """
            [mcp_servers.sql-mcp]
            command = "old"

            [mcp_servers.sql-mcp.env]
            X = "1"

            [mcp_servers."other"]
            command = "other" # keep
            """;
        _cli.Project.Write(".codex/config.toml", original);

        var already = await _cli.RunAsync("register", "codex", "--scope", "project");
        Assert.Equal(0, already.ExitCode);
        Assert.Contains("already has [mcp_servers.sql-mcp]", already.Output, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(CodexProjectFile));

        var forced = await _cli.RunAsync("register", "codex", "--scope", "project", "--force");
        Assert.Equal(0, forced.ExitCode);
        var text = File.ReadAllText(CodexProjectFile);
        Assert.DoesNotContain("command = \"old\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[mcp_servers.sql-mcp.env]", text, StringComparison.Ordinal);
        Assert.Contains("command = \"other\" # keep", text, StringComparison.Ordinal);
        Assert.Equal("sql-mcp", Server(text).Command);
    }

    [Fact]
    public async Task Codex_project_scope_invalid_toml_is_left_alone()
    {
        const string broken = "[mcp_servers\ncommand = ";
        _cli.Project.Write(".codex/config.toml", broken);

        var result = await _cli.RunAsync("register", "codex", "--scope", "project");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("isn't valid TOML", result.Error, StringComparison.Ordinal);
        Assert.Equal(broken, File.ReadAllText(CodexProjectFile));
    }

    [Theory]
    [InlineData("1.14.33", 1)]
    [InlineData("opencode v2.0.1\n", 2)]
    [InlineData("10.0.0", 10)]
    [InlineData("unknown", null)]
    public void OpenCode_major_version(string output, int? expected)
    {
        Assert.Equal(expected, OpenCodeRegistration.MajorVersion(output));
    }

    [Fact]
    public async Task OpenCode_v1_user_scope_writes_mcp_entry()
    {
        _cli.Processes.On("opencode", ["--version"], new ProcessResult(0, "1.14.33\n", ""));

        var result = await _cli.RunAsync("register", "opencode");

        Assert.Equal(0, result.ExitCode);
        var path = Path.Combine(_cli.HomeDirectory, ".config", "opencode", "opencode.json");
        var server = Json(path)["mcp"]!["sql-mcp"]!;
        Assert.Equal("local", server["type"]!.GetValue<string>());
        Assert.Equal(["sql-mcp", "serve"], server["command"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(server["enabled"]!.GetValue<bool>());
        Assert.Equal([["--version"]], _cli.Processes.CallsTo("opencode"));
    }

    [Fact]
    public async Task OpenCode_v2_project_scope_writes_servers_entry_in_existing_jsonc()
    {
        _cli.Processes.On("opencode", ["--version"], new ProcessResult(0, "2.0.0", ""));
        _cli.Project.Write("opencode.jsonc", """
            {
              // team settings
              "theme": "dark",
              "mcp": {
                "servers": {
                  "other": { "type": "local", "command": ["other"] },
                },
              },
            }
            """);

        var result = await _cli.RunAsync("register", "opencode", "--scope", "project");

        Assert.Equal(0, result.ExitCode);
        var path = Path.Combine(_cli.ProjectDirectory, "opencode.jsonc");
        var text = File.ReadAllText(path);
        Assert.Contains("// team settings", text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_cli.ProjectDirectory, "opencode.json")));
        var server = Json(path)["mcp"]!["servers"]!["sql-mcp"]!;
        Assert.Equal(["sql-mcp", "serve", "--project", _cli.ProjectDirectory], server["command"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(server["disabled"]);
        Assert.Null(server["enabled"]);
        Assert.NotNull(Json(path)["mcp"]!["servers"]!["other"]);
    }

    [Fact]
    public async Task OpenCode_already_registered_unless_forced()
    {
        _cli.Processes.On("opencode", ["--version"], new ProcessResult(0, "1.2.0", ""));
        _cli.Project.Write("opencode.json", """{ "mcp": { "sql-mcp": { "type": "local", "command": ["old"], "enabled": true } } }""");
        var path = Path.Combine(_cli.ProjectDirectory, "opencode.json");

        var already = await _cli.RunAsync("register", "opencode", "--scope", "project");
        Assert.Contains("already has a server named sql-mcp", already.Output, StringComparison.Ordinal);
        Assert.Equal("old", Json(path)["mcp"]!["sql-mcp"]!["command"]![0]!.GetValue<string>());

        await _cli.RunAsync("register", "opencode", "--scope", "project", "--force");
        Assert.Equal("sql-mcp", Json(path)["mcp"]!["sql-mcp"]!["command"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenCode_not_installed_prints_the_snippet()
    {
        _cli.Processes.Missing.Add("opencode");

        var result = await _cli.RunAsync("register", "opencode");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("opencode CLI wasn't found", result.Error, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": true", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenCode_unknown_version_is_refused()
    {
        _cli.Processes.On("opencode", ["--version"], new ProcessResult(0, "3.0.0", ""));

        var result = await _cli.RunAsync("register", "opencode");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("isn't one sql-mcp knows how to configure", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_cli.HomeDirectory, ".config", "opencode", "opencode.json")));
    }

    private static JsonNode Json(string path) => JsonNode.Parse(File.ReadAllText(path), documentOptions: ConfigLoader.DocumentOptions)!;

    private static (string Command, string[] Args) Server(string toml)
    {
        var document = SyntaxParser.Parse(toml);
        Assert.False(document.HasErrors, string.Join("; ", document.Diagnostics));
        var table = document.Tables.Single(t => t.Name!.ToString().Trim() == "mcp_servers.sql-mcp");
        var items = table.Items.ToDictionary(kv => kv.Key!.ToString().Trim(), kv => kv.Value!);
        var command = ((Tomlyn.Syntax.StringValueSyntax)items["command"]).Value!;
        var args = ((Tomlyn.Syntax.ArraySyntax)items["args"]).Items.Select(i => ((Tomlyn.Syntax.StringValueSyntax)i.Value!).Value!).ToArray();
        return (command, args);
    }
}
