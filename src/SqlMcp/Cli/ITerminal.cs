namespace SqlMcp.Cli;

/// <summary>The console as <c>approve</c> sees it: whether a person is attached, and their typed answer.</summary>
internal interface ITerminal
{
    bool IsInputRedirected { get; }

    bool IsOutputRedirected { get; }

    string? ReadLine();
}

internal sealed class SystemTerminal : ITerminal
{
    public static readonly SystemTerminal Instance = new();

    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public string? ReadLine() => Console.ReadLine();
}
