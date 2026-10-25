using System.CommandLine;
using System.Text.Json.Nodes;
using SqlMcp.Configuration;
using SqlMcp.Permissions;

namespace SqlMcp.Cli;

/// <summary>
/// <c>sql-mcp allow|deny &lt;group&gt; [--db &lt;id&gt;] [--user]</c>. The group moves between lists at one level of
/// one file: <c>allow</c> takes it off that level's deny list, since deny would otherwise win there.
/// </summary>
internal static class GrantCommands
{
    public static Command CreateAllow(CliContext context) => Create(context, allow: true);

    public static Command CreateDeny(CliContext context) => Create(context, allow: false);

    private static Command Create(CliContext context, bool allow)
    {
        var verb = allow ? "allow" : "deny";
        var group = new Argument<string>("group") { Description = "The tool group." };
        group.AcceptOnlyFromAmong([.. Enum.GetValues<ToolGroup>().Select(g => g.ToConfigName())]);
        var database = new Option<string?>("--db")
        {
            Description = "The database identifier, e.g. localhost:1433/app. Without it, the file's defaults are changed.",
        };
        var user = ConfigTarget.UserOption();
        var command = new Command(verb, allow ? "Allow a tool group." : "Deny a tool group.") { group, database, user };
        CliAction.Set(command, result =>
        {
            var target = ConfigTarget.For(context, result.GetValue(user));
            var name = result.GetValue(group)!;
            JsoncSegment[] level;
            string scope;
            if (result.GetValue(database) is { } id)
            {
                var index = target.IndexOf(id);
                level = ["databases", index];
                scope = $"on {target.Load()!.Databases[index].Identifier}";
            }
            else
            {
                level = ["defaults"];
                scope = "on every database";
            }

            JsoncSegment own = allow ? "allow" : "deny";
            JsoncSegment other = allow ? "deny" : "allow";
            var output = result.InvocationConfiguration.Output;

            // Load validates first, so a broken file is reported as a config error, not a parse exception.
            var current = target.Load() is null ? null : new JsoncEditor(File.ReadAllText(target.Path));
            if (current is not null && Contains(current, [.. level, own], name) && !Contains(current, [.. level, other], name))
            {
                output.WriteLine($"{name} is already {(allow ? "allowed" : "denied")} {scope} in {target.Path}. Nothing changed.");
                return 0;
            }

            target.Edit(
                editor =>
                {
                    RemoveAll(editor, [.. level, other], name);
                    if (!Contains(editor, [.. level, own], name))
                    {
                        editor.Append([.. level, own], name);
                    }
                },
                _ => $"{(allow ? "Allowed" : "Denied")} {name} {scope} in {target.Path}.",
                result);
            return 0;
        });
        return command;
    }

    private static bool Contains(JsoncEditor editor, JsoncSegment[] path, string name) =>
        editor.Get(path) is JsonArray array && array.Any(item => item?.GetValue<string>() == name);

    private static void RemoveAll(JsoncEditor editor, JsoncSegment[] path, string name)
    {
        if (editor.Get(path) is not JsonArray array)
        {
            return;
        }

        for (var index = array.Count - 1; index >= 0; index--)
        {
            if (array[index]?.GetValue<string>() == name)
            {
                editor.Remove([.. path, index]);
            }
        }
    }
}
