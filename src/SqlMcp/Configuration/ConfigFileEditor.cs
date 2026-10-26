using System.Text.Json;

namespace SqlMcp.Configuration;

/// <summary>
/// Applies a CLI edit to a sql-mcp config file (design D5, spec "Config editing"): the text is spliced by
/// <see cref="JsoncEditor"/>, re-validated against the schema, and only then written, atomically. An edit that
/// would leave the file invalid throws <see cref="ConfigException"/>, and the file is left as it was.
/// </summary>
internal static class ConfigFileEditor
{
    /// <summary>Edits <paramref name="path"/>, starting from <paramref name="whenMissing"/> if the file doesn't exist yet.</summary>
    public static ConfigFile Apply(string path, string whenMissing, Action<JsoncEditor> edit)
    {
        var original = File.Exists(path) ? ReadText(path) : whenMissing;

        JsoncEditor editor;
        try
        {
            editor = new JsoncEditor(original);
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path}: line {ex.LineNumber + 1}, column {ex.BytePositionInLine + 1}: not valid JSON", ex);
        }

        edit(editor);

        // Environment variables are only substituted when a server connects; an unset one is not an edit error.
        var file = ConfigLoader.Parse(path, editor.Text, _ => null);
        AtomicFile.WriteAllText(path, editor.Text);
        return file;
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"{path}: can't be read: {ex.Message}", ex);
        }
    }
}
