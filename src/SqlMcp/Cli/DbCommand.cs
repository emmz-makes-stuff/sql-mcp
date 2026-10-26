using System.CommandLine;
using System.Text.Json.Nodes;
using SqlMcp.Configuration;

namespace SqlMcp.Cli;

/// <summary><c>sql-mcp db add|remove</c>: add or remove a database entry in the project (or user) config.</summary>
internal static class DbCommand
{
    /// <summary>The project config is usually committed, so a written-out password would end up in git.</summary>
    public const string LiteralPasswordMessage =
        "Not added: the connection string has a literal password, and the project config is usually committed. Use Password=${env:NAME} to read it from an environment variable, or add it to your user config with --user.";

    public static Command Create(CliContext context) =>
        new("db", "Add or remove databases.") { CreateAdd(context), CreateRemove(context) };

    private static Command CreateAdd(CliContext context)
    {
        var connectionString = new Argument<string>("connection-string")
        {
            Description = "A SQL Server connection string. Use ${env:NAME} for secrets, e.g. Password=${env:APP_PW}.",
        };
        var user = ConfigTarget.UserOption();
        var command = new Command("add", "Add a database, and print its identifier.") { connectionString, user };
        CliAction.Set(command, result =>
        {
            var target = ConfigTarget.For(context, result.GetValue(user));
            var raw = result.GetValue(connectionString)!;
            var identity = DatabaseIdentity.FromConnectionString(raw);
            if (identity.Identifier == DatabaseIdentity.InvalidIdentifier)
            {
                throw new CliException($"Not added: {identity.UnavailableReason}");
            }

            if (!target.IsUser && ConnectionStringMasker.HasLiteralSecret(raw))
            {
                throw new CliException(LiteralPasswordMessage);
            }

            if (target.Load()?.Databases.Any(entry => entry.Identifier == identity.Identifier) == true)
            {
                throw new CliException($"{identity.Identifier} is already in the {target.Label} ({target.Path}).");
            }

            target.Edit(
                editor => editor.Append(["databases"], new JsonObject { ["connectionString"] = raw }),
                file => file.Databases[^1].Identifier,
                result);
            if (identity.UnavailableReason is { } reason)
            {
                result.InvocationConfiguration.Error.WriteLine($"Warning: {reason}");
            }

            return 0;
        });
        return command;
    }

    private static Command CreateRemove(CliContext context)
    {
        var database = new Argument<string>("database") { Description = "The database identifier, e.g. localhost:1433/app." };
        var user = ConfigTarget.UserOption();
        var command = new Command("remove", "Remove a database.") { database, user };
        CliAction.Set(command, result =>
        {
            var target = ConfigTarget.For(context, result.GetValue(user));
            var index = target.IndexOf(result.GetValue(database)!);
            var identifier = target.Load()!.Databases[index].Identifier;
            target.Edit(editor => editor.Remove(["databases", index]), _ => $"Removed {identifier} from {target.Path}.", result);
            return 0;
        });
        return command;
    }
}
