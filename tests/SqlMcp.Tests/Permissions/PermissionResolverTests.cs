using SqlMcp.Configuration;
using SqlMcp.Permissions;

namespace SqlMcp.Tests.Permissions;

public class PermissionResolverTests
{
    private const string UserPath = "/home/emmz/.config/sql-mcp/config.json";
    private const string ProjectPath = "/work/app/.sql-mcp/config.json";

    [Fact]
    public void Database_with_no_permission_settings_gets_query_and_introspection_only()
    {
        var db = Single(User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }"""));

        Assert.Equal([ToolGroup.Query, ToolGroup.Introspection], Allowed(db));
        Assert.All(db.Groups.Values, grant => Assert.Equal(PermissionSource.Default, grant.Source));
        Assert.Equal(EffectiveLimits.Default, db.Limits);
    }

    [Fact]
    public void Global_settings_override_the_defaults()
    {
        var db = Single(User("""
            { "defaults": { "allow": ["command"], "deny": ["introspection"] },
              "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }
            """));

        Assert.Equal([ToolGroup.Query, ToolGroup.Command], Allowed(db));
        Assert.Equal(PermissionSource.UserConfig, db.Groups[ToolGroup.Command].Source);
    }

    [Fact]
    public void Per_database_allow()
    {
        var databases = PermissionResolver.Resolve(User("""
            { "defaults": { "deny": ["command"] },
              "databases": [
                { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command"] },
                { "connectionString": "Server=sqlhost;Database=Hr" } ] }
            """), null, projectApproved: false);

        Assert.True(Find(databases, "sqlhost:1433/sales").IsAllowed(ToolGroup.Command));
        var hr = Find(databases, "sqlhost:1433/hr");
        Assert.False(hr.IsAllowed(ToolGroup.Command));
        Assert.Equal(PermissionSource.UserConfig, hr.Groups[ToolGroup.Command].Source);
    }

    [Fact]
    public void Deny_wins_at_the_same_level()
    {
        var db = Single(User("""
            { "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["ddl"], "deny": ["ddl"] } ] }
            """));

        Assert.False(db.IsAllowed(ToolGroup.Ddl));
    }

    [Fact]
    public void Deny_wins_in_file_defaults_too()
    {
        var db = Single(User("""
            { "defaults": { "allow": ["execute"], "deny": ["execute"] },
              "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }
            """));

        Assert.False(db.IsAllowed(ToolGroup.Execute));
    }

    [Fact]
    public void Limits_layer_field_by_field()
    {
        var db = Single(User("""
            { "defaults": { "limits": { "maxRows": 500, "timeoutSeconds": 10 } },
              "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "limits": { "pageSize": 20, "timeoutSeconds": 60 } } ] }
            """));

        Assert.Equal(new EffectiveLimits(500, EffectiveLimits.Default.MaxBytes, 20, 60), db.Limits);
    }

    [Fact]
    public void Project_adds_a_database()
    {
        var databases = PermissionResolver.Resolve(
            User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }"""),
            Project("""{ "databases": [ { "connectionString": "Server=localhost;Database=App" } ] }"""),
            projectApproved: false);

        var app = Find(databases, "localhost:1433/app");
        Assert.True(app.Entry.IsAvailable);
        Assert.Equal([ToolGroup.Query, ToolGroup.Introspection], Allowed(app));
        Assert.Equal(ProjectPath, app.DefinedIn);
        Assert.Empty(app.UnapprovedWidenings);
    }

    [Fact]
    public void Project_narrows_a_grant()
    {
        var db = Single(
            User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command"] } ] }"""),
            Project("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "deny": ["command"] } ] }"""),
            approved: false);

        Assert.False(db.IsAllowed(ToolGroup.Command));
        Assert.Equal(PermissionSource.ProjectConfig, db.Groups[ToolGroup.Command].Source);
    }

    [Fact]
    public void Project_defaults_narrow_databases_from_the_user_file()
    {
        var db = Single(
            User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }"""),
            Project("""{ "defaults": { "deny": ["introspection"] } }"""),
            approved: false);

        Assert.Equal([ToolGroup.Query], Allowed(db));
    }

    [Fact]
    public void Unapproved_widening_is_ignored()
    {
        var db = Single(null, Project("""
            { "databases": [ { "connectionString": "Server=localhost;Database=App", "allow": ["ddl"] } ] }
            """), approved: false);

        var ddl = db.Groups[ToolGroup.Ddl];
        Assert.False(ddl.Allowed);
        Assert.Equal(PermissionSource.ProjectConfigNotApproved, ddl.Source);
        Assert.Equal("project config not approved", ddl.Source.ToDisplayName());
        Assert.Equal(
            "project config grants ddl but is not approved. Ask the user to run `sql-mcp approve` in their own terminal. Agents cannot approve.",
            ddl.Reason);
        Assert.Equal(["grants ddl"], db.UnapprovedWidenings);
    }

    [Fact]
    public void Approved_widening_applies()
    {
        var db = Single(null, Project("""
            { "databases": [ { "connectionString": "Server=localhost;Database=App", "allow": ["ddl"] } ] }
            """), approved: true);

        Assert.True(db.IsAllowed(ToolGroup.Ddl));
        Assert.Equal(PermissionSource.ProjectConfig, db.Groups[ToolGroup.Ddl].Source);
        Assert.Empty(db.UnapprovedWidenings);
    }

    [Fact]
    public void Widening_is_judged_against_the_state_before_the_project_layer()
    {
        // The user already allows command, so the project allowing it again is not a widening.
        var db = Single(
            User("""{ "defaults": { "allow": ["command"] }, "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }"""),
            Project("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["command"] } ] }"""),
            approved: false);

        Assert.True(db.IsAllowed(ToolGroup.Command));
        Assert.Empty(db.UnapprovedWidenings);
    }

    [Fact]
    public void Project_deny_then_allow_of_a_denied_group_is_still_widening()
    {
        var db = Single(null, Project("""
            { "defaults": { "deny": ["ddl"] },
              "databases": [ { "connectionString": "Server=localhost;Database=App", "allow": ["ddl"] } ] }
            """), approved: false);

        Assert.False(db.IsAllowed(ToolGroup.Ddl));
        Assert.Equal(PermissionSource.ProjectConfigNotApproved, db.Groups[ToolGroup.Ddl].Source);
    }

    [Fact]
    public void Project_limits_narrow_always_and_widen_only_when_approved()
    {
        const string project = """
            { "databases": [ { "connectionString": "Server=localhost;Database=App", "limits": { "maxRows": 50, "timeoutSeconds": 300 } } ] }
            """;

        var unapproved = Single(null, Project(project), approved: false);
        var approved = Single(null, Project(project), approved: true);

        Assert.Equal(50, unapproved.Limits.MaxRows);
        Assert.Equal(EffectiveLimits.Default.TimeoutSeconds, unapproved.Limits.TimeoutSeconds);
        Assert.Equal(["raises timeoutSeconds"], unapproved.UnapprovedWidenings);
        Assert.Equal(300, approved.Limits.TimeoutSeconds);
    }

    [Fact]
    public void User_connection_string_wins_over_the_project_one()
    {
        var db = Single(
            User("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;User ID=reader" } ] }"""),
            Project("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;User ID=sa" } ] }"""),
            approved: true);

        Assert.Contains("reader", db.Entry.ConnectionString, StringComparison.Ordinal);
        Assert.Equal(UserPath, db.DefinedIn);
    }

    [Fact]
    public void Unparseable_entries_are_never_merged()
    {
        var databases = PermissionResolver.Resolve(
            User("""{ "databases": [ { "connectionString": "Server=a;Bogus=1" } ] }"""),
            Project("""{ "databases": [ { "connectionString": "Server=b;Bogus=2" } ] }"""),
            projectApproved: false);

        Assert.Equal(2, databases.Count);
    }

    private static ConfigFile User(string json) => ConfigLoader.Parse(UserPath, json, _ => null);

    private static ConfigFile Project(string json) => ConfigLoader.Parse(ProjectPath, json, _ => null);

    private static EffectiveDatabase Single(ConfigFile? user, ConfigFile? project = null, bool approved = false) =>
        Assert.Single(PermissionResolver.Resolve(user, project, approved));

    private static EffectiveDatabase Find(IReadOnlyList<EffectiveDatabase> databases, string identifier) =>
        Assert.Single(databases, db => db.Identifier == identifier);

    private static ToolGroup[] Allowed(EffectiveDatabase db) =>
        db.Groups.Values.Where(g => g.Allowed).Select(g => g.Group).Order().ToArray();
}
