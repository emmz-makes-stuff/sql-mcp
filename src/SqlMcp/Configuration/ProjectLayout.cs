namespace SqlMcp.Configuration;

/// <summary>The files sql-mcp keeps in a project's <c>.sql-mcp/</c> directory.</summary>
internal static class ProjectLayout
{
    public const string DirectoryName = ".sql-mcp";

    /// <summary>The published schema that <c>init</c> references. It matches the schema's <c>$id</c>.</summary>
    public const string SchemaUrl = "https://raw.githubusercontent.com/emmz-makes-stuff/sql-mcp/main/src/SqlMcp/schema/config.schema.json";

    public const string GitIgnoreContent = "logs/\n";

    /// <summary>Creates <c>.gitignore</c> (containing <c>logs/</c>) in <paramref name="sqlMcpDirectory"/>. Returns false if it already exists.</summary>
    public static bool EnsureGitIgnore(string sqlMcpDirectory)
    {
        var path = Path.Combine(sqlMcpDirectory, ".gitignore");
        if (File.Exists(path))
        {
            return false;
        }

        File.WriteAllText(path, GitIgnoreContent);
        return true;
    }
}
