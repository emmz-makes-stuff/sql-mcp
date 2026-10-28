using SqlMcp.Configuration;

namespace SqlMcp.Tests.Configuration;

/// <summary>Spec: creation-script, "Scripts are git-ignored" (design I6).</summary>
public sealed class ProjectLayoutTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string GitIgnorePath => Path.Combine(_temp.Path, ".gitignore");

    [Fact]
    public void Missing_file_is_created_with_both_lines()
    {
        var change = ProjectLayout.EnsureGitIgnoreLines(_temp.Path);

        Assert.Equal("logs/\nscripts/\n", File.ReadAllText(GitIgnorePath));
        Assert.True(change.Created);
        Assert.Equal(GitIgnorePath, change.Path);
    }

    [Fact]
    public void Project_initialised_before_this_change_gains_scripts()
    {
        _temp.Write(".gitignore", "logs/\n");

        var change = ProjectLayout.EnsureGitIgnoreLines(_temp.Path);

        Assert.Equal("logs/\nscripts/\n", File.ReadAllText(GitIgnorePath));
        Assert.False(change.Created);
        Assert.Equal(["scripts/"], change.Added);
    }

    [Fact]
    public void Extra_user_lines_are_kept_and_only_missing_lines_added()
    {
        _temp.Write(".gitignore", "# mine\r\n*.bak\r\n  scripts/  \r\nlocal/");

        var change = ProjectLayout.EnsureGitIgnoreLines(_temp.Path);

        Assert.Equal("# mine\r\n*.bak\r\n  scripts/  \r\nlocal/\nlogs/\n", File.ReadAllText(GitIgnorePath));
        Assert.Equal(["logs/"], change.Added);
    }

    [Fact]
    public void Complete_file_is_left_untouched()
    {
        var path = _temp.Write(".gitignore", "scripts/\nlogs/\n");
        var before = File.GetLastWriteTimeUtc(path);

        var change = ProjectLayout.EnsureGitIgnoreLines(_temp.Path);

        Assert.Empty(change.Added);
        Assert.False(change.Created);
        Assert.Equal("scripts/\nlogs/\n", File.ReadAllText(GitIgnorePath));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }
}
