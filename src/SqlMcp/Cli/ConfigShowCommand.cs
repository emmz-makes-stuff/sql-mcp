using System.CommandLine;
using System.Globalization;
using SqlMcp.Configuration;
using SqlMcp.Execution;
using SqlMcp.Permissions;

namespace SqlMcp.Cli;

/// <summary>
/// <c>sql-mcp config show</c>: each database's effective permissions with their sources, as the server would
/// resolve them now. Literal passwords are masked; <c>${env:NAME}</c> references are shown.
/// </summary>
internal static class ConfigShowCommand
{
    public static Command Create(CliContext context)
    {
        var show = new Command("show", "Show each database's effective permissions and where they come from.");
        CliAction.Set(show, result => Run(context, result.InvocationConfiguration.Output));
        return new Command("config", "Inspect the configuration.") { show };
    }

    private static int Run(CliContext context, TextWriter output)
    {
        var paths = context.Paths();
        var config = ConfigDiscovery.Discover(paths, context.GetVariable);
        var approved = config.Project is { } project && new ApprovalStore(paths.Approvals, TimeProvider.System).IsApproved(project.Path, project.Sha256);
        var databases = PermissionResolver.Resolve(config.User, config.Project, approved);

        output.WriteLine($"User config:    {paths.UserConfig}{(config.User is null ? " (not found)" : "")}");
        output.WriteLine($"Project config: {paths.ProjectConfig}{(config.Project is null ? " (not found)" : approved ? " (approved)" : " (not approved)")}");
        output.WriteLine();

        if (databases.Count == 0)
        {
            output.WriteLine(config.NoDatabasesMessage);
            return 0;
        }

        foreach (var database in databases)
        {
            var groups = database.Groups.Values.OrderBy(g => g.Group).ToList();
            var limits = database.Limits;
            output.WriteLine($"{database.Identifier}  ({(database.DefinedIn == paths.UserConfig ? "user config" : "project config")})");
            output.WriteLine($"  connection: {ConnectionStringMasker.MaskRaw(database.Entry.RawConnectionString)}");
            output.WriteLine($"  available:  {(database.Entry.UnavailableReason is { } reason ? $"no ({reason})" : "yes")}");
            output.WriteLine($"  allowed:    {Describe(groups.Where(g => g.Allowed))}");
            output.WriteLine($"  denied:     {Describe(groups.Where(g => !g.Allowed))}");
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  limits:     {limits.MaxRows} rows, {ByteSize.Format(limits.MaxBytes)}, {limits.PageSize} rows per page, {limits.TimeoutSeconds} s timeout"));
            if (database.UnapprovedWidenings.Count > 0)
            {
                output.WriteLine($"  note:       {ApprovalMessages.NotApproved(string.Join(", ", database.UnapprovedWidenings))}");
            }

            output.WriteLine();
        }

        return 0;
    }

    private static string Describe(IEnumerable<GroupGrant> grants)
    {
        var text = string.Join(", ", grants.Select(g => $"{g.Group.ToConfigName()} ({g.Source.ToDisplayName()})"));
        return text.Length == 0 ? "none" : text;
    }
}
