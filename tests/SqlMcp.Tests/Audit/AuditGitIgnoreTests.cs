using System.Diagnostics;
using SqlMcp.Audit;

namespace SqlMcp.Tests.Audit;

/// <summary>Spec "Logs are git-ignored": checked against real git, not by reading the .gitignore file.</summary>
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
