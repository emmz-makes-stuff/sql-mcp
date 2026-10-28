using System.Diagnostics;
using SqlMcp.Audit;
using SqlMcp.Introspection;

namespace SqlMcp.Tests.Audit;

/// <summary>
/// Specs "Logs are git-ignored" and "Scripts are git-ignored": checked against real git, not by reading the .gitignore
/// file.
/// </summary>
public sealed class AuditGitIgnoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Log_directory_in_a_repo_is_not_listed_by_git_status()
    {
        Git("init", "--quiet");
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".sql-mcp"));

        using (var log = AuditLog.Open(_temp.Path, Path.Combine(_temp.Path, "fallback"), TimeProvider.System, 77))
        {
            Assert.StartsWith(Path.Combine(_temp.Path, ".sql-mcp", "logs"), log.FilePath, StringComparison.Ordinal);
            log.WriteSessionStart("1.0.0", []);
        }

        var status = Git("status", "--porcelain", "--untracked-files=all");

        Assert.Contains(".sql-mcp/.gitignore", status, StringComparison.Ordinal);
        Assert.DoesNotContain("logs", status, StringComparison.Ordinal);
        Assert.DoesNotContain(".jsonl", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_initialised_before_this_change_does_not_show_the_script()
    {
        Git("init", "--quiet");
        _temp.Write(".sql-mcp/.gitignore", "logs/\n");

        var outcome = await ScriptFiles.WriteAsync<int>(
            _temp.Path,
            Path.Combine(_temp.Path, "fallback"),
            new ScriptHeader("localhost:1433/app", "17.0", DateTimeOffset.UtcNow, "1.0.0"),
            async writer =>
            {
                await writer.WriteAsync("SELECT 1;\nGO\n");
                return new CatalogSucceeded<int>(0, TimeSpan.Zero);
            },
            TestContext.Current.CancellationToken);

        var written = Assert.IsType<CatalogSucceeded<WrittenScript<int>>>(outcome).Value;
        Assert.StartsWith(Path.Combine(_temp.Path, ".sql-mcp", "scripts"), written.Path, StringComparison.Ordinal);
        var status = Git("status", "--porcelain", "--untracked-files=all");

        Assert.Contains(".sql-mcp/.gitignore", status, StringComparison.Ordinal);
        Assert.DoesNotContain("scripts", status, StringComparison.Ordinal);
        Assert.DoesNotContain(".sql", status.Replace(".sql-mcp", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _temp.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
