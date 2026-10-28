using System.Diagnostics.CodeAnalysis;
using Microsoft.SqlServer.TransactSql.ScriptDom;

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

    public static bool TryParse(string? name, out ObjectType type)
    {
        foreach (var (candidate, wire) in Names)
        {
            if (name is not null && string.Equals(wire, name.Trim(), StringComparison.OrdinalIgnoreCase))
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
    public static bool TryParseAll(IReadOnlyList<string?>? names, out IReadOnlyList<ObjectType> types, out string? error)
    {
        var parsed = new List<ObjectType>();
        foreach (var name in names ?? [])
        {
            if (!TryParse(name, out var type))
            {
                types = [];
                error = NotAType(name);
                return false;
            }

            parsed.Add(type);
        }

        types = parsed.Distinct().ToList();
        error = null;
        return true;
    }

    /// <summary>The message for a type name that isn't one, listing the valid ones. A null shows as <c>null</c>.</summary>
    public static string NotAType(string? name) => $"'{name ?? "null"}' is not an object type. Use one of: {string.Join(", ", WireNames)}.";
}

/// <summary>
/// One object in a listing. <see cref="Schema"/> is null for a database-level trigger. <see cref="Parent"/> is a
/// resolved DML trigger's table or view, so the script generator can find the trigger through it, and null otherwise.
/// </summary>
internal sealed record ObjectSummary(string? Schema, string Name, ObjectType Type, long? ApproxRows)
{
    public TriggerParent? Parent { get; init; }

    /// <summary>The object as the inventory and the script generator name it.</summary>
    public CatalogItem ToItem() => new(Schema, Name, Type.ToWireName());
}

/// <summary>A DML trigger's table or view, as the catalog reader resolved it. Both are schema-scoped.</summary>
internal sealed record TriggerParent(string Schema, string Name, ObjectType Type);

/// <summary>
/// What names resolved to: the <see cref="Objects"/> to script, and the <see cref="Inventory"/> a script of them accounts
/// for, which is those objects and the DML triggers of the tables and views among them, with their encryption.
/// </summary>
internal sealed record Resolution(IReadOnlyList<ObjectSummary> Objects, IReadOnlyList<InventoryItem> Inventory);

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

/// <summary>
/// An object name as an agent gives it: <c>schema.name</c> or a bare <c>name</c>, with or without brackets or double
/// quotes. A name with a database or server part is refused, as the write tools refuse them (design I4).
/// <see cref="Type"/> is given only to tell apart an object and a type that share a name, such as a table and a table
/// type: types are a namespace of their own.
/// </summary>
internal sealed record ObjectName(string? Schema, string Name)
{
    /// <summary>Separates a name's parts in the list the catalog reader resolves in one query, so no name may hold it.</summary>
    public const char FieldSeparator = '\u001F';

    /// <summary>Ends each name in the list the catalog reader resolves in one query, so no name may hold it.</summary>
    public const char RecordSeparator = '\u001E';

    public ObjectType? Type { get; init; }

    /// <summary>
    /// Parses <paramref name="text"/> with ScriptDom, which unquotes <c>[dbo].[orders]</c> and <c>"dbo"."orders"</c>.
    /// On failure, <paramref name="error"/> is a message for the agent.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ObjectName? name, [NotNullWhen(false)] out string? error)
    {
        name = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Give an object name, such as dbo.orders.";
            return false;
        }

        if (text.Contains(FieldSeparator, StringComparison.Ordinal) || text.Contains(RecordSeparator, StringComparison.Ordinal))
        {
            error = $"'{text}' holds a control character (U+001E or U+001F), which sql-mcp can't look up.";
            return false;
        }

        SchemaObjectName? parsed;
        IList<ParseError> errors;
        using (var reader = new StringReader(text.Trim()))
        {
            parsed = new TSql170Parser(initialQuotedIdentifiers: true).ParseSchemaObjectName(reader, out errors);
        }

        if (errors.Count > 0 || parsed?.BaseIdentifier is null || string.IsNullOrEmpty(parsed.BaseIdentifier.Value))
        {
            error = $"'{text}' is not an object name. Give schema.name, such as dbo.orders; use brackets around a name with spaces or other special characters, such as [dbo].[order lines].";
            return false;
        }

        if (parsed.DatabaseIdentifier is not null || parsed.ServerIdentifier is not null)
        {
            error = $"'{text}' names another database or server. Give schema.name in this database, such as dbo.orders.";
            return false;
        }

        name = new ObjectName(string.IsNullOrEmpty(parsed.SchemaIdentifier?.Value) ? null : parsed.SchemaIdentifier.Value, parsed.BaseIdentifier.Value);
        error = null;
        return true;
    }

    /// <summary>The name as the audit log and messages show it: <c>schema.name</c>, or <c>name</c> when unqualified.</summary>
    public override string ToString() => Schema is null ? Name : $"{Schema}.{Name}";

    /// <summary>The name as the audit log records it: with its type in brackets when one was given, such as <c>dbo.address (table_type)</c>.</summary>
    public string ToRequest() => Type is { } type ? $"{this} ({type.ToWireName()})" : ToString();
}

