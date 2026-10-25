using System.ComponentModel;
using System.Diagnostics;

namespace SqlMcp.Cli.Registration;

/// <summary>A finished child process. <see cref="NotFound"/> means the program couldn't be started at all.</summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool NotFound = false)
{
    public static readonly ProcessResult Missing = new(-1, "", "", NotFound: true);

    public bool Succeeded => !NotFound && ExitCode == 0;

    /// <summary>The process's own error text, or its output if it wrote nothing to stderr.</summary>
    public string Message => (StandardError.Trim().Length > 0 ? StandardError : StandardOutput).Trim();
}

/// <summary>Runs client CLIs with an argument array, never a shell string (design D10).</summary>
internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    public static readonly SystemProcessRunner Instance = new();

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var program = OperatingSystem.IsWindows()
            ? ProgramLookup.Resolve(fileName, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"), File.Exists)
            : fileName;
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception)
        {
            return ProcessResult.Missing;
        }

        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
    }
}
