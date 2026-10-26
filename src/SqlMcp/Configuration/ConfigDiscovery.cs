namespace SqlMcp.Configuration;

/// <summary>Where the config files live. Built from explicit inputs so tests can point it at temp directories.</summary>
internal sealed record ConfigPaths(string UserConfig, string ProjectDirectory)
{
    public string ProjectConfig => Path.Combine(ProjectDirectory, ".sql-mcp", "config.json");

    /// <summary><c>approvals.json</c> sits next to the user config.</summary>
    public string Approvals => Path.Combine(Path.GetDirectoryName(UserConfig)!, "approvals.json");

    /// <summary>
    /// The real locations: <c>~/.config/sql-mcp/config.json</c> (<c>%APPDATA%\sql-mcp\config.json</c> on Windows),
    /// and <c>.sql-mcp/config.json</c> under <paramref name="projectDirectory"/>, or the current directory.
    /// </summary>
    public static ConfigPaths ForCurrentUser(string? projectDirectory) => Create(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Directory.GetCurrentDirectory(),
        projectDirectory);

    public static ConfigPaths Create(
        bool isWindows,
        string homeDirectory,
        string appDataDirectory,
        string currentDirectory,
        string? projectDirectory)
    {
        var user = isWindows
            ? Path.Combine(appDataDirectory, "sql-mcp", "config.json")
            : Path.Combine(homeDirectory, ".config", "sql-mcp", "config.json");

        var project = projectDirectory is null
            ? currentDirectory
            : Path.GetFullPath(projectDirectory, currentDirectory);

        return new ConfigPaths(user, project);
    }
}

/// <summary>The config files that exist, loaded. Either or both may be missing; that is not an error.</summary>
internal sealed record DiscoveredConfig(ConfigPaths Paths, ConfigFile? User, ConfigFile? Project)
{
    /// <summary>The message shown when no database is configured, naming both places to add one.</summary>
    public string NoDatabasesMessage =>
        $"No databases are configured. Add them to the user config at {Paths.UserConfig} or the project config at {Paths.ProjectConfig}.";
}

internal static class ConfigDiscovery
{
    /// <summary>Loads whichever files exist. A file that exists but is invalid throws <see cref="ConfigException"/>.</summary>
    public static DiscoveredConfig Discover(ConfigPaths paths, Func<string, string?> getVariable) => new(
        paths,
        LoadIfPresent(paths.UserConfig, getVariable),
        LoadIfPresent(paths.ProjectConfig, getVariable));

    private static ConfigFile? LoadIfPresent(string path, Func<string, string?> getVariable) =>
        File.Exists(path) ? ConfigLoader.Load(path, getVariable) : null;
}