/// <summary>
/// Turns the objects a name matched into one object or a message (design I4): none is "not found", more than one is
/// "ambiguous", naming each. The catalog reader finds the candidates, by the database's own name comparison.
/// </summary>
internal static class NameResolution
{
    public static string? Problem(ObjectName name, IReadOnlyList<ObjectSummary> candidates) => candidates.Count switch
    {
        0 => $"No {(name.Type is { } type ? type.ToWireName() : "object")} named '{name}' was found. Use list_objects to find it; only objects this database login can see are found.",
        1 => null,
        _ when candidates.Select(c => c.Schema).Distinct().Count() == 1 =>
            $"'{name}' is ambiguous: it matches {string.Join(", ", candidates.Select(Describe))}, an object and a type with the same name. Give its type too, such as {candidates[^1].Type.ToWireName()}.",
        _ => $"'{name}' is ambiguous: it matches {string.Join(", ", candidates.Select(Describe))}. Give a schema-qualified name, such as {Example(candidates)}.{TypeClash(candidates)}",
    };

    /// <summary>
    /// When one schema holds both an object and a type of that name, a schema-qualified name still matches two, so
    /// the message says to give the type as well (code review, 2026-10-28).
    /// </summary>
    private static string TypeClash(IReadOnlyList<ObjectSummary> candidates) =>
        candidates.GroupBy(c => c.Schema).FirstOrDefault(g => g.Count() > 1) is { } clash
            ? $" In {clash.Key}, an object and a type share the name, so give its type too, such as {clash.Last().Type.ToWireName()}."
            : "";

    private static string Describe(ObjectSummary candidate) =>
        $"{(candidate.Schema is null ? candidate.Name : $"{candidate.Schema}.{candidate.Name}")} ({candidate.Type.ToWireName()})";

    private static string Example(IReadOnlyList<ObjectSummary> candidates) =>
        candidates.FirstOrDefault(c => c.Schema is not null) is { } qualified ? $"{qualified.Schema}.{qualified.Name}" : "dbo.orders";
}

/// <summary>
/// One object described (design I3): its identity, plus the parts that apply to its type. Parts that don't apply,
/// and parts that are empty, are null. <see cref="Schema"/> is null for a database-level trigger.
/// </summary>
internal sealed record ObjectDetails(string? Schema, string Name, ObjectType Type)
{
    /// <summary>A function's kind: <c>scalar</c>, <c>inline_table_valued</c>, <c>table_valued</c> or <c>clr</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>Columns in order, for a table, view, table-valued function or table type.</summary>
    public IReadOnlyList<ColumnDetails>? Columns { get; init; }

    public KeyDetails? PrimaryKey { get; init; }

    public IReadOnlyList<KeyDetails>? UniqueConstraints { get; init; }

    public IReadOnlyList<CheckDetails>? CheckConstraints { get; init; }

    /// <summary>Indexes other than the ones that back the primary key and unique constraints.</summary>
    public IReadOnlyList<IndexDetails>? Indexes { get; init; }

