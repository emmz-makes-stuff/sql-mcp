using System.Buffers;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

namespace SqlMcp.Execution.SqlServer;

/// <summary>
/// Converts SQL Server values without loss (design D7, spec: result values are serialised faithfully). Exact
/// numerics that a JSON reader could round (<c>decimal</c>, <c>numeric</c>, <c>money</c> and <c>bigint</c>) are
/// strings; dates and times are ISO 8601 at full precision; binary is base64.
/// </summary>
/// <remarks>
/// The reader must be opened with <c>CommandBehavior.SequentialAccess</c>. Values that can hold more than 8000 bytes
/// are streamed in chunks and abandoned as soon as they pass the byte budget, so one large value can't pull more than
/// the cap into memory (2026-10-26 decision).
/// </remarks>
internal static class SqlServerValues
{
    /// <summary>What a NULL counts towards the byte cap: the length of <c>null</c>.</summary>
    public const long NullSize = 4;

    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
    private const string DateTimeOffsetFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";
    private const string TimeFormat = @"hh\:mm\:ss\.fffffff";
    /// <summary>How many characters or bytes a streamed value is read in at a time.</summary>
    internal const int ChunkSize = 8192;

    /// <summary>A value and its size as the byte cap counts it.</summary>
    internal readonly record struct SizedValue(JsonNode? Value, long Size);

    /// <summary>Reads one non-NULL value within <c>budget</c> bytes, or returns null if it would go over.</summary>
    internal delegate ValueTask<SizedValue?> ValueReader(SqlDataReader reader, int ordinal, long budget, CancellationToken cancellationToken);

    /// <summary>How to read one column, decided once per result set from its schema.</summary>
    internal sealed record ColumnReader(ResultColumn Column, ValueReader Read);

    public static IReadOnlyList<ColumnReader> Describe(SqlDataReader reader)
    {
        var schema = reader.GetColumnSchema();
        var columns = new ColumnReader[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            columns[i] = Describe(schema[i]);
        }

        return columns;
    }

    /// <summary>
    /// Reads the current row if it fits in <paramref name="budget"/> bytes, counted as each value's unescaped UTF-8
    /// text. Returns null as soon as it can't fit, without reading the rest of the row.
    /// </summary>
    public static async Task<(JsonNode?[] Row, long Size)?> ReadRowAsync(
        SqlDataReader reader, IReadOnlyList<ColumnReader> columns, long budget, CancellationToken cancellationToken)
    {
        var row = new JsonNode?[columns.Count];
        var size = 0L;
        for (var i = 0; i < columns.Count; i++)
        {
            if (await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false))
            {
                size += NullSize;
            }
            else if (await columns[i].Read(reader, i, budget - size, cancellationToken).ConfigureAwait(false) is { } value)
            {
                row[i] = value.Value;
                size += value.Size;
            }
            else
            {
                return null;
            }

            if (size > budget)
            {
                return null;
            }
        }

