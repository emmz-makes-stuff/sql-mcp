namespace SqlMcp.Configuration;

/// <summary>A config file that can't be used. The message names the file and, where known, the JSON path.</summary>
internal sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ConfigException()
    {
    }
}
