using System.Security.Cryptography;
using System.Text;
using SqlMcp.Configuration;

namespace SqlMcp.Permissions;

/// <summary>
/// What <c>approvals.json</c> says about one project file (design D4). <see cref="FileApproved"/> covers the file's
/// grant and limit widenings, and holds only while the whole file's hash matches. <see cref="Databases"/> maps each
/// approved project-only database to the hash of its raw connection string, so it stays approved through edits
/// elsewhere in the file, but not through a change to its own string.
/// </summary>
internal sealed record ProjectApproval(bool FileApproved, IReadOnlyDictionary<string, string> Databases)
{
    public static readonly ProjectApproval None = new(false, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Approval of <paramref name="project"/> as it stands: the file and every database it defines.</summary>
    public static ProjectApproval Of(ConfigFile project) => new(
        true,
        project.Databases
            .Where(entry => entry.Identifier != DatabaseIdentity.InvalidIdentifier)
            .ToDictionary(entry => entry.Identifier, entry => Hash(entry.RawConnectionString), StringComparer.Ordinal));

    /// <summary>True if <paramref name="entry"/> is approved with its current raw connection string.</summary>
    public bool Covers(DatabaseEntry entry) =>
        Databases.TryGetValue(entry.Identifier, out var hash)
        && string.Equals(hash, Hash(entry.RawConnectionString), StringComparison.OrdinalIgnoreCase);

    /// <summary>The lower-case hex SHA-256 of the string before <c>${env:…}</c> substitution, so no secret is hashed.</summary>
    public static string Hash(string rawConnectionString) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawConnectionString)));
}