        return (row, size);
    }

    private static ColumnReader Describe(DbColumn column)
    {
        var name = column.ColumnName ?? string.Empty;
        var dataType = (column.DataTypeName ?? "unknown").ToLowerInvariant();

        // CLR UDTs (geometry, geography, hierarchyid) can't be deserialised without their assembly, so their
        // serialised bytes are passed on, with a note in the type.
        if (column.UdtAssemblyQualifiedName is not null || dataType.Contains('.', StringComparison.Ordinal))
        {
            var udt = dataType[(dataType.LastIndexOf('.') + 1)..];
            return new ColumnReader(new ResultColumn(name, $"{udt} (CLR type: serialised bytes, base64)"), ReadBinaryAsync);
        }

        var type = new ResultColumn(name, TypeName(dataType, column));
        // Only types that can hold more than 8000 bytes are streamed. The rest are read whole and then measured.
        ValueReader? streamed = (dataType, IsMax(column)) switch
        {
            ("varbinary", true) or ("image", _) => ReadBinaryAsync,
            ("varchar" or "nvarchar", true) or ("text" or "ntext" or "json", _) => ReadTextAsync,
            ("xml", _) => ReadXmlAsync,
            _ => null,
        };
        if (streamed is not null)
        {
            return new ColumnReader(type, streamed);
        }

        Func<SqlDataReader, int, JsonNode?> read = dataType switch
        {
            "bit" => static (r, i) => JsonValue.Create(r.GetBoolean(i)),
            "tinyint" => static (r, i) => JsonValue.Create(r.GetByte(i)),
            "smallint" => static (r, i) => JsonValue.Create(r.GetInt16(i)),
            "int" => static (r, i) => JsonValue.Create(r.GetInt32(i)),
            "bigint" => static (r, i) => Text(r.GetInt64(i).ToString(CultureInfo.InvariantCulture)),
            "decimal" or "numeric" => static (r, i) => Text(r.GetSqlDecimal(i).ToString()),
            "money" or "smallmoney" => static (r, i) => Text(r.GetDecimal(i).ToString(CultureInfo.InvariantCulture)),
            "float" => static (r, i) => JsonValue.Create(r.GetDouble(i)),
            "real" => static (r, i) => JsonValue.Create(r.GetFloat(i)),
            "date" => static (r, i) => Text(r.GetDateTime(i).ToString(DateFormat, CultureInfo.InvariantCulture)),
            "datetime" or "smalldatetime" or "datetime2" => static (r, i) => Text(r.GetDateTime(i).ToString(DateTimeFormat, CultureInfo.InvariantCulture)),
            "datetimeoffset" => static (r, i) => Text(r.GetDateTimeOffset(i).ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture)),
            "time" => static (r, i) => Text(r.GetTimeSpan(i).ToString(TimeFormat, CultureInfo.InvariantCulture)),
            "binary" or "varbinary" or "timestamp" or "rowversion" => static (r, i) => Base64(r.GetSqlBytes(i).Value),
            "uniqueidentifier" => static (r, i) => Text(r.GetGuid(i).ToString("D")),
            "vector" => static (r, i) => Vector(r.GetFieldValue<SqlVector<float>>(i)),
            "sql_variant" => static (r, i) => FromClr(r.GetValue(i)),
            _ => static (r, i) => Text(r.GetString(i)),
        };

        return new ColumnReader(type, (r, i, _, _) =>
        {
            var value = read(r, i);
            return ValueTask.FromResult<SizedValue?>(new SizedValue(value, SizeOf(value)));
        });
    }

    /// <summary>Streams text in chunks and stops once it passes <paramref name="budget"/>.</summary>
    private static async ValueTask<SizedValue?> ReadTextAsync(SqlDataReader reader, int ordinal, long budget, CancellationToken cancellationToken)
    {
        using var source = reader.GetTextReader(ordinal);
        var text = new BoundedText(budget);
        var buffer = ArrayPool<char>.Shared.Rent(ChunkSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                text.Append(buffer.AsSpan(0, read));
                if (text.IsOver)
                {
                    return null;
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        return text.Finish();
    }

    /// <summary>
    /// SqlClient streams <c>xml</c> only through an <see cref="System.Xml.XmlReader"/>, so its text is rebuilt; see
    /// <see cref="SqlServerXml"/>. The reader has no async form.
    /// </summary>
    private static ValueTask<SizedValue?> ReadXmlAsync(SqlDataReader reader, int ordinal, long budget, CancellationToken cancellationToken)
    {
        using var xml = reader.GetXmlReader(ordinal);
        return ValueTask.FromResult(SqlServerXml.Read(xml, budget));
    }

    /// <summary>
    /// Streams binary and stops once its base64 form would pass <paramref name="budget"/>: base64 takes four
    /// characters for every three bytes, so at most <c>budget / 4 * 3</c> bytes fit.
    /// </summary>
    private static async ValueTask<SizedValue?> ReadBinaryAsync(SqlDataReader reader, int ordinal, long budget, CancellationToken cancellationToken)
    {
        var limit = budget / 4 * 3;
        await using var stream = reader.GetStream(ordinal);
        using var bytes = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > limit)
                {
                    return null;
                }

                bytes.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var base64 = Convert.ToBase64String(bytes.GetBuffer(), 0, (int)bytes.Length);
        return new SizedValue(JsonValue.Create(base64), base64.Length);
    }

    /// <summary>A value's size for the byte cap: a string's UTF-8 bytes without quotes or escapes, otherwise its JSON text.</summary>
    private static long SizeOf(JsonNode? value) => value switch
    {
        null => NullSize,
        JsonValue text when text.TryGetValue<string>(out var s) => Encoding.UTF8.GetByteCount(s),
        _ => Encoding.UTF8.GetByteCount(value.ToJsonString()),
    };

    /// <summary>The type with its length, precision or scale, such as <c>nvarchar(50)</c> or <c>decimal(38,10)</c>.</summary>
    private static string TypeName(string dataType, DbColumn column) => dataType switch
    {
        "decimal" or "numeric" => $"{dataType}({column.NumericPrecision.GetValueOrDefault()},{column.NumericScale.GetValueOrDefault()})",
        "datetime2" or "datetimeoffset" or "time" => $"{dataType}({column.NumericScale.GetValueOrDefault()})",
        "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" => IsMax(column)
            ? $"{dataType}(max)"
            : $"{dataType}({column.ColumnSize.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)})",
        _ => dataType,
    };

    private static bool IsMax(DbColumn column) => column.ColumnSize is null or int.MaxValue or -1;

    /// <summary>A <c>sql_variant</c> value, converted by the CLR type SqlClient gives it.</summary>
    private static JsonValue? FromClr(object value) => value switch
    {
        DBNull => null,
        bool b => JsonValue.Create(b),
        byte or short or int => JsonValue.Create(Convert.ToInt32(value, CultureInfo.InvariantCulture)),
        long l => Text(l.ToString(CultureInfo.InvariantCulture)),
        decimal d => Text(d.ToString(CultureInfo.InvariantCulture)),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        DateTime dt => Text(dt.ToString(DateTimeFormat, CultureInfo.InvariantCulture)),
        DateTimeOffset dto => Text(dto.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture)),
        TimeSpan ts => Text(ts.ToString(TimeFormat, CultureInfo.InvariantCulture)),
        Guid g => Text(g.ToString("D")),
        byte[] bytes => Base64(bytes),
        string s => Text(s),
        _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private static JsonArray Vector(SqlVector<float> vector)
    {
        var array = new JsonArray();
        foreach (var element in vector.Memory.Span)
        {
            array.Add(JsonValue.Create(element));
        }

        return array;
    }

    private static JsonValue Text(string value) => JsonValue.Create(value);

    private static JsonValue Base64(byte[] value) => JsonValue.Create(Convert.ToBase64String(value));
}
