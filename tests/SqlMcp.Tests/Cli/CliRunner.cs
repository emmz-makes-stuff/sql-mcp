using System.CommandLine;
using SqlMcp.Cli;

namespace SqlMcp.Tests.Cli;

/// <summary>Runs the real root command against a temp home and project directory, capturing stdout and stderr.</summary>
internal sealed class CliRunner : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly TempDirectory _project = new();

    public string HomeDirectory => _home.Path;

    public string ProjectDirectory => _project.Path;

    public TempDirectory Project => _project;

    public Dictionary<string, string> Variables { get; } = [];

    public FakeTerminal Terminal { get; } = new();

    public FakeProcessRunner Processes { get; } = new();

    public bool IsWindows { get; set; }

    public CliContext Context => new(
        IsWindows: IsWindows,
        HomeDirectory: _home.Path,
        AppDataDirectory: _home.Path,
        CurrentDirectory: _project.Path,
        GetVariable: name => Variables.GetValueOrDefault(name),
        Terminal: Terminal,
        ProcessRunner: Processes);

    public async Task<CliResult> RunAsync(params string[] args) => await RunAsync(SqlMcpCli.Build(Context), args);

    public static async Task<CliResult> RunAsync(RootCommand root, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await root.Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error },
            TestContext.Current.CancellationToken);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        _home.Dispose();
        _project.Dispose();
    }
}

internal sealed record CliResult(int ExitCode, string Output, string Error);

/// <summary>An interactive terminal by default. Typed lines are queued in <see cref="Input"/>.</summary>
internal sealed class FakeTerminal : ITerminal
{
    public bool IsInputRedirected { get; set; }

    public bool IsOutputRedirected { get; set; }

    public Queue<string> Input { get; } = new();

    public int Reads { get; private set; }

    public string? ReadLine()
    {
        Reads++;
        return Input.TryDequeue(out var line) ? line : null;
    }
}
