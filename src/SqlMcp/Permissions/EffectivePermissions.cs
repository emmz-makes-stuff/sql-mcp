using SqlMcp.Configuration;

namespace SqlMcp.Permissions;

/// <summary>Where a group's effective grant came from, shown to the agent alongside it.</summary>
internal enum PermissionSource
{
    Default,
    UserConfig,
    ProjectConfig,
    ProjectConfigNotApproved,
}

internal static class PermissionSourceNames
{
    public static string ToDisplayName(this PermissionSource source) => source switch
    {
        PermissionSource.Default => "default",
        PermissionSource.UserConfig => "user config",
        PermissionSource.ProjectConfig => "project config",
        PermissionSource.ProjectConfigNotApproved => "project config not approved",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}

/// <summary>One group's effective grant. <see cref="Reason"/> is set when an unapproved project grant was ignored.</summary>
internal sealed record GroupGrant(ToolGroup Group, bool Allowed, PermissionSource Source, string? Reason = null);

/// <summary>The limits in force for one database.</summary>
internal sealed record EffectiveLimits(int MaxRows, long MaxBytes, int PageSize, int TimeoutSeconds)
{
    /// <summary>Design D7: 1000 rows, 5 MB, 100 rows per page, 30 seconds.</summary>
    public static readonly EffectiveLimits Default = new(1000, 5 * 1024 * 1024, 100, 30);
}

/// <summary>A database after layering: its entry, a grant per group, its limits, and any ignored widening.</summary>
internal sealed record EffectiveDatabase(
    string Identifier,
    DatabaseEntry Entry,
    string DefinedIn,
    IReadOnlyDictionary<ToolGroup, GroupGrant> Groups,
    EffectiveLimits Limits,
    IReadOnlyList<string> UnapprovedWidenings)
{
    public bool IsAllowed(ToolGroup group) => Groups[group].Allowed;
}
