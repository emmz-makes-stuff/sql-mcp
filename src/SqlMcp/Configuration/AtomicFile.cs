using System.Text;

namespace SqlMcp.Configuration;

/// <summary>
/// Writes a file by writing a temp file beside it and renaming it into place, so readers never see half a file.
/// A path that is a symbolic link is written through: the rename lands on the link's final target, so the link stays.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string text)
    {
        var target = Target(Path.GetFullPath(path));
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, text, Utf8NoBom);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The file a link at <paramref name="path"/> finally points to (which may not exist yet), or the path itself.</summary>
    private static string Target(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
            ? target.FullName
            : path;
    }
}
