using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;
using SqlMcp.Permissions;

namespace SqlMcp.Configuration;

/// <summary>Reads one config file: JSON with comments and trailing commas, checked against the embedded schema.</summary>
internal static class ConfigLoader
{
    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(ConfigSchema.Text));

    private static readonly HashSet<string> ApplicatorKeywords =
        ["properties", "items", "prefixItems", "additionalProperties", "$ref", "allOf", "anyOf", "oneOf"];

    public static ConfigFile Load(string path, Func<string, string?> getVariable)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"{path}: can't be read: {ex.Message}", ex);
        }

        // The hash is of the exact bytes, so approval lapses on any change, even to whitespace or encoding.
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(path, reader.ReadToEnd(), Hash(bytes), getVariable);
    }

    /// <summary>Parses and validates <paramref name="text"/>. <paramref name="path"/> is only used in messages.</summary>
    public static ConfigFile Parse(string path, string text, Func<string, string?> getVariable) =>
        Parse(path, text, Hash(Encoding.UTF8.GetBytes(text)), getVariable);

    /// <summary>Lower-case hex SHA-256, the fingerprint stored in <c>approvals.json</c>.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ConfigFile Parse(string path, string text, string sha256, Func<string, string?> getVariable)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, DocumentOptions);
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber + 1;
            var column = ex.BytePositionInLine + 1;
            throw new ConfigException($"{path}: line {line}, column {column}: not valid JSON", ex);
        }

        using (document)
        {
            var errors = Validate(document.RootElement);
            if (errors.Count > 0)
            {
                throw new ConfigException(string.Join(Environment.NewLine, errors.Select(e => $"{path}: {e}")));
            }

            var file = Map(path, sha256, document.RootElement, getVariable);
            CheckDuplicates(file);
            return file;
        }
    }

    /// <summary>Schema errors as "<c>$.json.path: problem</c>", in document order.</summary>
    internal static IReadOnlyList<string> Validate(JsonElement root)
    {
        var results = Schema.Value.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        var errors = new List<string>();
        foreach (var node in results.Details ?? [])
        {
            if (node.Errors is null || node.IsValid)
            {
                continue;
            }

            var pointer = node.InstanceLocation.ToString();
            var evaluationPath = node.EvaluationPath.ToString();
            foreach (var (keyword, message) in node.Errors)
            {
                // Applicators only summarise their children, whose own errors are more precise.
                if (ApplicatorKeywords.Contains(keyword))
                {
                    continue;
                }

                var error = $"{ToJsonPath(pointer)}: {Describe(root, pointer, evaluationPath, keyword, message)}";
                if (!errors.Contains(error))
                {
                    errors.Add(error);
                }
            }
        }

        errors.Sort(StringComparer.Ordinal);
        return errors;
    }

    private static string Describe(JsonElement root, string pointer, string evaluationPath, string keyword, string message)
    {
        if (keyword == "enum" && Resolve(root, pointer) is { ValueKind: JsonValueKind.String } value)
        {
            return $"\"{value.GetString()}\" is not a known value";
        }

        // A property that additionalProperties: false rejects is reported as the false schema, with no keyword.
        if (keyword.Length == 0 && evaluationPath.EndsWith("/additionalProperties", StringComparison.Ordinal))
        {
            return $"unknown property \"{LastSegment(pointer)}\"";
        }

        // Values are left out on purpose: a mistyped property could hold a connection string.
        return message;
    }

    private static ConfigFile Map(string path, string sha256, JsonElement root, Func<string, string?> getVariable)
    {
        var defaults = root.TryGetProperty("defaults", out var d) ? MapGrants(d) : GrantSettings.None;

        var databases = new List<DatabaseEntry>();
        if (root.TryGetProperty("databases", out var array))
        {
            var index = 0;
            foreach (var element in array.EnumerateArray())
            {
                databases.Add(MapDatabase($"databases[{index.ToString(CultureInfo.InvariantCulture)}]", element, getVariable));
                index++;
            }
        }

        return new ConfigFile(path, sha256, defaults, databases);
    }

    private static DatabaseEntry MapDatabase(string location, JsonElement element, Func<string, string?> getVariable)
    {
        var raw = element.GetProperty("connectionString").GetString()!;
        var identity = DatabaseIdentity.FromConnectionString(raw);

        string? connectionString = null;
        var reason = identity.UnavailableReason;
        if (reason is null)
        {
            var substitution = EnvSubstitution.Substitute(raw, getVariable);
            connectionString = substitution.ConnectionString;
            reason = substitution.UnavailableReason;
        }

        // The schema allows only "sqlserver", which is also the default.
        return new DatabaseEntry(location, identity.Identifier, Engine.SqlServer, MapGrants(element), raw, connectionString, reason);
    }

    private static GrantSettings MapGrants(JsonElement element) => new(
        MapGroups(element, "allow"),
        MapGroups(element, "deny"),
        element.TryGetProperty("limits", out var limits) ? MapLimits(limits) : LimitSettings.None);

    private static List<ToolGroup> MapGroups(JsonElement element, string name)
    {
        var groups = new List<ToolGroup>();
        if (element.TryGetProperty(name, out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                // Already checked against the schema's enum.
                if (ToolGroupNames.TryParse(item.GetString()!, out var group))
                {
                    groups.Add(group);
                }
            }
        }

        return groups;
    }

    private static LimitSettings MapLimits(JsonElement element) => new(
        element.TryGetProperty("maxRows", out var rows) ? rows.GetInt32() : null,
        element.TryGetProperty("maxBytes", out var bytes) ? bytes.GetInt64() : null,
        element.TryGetProperty("pageSize", out var page) ? page.GetInt32() : null,
        element.TryGetProperty("timeoutSeconds", out var timeout) ? timeout.GetInt32() : null);

    private static void CheckDuplicates(ConfigFile file)
    {
        var errors = file.Databases
            .Where(entry => entry.Identifier != DatabaseIdentity.InvalidIdentifier)
            .GroupBy(entry => entry.Identifier, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"{file.Path}: {string.Join(" and ", group.Select(e => $"$.{e.Location}"))} both identify {group.Key}")
            .ToList();

        if (errors.Count > 0)
        {
            throw new ConfigException(string.Join(Environment.NewLine, errors));
        }
    }

    /// <summary>Turns a JSON Pointer such as <c>/databases/0/allow/1</c> into <c>$.databases[0].allow[1]</c>.</summary>
    internal static string ToJsonPath(string pointer)
    {
        var path = new StringBuilder("$");
        foreach (var segment in Segments(pointer))
        {
            if (segment.Length > 0 && segment.All(char.IsAsciiDigit))
            {
                path.Append('[').Append(segment).Append(']');
            }
            else
            {
                path.Append('.').Append(segment);
            }
        }

        return path.ToString();
    }

    private static string LastSegment(string pointer) => Segments(pointer).LastOrDefault() ?? "";

    private static IEnumerable<string> Segments(string pointer) => pointer
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));

    private static JsonElement? Resolve(JsonElement root, string pointer)
    {
        var current = root;
        foreach (var segment in Segments(pointer))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var child))
            {
                current = child;
            }
            else if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current;
    }
}

/// <summary>The embedded <c>schema/config.schema.json</c>, also shipped in the package.</summary>
internal static class ConfigSchema
{
    public const string ResourceName = "SqlMcp.config.schema.json";

    public static string Text { get; } = ReadResource();

    private static string ReadResource()
    {
        using var stream = typeof(ConfigSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
