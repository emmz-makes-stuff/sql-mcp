using System.CommandLine;
using System.Globalization;
using SqlMcp.Configuration;
using SqlMcp.Permissions;

namespace SqlMcp.Cli;

/// <summary>
/// <c>sql-mcp approve</c> (design D4). It shows what the project config widens, and records approval of the file's
/// current content once the person types the project directory's name. It refuses to run unless both stdin and
/// stdout are a terminal, and has no option to skip the prompt: an agent told "needs approval" must not be able to
/// approve on the user's behalf.
/// </summary>
internal static class ApproveCommand
{
    public const string NotInteractiveMessage =
        "Approval must be done by a person in an interactive terminal. Ask the user to run `sql-mcp approve`.";

    public static Command Create(CliContext context)
    {
        // Deliberately no options: --yes and the like fail as unknown options.
        var command = new Command("approve", "Approve the access this project's config widens. Asks you to confirm by typing the project directory's name.");
        CliAction.Set(command, result => Run(context, result.InvocationConfiguration.Output));
        return command;
    }

    private static int Run(CliContext context, TextWriter output)
    {
        if (context.Terminal.IsInputRedirected || context.Terminal.IsOutputRedirected)
        {
            throw new CliException(NotInteractiveMessage);
        }

        var paths = context.Paths();
        var config = ConfigDiscovery.Discover(paths, context.GetVariable);
        if (config.Project is not { } project)
        {
            output.WriteLine($"There is no project config at {paths.ProjectConfig}. Nothing to approve.");
            return 0;
        }

        var store = new ApprovalStore(paths.Approvals, TimeProvider.System);
        if (store.IsApproved(project.Path, project.Sha256))
        {
            output.WriteLine($"{project.Path} is already approved as it stands. Nothing to approve.");
            return 0;
        }

        var diff = Widenings(config.User, project);
        if (diff.Count == 0)
        {
            output.WriteLine($"{project.Path} only narrows access. Nothing needs approval.");
            return 0;
        }

        output.WriteLine($"{project.Path} widens access:");
        foreach (var line in diff)
        {
            output.WriteLine($"  {line}");
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(paths.ProjectDirectory));
        output.WriteLine();
        output.Write($"To approve, type the project directory's name ({name}): ");
        output.Flush();

        if (context.Terminal.ReadLine()?.Trim() != name)
        {
            throw new CliException("Not approved: that wasn't the project directory's name.");
        }

        // The hash is of the bytes shown above. If the file changes after this, the approval no longer matches.
        store.Approve(project.Path, project.Sha256);
        output.WriteLine("Approved. The next server start applies these grants. Any change to the file needs approving again.");
        return 0;
    }

    /// <summary>What approving would change: groups that become allowed, and limits that go up, per database.</summary>
    private static List<string> Widenings(ConfigFile? user, ConfigFile project)
    {
        var before = PermissionResolver.Resolve(user, project, projectApproved: false).ToDictionary(d => d.Identifier, StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var after in PermissionResolver.Resolve(user, project, projectApproved: true))
        {
            var current = before[after.Identifier];
            var changes = after.Groups.Values
                .Where(g => g.Allowed && !current.IsAllowed(g.Group))
                .OrderBy(g => g.Group)
                .Select(g => $"allows {g.Group.ToConfigName()}")
                .Concat(Limit("maxRows", current.Limits.MaxRows, after.Limits.MaxRows))
                .Concat(Limit("maxBytes", current.Limits.MaxBytes, after.Limits.MaxBytes))
                .Concat(Limit("pageSize", current.Limits.PageSize, after.Limits.PageSize))
                .Concat(Limit("timeoutSeconds", current.Limits.TimeoutSeconds, after.Limits.TimeoutSeconds))
                .ToList();
            if (changes.Count > 0)
            {
                lines.Add($"{after.Identifier}: {string.Join(", ", changes)}");
            }
        }

        return lines;
    }

    private static IEnumerable<string> Limit(string name, long before, long after) => after > before
        ? [string.Create(CultureInfo.InvariantCulture, $"{name} {before} → {after}")]
        : [];
}
