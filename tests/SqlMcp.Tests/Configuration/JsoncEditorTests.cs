using System.Text.Json;
using System.Text.Json.Nodes;
using SqlMcp.Configuration;

namespace SqlMcp.Tests.Configuration;

/// <summary>Design D5: edits splice the text, so comments, trailing commas and formatting survive.</summary>
public sealed class JsoncEditorTests
{
    private const string Config = """
        {
          // Shared with the team.
          "$schema": "https://example.test/schema.json",
          "defaults": {
            "allow": ["query"], // reads everywhere
          },
          "databases": [
            /* the app database */
            {
              "connectionString": "Server=localhost,1433;Database=app",
              "allow": [
                "query",
              ],
            },
          ],
        }
        """;

    [Fact]
    public void Untouched_text_round_trips_exactly()
    {
        Assert.Equal(Config, new JsoncEditor(Config).Text);
    }

    [Fact]
    public void Append_to_a_multi_line_array_with_a_trailing_comma_keeps_it()
    {
        var editor = new JsoncEditor(Config);

        editor.Append(["databases", 0, "allow"], "command");

        Assert.Equal(Config.Replace("""
                    "query",
                  ],
            """, """
                    "query",
                    "command",
                  ],
            """, StringComparison.Ordinal), editor.Text);
    }

