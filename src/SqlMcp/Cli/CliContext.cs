using SqlMcp.Cli.Registration;
using SqlMcp.Configuration;

namespace SqlMcp.Cli;

/// <summary>
/// What a command reads from the process: directories, environment variables, the terminal, and the child
/// processes it starts. Output goes through the invocation's writers instead. Tests build one over temp
/// directories, a fake terminal and a fake process runner.
/// </summary>
internal sealed record CliContext(
    bool IsWindows,
    string HomeDirectory,
    string AppDataDirectory,
    string CurrentDirectory,
    Func<string, string?> GetVariable,
    ITerminal Terminal,
    IProcessRunner ProcessRunner)
{
    public static CliContext ForCurrentProcess() => new(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Directory.GetCurrentDirectory(),
        Environment.GetEnvironmentVariable,
        SystemTerminal.Instance,
        SystemProcessRunner.Instance);

    public ConfigPaths Paths(string? projectDirectory = null) =>
        ConfigPaths.Create(IsWindows, HomeDirectory, AppDataDirectory, CurrentDirectory, projectDirectory);
}