    /// <summary>The names of the triggers on a table or view.</summary>
    public IReadOnlyList<string>? Triggers { get; init; }

    /// <summary>The foreign keys this table declares.</summary>
    public IReadOnlyList<ForeignKeyDetails>? ForeignKeys { get; init; }

    /// <summary>The foreign keys in other tables (or this one) that reference this table.</summary>
    public IReadOnlyList<ForeignKeyDetails>? ReferencedBy { get; init; }

    public TemporalDetails? Temporal { get; init; }

    /// <summary>A procedure's or function's parameters, in order.</summary>
    public IReadOnlyList<ParameterDetails>? Parameters { get; init; }

    /// <summary>A function's return type: a SQL type for a scalar function, <c>table</c> for a table-valued one.</summary>
    public string? ReturnType { get; init; }

    public Definition? Definition { get; init; }

    public SequenceDetails? Sequence { get; init; }

    /// <summary>The object a synonym points to, as <c>schema.name</c> (with database and server parts if it has them).</summary>
    public string? Target { get; init; }

    /// <summary>A user-defined type's base type.</summary>
    public string? BaseType { get; init; }

    /// <summary>Whether a user-defined type allows NULL.</summary>
    public bool? Nullable { get; init; }
}

/// <summary>
/// One column. <see cref="Default"/> is the default's expression, <see cref="Computed"/> a computed column's
/// expression; <see cref="Identity"/> is true for an identity column and otherwise null.
/// </summary>
internal sealed record ColumnDetails(string Name, string Type, bool Nullable, string? Default, bool? Identity, string? Computed);

/// <summary>A primary key or unique constraint. A descending column is written <c>name DESC</c>.</summary>
internal sealed record KeyDetails(string Name, IReadOnlyList<string> Columns, bool Clustered);

internal sealed record CheckDetails(string Name, string Expression);

/// <summary>
/// An index. <see cref="Kind"/> is SQL Server's index type in lower case, such as <c>nonclustered</c> or
/// <c>clustered columnstore</c>. A descending key column is written <c>name DESC</c>.
/// </summary>
internal sealed record IndexDetails(string Name, string Kind, bool Unique, IReadOnlyList<string> Columns, IReadOnlyList<string>? Included, string? Filter);

/// <summary>
/// A foreign key from <see cref="Table"/>'s columns to <see cref="ReferencedTable"/>'s. The actions are null for
/// <c>NO ACTION</c>, otherwise <c>cascade</c>, <c>set_null</c> or <c>set_default</c>.
/// </summary>
internal sealed record ForeignKeyDetails(
    string Name, string Table, IReadOnlyList<string> Columns, string ReferencedTable, IReadOnlyList<string> ReferencedColumns, string? OnDelete, string? OnUpdate);

/// <summary>A system-versioned table's history table and period columns.</summary>
internal sealed record TemporalDetails(string HistoryTable, string PeriodStart, string PeriodEnd);

/// <summary>
/// A parameter. <see cref="Direction"/> is <c>input</c> or <c>output</c>. <see cref="HasDefault"/> is null when it
/// can't be known, because the definition is unavailable.
/// </summary>
internal sealed record ParameterDetails(string Name, string Type, string Direction, bool? HasDefault);

/// <summary>
/// A sequence. The values are text, as the query tools give large numbers: a <c>decimal(38)</c> sequence's bounds don't
/// fit a JSON number exactly.
/// </summary>
internal sealed record SequenceDetails(string Type, string Start, string Increment, string Minimum, string Maximum, bool Cycles);

/// <summary>A code object's definition: its text, or why it can't be read (design I3). Never left out silently.</summary>
internal abstract record Definition
{
    public sealed record Text(string Value) : Definition;

    /// <summary><see cref="Reason"/> is one of <see cref="Encrypted"/>, <see cref="Clr"/> or <see cref="NoPermission"/>.</summary>
    public sealed record Unavailable(string Reason) : Definition;

    public const string Encrypted = "encrypted";

    /// <summary>A CLR object's code is in an assembly, so there is no T-SQL definition to show.</summary>
    public const string Clr = "clr";

    public const string NoPermission = "no permission";
}
