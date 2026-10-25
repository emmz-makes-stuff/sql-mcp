using SqlMcp.Cli.Registration;

namespace SqlMcp.Tests.Cli;

/// <summary>
/// Records every call, and answers from rules matched on the program and the leading arguments (most recent rule
/// first). Unmatched calls succeed with no output. <see cref="Missing"/> programs behave as not installed.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(string Program, string[] Prefix, ProcessResult Result)> _rules = [];

    public List<(string Program, string[] Arguments, string WorkingDirectory)> Calls { get; } = [];

    public HashSet<string> Missing { get; } = [];

    public void On(string program, string[] argumentPrefix, ProcessResult result) => _rules.Add((program, argumentPrefix, result));

    public IEnumerable<string[]> CallsTo(string program) => Calls.Where(c => c.Program == program).Select(c => c.Arguments);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, [.. arguments], workingDirectory));
        if (Missing.Contains(fileName))
        {
            return Task.FromResult(ProcessResult.Missing);
        }

        for (var i = _rules.Count - 1; i >= 0; i--)
        {
            var (program, prefix, result) = _rules[i];
            if (program == fileName && arguments.Take(prefix.Length).SequenceEqual(prefix))
            {
                return Task.FromResult(result);
            }
        }

        return Task.FromResult(new ProcessResult(0, "", ""));
    }
}
