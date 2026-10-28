using System.CommandLine;
using SqlMcp.Configuration;

namespace SqlMcp.Cli;

/// <summary>
/// <c>sql-mcp init</c>: creates <c>.sql-mcp/config.json</c> (no databases, with a <c>$schema</c> reference) and
/// <c>.sql-mcp/.gitignore</c> in the current directory. An existing config file is never overwritten.
/// </summary>
internal static class InitCommand
{
    public const string ConfigContent = $$"""
        {
          "$schema": "{{ProjectLayout.SchemaUrl}}",
          "databases": []
        }

        """;

    public static Command Create(CliContext context)
    {
        var command = new Command("init", "Create .sql-mcp/config.json and .sql-mcp/.gitignore in the current directory.");
        CliAction.Set(command, result => Run(context, result.InvocationConfiguration.Output));
        return command;
    }

    private static int Run(CliContext context, TextWriter output)
    {
        var paths = context.Paths();
        var directory = Path.GetDirectoryName(paths.ProjectConfig)!;
        var configExists = File.Exists(paths.ProjectConfig);

        Directory.CreateDirectory(directory);
        if (!configExists)
        {
            using var stream = new FileStream(paths.ProjectConfig, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(ConfigContent);
        }

        var gitIgnore = ProjectLayout.EnsureGitIgnoreLines(directory);

        output.WriteLine(configExists
            ? $"{paths.ProjectConfig} already exists. It was left unchanged."
            : $"Created {paths.ProjectConfig}.");
        if (gitIgnore.Created)
        {
            output.WriteLine($"Created {gitIgnore.Path}.");
        }
        else if (gitIgnore.Added.Count > 0)
        {
            output.WriteLine($"Added {string.Join(" and ", gitIgnore.Added)} to {gitIgnore.Path}.");
        }

        if (!configExists)
        {
            output.WriteLine("Add a database with: sql-mcp db add '<connection string>'");
        }

        return 0;
    }
}
