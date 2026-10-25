using System.Buffers.Text;
using System.Security.Cryptography;

namespace SqlMcp.Execution;

/// <summary>
/// The session's buffered results, keyed by opaque handles (design D7). A handle is released when its last page is
/// served, when it is abandoned, when it has been idle for <see cref="IdleTimeout"/>, or when the store is disposed
/// at session end. Idle handles are swept by a timer, and expiry is also checked on every access, so a late sweep
/// never serves an expired handle.
/// </summary>
internal sealed class ResultStore : IDisposable
{
    public const string UnknownHandleMessage =
        "unknown or expired result handle: results are released after the last page, on abandon, and after 10 minutes idle. Run the query again.";

    /// <summary>Spec: handles are released after 10 minutes idle. Not configurable.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly ITimer _sweeper;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    public ResultStore(TimeProvider time)
    {
        _time = time;
        _sweeper = time.CreateTimer(_ => EvictIdle(), null, SweepInterval, SweepInterval);
    }

    /// <summary>The number of handles held, for tests and diagnostics.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Returns the first page of <paramref name="result"/>. If more pages remain, the result is kept under a new
    /// handle, which the page carries; otherwise nothing is kept.
    /// </summary>
    public ResultPage Open(BufferedResult result, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var spans = Paginate(result, pageSize);
        var handle = spans.Count == 1 ? null : NewHandle();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (handle is not null)
            {
                _entries.Add(handle, new Entry(result, spans) { NextPage = 1, LastTouched = _time.GetUtcNow() });
            }
        }

        return ToPage(result, spans, 0, handle);
    }

    /// <summary>
    /// Returns the next page for <paramref name="handle"/>, releasing the handle if it is the last one, or null if
    /// the handle is unknown, released or expired.
    /// </summary>
    public ResultPage? NextPage(string handle)
    {
        Entry entry;
        int index;
        lock (_lock)
        {
            if (!TryGetLive(handle, out entry))
            {
                return null;
            }

            index = entry.NextPage++;
            entry.LastTouched = _time.GetUtcNow();
            if (entry.NextPage == entry.Spans.Count)
            {
                _entries.Remove(handle);
            }
        }

        var isLast = index == entry.Spans.Count - 1;
        return ToPage(entry.Result, entry.Spans, index, isLast ? null : handle);
    }

    /// <summary>Releases <paramref name="handle"/> and its buffer. Returns false if it was unknown, released or expired.</summary>
    public bool Abandon(string handle)
    {
        lock (_lock)
        {
            return TryGetLive(handle, out _) && _entries.Remove(handle);
        }
    }

    /// <summary>Releases every handle idle for longer than <see cref="IdleTimeout"/>. Called by the timer.</summary>
    private void EvictIdle()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            foreach (var (handle, entry) in _entries)
            {
                if (IsExpired(entry, now))
                {
                    _entries.Remove(handle);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _entries.Clear();
        }

        _sweeper.Dispose();
    }

    private bool TryGetLive(string handle, out Entry entry)
    {
        if (!_entries.TryGetValue(handle, out entry!))
        {
            return false;
        }

        if (IsExpired(entry, _time.GetUtcNow()))
        {
            _entries.Remove(handle);
            return false;
        }

        return true;
    }

    private static bool IsExpired(Entry entry, DateTimeOffset now) => now - entry.LastTouched > IdleTimeout;

    private static string NewHandle()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>
    /// Cuts the result into pages of at most <paramref name="pageSize"/> rows, never crossing a result set. An empty
    /// set still gets a page, so the agent sees its columns. A result with no sets at all gets one empty page.
    /// </summary>
    private static List<PageSpan> Paginate(BufferedResult result, int pageSize)
    {
        var spans = new List<PageSpan>();
        for (var set = 0; set < result.ResultSets.Count; set++)
        {
            var rows = result.ResultSets[set].Rows.Count;
            if (rows == 0)
            {
                spans.Add(new PageSpan(set, 0, 0));
                continue;
            }

            for (var start = 0; start < rows; start += pageSize)
            {
                spans.Add(new PageSpan(set, start, Math.Min(pageSize, rows - start)));
            }
        }

        if (spans.Count == 0)
        {
            spans.Add(new PageSpan(-1, 0, 0));
        }

        return spans;
    }

    private static ResultPage ToPage(BufferedResult result, List<PageSpan> spans, int index, string? handle)
    {
        var span = spans[index];
        if (span.ResultSet < 0)
        {
            return new ResultPage(0, [], 0, 0, [], 1, 1, handle);
        }

        var set = result.ResultSets[span.ResultSet];
        var rows = set.Rows.Skip(span.Start).Take(span.Count).ToArray();
        var first = span.Count == 0 ? 0 : span.Start + 1;
        var last = span.Start + span.Count;
        return new ResultPage(span.ResultSet, set.Columns, first, last, rows, index + 1, spans.Count, handle);
    }

    private readonly record struct PageSpan(int ResultSet, int Start, int Count);

    private sealed class Entry(BufferedResult result, List<PageSpan> spans)
    {
        public BufferedResult Result { get; } = result;

        public List<PageSpan> Spans { get; } = spans;

        public int NextPage { get; set; }

        public DateTimeOffset LastTouched { get; set; }
    }
}
