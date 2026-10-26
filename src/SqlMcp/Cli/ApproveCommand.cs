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
        var current = store.Read(project);
        var diff = Widenings(config.User, project, current);
        if (diff.Count == 0)
        {
            output.WriteLine(current.FileApproved
                ? $"{project.Path} is already approved as it stands. Nothing to approve."
                : $"{project.Path} only narrows access. Nothing needs approval.");
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
        store.Approve(project);
        output.WriteLine(
            "Approved. The next server start applies this. Any change to the file needs approving again for its grants, and a change to a project-only database's connection string for that database.");
        return 0;
    }

    /// <summary>
    /// What approving would change, per database, compared with what is approved now: project-only databases that
    /// become available (with the environment variables they read, by name only), groups that become allowed, and
    /// limits that go up.
    /// </summary>
    private static List<string> Widenings(ConfigFile? user, ConfigFile project, ProjectApproval current)
    {
        // Both lists come from the same files in the same order, so they pair up by position. Identifiers can't
        // key them: every unparseable entry shares one.
        var before = PermissionResolver.Resolve(user, project, current);
        var after = PermissionResolver.Resolve(user, project, ProjectApproval.Of(project));
        var lines = new List<string>();
        foreach (var (was, now) in before.Zip(after))
        {
            var changes = Added(was, now)
                .Concat(now.Groups.Values
                    .Where(g => g.Allowed && !was.IsAllowed(g.Group))
                    .OrderBy(g => g.Group)
                    .Select(g => $"allows {g.Group.ToConfigName()}"))
                .Concat(Limit("maxRows", was.Limits.MaxRows, now.Limits.MaxRows))
                .Concat(Limit("maxBytes", was.Limits.MaxBytes, now.Limits.MaxBytes))
                .Concat(Limit("pageSize", was.Limits.PageSize, now.Limits.PageSize))
                .Concat(Limit("timeoutSeconds", was.Limits.TimeoutSeconds, now.Limits.TimeoutSeconds))
                .Concat(Limit("maxAffectedRows", was.Limits.MaxAffectedRows, now.Limits.MaxAffectedRows))
                .ToList();
            if (changes.Count > 0)
            {
                lines.Add($"{now.Identifier}: {string.Join(", ", changes)}");
            }
        }

        return lines;
    }

    /// <summary>"adds this database, reading ${env:SALES_PW}" when approval makes a project-only database usable.</summary>
    private static IEnumerable<string> Added(EffectiveDatabase was, EffectiveDatabase now)
    {
        if (!was.UnapprovedWidenings.Contains(ApprovalMessages.AddsDatabase))
        {
            return [];
        }

        var variables = EnvSubstitution.References(now.Entry.RawConnectionString);
        return [variables.Count == 0
            ? ApprovalMessages.AddsDatabase
            : $"{ApprovalMessages.AddsDatabase}, reading {string.Join(", ", variables.Select(name => $"${{env:{name}}}"))}"];
    }

    private static IEnumerable<string> Limit(string name, long before, long after) => after > before
        ? [string.Create(CultureInfo.InvariantCulture, $"{name} {before} → {after}")]
        : [];
}
