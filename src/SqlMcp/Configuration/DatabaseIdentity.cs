using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SqlMcp.Configuration;

/// <summary>The <c>host:port/database</c> identity of a connection string, or why it has none.</summary>
internal sealed record IdentityResult(string Identifier, string? UnavailableReason);

internal static class DatabaseIdentity
{
    /// <summary>The placeholder identifier of an entry whose connection string can't be parsed.</summary>
    public const string InvalidIdentifier = "(invalid)";

    private const int DefaultPort = 1433;

    /// <summary>
    /// Computes the identity from the raw (unsubstituted) connection string. Host names are lower-cased but never
    /// resolved. Anything that can't give a TCP identity gets a reason, and a display identifier that can't match a
    /// tool-call identifier because it has no <c>:port</c>.
    /// </summary>
    public static IdentityResult FromConnectionString(string connectionString)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            // SqlClient's messages name keywords and positions, never values.
            return new IdentityResult(InvalidIdentifier, $"connection string is not valid: {ex.Message}");
        }

        var database = builder.InitialCatalog.Trim().ToLowerInvariant();
        var dataSource = builder.DataSource.Trim().ToLowerInvariant();

        var source = dataSource;
        if (source.StartsWith("tcp:", StringComparison.Ordinal))
        {
            source = source["tcp:".Length..];
        }
        else if (IsNonTcp(source))
        {
            return new IdentityResult($"{dataSource}/{database}", "only TCP connections are supported");
        }

        var port = DefaultPort;
        var comma = source.LastIndexOf(',');
        var hasPort = comma >= 0;
        if (hasPort)
        {
            var portText = source[(comma + 1)..].Trim();
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                return new IdentityResult($"{dataSource}/{database}", $"port \"{portText}\" is not valid");
            }

            source = source[..comma].Trim();
        }

        var host = source;
        var backslash = source.IndexOf('\\', StringComparison.Ordinal);
        if (backslash >= 0)
        {
            if (!hasPort)
            {
                return new IdentityResult($"{dataSource}/{database}", "a named instance needs an explicit port");
            }

            // With an explicit port SqlClient connects straight to it, so the instance name doesn't matter.
            host = source[..backslash];
        }

        if (host.Length == 0)
        {
            return new IdentityResult($"{dataSource}/{database}", "connection string names no server");
        }

        var identifier = $"{host}:{port.ToString(CultureInfo.InvariantCulture)}/{database}";
        return database.Length == 0
            ? new IdentityResult(identifier, "connection string names no database")
            : new IdentityResult(identifier, null);
    }

    /// <summary>Normalises an identifier given in a tool call, so <c>SQLHOST:1433/Sales</c> matches.</summary>
    public static string Normalise(string identifier) => identifier.Trim().ToLowerInvariant();

    /// <summary>The error for a tool call naming a database that isn't configured.</summary>
    public static string UnknownIdentifierMessage(string requested, IEnumerable<string> known)
    {
        var list = known.Order(StringComparer.Ordinal).ToList();
        return list.Count == 0
            ? $"Unknown database \"{requested}\". No databases are configured."
            : $"Unknown database \"{requested}\". Known databases: {string.Join(", ", list)}.";
    }

    private static bool IsNonTcp(string dataSource) =>
        dataSource.StartsWith("np:", StringComparison.Ordinal)
        || dataSource.StartsWith("lpc:", StringComparison.Ordinal)
        || dataSource.StartsWith("admin:", StringComparison.Ordinal)
        || dataSource.StartsWith("(localdb)", StringComparison.Ordinal)
        || dataSource is "." or "(local)"
        || dataSource.StartsWith(@".\", StringComparison.Ordinal)
        || dataSource.StartsWith(@"(local)\", StringComparison.Ordinal);
}

/// <summary>Hides credentials in a connection string before it is shown anywhere.</summary>
internal static class ConnectionStringMasker
{
    private const string Mask = "***";

    private static readonly string[] SecretKeys = ["password", "pwd"];

    /// <summary>
    /// Masks literal passwords. <c>${env:NAME}</c> references are kept, since they name a variable rather than
    /// hold a secret. Use this on the raw string from the file, never on the substituted one.
    /// </summary>
    public static string MaskRaw(string rawConnectionString)
    {
        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = rawConnectionString;
        }
        catch (ArgumentException)
        {
            return "(invalid connection string)";
        }

        foreach (var key in builder.Keys.Cast<string>().ToList())
        {
            if (SecretKeys.Contains(key.ToLowerInvariant()) && builder[key] is string value && !EnvSubstitution.IsOnlyReference(value))
            {
                builder[key] = Mask;
            }
        }

        return builder.ConnectionString;
    }
}
