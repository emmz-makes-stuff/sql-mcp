using System.Globalization;
using System.Text;
using SqlMcp.Audit;
using SqlMcp.Configuration;

namespace SqlMcp.Introspection;

/// <summary>What the comment at the top of a script names (design I6). It never holds the connection string.</summary>
internal sealed record ScriptHeader(string Identifier, string ServerVersion, DateTimeOffset Time, string SqlMcpVersion);

/// <summary>A script moved into place at <see cref="Path"/>, <see cref="Bytes"/> long, with what the writer returned.</summary>
internal sealed record WrittenScript<T>(string Path, long Bytes, T Value);

/// <summary>
/// Where scripts go and how they are written (design I6): a new file in the project's <c>.sql-mcp/scripts/</c>, or the
/// user-level fallback, written to a <c>.sql.tmp</c> file and moved into place without overwriting anything. A failed,
/// cancelled or thrown write leaves no file behind.
/// </summary>
internal static class ScriptFiles
{
    private const int MaxAttempts = 1000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The user-level scripts directory: <c>~/.local/state/sql-mcp/scripts</c>, or <c>%LOCALAPPDATA%\sql-mcp\scripts</c>.</summary>
    public static string FallbackDirectory(bool isWindows, string homeDirectory, string localAppDataDirectory) => isWindows
        ? Path.Combine(localAppDataDirectory, "sql-mcp", "scripts")
        : Path.Combine(homeDirectory, ".local", "state", "sql-mcp", "scripts");

    public static string FallbackDirectoryForCurrentUser() => FallbackDirectory(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// The file name for <paramref name="attempt"/> (1 for the first): the identifier with anything outside
    /// <c>[A-Za-z0-9._-]</c> replaced by <c>_</c>, the UTC time, then <c>-2</c>, <c>-3</c> and so on after the first.
    /// </summary>
    public static string FileName(string identifier, DateTimeOffset time, int attempt = 1)
    {
        var safe = string.Concat(identifier.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
        var stamp = time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var suffix = attempt == 1 ? "" : "-" + attempt.ToString(CultureInfo.InvariantCulture);
        return $"{safe}-{stamp}{suffix}.sql";
    }

    /// <summary>The comment the script starts with, ending in a blank line.</summary>
    public static string Header(ScriptHeader header) =>
        $"""
        -- Schema script for {Line(header.Identifier)}, written by sql-mcp {Line(header.SqlMcpVersion)}.
        -- Server version: {Line(header.ServerVersion)}
        -- Written at: {header.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}
        -- Schema only, with no data and no CREATE DATABASE: run it in an existing, empty database.


        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Writes <paramref name="header"/>, then whatever <paramref name="write"/> writes, to a new script file. The file
    /// goes in <c>&lt;project&gt;/.sql-mcp/scripts/</c> when <c>.sql-mcp/</c> exists and the file can be created there
    /// (with <c>scripts/</c> added to its <c>.gitignore</c>), and in <paramref name="fallbackDirectory"/> otherwise, as
    /// the audit log does. If <paramref name="write"/> fails or throws, or the file can't be written, no file is left.
    /// </summary>
    public static async Task<CatalogOutcome<WrittenScript<T>>> WriteAsync<T>(
        string projectDirectory,
        string fallbackDirectory,
        ScriptHeader header,
        Func<TextWriter, Task<CatalogOutcome<T>>> write,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var reserved = Reserve(projectDirectory, fallbackDirectory, header, failures);
        if (reserved is null)
        {
            return new CatalogFailed<WrittenScript<T>>(
                FailureKind.Other, $"Couldn't create a script file: {string.Join("; ", failures)}", null, TimeSpan.Zero);
        }

        var temp = reserved.Stream.Name;
        try
        {
            CatalogOutcome<T> outcome;
            long bytes;
            await using (var writer = new StreamWriter(reserved.Stream, Utf8NoBom) { NewLine = "\n" })
            {
                await writer.WriteAsync(Header(header).AsMemory(), cancellationToken).ConfigureAwait(false);
                outcome = await write(writer).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                bytes = reserved.Stream.Length;
            }

            switch (outcome)
            {
                case CatalogFailed<T> failed:
                    return new CatalogFailed<WrittenScript<T>>(failed.Kind, failed.Message, failed.ErrorNumber, failed.Duration);
                case CatalogSucceeded<T> succeeded:
                    var path = MoveIntoPlace(temp, reserved.Directory, header, reserved.Attempt);
                    return new CatalogSucceeded<WrittenScript<T>>(new WrittenScript<T>(path, bytes, succeeded.Value), succeeded.Duration);
                default:
                    throw new InvalidOperationException($"Unexpected outcome {outcome.GetType().Name}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CatalogFailed<WrittenScript<T>>(FailureKind.Other, $"Couldn't write the script file: {ex.Message}", null, TimeSpan.Zero);
        }
        finally
        {
            // After a successful move the temp file is gone, and this does nothing.
            File.Delete(temp);
        }
    }

    private sealed record Reservation(string Directory, int Attempt, FileStream Stream);

    private static Reservation? Reserve(string projectDirectory, string fallbackDirectory, ScriptHeader header, List<string> failures)
    {
        var sqlMcpDirectory = Path.Combine(projectDirectory, ProjectLayout.DirectoryName);
        if (Directory.Exists(sqlMcpDirectory)
            && TryReserve(Path.Combine(sqlMcpDirectory, "scripts"), header, failures, () => ProjectLayout.EnsureGitIgnoreLines(sqlMcpDirectory)) is { } inProject)
        {
            return inProject;
        }

        return TryReserve(fallbackDirectory, header, failures, () => { });
    }

    /// <summary>Creates the temp file for the first name whose <c>.sql</c> and <c>.sql.tmp</c> are both free.</summary>
    private static Reservation? TryReserve(string directory, ScriptHeader header, List<string> failures, Action prepare)
    {
        try
        {
            Directory.CreateDirectory(directory);
            prepare();
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var path = Path.Combine(directory, FileName(header.Identifier, header.Time, attempt));
                if (File.Exists(path))
                {
                    continue;
                }

                try
                {
                    return new Reservation(directory, attempt, new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None));
                }
                catch (IOException) when (File.Exists(path + ".tmp"))
                {
                    // Another script of the same database in the same second holds this name.
                }
            }

            failures.Add($"{directory}: no free file name after {MaxAttempts} attempts");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{directory}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Moves the temp file to its name, never overwriting. If a file took that name after it was reserved, the next
    /// free name is used.
    /// </summary>
    private static string MoveIntoPlace(string temp, string directory, ScriptHeader header, int reservedAttempt)
    {
        for (var attempt = reservedAttempt; attempt <= MaxAttempts; attempt++)
        {
            var path = Path.Combine(directory, FileName(header.Identifier, header.Time, attempt));
            if (attempt != reservedAttempt && (File.Exists(path) || File.Exists(path + ".tmp")))
            {
                continue;
            }

            try
            {
                File.Move(temp, path, overwrite: false);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Taken since the reservation; try the next name.
            }
        }

        throw new IOException($"No free script file name in {directory} after {MaxAttempts} attempts.");
    }

    /// <summary>Keeps a header value on its comment line.</summary>
    private static string Line(string value) => string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c));
}
