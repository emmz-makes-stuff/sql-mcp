namespace SqlMcp.Introspection;

/// <summary>The object types agents see, engine-neutral (design I3). A function's kind is a detail, not a type.</summary>
internal enum ObjectType
{
    Table,
    View,
    Procedure,
    Function,
    Trigger,
    Sequence,
    Synonym,
    Type,
    TableType,
}

internal static class ObjectTypes
{
    private static readonly Dictionary<ObjectType, string> Names = new()
    {
        [ObjectType.Table] = "table",
        [ObjectType.View] = "view",
        [ObjectType.Procedure] = "procedure",
        [ObjectType.Function] = "function",
        [ObjectType.Trigger] = "trigger",
        [ObjectType.Sequence] = "sequence",
        [ObjectType.Synonym] = "synonym",
        [ObjectType.Type] = "type",
        [ObjectType.TableType] = "table_type",
    };

    /// <summary>The wire names, in declaration order.</summary>
    public static IReadOnlyList<string> WireNames { get; } = [.. Enum.GetValues<ObjectType>().Select(t => Names[t])];

    public static string ToWireName(this ObjectType type) => Names[type];

    public static bool TryParse(string name, out ObjectType type)
    {
        foreach (var (candidate, wire) in Names)
        {
            if (string.Equals(wire, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }

    /// <summary>
    /// Parses a type filter. Null or empty means every type. Fails on the first unknown name, with a message that
    /// lists the valid ones (spec: unknown object type).
    /// </summary>
    public static bool TryParseAll(IReadOnlyList<string>? names, out IReadOnlyList<ObjectType> types, out string? error)
    {
        var parsed = new List<ObjectType>();
        foreach (var name in names ?? [])
        {
            if (!TryParse(name, out var type))
            {
                types = [];
                error = $"'{name}' is not an object type. Use one of: {string.Join(", ", WireNames)}.";
                return false;
            }

            parsed.Add(type);
        }

        types = parsed.Distinct().ToList();
        error = null;
        return true;
    }
}

/// <summary>One object in a listing. <see cref="Schema"/> is null for a database-level trigger.</summary>
internal sealed record ObjectSummary(string? Schema, string Name, ObjectType Type, long? ApproxRows);

/// <summary>
/// What <c>list_objects</c> asked for. Each part is optional, and an object must match every part given. An empty
/// <see cref="Types"/> means every type. <see cref="Pattern"/> is a glob (<see cref="Glob"/>).
/// </summary>
internal sealed record ObjectFilter(string? Schema, IReadOnlyList<ObjectType> Types, string? Pattern)
{
    /// <summary>The filter as the audit log records it: one <c>name=value</c> entry per part given.</summary>
    public IReadOnlyList<string> Describe()
    {
        var parts = new List<string>();
        if (Schema is not null)
        {
            parts.Add($"schema={Schema}");
        }

        if (Types.Count > 0)
        {
            parts.Add($"types={string.Join(",", Types.Select(t => t.ToWireName()))}");
        }

        if (Pattern is not null)
        {
            parts.Add($"pattern={Pattern}");
        }

        return parts;
    }
}

/// <summary>A capped listing: <see cref="Total"/> counts every match, <see cref="Objects"/> holds at most the cap.</summary>
internal sealed record ObjectList(IReadOnlyList<ObjectSummary> Objects, int Total, bool Truncated);
