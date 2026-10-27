using System.Globalization;

namespace SqlMcp.Introspection.SqlServer;

/// <summary>Writes a catalog type as T-SQL declares it, such as <c>nvarchar(200)</c>, <c>decimal(10,2)</c> or <c>dbo.phone</c>.</summary>
internal static class SqlServerTypeNames
{
    /// <summary>
    /// <paramref name="maxLength"/> is in bytes, as the catalog stores it, and -1 for <c>max</c>. A user-defined type
    /// is written schema-qualified, with no length: its definition carries that.
    /// </summary>
    public static string Format(string name, string? schema, bool userDefined, short maxLength, byte precision, byte scale)
    {
        if (userDefined)
        {
            return schema is null ? name : $"{schema}.{name}";
        }

        return name switch
        {
            "nvarchar" or "nchar" => maxLength == -1 ? $"{name}(max)" : Sized(name, maxLength / 2),
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{name}(max)" : Sized(name, maxLength),
            "decimal" or "numeric" => string.Create(CultureInfo.InvariantCulture, $"{name}({precision},{scale})"),
            "datetime2" or "datetimeoffset" or "time" => Sized(name, scale),

            // A vector stores an 8-byte header and 4 bytes per float32 dimension.
            "vector" => Sized(name, (maxLength - 8) / 4),
            _ => name,
        };
    }

    private static string Sized(string name, int size) => string.Create(CultureInfo.InvariantCulture, $"{name}({size})");
}
