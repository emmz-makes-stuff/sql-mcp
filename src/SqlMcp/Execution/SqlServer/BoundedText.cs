using System.Text;
using System.Text.Json.Nodes;

namespace SqlMcp.Execution.SqlServer;

/// <summary>
/// Collects text up to a budget of UTF-8 bytes, the unit the byte cap counts. Once the text passes the budget, nothing
/// more is kept, so a value that's too large never holds more than about the budget in memory.
/// </summary>
internal sealed class BoundedText(long budget)
{
    /// <summary>A lone surrogate is written as U+FFFD, which is three bytes.</summary>
    private const int LoneSurrogateSize = 3;

    private readonly StringBuilder _text = new();
    private long _size;

    // A high surrogate at the end of one append, waiting for its low surrogate. Encoder.GetByteCount doesn't carry
    // it between calls, so the pair is counted here, once, as four bytes.
    private bool _pendingHigh;

    public bool IsOver => _size > budget;

    public void Append(ReadOnlySpan<char> chars)
    {
        if (IsOver)
        {
            return;
        }

        _size += Count(chars);
        if (!IsOver)
        {
            _text.Append(chars);
        }
    }

    /// <summary>The text and its size, or null if it went over the budget.</summary>
    public SqlServerValues.SizedValue? Finish()
    {
        if (_pendingHigh)
        {
            _size += LoneSurrogateSize;
            _pendingHigh = false;
        }

        return IsOver ? null : new SqlServerValues.SizedValue(JsonValue.Create(_text.ToString()), _size);
    }

    private long Count(ReadOnlySpan<char> chars)
    {
        var size = 0L;
        if (_pendingHigh && !chars.IsEmpty)
        {
            _pendingHigh = false;
            if (char.IsLowSurrogate(chars[0]))
            {
                size += 4;
                chars = chars[1..];
            }
            else
            {
                size += LoneSurrogateSize;
            }
        }

        if (!chars.IsEmpty && char.IsHighSurrogate(chars[^1]))
        {
            _pendingHigh = true;
            chars = chars[..^1];
        }

        return size + Encoding.UTF8.GetByteCount(chars);
    }
}
