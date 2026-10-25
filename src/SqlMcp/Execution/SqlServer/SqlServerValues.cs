using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

namespace SqlMcp.Execution.SqlServer;

/// <summary>
/// Converts SQL Server values without loss (design D7, spec: result values are serialised faithfully). Exact
/// numerics that a JSON reader could round (<c>decimal</c>, <c>numeric</c>, <c>money</c> and <c>bigint</c>) are
/// strings; dates and times are ISO 8601 at full precision; binary is base64.
/// </summary>
internal static class SqlServerValues
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
    private const string DateTimeOffsetFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";
    private const string TimeFormat = @"hh\:mm\:ss\.fffffff";

    /// <summary>How to read one column, decided once per result set from its schema.</summary>
    internal sealed record ColumnReader(ResultColumn Column, Func<SqlDataReader, int, JsonNode?> Read);

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

    public static JsonNode?[] ReadRow(SqlDataReader reader, IReadOnlyList<ColumnReader> columns)
    {
        var row = new JsonNode?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            row[i] = reader.IsDBNull(i) ? null : columns[i].Read(reader, i);
        }

        return row;
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
            return new ColumnReader(new ResultColumn(name, $"{udt} (CLR type: serialised bytes, base64)"), static (r, i) => Base64(r.GetSqlBytes(i).Value));
        }

        var type = new ResultColumn(name, TypeName(dataType, column));
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
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => static (r, i) => Base64(r.GetSqlBytes(i).Value),
            "uniqueidentifier" => static (r, i) => Text(r.GetGuid(i).ToString("D")),
            "vector" => static (r, i) => Vector(r.GetFieldValue<SqlVector<float>>(i)),
            "sql_variant" => static (r, i) => FromClr(r.GetValue(i)),
            // char, varchar, nchar, nvarchar, text, ntext, xml, json and sysname all read as text.
            _ => static (r, i) => Text(r.GetString(i)),
        };

        return new ColumnReader(type, read);
    }

    /// <summary>The type with its length, precision or scale, such as <c>nvarchar(50)</c> or <c>decimal(38,10)</c>.</summary>
    private static string TypeName(string dataType, DbColumn column) => dataType switch
    {
        "decimal" or "numeric" => $"{dataType}({column.NumericPrecision.GetValueOrDefault()},{column.NumericScale.GetValueOrDefault()})",
        "datetime2" or "datetimeoffset" or "time" => $"{dataType}({column.NumericScale.GetValueOrDefault()})",
        "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" => column.ColumnSize is null or int.MaxValue or -1
            ? $"{dataType}(max)"
            : $"{dataType}({column.ColumnSize.Value.ToString(CultureInfo.InvariantCulture)})",
        _ => dataType,
    };

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
