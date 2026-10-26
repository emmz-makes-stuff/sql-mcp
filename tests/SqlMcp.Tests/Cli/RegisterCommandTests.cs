using System.Text.Json.Nodes;
using SqlMcp.Cli;
using SqlMcp.Cli.Registration;

namespace SqlMcp.Tests.Cli;

/// <summary>Spec: cli, "register" (Claude Code and Gemini CLI), against a fake process runner.</summary>
public sealed class RegisterCommandTests : IDisposable
{
    private static readonly ProcessResult NotFoundExit = new(1, "", "No MCP server found with name: sql-mcp");

    private readonly CliRunner _cli = new();

    public RegisterCommandTests()
    {
        // sql-mcp on PATH, so the launcher is the tool unless a test says otherwise.
        var bin = Path.Combine(_cli.HomeDirectory, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "sql-mcp"), "");
        _cli.Variables["PATH"] = bin;
    }

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Register_with_claude_code_at_project_scope()
    {
        _cli.Processes.On("claude", ["mcp", "get"], NotFoundExit);

        var result = await _cli.RunAsync("register", "claude", "--scope", "project");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["mcp", "add", "-s", "project", "sql-mcp", "--", "sql-mcp", "serve", "--project", _cli.ProjectDirectory],
            _cli.Processes.CallsTo("claude").Last());
        Assert.All(_cli.Processes.Calls, call => Assert.Equal(_cli.ProjectDirectory, call.WorkingDirectory));
        Assert.Contains("Registered sql-mcp with claude at project scope", result.Output, StringComparison.Ordinal);
        Assert.Contains("Teammates whose checkout is elsewhere", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scope_defaults_to_user_without_project_directory()
    {
        _cli.Processes.On("claude", ["mcp", "get"], NotFoundExit);

        var result = await _cli.RunAsync("register", "claude");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["mcp", "add", "-s", "user", "sql-mcp", "--", "sql-mcp", "serve"], _cli.Processes.CallsTo("claude").Last());
        Assert.DoesNotContain("Teammates", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Already_registered_is_reported_and_left_unchanged()
    {
        var result = await _cli.RunAsync("register", "claude");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already has a server named sql-mcp", result.Output, StringComparison.Ordinal);
        Assert.Equal([["mcp", "get", "sql-mcp"]], _cli.Processes.CallsTo("claude"));
    }

    [Fact]
    public async Task Force_removes_at_that_scope_then_adds()
    {
        var result = await _cli.RunAsync("register", "claude", "--scope", "project", "--force");

        Assert.Equal(0, result.ExitCode);
        var calls = _cli.Processes.CallsTo("claude").ToList();
        Assert.Equal(["mcp", "remove", "-s", "project", "sql-mcp"], calls[1]);
        Assert.Equal(["mcp", "add", "-s", "project", "sql-mcp"], calls[2].Take(5));
    }

    [Fact]
    public async Task Client_not_installed_exits_non_zero_and_prints_the_config_to_add()
    {
        _cli.Processes.Missing.Add("claude");

        var result = await _cli.RunAsync("register", "claude", "--scope", "project");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("claude CLI wasn't found", result.Error, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_cli.ProjectDirectory, ".mcp.json"), result.Error, StringComparison.Ordinal);
        var snippet = JsonNode.Parse(result.Error[result.Error.IndexOf('{', StringComparison.Ordinal)..])!;
        var server = snippet["mcpServers"]!["sql-mcp"]!;
        Assert.Equal("stdio", server["type"]!.GetValue<string>());
        Assert.Equal("sql-mcp", server["command"]!.GetValue<string>());
        Assert.Equal(["serve", "--project", _cli.ProjectDirectory], server["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public async Task Client_add_failure_passes_on_its_message_with_the_snippet()
    {
        _cli.Processes.On("claude", ["mcp", "get"], NotFoundExit);
        _cli.Processes.On("claude", ["mcp", "add"], new ProcessResult(1, "", "boom"));

        var result = await _cli.RunAsync("register", "claude");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("claude mcp add failed: boom", result.Error, StringComparison.Ordinal);
        Assert.Contains(".claude.json", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dnx_launcher_when_sql_mcp_is_not_on_path()
    {
        _cli.Variables["PATH"] = Path.Combine(_cli.HomeDirectory, "empty");
        _cli.Processes.On("claude", ["mcp", "get"], NotFoundExit);

        await _cli.RunAsync("register", "claude");

        var add = _cli.Processes.CallsTo("claude").Last();
        Assert.Equal("dnx", add[6]);
        Assert.StartsWith($"{LaunchCommand.PackageId}@", add[7], StringComparison.Ordinal);
        Assert.DoesNotContain("+", add[7], StringComparison.Ordinal);
        Assert.Equal("serve", add[8]);
    }

    [Theory]
    [InlineData("sql-mcp.bat", "sql-mcp")]
    [InlineData("sql-mcp", "dnx")]
    public async Task On_windows_sql_mcp_is_on_path_only_with_a_PATHEXT_extension(string installed, string launcher)
    {
        var a = Path.Combine(_cli.HomeDirectory, "a");
        var b = Path.Combine(_cli.HomeDirectory, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, installed), "");
        _cli.IsWindows = true;
        _cli.Variables["PATH"] = $"{a};{b}";
        _cli.Variables["PATHEXT"] = ".BAT";
        _cli.Processes.On("claude", ["mcp", "get"], NotFoundExit);

        await _cli.RunAsync("register", "claude");

        Assert.Equal(launcher, _cli.Processes.CallsTo("claude").Last()[6]);
    }

    [Fact]
    public async Task Launcher_option_overrides_detection()
    {
        _cli.Processes.On("claude", ["mcp", "get"], NotFoundExit);

        await _cli.RunAsync("register", "claude", "--launcher", "dnx");

        Assert.Equal("dnx", _cli.Processes.CallsTo("claude").Last()[6]);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("1.2.3-beta.1+abc", "1.2.3-beta.1")]
    public void Package_version_drops_the_commit_suffix(string informational, string expected)
    {
        Assert.Equal(expected, LaunchCommand.PackageVersion(informational));
    }

    [Fact]
    public async Task Gemini_user_scope_passes_launch_without_a_separator()
    {
        _cli.Processes.On("gemini", ["mcp", "list"], new ProcessResult(0, "Configured MCP servers:\n\n✓ zen: /x/zen (stdio) - Connected\n", ""));

        var result = await _cli.RunAsync("register", "gemini");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["mcp", "add", "-s", "user", "sql-mcp", "sql-mcp", "serve"], _cli.Processes.CallsTo("gemini").Last());
    }

    [Fact]
    public async Task Gemini_project_scope_arguments()
    {
        _cli.Processes.On("gemini", ["mcp", "list"], new ProcessResult(0, "No MCP servers configured.", ""));

        await _cli.RunAsync("register", "gemini", "--scope", "project", "--launcher", "dnx");

        var add = _cli.Processes.CallsTo("gemini").Last();
        Assert.Equal(["mcp", "add", "-s", "project", "sql-mcp", "dnx"], add.Take(6));
        Assert.Equal(["serve", "--project", _cli.ProjectDirectory], add.Skip(7));
    }

    [Fact]
    public async Task Gemini_already_registered_is_found_in_the_list()
    {
        _cli.Processes.On("gemini", ["mcp", "list"], new ProcessResult(0, "Configured MCP servers:\n\n✗ sql-mcp: sql-mcp serve (stdio) - Disconnected\n", ""));

        var result = await _cli.RunAsync("register", "gemini");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already has a server named sql-mcp", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_cli.Processes.CallsTo("gemini"), call => call[1] == "add");
    }

    [Fact]
    public async Task Gemini_force_upserts_without_listing()
    {
        await _cli.RunAsync("register", "gemini", "--force");

        Assert.Equal([["mcp", "add", "-s", "user", "sql-mcp", "sql-mcp", "serve"]], _cli.Processes.CallsTo("gemini"));
    }

    [Fact]
    public async Task Gemini_untrusted_workspace_fails_with_its_message_and_the_snippet()
    {
        _cli.Processes.On("gemini", ["mcp", "add"], new ProcessResult(1, "", "Error: Cannot modify settings in an untrusted workspace."));

        var result = await _cli.RunAsync("register", "gemini", "--scope", "project");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("untrusted workspace", result.Error, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_cli.ProjectDirectory, ".gemini", "settings.json"), result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(_cli.Processes.Calls, call => call.Arguments.Any(a => a.Contains("TRUST", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Gemini_not_installed()
    {
        _cli.Processes.Missing.Add("gemini");

        var result = await _cli.RunAsync("register", "gemini");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("gemini CLI wasn't found", result.Error, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_cli.HomeDirectory, ".gemini", "settings.json"), result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_client_or_scope_is_a_parse_error()
    {
        Assert.NotEqual(0, (await _cli.RunAsync("register", "cursor")).ExitCode);
        Assert.NotEqual(0, (await _cli.RunAsync("register", "claude", "--scope", "local")).ExitCode);
        Assert.Empty(_cli.Processes.Calls);
    }
}
