namespace SqlMcp.Cli;

/// <summary>A command failed for a reason the user can act on. The message is printed to stderr as is.</summary>
internal sealed class CliException : Exception
{
    public CliException(string message)
        : base(message)
    {
    }

    public CliException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CliException()
    {
    }
}
