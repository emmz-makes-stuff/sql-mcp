using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlMcp.Execution;
using SqlMcp.Introspection;
using SqlMcp.Permissions;

namespace SqlMcp.Mcp;

/// <summary>What a tool returns: JSON text on success, or a message the agent reads on error.</summary>
internal sealed record ToolResult(bool IsError, string Text)
{
    /// <summary>
    /// snake_case, nulls left out. Escaping is relaxed so text such as <c>é</c> or <c>+</c> reaches the agent as
    /// written; the output is JSON in a text block, never HTML.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ToolResult Ok<T>(T body) => new(false, JsonSerializer.Serialize(body, Json));

    public static ToolResult Error(string message) => new(true, message);
}

internal sealed record DatabaseListing(IReadOnlyList<DatabaseInfo> Databases, string? Message = null);

/// <summary>
/// One database as <c>list_databases</c> shows it. There is deliberately no connection-string field.
/// <see cref="NotApproved"/> covers every ignored project widening, raised limits included, in the approval wording.
/// </summary>
internal sealed record DatabaseInfo(
    string Id,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<GroupInfo> Allowed,
    IReadOnlyList<GroupInfo> Denied,
    LimitsInfo Limits,
    string? NotApproved)
{
    public static DatabaseInfo From(EffectiveDatabase database)
    {
        var groups = database.Groups.Values.OrderBy(g => g.Group).ToList();
        return new DatabaseInfo(
            database.Identifier,
            database.Entry.IsAvailable,
            database.Entry.UnavailableReason,
            [.. groups.Where(g => g.Allowed).Select(GroupInfo.From)],
            [.. groups.Where(g => !g.Allowed).Select(GroupInfo.From)],
            LimitsInfo.From(database.Limits),
            database.UnapprovedWidenings.Count == 0 ? null : ApprovalMessages.NotApproved(string.Join(", ", database.UnapprovedWidenings)));
    }
}

/// <summary>A group with the source of its grant, and the approval wording when unapproved widening was ignored.</summary>
internal sealed record GroupInfo(string Group, string Source, string? Reason)
{
    public static GroupInfo From(GroupGrant grant) => new(grant.Group.ToConfigName(), grant.Source.ToDisplayName(), grant.Reason);
}

internal sealed record LimitsInfo(int MaxRows, long MaxBytes, int PageSize, int TimeoutSeconds, int MaxAffectedRows)
{
    public static LimitsInfo From(EffectiveLimits limits) =>
        new(limits.MaxRows, limits.MaxBytes, limits.PageSize, limits.TimeoutSeconds, limits.MaxAffectedRows);
}

/// <summary>
/// One page, as <c>query</c> and <c>next_page</c> return it. <see cref="Handle"/> is present while more pages remain;
/// <see cref="LastPage"/> says when it is gone. The totals are only on the first page. A write with result sets
/// adds <see cref="Committed"/> and <see cref="RowsAffected"/> to its first page (design W7).
/// </summary>
internal sealed record PageResponse(
    int ResultSet,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<JsonNode?[]> Rows,
    int FirstRow,
    int LastRow,
    int Page,
    int Pages,
    bool LastPage,
    string? Handle,
    int? ResultSets = null,
    int? RowsBuffered = null,
    bool? Truncated = null,
    string? TruncationMessage = null,
    bool? Committed = null,
    IReadOnlyList<int>? RowsAffected = null)
{
    public static PageResponse From(ResultPage page) => new(
        page.ResultSetIndex,
        [.. page.Columns.Select(c => new ColumnInfo(c.Name, c.SqlType))],
        page.Rows,
        page.FirstRow,
        page.LastRow,
        page.PageNumber,
        page.PageCount,
        page.IsLastPage,
        page.Handle);

    public static PageResponse First(ResultPage page, BufferedResult result) => From(page) with
    {
        ResultSets = result.ResultSets.Count,
        RowsBuffered = result.RowCount,
        Truncated = result.Truncated,
        TruncationMessage = result.TruncationMessage,
    };
}

/// <summary>A committed write with no result sets: no pages and no handle (design W7).</summary>
internal sealed record WriteResponse(bool Committed, IReadOnlyList<int> RowsAffected);

