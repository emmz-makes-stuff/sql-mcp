using System.Text.Json.Nodes;
using SqlMcp.Cli;
using SqlMcp.Configuration;

namespace SqlMcp.Tests.Configuration;

/// <summary>Spec "Config editing": edits keep the file valid against the schema and keep comments.</summary>
public sealed class ConfigFileEditorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Edit_is_validated_then_written_keeping_comments()
    {
        var path = _temp.Write("config.json", """
            {
              // keep me
              "databases": [],
            }
            """);

        var file = ConfigFileEditor.Apply(path, InitCommand.ConfigContent, editor =>
            editor.Append(["databases"], new JsonObject { ["connectionString"] = "Server=localhost,1433;Database=app;Password=${env:APP_PW}" }));

        var text = File.ReadAllText(path);
        Assert.Contains("// keep me", text, StringComparison.Ordinal);
        Assert.Contains("\"databases\": [", text, StringComparison.Ordinal);
        Assert.Equal("localhost:1433/app", Assert.Single(file.Databases).Identifier);
        Assert.Equal(file.Sha256, ConfigLoader.Load(path, _ => null).Sha256);
    }

    [Fact]
    public void Edit_that_breaks_the_schema_throws_and_leaves_the_file_unchanged()
    {
        const string original = "{ \"databases\": [] }";
        var path = _temp.Write("config.json", original);

        var ex = Assert.Throws<ConfigException>(() =>
            ConfigFileEditor.Apply(path, InitCommand.ConfigContent, editor => editor.Set(["defaults", "allow"], new JsonArray("admin"))));

        Assert.Contains("$.defaults.allow[0]", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(["config.json"], Directory.GetFiles(_temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Edit_through_a_symbolic_link_writes_the_target_and_keeps_the_link()
    {
        var target = _temp.Write("dotfiles/config.json", """{ "databases": [] }""");
        var link = Path.Combine(_temp.Path, "config.json");
        File.CreateSymbolicLink(link, target);

        ConfigFileEditor.Apply(link, InitCommand.ConfigContent, editor => editor.Set(["defaults"], new JsonObject { ["allow"] = new JsonArray("query") }));

        Assert.Equal(target, new FileInfo(link).LinkTarget);
        Assert.Contains("\"allow\":[\"query\"]", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Equal(["config.json"], Directory.GetFiles(Path.Combine(_temp.Path, "dotfiles")).Select(Path.GetFileName));
    }

    [Fact]
    public void Edit_that_duplicates_a_database_throws()
    {
        var path = _temp.Write("config.json", """{ "databases": [ { "connectionString": "Server=a;Database=b" } ] }""");

        Assert.Throws<ConfigException>(() => ConfigFileEditor.Apply(path, InitCommand.ConfigContent, editor =>
            editor.Append(["databases"], new JsonObject { ["connectionString"] = "Server=a;Database=b;User Id=x" })));
    }

    [Fact]
    public void Missing_file_starts_from_the_given_text_and_creates_folders()
    {
        var path = Path.Combine(_temp.Path, "nested", "config.json");

        ConfigFileEditor.Apply(path, InitCommand.ConfigContent, editor => editor.Set(["defaults"], new JsonObject { ["allow"] = new JsonArray("query") }));

        var text = File.ReadAllText(path);
        Assert.Contains(ProjectLayout.SchemaUrl, text, StringComparison.Ordinal);
        Assert.Contains("\"allow\": [", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unparseable_file_is_a_config_error_naming_the_file()
    {
        var path = _temp.Write("config.json", "{ \"databases\": [ ");

        var ex = Assert.Throws<ConfigException>(() => ConfigFileEditor.Apply(path, InitCommand.ConfigContent, _ => { }));

        Assert.StartsWith(path, ex.Message, StringComparison.Ordinal);
    }
}
