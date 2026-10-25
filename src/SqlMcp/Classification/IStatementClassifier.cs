using System.Globalization;

namespace SqlMcp.Classification;

/// <summary>The category of one statement. Each tool runs only its own category; <see cref="Rejected"/> runs nowhere.</summary>
internal enum StatementCategory
{
    Query,
    Command,
    Ddl,
    Execute,
    Rejected,
}

internal static class StatementCategoryNames
{
    /// <summary>The name used in messages, which is also the name of the tool that runs the category.</summary>
    public static string ToName(this StatementCategory category) => category switch
    {
        StatementCategory.Query => "query",
        StatementCategory.Command => "command",
        StatementCategory.Ddl => "ddl",
        StatementCategory.Execute => "execute",
        StatementCategory.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };
}

/// <summary>Parses SQL for one engine and classifies each statement against that engine's allowlists.</summary>
internal interface IStatementClassifier
{
    ClassificationResult Classify(string sql);
}

/// <summary>
/// One statement's category. <see cref="Number"/> is 1-based. <see cref="Reason"/> says why a statement was
/// rejected, naming the syntax element and its position.
/// </summary>
internal sealed record StatementClassification(int Number, StatementCategory Category, int Line, int Column, string? Reason = null);

/// <summary>Whether a whole batch may run under one tool, and if not, why.</summary>
internal sealed record BatchVerdict(bool Accepted, string? Reason)
{
    public static readonly BatchVerdict Accept = new(true, null);

    public static BatchVerdict Reject(string reason) => new(false, reason);
}

/// <summary>
/// The classification of a batch. <see cref="BatchError"/> is set when the batch as a whole can't be classified
/// (a parse error, more than one batch, or nothing to run); <see cref="Statements"/> is then empty.
/// </summary>
internal sealed record ClassificationResult(IReadOnlyList<StatementClassification> Statements, string? BatchError = null)
{
    /// <summary>
    /// Accepts the batch for the tool of <paramref name="tool"/> only if every statement is in that category.
    /// Otherwise the whole batch is rejected, naming each statement that doesn't fit.
    /// </summary>
    public BatchVerdict ForTool(StatementCategory tool)
    {
        if (tool == StatementCategory.Rejected)
        {
            throw new ArgumentOutOfRangeException(nameof(tool), tool, "There is no tool for rejected statements.");
        }

        if (BatchError is not null)
        {
            return BatchVerdict.Reject(BatchError);
        }

        var misfits = Statements.Where(s => s.Category != tool).ToList();
        if (misfits.Count == 0)
        {
            return BatchVerdict.Accept;
        }

        if (Statements.Count == 1)
        {
            var only = misfits[0];
            return BatchVerdict.Reject(only.Category == StatementCategory.Rejected ? only.Reason! : Describe(only));
        }

        var details = misfits.Select(s =>
            $"statement {s.Number.ToString(CultureInfo.InvariantCulture)} (line {s.Line.ToString(CultureInfo.InvariantCulture)}) {Describe(s)}");
        return BatchVerdict.Reject($"{string.Join("; ", details)}. The whole batch is rejected, so nothing in it runs.");
    }

    private static string Describe(StatementClassification statement) => statement.Category == StatementCategory.Rejected
        ? $"is rejected: {statement.Reason}"
        : $"classified as {statement.Category.ToName()}; use the {statement.Category.ToName()} tool";
}
