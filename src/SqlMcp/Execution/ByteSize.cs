using System.Globalization;

namespace SqlMcp.Execution;

internal static class ByteSize
{
    /// <summary>A byte cap for messages: <c>5 MB</c>, <c>10 KB</c>, or <c>1234 bytes</c>.</summary>
    public static string Format(long bytes) => bytes switch
    {
        _ when bytes % (1024 * 1024) == 0 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024 * 1024)} MB"),
        _ when bytes % 1024 == 0 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
    };
}
