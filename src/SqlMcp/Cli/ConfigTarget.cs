using System.CommandLine;
using SqlMcp.Configuration;
using SqlMcp.Permissions;

namespace SqlMcp.Cli;

/// <summary>
/// The file a config-editing command writes: the project file by default, the user file with <c>--user</c>
/// (spec "Config editing"). It finds database entries by identifier, and warns after project-file writes.
/// </summary>
internal sealed class ConfigTarget
{
    private readonly CliContext _context;
    private readonly ConfigPaths _paths;

    private ConfigTarget(CliContext context, bool user)
    {
        _context = context;
        _paths = context.Paths();
        IsUser = user;
    }

    public bool IsUser { get; }

    public string Path => IsUser ? _paths.UserConfig : _paths.ProjectConfig;

    public string Label => IsUser ? "user config" : "project config";

    public static Option<bool> UserOption() => new("--user")
    {
        Description = "Edit the user config instead of the project's .sql-mcp/config.json.",
    };

    public static ConfigTarget For(CliContext context, bool user) => new(context, user);

    /// <summary>The target file as it stands, or null if it doesn't exist yet. An invalid file throws.</summary>
    public ConfigFile? Load() => File.Exists(Path) ? ConfigLoader.Load(Path, _context.GetVariable) : null;

    /// <summary>The index of <paramref name="identifier"/> in the target's <c>databases</c> array, or a <see cref="CliException"/> with a hint.</summary>
    public int IndexOf(string identifier)
    {
        var normalised = DatabaseIdentity.Normalise(identifier);
        var index = Load()?.Databases.ToList().FindIndex(entry => entry.Identifier == normalised) ?? -1;
        if (index >= 0)
        {
            return index;
        }

        var otherPath = IsUser ? _paths.ProjectConfig : _paths.UserConfig;
        var inOther = TryLoad(otherPath)?.Databases.Any(entry => entry.Identifier == normalised) == true;
        var hint = inOther
            ? $"It's in the {(IsUser ? "project config" : "user config")}: {(IsUser ? "leave out --user" : "add --user")} to edit that file."
            : $"Add it first with: sql-mcp db add{(IsUser ? " --user" : "")} '<connection string>'";
        throw new CliException($"{normalised} isn't in the {Label} ({Path}). {hint}");
    }

    /// <summary>
    /// Applies an edit, starting a missing file from <c>init</c>'s content. Prints <paramref name="done"/>'s message
    /// for the written file, then warns about anything not approved.
    /// </summary>
    public void Edit(Action<JsoncEditor> edit, Func<ConfigFile, string> done, ParseResult result)
    {
        if (!IsUser)
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(directory);
            ProjectLayout.EnsureGitIgnore(directory);
        }

        var file = ConfigFileEditor.Apply(Path, InitCommand.ConfigContent, edit);
        result.InvocationConfiguration.Output.WriteLine(done(file));
        if (!IsUser)
        {
            WarnIfNotApproved(result.InvocationConfiguration.Error);
        }
    }

    /// <summary>
    /// Any write changes the project file's hash, so an earlier approval of its widenings lapses; a new widening, or a
    /// new or changed project-only database, isn't approved yet. Either way, list what is being ignored.
    /// </summary>
    private void WarnIfNotApproved(TextWriter error)
    {
        IReadOnlyList<EffectiveDatabase> databases;
        try
        {
            var config = ConfigDiscovery.Discover(_paths, _context.GetVariable);
            var approval = new ApprovalStore(_paths.Approvals, TimeProvider.System).Read(config.Project);
            databases = PermissionResolver.Resolve(config.User, config.Project, approval);
        }
        catch (ConfigException ex)
        {
            error.WriteLine($"Warning: couldn't check whether the project config is approved: {ex.Message}");
            return;
        }

        foreach (var database in databases.Where(d => d.UnapprovedWidenings.Count > 0))
        {
            error.WriteLine($"Warning: not in effect until approved. {database.Identifier}: {ApprovalMessages.NotApproved(string.Join(", ", database.UnapprovedWidenings))}");
        }
    }

    private ConfigFile? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? ConfigLoader.Load(path, _context.GetVariable) : null;
        }
        catch (ConfigException)
        {
            return null;
        }
    }
}
