namespace SqlMcp.Permissions;

/// <summary>A group of tools that is granted or denied as a unit. There is deliberately no group for GRANT/REVOKE/DENY.</summary>
internal enum ToolGroup
{
    Query,
    Introspection,
    Command,
    Ddl,
    Execute,
}

internal static class ToolGroupNames
{
    /// <summary>The name used in config files and messages, e.g. <c>ddl</c>.</summary>
    public static string ToConfigName(this ToolGroup group) => group switch
    {
        ToolGroup.Query => "query",
        ToolGroup.Introspection => "introspection",
        ToolGroup.Command => "command",
        ToolGroup.Ddl => "ddl",
        ToolGroup.Execute => "execute",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
    };

    public static bool TryParse(string name, out ToolGroup group)
    {
        foreach (var candidate in Enum.GetValues<ToolGroup>())
        {
            if (candidate.ToConfigName() == name)
            {
                group = candidate;
                return true;
            }
        }

        group = default;
        return false;
    }
}