    [Fact]
    public void Append_to_an_inline_array_stays_inline_and_keeps_the_trailing_comment()
    {
        var editor = new JsoncEditor(Config);

        editor.Append(["defaults", "allow"], "ddl");

        Assert.Contains("""    "allow": ["query", "ddl"], // reads everywhere""", editor.Text, StringComparison.Ordinal);
        Assert.Contains("// Shared with the team.", editor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_an_object_follows_the_surrounding_indentation()
    {
        var editor = new JsoncEditor(Config);

        editor.Append(["databases"], new JsonObject { ["connectionString"] = "Server=db2;Database=reports" });

        Assert.EndsWith("""
                ],
              },
              {
                "connectionString": "Server=db2;Database=reports"
              },
            ],
          }
          """, editor.Text, StringComparison.Ordinal);
        Assert.Contains("/* the app database */", editor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_without_trailing_comma_adds_the_separator_after_the_last_value_not_after_its_comment()
    {
        const string text = """
            {
              "allow": [
                "query" // the default
              ]
            }
            """;
        var editor = new JsoncEditor(text);

        editor.Append(["allow"], "dml");

        Assert.Equal("""
            {
              "allow": [
                "query", // the default
                "dml"
              ]
            }
            """, editor.Text);
    }

    [Fact]
    public void Append_creates_a_missing_array()
    {
        var editor = new JsoncEditor(Config);

        editor.Append(["databases", 0, "deny"], "dml");

        Assert.Contains("""
                    "query",
                  ],
                  "deny": [
                    "dml"
                  ],
                },
            """, editor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_to_an_empty_array_of_objects_goes_multi_line()
    {
        var editor = new JsoncEditor("""
            {
              "databases": []
            }
            """);

        editor.Append(["databases"], new JsonObject { ["connectionString"] = "Server=a;Database=b;Password=${env:PW}" });

        Assert.Equal("""
            {
              "databases": [
                {
                  "connectionString": "Server=a;Database=b;Password=${env:PW}"
                }
              ]
            }
            """, editor.Text);
    }

    [Fact]
    public void Append_a_scalar_to_an_empty_array_stays_inline()
    {
        var editor = new JsoncEditor("""{ "allow": [] }""");

        editor.Append(["allow"], "query");

        Assert.Equal("""{ "allow": ["query"] }""", editor.Text);
    }

    [Fact]
    public void Set_replaces_an_existing_value_in_place()
    {
        var editor = new JsoncEditor(Config);

        editor.Set(["$schema"], "https://example.test/v2.json");

        Assert.Equal(Config.Replace("schema.json", "v2.json", StringComparison.Ordinal), editor.Text);
    }

    [Fact]
    public void Set_creates_missing_objects_on_the_way()
    {
        var editor = new JsoncEditor("""
            {
              // OpenCode
              "theme": "dark",
            }
            """);

        editor.Set(["mcp", "sql-mcp"], new JsonObject { ["type"] = "local", ["command"] = new JsonArray("sql-mcp", "serve") });

        Assert.Equal("""
            {
              // OpenCode
              "theme": "dark",
              "mcp": {
                "sql-mcp": {
                  "type": "local",
                  "command": [
                    "sql-mcp",
                    "serve"
                  ]
                }
              },
            }
            """, editor.Text);
    }

    [Fact]
    public void Set_into_an_empty_object_goes_multi_line()
    {
        var editor = new JsoncEditor("{}");

        editor.Set(["defaults"], new JsonObject());

        Assert.Equal("{\n  \"defaults\": {}\n}", editor.Text);
    }

    [Fact]
    public void Set_on_a_missing_array_index_throws()
    {
        var editor = new JsoncEditor(Config);

        var ex = Assert.Throws<InvalidOperationException>(() => editor.Set(["databases", 5, "allow"], new JsonArray()));

        Assert.Contains("$.databases[5]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_a_middle_item_takes_its_line_and_comma()
    {
        var editor = new JsoncEditor("""
            [
              "a",
              "b", // bee
              "c"
            ]
            """);

        Assert.True(editor.Remove([1]));

        Assert.Equal("""
            [
              "a",
              "c"
            ]
            """, editor.Text);
    }

    [Fact]
    public void Remove_the_last_item_without_trailing_comma_takes_the_comma_before_it()
    {
        var editor = new JsoncEditor("""
            [
              "a",
              "b" // bee
            ]
            """);

        Assert.True(editor.Remove([1]));

        Assert.Equal("""
            [
              "a"
            ]
            """, editor.Text);
    }

    [Fact]
    public void Remove_from_an_inline_array()
    {
        var first = new JsoncEditor("""{ "allow": ["query", "dml", "ddl"] }""");
        var last = new JsoncEditor("""{ "allow": ["query", "dml", "ddl"] }""");

        first.Remove(["allow", 0]);
        last.Remove(["allow", 2]);

        Assert.Equal("""{ "allow": ["dml", "ddl"] }""", first.Text);
        Assert.Equal("""{ "allow": ["query", "dml"] }""", last.Text);
    }

    [Fact]
    public void Remove_the_only_item_leaves_an_empty_array()
    {
        var editor = new JsoncEditor(Config);

        editor.Remove(["databases", 0, "allow", 0]);

        Assert.Contains("\"allow\": [],", editor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_a_database_keeps_the_comment_above_it()
    {
        var editor = new JsoncEditor(Config);

        editor.Remove(["databases", 0]);

        Assert.Contains("""
              "databases": [
                /* the app database */
              ],
            """, editor.Text, StringComparison.Ordinal);
        Assert.Empty(JsonNode.Parse(editor.Text, documentOptions: ConfigLoader.DocumentOptions)!["databases"]!.AsArray());
    }

    [Fact]
    public void Remove_a_property()
    {
        var editor = new JsoncEditor(Config);

        Assert.True(editor.Remove(["defaults"]));

        Assert.DoesNotContain("defaults", editor.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("reads everywhere", editor.Text, StringComparison.Ordinal);
        Assert.Contains("// Shared with the team.", editor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_a_missing_path_returns_false_and_changes_nothing()
    {
        var editor = new JsoncEditor(Config);

        Assert.False(editor.Remove(["databases", 3]));
        Assert.False(editor.Remove(["nope", "deeper"]));
        Assert.Equal(Config, editor.Text);
    }

    [Fact]
    public void Crlf_and_tabs_are_followed()
    {
        var editor = new JsoncEditor("{\r\n\t\"allow\": [\r\n\t\t\"query\"\r\n\t]\r\n}");

        editor.Append(["allow"], "dml");
        editor.Set(["deny"], new JsonArray("ddl"));

        Assert.Equal("{\r\n\t\"allow\": [\r\n\t\t\"query\",\r\n\t\t\"dml\"\r\n\t],\r\n\t\"deny\": [\r\n\t\t\"ddl\"\r\n\t]\r\n}", editor.Text);
    }

    [Fact]
    public void Offsets_survive_non_ascii_comments()
    {
        var editor = new JsoncEditor("{\n  // café ☕ 🙂\n  \"allow\": [\"query\"]\n}");

        editor.Append(["allow"], "dml");

        Assert.Equal("{\n  // café ☕ 🙂\n  \"allow\": [\"query\", \"dml\"]\n}", editor.Text);
    }

    [Fact]
    public void Get_reads_a_value_inside_comments()
    {
        var editor = new JsoncEditor(Config);

        Assert.Equal("query", editor.Get("defaults", "allow", 0)!.GetValue<string>());
        Assert.Null(editor.Get("nope"));
        Assert.True(editor.Exists("databases", 0, "connectionString"));
    }

    [Fact]
    public void Invalid_text_throws_json_exception()
    {
        Assert.ThrowsAny<JsonException>(() => new JsoncEditor("{ \"a\": "));
    }
}
