using System.Text.Json;
using System.Text.Json.Nodes;
using SqlMcp.Configuration;

namespace SqlMcp.Permissions;

/// <summary>
/// <c>approvals.json</c>, next to the user config:
/// <c>{ "&lt;canonical project config path&gt;": { "sha256": "…", "approvedAt": "…", "databases": { "&lt;identifier&gt;": { "sha256": "…" } } } }</c>.
/// The file's widenings are approved only while the hash of its current bytes matches. Each project-only database
/// is approved while the hash of its raw connection string matches (design D4).
/// </summary>
internal sealed class ApprovalStore(string path, TimeProvider time)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string Path { get; } = path;

    /// <summary>
    /// What is approved for <paramref name="project"/>, or <see cref="ProjectApproval.None"/> without a project file.
    /// A missing or unreadable store approves nothing, so a damaged file fails closed.
    /// </summary>
    public ProjectApproval Read(ConfigFile? project)
    {
        if (project is null || TryRead()?[CanonicalPath(project.Path)] is not JsonObject approval)
        {
            return ProjectApproval.None;
        }

        var fileApproved = approval["sha256"] is JsonValue fileHash
            && fileHash.TryGetValue<string>(out var stored)
            && string.Equals(stored, project.Sha256, StringComparison.OrdinalIgnoreCase);

        var databases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (approval["databases"] is JsonObject approvedDatabases)
        {
            foreach (var (identifier, value) in approvedDatabases)
            {
                if (value?["sha256"] is JsonValue hash && hash.TryGetValue<string>(out var text))
                {
                    databases[identifier] = text;
                }
            }
        }

        return new ProjectApproval(fileApproved, databases);
    }

    /// <summary>
    /// Records approval of <paramref name="project"/> as it stands, its databases included, replacing any earlier
    /// approval of that file. Written atomically.
    /// </summary>
    public void Approve(ConfigFile project)
    {
        var approvals = File.Exists(Path)
            ? TryRead() ?? throw new ConfigException($"{Path}: is not valid JSON, so the approval was not saved. Fix or delete the file.")
            : [];

        var databases = new JsonObject();
        foreach (var (identifier, hash) in ProjectApproval.Of(project).Databases)
        {
            databases[identifier] = new JsonObject { ["sha256"] = hash };
        }

        approvals[CanonicalPath(project.Path)] = new JsonObject
        {
            ["sha256"] = project.Sha256,
            ["approvedAt"] = time.GetUtcNow().ToString("O"),
            ["databases"] = databases,
        };

        AtomicFile.WriteAllText(Path, approvals.ToJsonString(WriteOptions));
    }

    private JsonObject? TryRead()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The absolute path with every symbolic link resolved, so the same file reached through a link (for example
    /// macOS's <c>/var</c> → <c>/private/var</c>) has one key.
    /// </summary>
    public static string CanonicalPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                // The target may itself sit under a linked directory.
                current = CanonicalPath(target.FullName);
            }
        }

        return current;
    }
}
