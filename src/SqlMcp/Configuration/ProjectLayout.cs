namespace SqlMcp.Configuration;

/// <summary>The files sql-mcp keeps in a project's <c>.sql-mcp/</c> directory.</summary>
internal static class ProjectLayout
{
    public const string DirectoryName = ".sql-mcp";

    /// <summary>The published schema that <c>init</c> references. It matches the schema's <c>$id</c>.</summary>
    public const string SchemaUrl = "https://raw.githubusercontent.com/emmz-makes-stuff/sql-mcp/main/src/SqlMcp/schema/config.schema.json";

    /// <summary>The directories sql-mcp writes into <c>.sql-mcp/</c>, which <c>.gitignore</c> must list (design I6).</summary>
    public static readonly IReadOnlyList<string> IgnoredDirectories = ["logs/", "scripts/"];

    /// <summary>
    /// Makes sure <c>.gitignore</c> in <paramref name="sqlMcpDirectory"/> lists each of <see cref="IgnoredDirectories"/>,
    /// creating the file or adding the missing lines. The file's other lines are kept as they are.
    /// </summary>
    public static GitIgnoreChange EnsureGitIgnoreLines(string sqlMcpDirectory)
    {
        var path = Path.Combine(sqlMcpDirectory, ".gitignore");
        if (!File.Exists(path))
        {
            AtomicFile.WriteAllText(path, string.Concat(IgnoredDirectories.Select(line => line + "\n")));
            return new GitIgnoreChange(path, Created: true, IgnoredDirectories);
        }

        var existing = File.ReadAllText(path);
        var present = existing.Split('\n').Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = IgnoredDirectories.Where(line => !present.Contains(line)).ToList();
        if (missing.Count == 0)
        {
            return new GitIgnoreChange(path, Created: false, []);
        }

        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        AtomicFile.WriteAllText(path, existing + separator + string.Concat(missing.Select(line => line + "\n")));
        return new GitIgnoreChange(path, Created: false, missing);
    }
}

/// <summary>What <see cref="ProjectLayout.EnsureGitIgnoreLines"/> did: created the file, added <see cref="Added"/>, or nothing.</summary>
internal sealed record GitIgnoreChange(string Path, bool Created, IReadOnlyList<string> Added);
