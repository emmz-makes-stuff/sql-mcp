using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlMcp.Configuration;

/// <summary>One step in a path into a JSONC document: a property name or an array index.</summary>
internal readonly record struct JsoncSegment(string? Name, int Index)
{
    public static implicit operator JsoncSegment(string name) => FromName(name);

    public static implicit operator JsoncSegment(int index) => FromIndex(index);

    public static JsoncSegment FromName(string name) => new(name, -1);

    public static JsoncSegment FromIndex(int index) => new(null, index);

    public override string ToString() => Name ?? $"[{Index}]";
}

/// <summary>
/// Edits JSON-with-comments text in place (design D5). Each edit finds values by their token offsets and splices
/// new text in, so comments, trailing commas and the rest of the formatting are kept. New text follows the
/// surrounding indentation and newline style. Every edit re-parses the result, and an edit that would break the
/// document throws <see cref="InvalidOperationException"/>; checking it against a schema is the caller's job.
/// </summary>
internal sealed class JsoncEditor
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private Node _root;

    /// <exception cref="JsonException"><paramref name="text"/> is not valid JSONC.</exception>
    public JsoncEditor(string text)
    {
        Text = text;
        _root = Parse(text);
    }

    public string Text { get; private set; }

    private string NewLine => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    public bool Exists(params JsoncSegment[] path) => Find(path) is not null;

    /// <summary>The value at <paramref name="path"/>, or null if it doesn't exist.</summary>
    public JsonNode? Get(params JsoncSegment[] path) =>
        Find(path) is { } node ? JsonNode.Parse(Text[node.Start..node.End], documentOptions: ConfigLoader.DocumentOptions) : null;

    /// <summary>
    /// Replaces the value at <paramref name="path"/>, or adds it as a new property. Missing objects on the way are
    /// created. A missing array index throws.
    /// </summary>
    public void Set(IReadOnlyList<JsoncSegment> path, JsonNode? value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(path.Count);

        var node = _root;
        for (var depth = 0; depth < path.Count; depth++)
        {
            var segment = path[depth];
            var index = IndexOf(node, segment);
            if (index < 0)
            {
                if (segment.Name is null || node.Kind != JsonTokenType.StartObject)
                {
                    throw new InvalidOperationException($"{Describe(path, depth + 1)} doesn't exist.");
                }

                // Wrap the value in the objects still missing, innermost first.
                for (var missing = path.Count - 1; missing > depth; missing--)
                {
                    value = new JsonObject { [path[missing].Name!] = value };
                }

                Insert(node, segment.Name, value);
                return;
            }

            if (depth == path.Count - 1)
            {
                var target = node.Members[index].Value;
                Apply(Splice.Replace(target.Start, target.End, Render(value, LineIndent(node.Members[index].Start), inline: false)));
                return;
            }

            node = node.Members[index].Value;
        }
    }

    /// <summary>Adds <paramref name="value"/> to the end of the array at <paramref name="path"/>, creating it if missing.</summary>
    public void Append(IReadOnlyList<JsoncSegment> path, JsonNode? value)
    {
        var array = Find(path);
        if (array is null)
        {
            Set(path, new JsonArray(value));
            return;
        }

        if (array.Kind != JsonTokenType.StartArray)
        {
            throw new InvalidOperationException($"{Describe(path, path.Count)} is not an array.");
        }

        Insert(array, null, value);
    }

    /// <summary>Removes the property or array item at <paramref name="path"/>. Returns false if it doesn't exist.</summary>
    public bool Remove(IReadOnlyList<JsoncSegment> path)
    {
        ArgumentOutOfRangeException.ThrowIfZero(path.Count);

        var container = Find(path.Take(path.Count - 1).ToArray());
        var index = container is null ? -1 : IndexOf(container, path[^1]);
        if (index < 0)
        {
            return false;
        }

        RemoveMember(container!, index);
        return true;
    }

    private void Insert(Node container, string? name, JsonNode? value)
    {
        var unit = IndentUnit();
        var prefix = name is null ? "" : JsonSerializer.Serialize(name, RenderOptions(unit, NewLine, inline: true)) + ": ";
        var nested = value is JsonObject or JsonArray;

        if (container.Members.Count == 0)
        {
            var innerStart = container.Start + 1;
            var innerEnd = container.End - 1;
            var innerIsBlank = string.IsNullOrWhiteSpace(Text[innerStart..innerEnd]);
            if (container.Kind == JsonTokenType.StartArray && !nested && innerIsBlank)
            {
                Apply(Splice.Replace(innerStart, innerEnd, Render(value, "", inline: true)));
                return;
            }

            var outer = LineIndent(container.Start);
            var indent = outer + unit;
            var member = NewLine + indent + prefix + Render(value, indent, inline: false);
            Apply(innerIsBlank
                ? Splice.Replace(innerStart, innerEnd, member + NewLine + outer)
                : Splice.Insert(innerStart, member));
            return;
        }

        var last = container.Members[^1];
        var lastEnd = last.Value.End;

        if (IsInline(container))
        {
            Apply(Splice.Insert(lastEnd, ", " + prefix + Render(value, "", inline: true)));
            return;
        }

        var memberIndent = LineIndent(last.Start);
        var position = SkipSpaces(lastEnd);
        var hadComma = position < Text.Length && Text[position] == ',';
        var insertAt = SkipSameLineComments(hadComma ? position + 1 : position);
        var text = NewLine + memberIndent + prefix + Render(value, memberIndent, inline: false) + (hadComma ? "," : "");

        // The later splice goes first so the earlier offset stays valid.
        Apply(hadComma
            ? [Splice.Insert(insertAt, text)]
            : [Splice.Insert(insertAt, text), Splice.Insert(lastEnd, ",")]);
    }

    private void RemoveMember(Node container, int index)
    {
        var member = container.Members[index];
        var start = member.Start;
        var end = member.Value.End;

        var afterValue = SkipSpacesAndComments(end);
        var hasComma = afterValue < Text.Length && Text[afterValue] == ',';

        if (container.Members.Count == 1)
        {
            // The only member: leave an empty container if nothing else (a comment) is inside it.
            var removeEnd = hasComma ? afterValue + 1 : end;
            var rest = Text[(container.Start + 1)..start] + Text[removeEnd..(container.End - 1)];
            Apply(string.IsNullOrWhiteSpace(rest)
                ? Splice.Remove(container.Start + 1, container.End - 1)
                : ExpandToLines(start, removeEnd) ?? Splice.Remove(start, removeEnd));
            return;
        }

        if (hasComma)
        {
            // A comma follows (another member, or a trailing comma): remove the member with it.
            var removeEnd = afterValue + 1;
            Apply(ExpandToLines(start, removeEnd) ?? Splice.Remove(start, SkipSpaces(removeEnd)));
            return;
        }

        // The last member, with no trailing comma: the comma before it goes too.
        var previousEnd = container.Members[index - 1].Value.End;
        var comma = SkipSpacesAndComments(previousEnd);
        if (string.IsNullOrWhiteSpace(Text[(comma + 1)..start]))
        {
            // Only whitespace between the comma and the member: remove the comma, the member and any comment
            // after it on the same line.
            var lineEnd = SkipSameLineComments(SkipSpaces(end));
            Apply(Splice.Remove(comma, IsMultiLine(comma, start) ? lineEnd : end));
            return;
        }

        Apply([ExpandToLines(start, end) ?? Splice.Remove(start, end), Splice.Remove(comma, comma + 1)]);
    }

    /// <summary>
    /// If the member stands alone on its line(s), removes the whole lines, including a trailing comment and the
    /// line break. Returns null if it shares a line with something else.
    /// </summary>
    private Splice? ExpandToLines(int start, int end)
    {
        var lineStart = start;
        while (lineStart > 0 && Text[lineStart - 1] is ' ' or '\t')
        {
            lineStart--;
        }

        if (lineStart > 0 && Text[lineStart - 1] != '\n')
        {
            return null;
        }

        var lineEnd = SkipSameLineComments(SkipSpaces(end));
        if (lineEnd < Text.Length && Text[lineEnd] is not ('\r' or '\n'))
        {
            return null;
        }

        if (lineEnd < Text.Length && Text[lineEnd] == '\r')
        {
            lineEnd++;
        }

        if (lineEnd < Text.Length && Text[lineEnd] == '\n')
        {
            lineEnd++;
        }

        return Splice.Remove(lineStart, lineEnd);
    }

    private Node? Find(IReadOnlyList<JsoncSegment> path)
    {
        var node = _root;
        foreach (var segment in path)
        {
            var index = IndexOf(node, segment);
            if (index < 0)
            {
                return null;
            }

            node = node.Members[index].Value;
        }

        return node;
    }

    private static int IndexOf(Node node, JsoncSegment segment) => segment.Name is null
        ? node.Kind == JsonTokenType.StartArray && segment.Index >= 0 && segment.Index < node.Members.Count ? segment.Index : -1
        : node.Kind == JsonTokenType.StartObject ? node.Members.FindLastIndex(m => m.Name == segment.Name) : -1;

    private static string Describe(IReadOnlyList<JsoncSegment> path, int count) =>
        "$" + string.Concat(path.Take(count).Select(s => s.Name is null ? s.ToString() : "." + s.Name));

    private void Apply(Splice splice) => Apply([splice]);

    /// <summary>Applies splices given latest first, then re-parses.</summary>
    private void Apply(Splice[] splices)
    {
        var builder = new StringBuilder(Text);
        foreach (var splice in splices)
        {
            builder.Remove(splice.Start, splice.End - splice.Start).Insert(splice.Start, splice.Text);
        }

        var text = builder.ToString();
        try
        {
            _root = Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The edit would leave the document invalid.", ex);
        }

        Text = text;
    }

    private string Render(JsonNode? value, string indent, bool inline)
    {
        var json = value is null ? "null" : value.ToJsonString(RenderOptions(IndentUnit(), NewLine, inline));
        return inline ? json : json.Replace(NewLine, NewLine + indent, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions RenderOptions(string unit, string newLine, bool inline) => new()
    {
        WriteIndented = !inline,
        IndentCharacter = unit.StartsWith('\t') ? '\t' : ' ',
        IndentSize = unit.Length,
        NewLine = newLine,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The document's indentation step, from the first member of the root, or two spaces.</summary>
    private string IndentUnit()
    {
        if (_root.Members.Count > 0 && !IsInline(_root))
        {
            var root = LineIndent(_root.Start);
            var member = LineIndent(_root.Members[0].Start);
            if (member.Length > root.Length && member.StartsWith(root, StringComparison.Ordinal))
            {
                return member[root.Length..];
            }
        }

        return "  ";
    }

    private string LineIndent(int position)
    {
        var lineStart = position == 0 ? 0 : Text.LastIndexOf('\n', position - 1) + 1;
        var end = lineStart;
        while (end < Text.Length && Text[end] is ' ' or '\t')
        {
            end++;
        }

        return Text[lineStart..end];
    }

    private bool IsInline(Node container) => !IsMultiLine(container.Start, container.End);

    private bool IsMultiLine(int start, int end) => Text.AsSpan(start, end - start).Contains('\n');

    private int SkipSpaces(int position)
    {
        while (position < Text.Length && Text[position] is ' ' or '\t')
        {
            position++;
        }

        return position;
    }

    /// <summary>Skips spaces and comments that end on the current line, stopping at a line break or anything else.</summary>
    private int SkipSameLineComments(int position)
    {
        while (true)
        {
            position = SkipSpaces(position);
            if (Text.AsSpan(position).StartsWith("//"))
            {
                var newline = Text.IndexOfAny(['\r', '\n'], position);
                return newline < 0 ? Text.Length : newline;
            }

            if (Text.AsSpan(position).StartsWith("/*"))
            {
                var close = Text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (close < 0 || IsMultiLine(position, close))
                {
                    return position;
                }

                position = close + 2;
                continue;
            }

            return position;
        }
    }

    private int SkipSpacesAndComments(int position)
    {
        while (true)
        {
            while (position < Text.Length && char.IsWhiteSpace(Text[position]))
            {
                position++;
            }

            if (Text.AsSpan(position).StartsWith("//"))
            {
                var newline = Text.IndexOf('\n', position);
                position = newline < 0 ? Text.Length : newline;
                continue;
            }

            if (Text.AsSpan(position).StartsWith("/*"))
            {
                var close = Text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                position = close < 0 ? Text.Length : close + 2;
                continue;
            }

            return position;
        }
    }

    private static Node Parse(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var charAt = ByteToCharOffsets(text, bytes.Length);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        if (!reader.Read())
        {
            throw new JsonException("The document is empty.");
        }

        var root = ReadValue(ref reader, charAt);
        if (reader.Read())
        {
            throw new JsonException("The document has more than one value.");
        }

        return root;
    }

    private static Node ReadValue(ref Utf8JsonReader reader, int[] charAt)
    {
        var node = new Node(reader.TokenType, charAt[reader.TokenStartIndex]);
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var nameStart = charAt[reader.TokenStartIndex];
                var name = reader.GetString();
                reader.Read();
                node.Members.Add(new Member(name, nameStart, ReadValue(ref reader, charAt)));
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var item = ReadValue(ref reader, charAt);
                node.Members.Add(new Member(null, item.Start, item));
            }
        }

        node.End = charAt[reader.BytesConsumed];
        return node;
    }

    /// <summary>Maps each UTF-8 byte offset to its UTF-16 char offset (offsets inside a character map to its end).</summary>
    private static int[] ByteToCharOffsets(string text, int byteCount)
    {
        var map = new int[byteCount + 1];
        var bytePosition = 0;
        for (var charIndex = 0; charIndex < text.Length; charIndex++)
        {
            int width;
            if (char.IsHighSurrogate(text[charIndex]) && charIndex + 1 < text.Length)
            {
                width = 4;
                charIndex++;
            }
            else
            {
                width = text[charIndex] switch { < '\u0080' => 1, < 'ࠀ' => 2, _ => 3 };
            }

            for (var i = 1; i <= width; i++)
            {
                map[bytePosition + i] = charIndex + 1;
            }

            bytePosition += width;
        }

        return map;
    }

    private sealed class Node(JsonTokenType kind, int start)
    {
        public JsonTokenType Kind { get; } = kind;

        public int Start { get; } = start;

        public int End { get; set; }

        public List<Member> Members { get; } = [];
    }

    /// <summary>A property (with its name's offset as <see cref="Start"/>) or an array item.</summary>
    private sealed record Member(string? Name, int Start, Node Value);

    private readonly record struct Splice(int Start, int End, string Text)
    {
        public static Splice Insert(int position, string text) => new(position, position, text);

        public static Splice Remove(int start, int end) => new(start, end, "");

        public static Splice Replace(int start, int end, string text) => new(start, end, text);
    }
}