internal sealed record ColumnInfo(string Name, string Type);

internal sealed record AbandonResponse(bool Released);

/// <summary>
/// What <c>list_objects</c> returns. <see cref="Total"/> counts every match; past the row cap, <see cref="Truncated"/>
/// is set and <see cref="Message"/> says to filter (spec: object list is capped).
/// </summary>
internal sealed record ObjectListResponse(IReadOnlyList<ObjectEntry> Objects, int Total, bool Truncated, string? Message = null)
{
    public static ObjectListResponse From(ObjectList list) => new(
        [.. list.Objects.Select(ObjectEntry.From)],
        list.Total,
        list.Truncated,
        list.Truncated
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Showing {list.Objects.Count} of {list.Total} matching objects: the list is capped at this database's row cap. Filter by schema, type or name pattern to see the rest.")
            : null);
}

/// <summary>One listed object. A database-level trigger has no schema, so <see cref="Schema"/> is left out.</summary>
internal sealed record ObjectEntry(string? Schema, string Name, string Type, long? ApproxRows)
{
    public static ObjectEntry From(ObjectSummary summary) => new(summary.Schema, summary.Name, summary.Type.ToWireName(), summary.ApproxRows);
}

/// <summary>
/// What <c>describe_object</c> returns: the object's identity and the parts for its type (design I3). Parts that
/// don't apply or are empty are null, so they're left out. A definition is its text, or
/// <c>{ "unavailable": "&lt;reason&gt;" }</c>.
/// </summary>
internal sealed record ObjectDetailsResponse(
    string? Schema,
    string Name,
    string Type,
    string? Kind,
    IReadOnlyList<ColumnDetails>? Columns,
    KeyDetails? PrimaryKey,
    IReadOnlyList<KeyDetails>? UniqueConstraints,
    IReadOnlyList<CheckDetails>? CheckConstraints,
    IReadOnlyList<IndexDetails>? Indexes,
    IReadOnlyList<string>? Triggers,
    IReadOnlyList<ForeignKeyDetails>? ForeignKeys,
    IReadOnlyList<ForeignKeyDetails>? ReferencedBy,
    TemporalDetails? Temporal,
    IReadOnlyList<ParameterDetails>? Parameters,
    string? ReturnType,
    JsonNode? Definition,
    SequenceDetails? Sequence,
    string? Target,
    string? BaseType,
    bool? Nullable)
{
    public static ObjectDetailsResponse From(ObjectDetails details) => new(
        details.Schema,
        details.Name,
        details.Type.ToWireName(),
        details.Kind,
        NullIfEmpty(details.Columns),
        details.PrimaryKey,
        NullIfEmpty(details.UniqueConstraints),
        NullIfEmpty(details.CheckConstraints),
        NullIfEmpty(details.Indexes),
        NullIfEmpty(details.Triggers),
        NullIfEmpty(details.ForeignKeys),
        NullIfEmpty(details.ReferencedBy),
        details.Temporal,
        NullIfEmpty(details.Parameters),
        details.ReturnType,
        details.Definition switch
        {
            Introspection.Definition.Text text => JsonValue.Create(text.Value),
            Introspection.Definition.Unavailable unavailable => new JsonObject { ["unavailable"] = unavailable.Reason },
            _ => null,
        },
        details.Sequence,
        details.Target,
        details.BaseType,
        details.Nullable);

    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T>? items) => items is { Count: > 0 } ? items : null;
}

/// <summary>
/// What <c>script_database</c> returns (design I7): where the script is, its size, how many objects of each type it
/// holds, and what it left out with why. Never the script text: the agent reads the file.
/// </summary>
internal sealed record ScriptResponse(string Path, long Bytes, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<OmittedEntry> Omitted)
{
    public static ScriptResponse From(ScriptResult script) => new(
        script.Path,
        script.Bytes,
        script.Counts,
        [.. script.Omitted.Select(o => new OmittedEntry(o.Item.Schema, o.Item.Name, o.Item.Type, o.Reason))]);
}

/// <summary>One object the script left out. Something that isn't schema-scoped, such as a user, has no schema.</summary>
internal sealed record OmittedEntry(string? Schema, string Name, string Type, string Reason);
