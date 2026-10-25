using SqlMcp.Cli.Registration;

namespace SqlMcp.Tests.Cli;

/// <summary>The Windows PATH and PATHEXT lookup for client CLIs, which may be npm .cmd shims.</summary>
public sealed class ProgramLookupTests
{
    [Fact]
    public void Finds_a_cmd_shim_in_path_order()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("B", "gemini.cmd"), Path.Combine("C", "gemini.exe") };

        var resolved = ProgramLookup.Resolve("gemini", "A;B;C", ".COM;.EXE;.BAT;.CMD", files.Contains);

        Assert.Equal(Path.Combine("B", "gemini.cmd"), resolved, ignoreCase: true);
    }

    [Fact]
    public void Extension_order_within_a_directory_follows_pathext()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("A", "claude.cmd"), Path.Combine("A", "claude.exe") };

        Assert.Equal(Path.Combine("A", "claude.exe"), ProgramLookup.Resolve("claude", "A", ".EXE;.CMD", files.Contains), ignoreCase: true);
    }

    [Fact]
    public void Missing_pathext_uses_the_windows_default()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("A", "codex.CMD") };

        Assert.Equal(Path.Combine("A", "codex.CMD"), ProgramLookup.Resolve("codex", "A", null, files.Contains));
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("tool.exe")]
    public void Not_found_or_already_has_an_extension_is_left_unchanged(string program)
    {
        Assert.Equal(program, ProgramLookup.Resolve(program, "A;B", ".EXE;.CMD", _ => false));
    }

    [Fact]
    public void Empty_path_is_left_unchanged()
    {
        Assert.Equal("claude", ProgramLookup.Resolve("claude", null, ".EXE", _ => true));
    }
}
