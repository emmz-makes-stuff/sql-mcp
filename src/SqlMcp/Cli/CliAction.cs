using System.CommandLine;
using SqlMcp.Configuration;

namespace SqlMcp.Cli;

/// <summary>
/// Spec "Exit codes": every command exits 0 on success and 1 on failure, with the error on stderr. Expected
/// failures print their message only; anything else falls through to System.CommandLine's handler.
/// </summary>
internal static class CliAction
{
    public const int Failure = 1;

    public static void Set(Command command, Func<ParseResult, CancellationToken, Task<int>> action) =>
        command.SetAction((result, cancellationToken) => RunAsync(result, action, cancellationToken));

    public static void Set(Command command, Func<ParseResult, int> action) =>
        Set(command, (result, _) => Task.FromResult(action(result)));

    public static async Task<int> RunAsync(
        ParseResult result,
        Func<ParseResult, CancellationToken, Task<int>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CliException or ConfigException or IOException or UnauthorizedAccessException)
        {
            await result.InvocationConfiguration.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return Failure;
        }
    }
}
