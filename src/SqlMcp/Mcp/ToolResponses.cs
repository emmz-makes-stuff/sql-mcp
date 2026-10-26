using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlMcp.Execution;
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
