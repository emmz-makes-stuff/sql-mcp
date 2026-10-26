using System.Buffers;
using System.Globalization;
using System.Xml;

namespace SqlMcp.Execution.SqlServer;

/// <summary>
/// Rebuilds an <c>xml</c> value's text from SqlClient's <see cref="XmlReader"/>, the only way SqlClient streams it:
/// every text accessor refuses <c>xml</c> or reads it whole (2026-10-26 decision). The output follows SQL Server's own
/// serialisation, which is what <c>CAST(x AS nvarchar(max))</c> and a whole read return:
/// <list type="bullet">
/// <item>an element with no content is <c>&lt;a/&gt;</c>, and attributes are double-quoted;</item>
/// <item>text escapes <c>&amp; &lt; &gt;</c> and carriage return; attributes also escape the double quote, line feed
/// and tab;</item>
/// <item>a text node of only whitespace writes its last character as a character reference, so it survives a re-parse;</item>
/// <item>CDATA comes out as escaped text, and comments and processing instructions as they are.</item>
/// </list>
/// Text content is read in chunks against the budget. The parser still reads a single attribute, comment or
/// processing instruction whole.
/// </summary>
internal static class SqlServerXml
{
    public static SqlServerValues.SizedValue? Read(XmlReader xml, long budget)
    {
        var text = new BoundedText(budget);
        var buffer = ArrayPool<char>.Shared.Rent(SqlServerValues.ChunkSize);
        try
        {
            return ReadNodes(xml, text, buffer);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static SqlServerValues.SizedValue? ReadNodes(XmlReader xml, BoundedText text, char[] buffer)
    {
        while (xml.Read())
        {
            switch (xml.NodeType)
            {
                case XmlNodeType.Element:
                    WriteStartElement(xml, text);
                    break;
                case XmlNodeType.EndElement:
                    text.Append("</");
                    text.Append(xml.Name);
                    text.Append(">");
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (!WriteText(xml, text, buffer))
                    {
                        return null;
                    }

                    break;
                case XmlNodeType.Comment:
                    text.Append("<!--");
                    text.Append(xml.Value);
                    text.Append("-->");
                    break;
                case XmlNodeType.ProcessingInstruction:
                    text.Append("<?");
                    text.Append(xml.Name);
                    text.Append(" ");
                    text.Append(xml.Value);
                    text.Append("?>");
                    break;
                default:
                    break;
            }

            if (text.IsOver)
            {
                return null;
            }
        }

        return text.Finish();
    }

    private static void WriteStartElement(XmlReader xml, BoundedText text)
    {
        text.Append("<");
        text.Append(xml.Name);
        var empty = xml.IsEmptyElement;
        if (xml.MoveToFirstAttribute())
        {
            do
            {
                text.Append(" ");
                text.Append(xml.Name);
                text.Append("=\"");
                Escape(xml.Value, text, attribute: true);
                text.Append("\"");
            }
            while (xml.MoveToNextAttribute());

            xml.MoveToElement();
        }

        text.Append(empty ? "/>" : ">");
    }

    /// <summary>
    /// Writes a text node in chunks, holding back its last character until the end, when a whitespace-only node writes
    /// it as a character reference. <see cref="XmlReader.ReadValueChunk"/> never splits a surrogate pair, so only a
    /// whitespace character is ever held back alone. Returns false once the budget is passed.
    /// </summary>
    private static bool WriteText(XmlReader xml, BoundedText text, char[] buffer)
    {
        var whitespace = xml.NodeType is XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace;
        char? held = null;
        int read;
        while ((read = xml.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
        {
            if (held is { } previous)
            {
                Escape([previous], text, attribute: false);
            }

            Escape(buffer.AsSpan(0, read - 1), text, attribute: false);
            held = buffer[read - 1];
            if (text.IsOver)
            {
                return false;
            }
        }

        if (held is { } last)
        {
            if (whitespace)
            {
                text.Append(CharacterReference(last));
            }
            else
            {
                Escape([last], text, attribute: false);
            }
        }

        return !text.IsOver;
    }

    private static void Escape(ReadOnlySpan<char> value, BoundedText text, bool attribute)
    {
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var entity = value[i] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '\r' => "&#x0D;",
                '"' when attribute => "&quot;",
                '\n' or '\t' when attribute => CharacterReference(value[i]),
                _ => null,
            };
            if (entity is null)
            {
                continue;
            }

            text.Append(value[start..i]);
            text.Append(entity);
            start = i + 1;
        }

        text.Append(value[start..]);
    }

    private static string CharacterReference(char c) => string.Create(CultureInfo.InvariantCulture, $"&#x{(int)c:X2};");
}
