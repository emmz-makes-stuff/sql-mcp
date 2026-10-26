using SqlMcp.Execution.SqlServer;

namespace SqlMcp.Tests.Execution;

public sealed class BoundedTextTests
{
    [Fact]
    public void Text_is_counted_in_UTF8_bytes()
    {
        var text = new BoundedText(budget: 3);
        text.Append("aé");

        var value = text.Finish();

        Assert.NotNull(value);
        Assert.Equal(("aé", 3L), (value.Value.Value!.GetValue<string>(), value.Value.Size));
    }

    [Fact]
    public void A_surrogate_pair_split_across_appends_counts_as_four_bytes()
    {
        const string emoji = "😀";
        var text = new BoundedText(budget: 4);
        text.Append(emoji.AsSpan(0, 1));
        text.Append(emoji.AsSpan(1, 1));

        var value = text.Finish();

        Assert.NotNull(value);
        Assert.Equal((emoji, 4L), (value.Value.Value!.GetValue<string>(), value.Value.Size));
    }

    [Fact]
    public void Text_over_the_budget_is_dropped()
    {
        var text = new BoundedText(budget: 3);
        text.Append("abc");
        Assert.False(text.IsOver);

        text.Append("d");

        Assert.True(text.IsOver);
        Assert.Null(text.Finish());
    }
}
