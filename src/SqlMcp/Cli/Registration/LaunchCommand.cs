namespace SqlMcp.Cli.Registration;

/// <summary>
/// The command a client runs to start the server (design D10): <c>sql-mcp serve</c> if the tool is on PATH,
/// otherwise <c>dnx Emmz.SqlMcp@&lt;this version&gt; serve</c>. Project scope adds <c>--project &lt;dir&gt;</c>.
/// </summary>
internal static class LaunchCommand
{
    public const string PackageId = "Emmz.SqlMcp";

    public static IReadOnlyList<string> Build(Launcher launcher, string version, RegistrationScope scope, string projectDirectory)
    {
        List<string> launch = launcher == Launcher.Tool
            ? ["sql-mcp", "serve"]
            : ["dnx", $"{PackageId}@{PackageVersion(version)}", "serve"];
        if (scope == RegistrationScope.Project)
        {
            launch.AddRange(["--project", projectDirectory]);
        }

        return launch;
    }

    /// <summary><c>--launcher</c> if given, otherwise <see cref="Launcher.Tool"/> when <c>sql-mcp</c> is on PATH.</summary>
    public static Launcher Choose(Launcher? requested, CliContext context) =>
        requested ?? (IsOnPath("sql-mcp", context) ? Launcher.Tool : Launcher.Dnx);

    /// <summary>The informational version without its <c>+commit</c> suffix, which NuGet doesn't accept.</summary>
    public static string PackageVersion(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    private static bool IsOnPath(string program, CliContext context)
    {
        var path = context.GetVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string[] names = context.IsWindows ? [$"{program}.exe", $"{program}.cmd", program] : [program];
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => names.Any(name => File.Exists(Path.Combine(directory, name))));
    }
}
