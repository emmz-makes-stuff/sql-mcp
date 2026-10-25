using System.Data.Common;
using System.Text.RegularExpressions;

namespace SqlMcp.Configuration;

/// <summary>The outcome of substituting <c>${env:NAME}</c> references in one connection string.</summary>
internal sealed record SubstitutionResult(string? ConnectionString, IReadOnlyList<string> MissingVariables)
{
    public string? UnavailableReason => MissingVariables.Count == 0
        ? null
        : string.Join("; ", MissingVariables.Select(name => $"environment variable {name} is not set"));
}

internal static partial class EnvSubstitution
{
    [GeneratedRegex(@"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Reference();

    /// <summary>
    /// Replaces each <c>${env:NAME}</c> with the variable's value, one keyword value at a time, so a value that
    /// contains <c>;</c> or <c>=</c> is quoted rather than read as more keywords. An unset variable is never
    /// replaced with an empty string: the result has no connection string and names every missing variable.
    /// </summary>
    public static SubstitutionResult Substitute(string connectionString, Func<string, string?> getVariable)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var missing = new List<string>();

        foreach (var key in builder.Keys.Cast<string>().ToList())
        {
            if (builder[key] is not string value || !Reference().IsMatch(value))
            {
                continue;
            }

            builder[key] = Reference().Replace(value, match =>
            {
                var name = match.Groups[1].Value;
                var resolved = getVariable(name);
                if (resolved is null)
                {
                    if (!missing.Contains(name))
                    {
                        missing.Add(name);
                    }

                    return match.Value;
                }

                return resolved;
            });
        }

        return missing.Count == 0
            ? new SubstitutionResult(builder.ConnectionString, [])
            : new SubstitutionResult(null, missing);
    }

    /// <summary>True when the whole value is a single <c>${env:NAME}</c> reference.</summary>
    public static bool IsOnlyReference(string value)
    {
        var match = Reference().Match(value);
        return match.Success && match.Index == 0 && match.Length == value.Length;
    }
}
