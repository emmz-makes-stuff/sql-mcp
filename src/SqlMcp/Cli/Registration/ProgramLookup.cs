namespace SqlMcp.Cli.Registration;

/// <summary>
/// Windows only: npm installs client CLIs as <c>.cmd</c> shims, which <c>Process.Start</c> won't find from a bare
/// name when it isn't using the shell. This finds the full path through PATH and PATHEXT, the way the shell would.
/// Windows starts a <c>.cmd</c> file through cmd.exe, which applies its own parsing to the arguments; sql-mcp's
/// arguments are names, paths and a package id. Untested on real Windows (CI is Ubuntu only).
/// </summary>
internal static class ProgramLookup
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <summary>The first <c>dir\program.ext</c> that exists, or <paramref name="program"/> unchanged.</summary>
    public static string Resolve(string program, string? path, string? pathExt, Func<string, bool> exists)
    {
        if (Path.IsPathRooted(program) || Path.HasExtension(program) || string.IsNullOrEmpty(path))
        {
            return program;
        }

        var extensions = (string.IsNullOrEmpty(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, program + extension);
                if (exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return program;
    }
}
