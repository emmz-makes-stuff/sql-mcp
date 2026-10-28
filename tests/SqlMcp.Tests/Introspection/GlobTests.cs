using SqlMcp.Introspection;

namespace SqlMcp.Tests.Introspection;

public sealed class GlobTests
{
    [Theory]
    [InlineData("order*", "order%")]
    [InlineData("a_b?", @"a\_b_")]
    [InlineData("[x]*", @"\[x]%")]
    [InlineData("100%", @"100\%")]
    [InlineData(@"back\slash", @"back\\slash")]
    [InlineData("users", "users")]
    public void Glob_becomes_a_like_pattern_with_like_wildcards_escaped(string glob, string like) =>
        Assert.Equal(like, Glob.ToLike(glob));
}
