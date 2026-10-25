using System.Text.Json;
using System.Text.Json.Nodes;
using SqlMcp.Configuration;

namespace SqlMcp.Permissions;

/// <summary>
/// <c>approvals.json</c>, next to the user config:
/// <c>{ "&lt;canonical project config path&gt;": { "sha256": "…", "approvedAt": "…" } }</c>.
/// A project file is approved only while the hash of its current bytes matches (design D4).
/// </summary>
internal sealed class ApprovalStore(string path, TimeProvider time)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string Path { get; } = path;

    /// <summary>
    /// True if <paramref name="configPath"/> is approved with this exact hash. A missing or unreadable store
    /// approves nothing, so a damaged file fails closed.
    /// </summary>
    public bool IsApproved(string configPath, string sha256)
    {
        var approvals = TryRead();
        return approvals?[CanonicalPath(configPath)] is JsonObject approval
            && approval["sha256"]?.GetValue<string>() is { } stored
            && string.Equals(stored, sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records approval of the file's current hash, replacing any earlier one. Written atomically.</summary>
    public void Approve(string configPath, string sha256)
    {
        var approvals = File.Exists(Path)
            ? TryRead() ?? throw new ConfigException($"{Path}: is not valid JSON, so the approval was not saved. Fix or delete the file.")
            : [];

        approvals[CanonicalPath(configPath)] = new JsonObject
        {
            ["sha256"] = sha256,
            ["approvedAt"] = time.GetUtcNow().ToString("O"),
        };

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = $"{Path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, approvals.ToJsonString(WriteOptions));
        File.Move(temp, Path, overwrite: true);
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
