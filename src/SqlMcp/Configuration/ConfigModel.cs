using SqlMcp.Permissions;

namespace SqlMcp.Configuration;

internal enum Engine
{
    SqlServer,
}

/// <summary>Limits as written in one place in one file. A null value means "not set here".</summary>
internal sealed record LimitSettings(int? MaxRows, long? MaxBytes, int? PageSize, int? TimeoutSeconds)
{
    public static readonly LimitSettings None = new(null, null, null, null);
}

/// <summary>An allow list, a deny list and limits, as written at one level (file defaults or one database).</summary>
internal sealed record GrantSettings(
    IReadOnlyList<ToolGroup> Allow,
    IReadOnlyList<ToolGroup> Deny,
    LimitSettings Limits)
{
    public static readonly GrantSettings None = new([], [], LimitSettings.None);
}

/// <summary>One parsed and validated config file. <see cref="Sha256"/> is the lower-case hex hash of its bytes.</summary>
internal sealed record ConfigFile(string Path, string Sha256, GrantSettings Defaults, IReadOnlyList<DatabaseEntry> Databases);

/// <summary>
/// One entry of a file's <c>databases</c> array. <see cref="Identifier"/> is computed from the connection string
/// before environment substitution. <see cref="ConnectionString"/> is the substituted string, and is null when the
/// entry is unavailable.
/// </summary>
internal sealed record DatabaseEntry(
    string Location,
    string Identifier,
    Engine Engine,
    GrantSettings Grants,
    string RawConnectionString,
    string? ConnectionString,
    string? UnavailableReason)
{
    public bool IsAvailable => UnavailableReason is null;

    // Records print every property by default, which would put the connection string in any log or message.
    public override string ToString() => $"{Identifier} ({Location})";
}
