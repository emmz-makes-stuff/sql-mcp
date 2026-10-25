using System.Text.Json.Nodes;

namespace SqlMcp.Execution;

/// <summary>A result column: its name (empty for an unnamed expression) and its SQL type, such as <c>decimal(38,10)</c>.</summary>
internal sealed record ResultColumn(string Name, string SqlType);

/// <summary>
/// One result set as buffered. Each row is an array aligned with <see cref="Columns"/>. A null element is SQL NULL;
/// other values are already converted for faithful serialisation (design D7).
/// </summary>
internal sealed record ResultSet(IReadOnlyList<ResultColumn> Columns, IReadOnlyList<JsonNode?[]> Rows);

/// <summary>
/// Everything one query call buffered, across all its result sets. <see cref="TruncationMessage"/> is set when a cap
/// was reached, and tells the agent how to narrow the query.
/// </summary>
internal sealed record BufferedResult(IReadOnlyList<ResultSet> ResultSets, bool Truncated, string? TruncationMessage)
{
    public int RowCount => ResultSets.Sum(set => set.Rows.Count);
}

/// <summary>
/// One page of a result. A page never crosses a result-set boundary (2026-10-25 devlog decision), so it has one
/// column list. <see cref="FirstRow"/> and <see cref="LastRow"/> are 1-based within the result set, and are both 0
/// for an empty set. <see cref="Handle"/> is set while more pages remain.
/// </summary>
internal sealed record ResultPage(
    int ResultSetIndex,
    IReadOnlyList<ResultColumn> Columns,
    int FirstRow,
    int LastRow,
    IReadOnlyList<JsonNode?[]> Rows,
    int PageNumber,
    int PageCount,
    string? Handle)
{
    public bool IsLastPage => Handle is null;
}
