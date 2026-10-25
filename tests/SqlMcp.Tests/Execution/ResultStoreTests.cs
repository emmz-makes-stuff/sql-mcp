using System.Text.Json.Nodes;
using SqlMcp.Execution;

namespace SqlMcp.Tests.Execution;

public sealed class ResultStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 18, 20, 0, TimeSpan.Zero);
    private static readonly ResultColumn[] IdColumn = [new("id", "int")];

    private readonly FakeTime _time = new(Start);
    private readonly ResultStore _store;

    public ResultStoreTests() => _store = new ResultStore(_time);

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Read_every_page()
    {
        var first = _store.Open(Result(Set(250)), pageSize: 100);
        Assert.Equal((1, 100, 1, 3), (first.FirstRow, first.LastRow, first.PageNumber, first.PageCount));
        Assert.Equal(1, Id(first.Rows[0]));
        Assert.NotNull(first.Handle);

        var second = _store.NextPage(first.Handle);
        Assert.NotNull(second);
        Assert.Equal((101, 200), (second.FirstRow, second.LastRow));
        Assert.Equal(101, Id(second.Rows[0]));
        Assert.False(second.IsLastPage);
        Assert.Equal(first.Handle, second.Handle);

        var third = _store.NextPage(first.Handle);
        Assert.NotNull(third);
        Assert.Equal((201, 250, 50), (third.FirstRow, third.LastRow, third.Rows.Count));
        Assert.Equal(250, Id(third.Rows[^1]));
        Assert.True(third.IsLastPage);

        // The last page releases the handle.
        Assert.Equal(0, _store.Count);
        Assert.Null(_store.NextPage(first.Handle));
    }

    [Fact]
    public void A_result_that_fits_one_page_gets_no_handle()
    {
        var page = _store.Open(Result(Set(42)), pageSize: 100);

        Assert.True(page.IsLastPage);
        Assert.Equal((1, 42, 1, 1), (page.FirstRow, page.LastRow, page.PageNumber, page.PageCount));
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void Pages_never_cross_result_sets()
    {
        // 2026-10-25 decision: a page holds one set's rows. An empty set in between still gets its own page.
        var otherColumns = new[] { new ResultColumn("name", "nvarchar(50)") };
        var result = Result(Set(250), Set(0), new ResultSet(otherColumns, Rows(5)));

        var pages = ReadAll(_store.Open(result, pageSize: 100));

        Assert.Equal([0, 0, 0, 1, 2], pages.Select(p => p.ResultSetIndex));
        Assert.All(pages, p => Assert.Equal(5, p.PageCount));

        var empty = pages[3];
        Assert.Equal((0, 0, 0), (empty.FirstRow, empty.LastRow, empty.Rows.Count));
        Assert.Equal(IdColumn, empty.Columns);

        var last = pages[4];
        Assert.Equal(otherColumns, last.Columns);
        Assert.Equal((1, 5), (last.FirstRow, last.LastRow));
        Assert.True(last.IsLastPage);
    }

    [Fact]
    public void A_result_with_no_sets_is_one_empty_page()
    {
        var page = _store.Open(new BufferedResult([], false, null), pageSize: 100);

        Assert.True(page.IsLastPage);
        Assert.Empty(page.Columns);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public void Unknown_handle()
    {
        Assert.Null(_store.NextPage("not-a-handle"));
        Assert.False(_store.Abandon("not-a-handle"));
        Assert.Contains("run the query again", ResultStore.UnknownHandleMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Abandon()
    {
        var first = _store.Open(Result(Set(250)), pageSize: 100);

        Assert.True(_store.Abandon(first.Handle!));

        Assert.Equal(0, _store.Count);
        Assert.Null(_store.NextPage(first.Handle!));
        Assert.False(_store.Abandon(first.Handle!));
    }

    [Fact]
    public void Expired_handle_is_swept_by_the_timer()
    {
        var first = _store.Open(Result(Set(250)), pageSize: 100);

        _time.Advance(ResultStore.IdleTimeout + TimeSpan.FromMinutes(1));

        Assert.Equal(0, _store.Count);
        Assert.Null(_store.NextPage(first.Handle!));
    }

    [Fact]
    public void Expired_handle_is_refused_even_before_a_sweep()
    {
        var first = _store.Open(Result(Set(250)), pageSize: 100);

        // Swept only on a later tick: the time check on access must refuse it anyway.
        _time.Advance(ResultStore.IdleTimeout + TimeSpan.FromSeconds(1));
        Assert.Equal(1, _store.Count);

        Assert.Null(_store.NextPage(first.Handle!));
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void Reading_a_page_keeps_the_handle_alive()
    {
        var first = _store.Open(Result(Set(350)), pageSize: 100);

        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.NotNull(_store.NextPage(first.Handle!));
        _time.Advance(TimeSpan.FromMinutes(9));

        Assert.NotNull(_store.NextPage(first.Handle!));
    }

    [Fact]
    public void Disposing_releases_every_handle()
    {
        var first = _store.Open(Result(Set(250)), pageSize: 100);
        _store.Open(Result(Set(250)), pageSize: 100);

        _store.Dispose();

        Assert.Equal(0, _store.Count);
        Assert.Null(_store.NextPage(first.Handle!));
        Assert.Throws<ObjectDisposedException>(() => _store.Open(Result(Set(250)), pageSize: 100));
        Assert.Throws<ObjectDisposedException>(() => _store.Open(Result(Set(1)), pageSize: 100));
    }

    [Fact]
    public void Handles_are_opaque_and_distinct()
    {
        var handles = Enumerable.Range(0, 50).Select(_ => _store.Open(Result(Set(250)), pageSize: 100).Handle!).ToList();

        Assert.Equal(50, handles.Distinct(StringComparer.Ordinal).Count());
        Assert.All(handles, h => Assert.Matches("^[A-Za-z0-9_-]{22}$", h));
    }

    private List<ResultPage> ReadAll(ResultPage first)
    {
        var pages = new List<ResultPage> { first };
        while (pages[^1].Handle is { } handle)
        {
            pages.Add(_store.NextPage(handle) ?? throw new InvalidOperationException("handle released early"));
        }

        return pages;
    }

    private static BufferedResult Result(params ResultSet[] sets) => new(sets, false, null);

    private static ResultSet Set(int rows) => new(IdColumn, Rows(rows));

    private static JsonNode?[][] Rows(int count) =>
        Enumerable.Range(1, count).Select(i => new JsonNode?[] { JsonValue.Create(i) }).ToArray();

    private static int Id(JsonNode?[] row) => row[0]!.GetValue<int>();
}
