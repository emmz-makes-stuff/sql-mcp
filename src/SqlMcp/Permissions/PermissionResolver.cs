using SqlMcp.Configuration;

namespace SqlMcp.Permissions;

/// <summary>
/// Layers the user and project files into effective permissions per database (design D4):
/// <c>defaults → user.defaults → user.db → project.defaults → project.db</c>, allow then deny at each step so that
/// deny wins at the same level. The project layer's narrowing always applies; its widening (allowing a group that
/// was denied before the project layer, or raising a limit) applies only when the project file is approved. A
/// database that only the project file defines is unavailable until it is approved on its own, by the hash of its
/// raw connection string, so an unapproved file can never make sql-mcp connect somewhere with the user's secrets.
/// </summary>
internal static class PermissionResolver
{
    private static readonly HashSet<ToolGroup> DefaultAllowed = [ToolGroup.Query, ToolGroup.Introspection];

    public static IReadOnlyList<EffectiveDatabase> Resolve(ConfigFile? user, ConfigFile? project, ProjectApproval approval)
    {
        var userEntries = Index(user);
        var projectEntries = Index(project);

        var result = new List<EffectiveDatabase>();
        foreach (var (key, userEntry) in userEntries)
        {
            projectEntries.TryGetValue(key, out var projectEntry);
            result.Add(Resolve(user!, userEntry, project, projectEntry, approval));
        }

        foreach (var (key, projectEntry) in projectEntries)
        {
            if (!userEntries.ContainsKey(key))
            {
                result.Add(Resolve(null, null, project!, projectEntry, approval));
            }
        }

        return result;
    }

    private static EffectiveDatabase Resolve(
        ConfigFile? user,
        DatabaseEntry? userEntry,
        ConfigFile? project,
        DatabaseEntry? projectEntry,
        ProjectApproval approval)
    {
        var projectApproved = approval.FileApproved;
        var groups = Enum.GetValues<ToolGroup>()
            .ToDictionary(g => g, g => new GroupGrant(g, DefaultAllowed.Contains(g), PermissionSource.Default));
        var limits = EffectiveLimits.Default;

        if (user is not null)
        {
            Apply(groups, user.Defaults, PermissionSource.UserConfig);
            limits = Apply(limits, user.Defaults.Limits);
        }

        if (userEntry is not null)
        {
            Apply(groups, userEntry.Grants, PermissionSource.UserConfig);
            limits = Apply(limits, userEntry.Grants.Limits);
        }

        var widenings = new List<string>();
        if (project is not null)
        {
            var requested = new Dictionary<ToolGroup, GroupGrant>(groups);
            var requestedLimits = limits;
            Apply(requested, project.Defaults, PermissionSource.ProjectConfig);
            requestedLimits = Apply(requestedLimits, project.Defaults.Limits);
            if (projectEntry is not null)
            {
                Apply(requested, projectEntry.Grants, PermissionSource.ProjectConfig);
                requestedLimits = Apply(requestedLimits, projectEntry.Grants.Limits);
            }

            foreach (var (group, grant) in requested)
            {
                var widens = grant.Allowed && !groups[group].Allowed;
                if (widens && !projectApproved)
                {
                    var widening = ApprovalMessages.Grants(group);
                    widenings.Add(widening);
                    groups[group] = new GroupGrant(
                        group, false, PermissionSource.ProjectConfigNotApproved, ApprovalMessages.NotApproved(widening));
                }
                else
                {
                    groups[group] = grant;
                }
            }

            limits = new EffectiveLimits(
                ProjectLimit(limits.MaxRows, requestedLimits.MaxRows, "maxRows", projectApproved, widenings),
                ProjectLimit(limits.MaxBytes, requestedLimits.MaxBytes, "maxBytes", projectApproved, widenings),
                ProjectLimit(limits.PageSize, requestedLimits.PageSize, "pageSize", projectApproved, widenings),
                ProjectLimit(limits.TimeoutSeconds, requestedLimits.TimeoutSeconds, "timeoutSeconds", projectApproved, widenings),
                ProjectLimit(limits.MaxAffectedRows, requestedLimits.MaxAffectedRows, "maxAffectedRows", projectApproved, widenings));
        }

        // The user file owns the connection string of a database it defines (see the 2026-10-25 devlog decision).
        var entry = userEntry ?? projectEntry!;

        // A project-only database is never connected to until approved: its string may name any host and pull in
        // the user's environment variables. An unparseable entry is unavailable anyway.
        if (userEntry is null && entry.Identifier != DatabaseIdentity.InvalidIdentifier && !approval.Covers(entry))
        {
            widenings.Insert(0, ApprovalMessages.AddsDatabase);
            var notApproved = ApprovalMessages.NotApproved(ApprovalMessages.AddsDatabase);
            entry = entry with
            {
                ConnectionString = null,
                UnavailableReason = entry.UnavailableReason is { } other ? $"{notApproved} Also: {other}" : notApproved,
            };
        }

        var definedIn = userEntry is not null ? user!.Path : project!.Path;
        return new EffectiveDatabase(entry.Identifier, entry, definedIn, groups, limits, widenings);
    }

    private static void Apply(Dictionary<ToolGroup, GroupGrant> groups, GrantSettings settings, PermissionSource source)
    {
        foreach (var group in settings.Allow)
        {
            groups[group] = new GroupGrant(group, true, source);
        }

        foreach (var group in settings.Deny)
        {
            groups[group] = new GroupGrant(group, false, source);
        }
    }

    private static EffectiveLimits Apply(EffectiveLimits limits, LimitSettings settings) => new(
        settings.MaxRows ?? limits.MaxRows,
        settings.MaxBytes ?? limits.MaxBytes,
        settings.PageSize ?? limits.PageSize,
        settings.TimeoutSeconds ?? limits.TimeoutSeconds,
        settings.MaxAffectedRows ?? limits.MaxAffectedRows);

    private static T ProjectLimit<T>(T before, T requested, string name, bool approved, List<string> widenings)
        where T : IComparable<T>
    {
        if (requested.CompareTo(before) <= 0 || approved)
        {
            return requested;
        }

        widenings.Add(ApprovalMessages.Raises(name));
        return before;
    }

    /// <summary>
    /// Entries keyed by identity. An unparseable entry has no real identity, so it is keyed by its location and
    /// never merged with another file's entry.
    /// </summary>
    private static Dictionary<string, DatabaseEntry> Index(ConfigFile? file)
    {
        var entries = new Dictionary<string, DatabaseEntry>(StringComparer.Ordinal);
        foreach (var entry in file?.Databases ?? [])
        {
            var key = entry.Identifier == DatabaseIdentity.InvalidIdentifier
                ? $"{file!.Path}#{entry.Location}"
                : entry.Identifier;
            entries[key] = entry;
        }

        return entries;
    }
}
