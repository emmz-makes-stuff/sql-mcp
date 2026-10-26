using System.Text.Json.Nodes;
using SqlMcp.Configuration;
using SqlMcp.Permissions;

namespace SqlMcp.Tests.Permissions;

public sealed class ApprovalTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ApprovalStore _store;

    public ApprovalTests() => _store = new ApprovalStore(Path.Combine(_temp.Path, "user", "approvals.json"), TimeProvider.System);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Nothing_is_approved_without_a_store()
    {
        var path = WriteProject("""{ "databases": [] }""");

        Assert.Equal(ProjectApproval.None, _store.Read(Load(path)));
    }

    [Fact]
    public void Approval_is_stored_against_the_path_and_hash()
    {
        var path = WriteProject("""{ "databases": [] }""");
        var file = Load(path);

        _store.Approve(file);

        Assert.True(_store.Read(file).FileApproved);
        var stored = JsonNode.Parse(File.ReadAllText(_store.Path))!.AsObject();
        var approval = Assert.Single(stored);
        Assert.Equal(ApprovalStore.CanonicalPath(path), approval.Key);
        Assert.Equal(file.Sha256, approval.Value!["sha256"]!.GetValue<string>());
        Assert.NotNull(approval.Value!["approvedAt"]);
    }

    [Fact]
    public void Editing_the_file_invalidates_approval_of_its_grants_but_not_its_database()
    {
        const string original = """
            { "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["ddl"], "deny": ["introspection"] } ] }
            """;
        var path = WriteProject(original);
        _store.Approve(Load(path));
        Assert.True(Effective(path).IsAllowed(ToolGroup.Ddl));

        // Any change at all, even whitespace.
        File.WriteAllText(path, original + " ");

        var db = Effective(path);
        Assert.False(db.IsAllowed(ToolGroup.Ddl));
        Assert.Equal(PermissionSource.ProjectConfigNotApproved, db.Groups[ToolGroup.Ddl].Source);
        Assert.False(db.IsAllowed(ToolGroup.Introspection));
        Assert.True(db.Entry.IsAvailable);
    }

    [Fact]
    public void Approval_records_each_database_by_the_hash_of_its_raw_connection_string()
    {
        var path = WriteProject("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;Password=${env:PW}" } ] }""");

        _store.Approve(Load(path));

        var stored = JsonNode.Parse(File.ReadAllText(_store.Path))![ApprovalStore.CanonicalPath(path)]!["databases"]!.AsObject();
        var database = Assert.Single(stored);
        Assert.Equal("sqlhost:1433/sales", database.Key);
        Assert.Equal(ProjectApproval.Hash("Server=sqlhost;Database=Sales;Password=${env:PW}"), database.Value!["sha256"]!.GetValue<string>());
    }

    [Fact]
    public void Changing_a_databases_connection_string_makes_it_unavailable_until_approved_again()
    {
        var path = WriteProject("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales" } ] }""");
        _store.Approve(Load(path));

        WriteProject("""{ "databases": [ { "connectionString": "Server=sqlhost;Database=Sales;User ID=sa" } ] }""");

        var db = Effective(path);
        Assert.False(db.Entry.IsAvailable);
        Assert.Contains(ApprovalMessages.AddsDatabase, db.Entry.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Approving_again_replaces_the_old_hash()
    {
        var path = WriteProject("""{ "databases": [] }""");
        _store.Approve(Load(path));
        File.WriteAllText(path, """{ "databases": [ ] }""");
        var edited = Load(path);

        _store.Approve(edited);

        Assert.True(_store.Read(edited).FileApproved);
        Assert.Single(JsonNode.Parse(File.ReadAllText(_store.Path))!.AsObject());
    }

    [Fact]
    public void A_damaged_store_approves_nothing_and_is_not_overwritten()
    {
        var path = WriteProject("""{ "databases": [] }""");
        Directory.CreateDirectory(Path.GetDirectoryName(_store.Path)!);
        File.WriteAllText(_store.Path, "{ not json");

        Assert.Equal(ProjectApproval.None, _store.Read(Load(path)));
        var ex = Assert.Throws<ConfigException>(() => _store.Approve(Load(path)));
        Assert.Contains(_store.Path, ex.Message, StringComparison.Ordinal);
        Assert.Equal("{ not json", File.ReadAllText(_store.Path));
    }

    [Fact]
    public void A_path_through_a_symbolic_link_has_the_same_key()
    {
        var path = WriteProject("""{ "databases": [] }""");
        var link = Path.Combine(_temp.Path, "linked-project");
        Directory.CreateSymbolicLink(link, Path.Combine(_temp.Path, "project"));
        var throughLink = Path.Combine(link, ".sql-mcp", "config.json");

        _store.Approve(Load(throughLink));

        Assert.True(_store.Read(Load(path)).FileApproved);
        Assert.Equal(ApprovalStore.CanonicalPath(path), ApprovalStore.CanonicalPath(throughLink));
    }

    [Fact]
    public void Approving_through_a_linked_store_keeps_the_link()
    {
        var path = WriteProject("""{ "databases": [] }""");
        var target = _temp.Write("dotfiles/approvals.json", "{}");
        Directory.CreateDirectory(Path.GetDirectoryName(_store.Path)!);
        File.CreateSymbolicLink(_store.Path, target);

        _store.Approve(Load(path));

        Assert.Equal(target, new FileInfo(_store.Path).LinkTarget);
        Assert.Single(JsonNode.Parse(File.ReadAllText(target))!.AsObject());
        Assert.True(_store.Read(Load(path)).FileApproved);
    }

    [Fact]
    public void Denial_wording_is_addressed_to_a_person()
    {
        var messages = new[]
        {
            ApprovalMessages.NotApproved(ApprovalMessages.Grants(ToolGroup.Ddl)),
            ApprovalMessages.NotApproved(ApprovalMessages.Raises("maxRows")),
            ApprovalMessages.AskTheUser,
        };

        Assert.All(messages, message =>
        {
            Assert.Contains("Ask the user to run `sql-mcp approve` in their own terminal", message, StringComparison.Ordinal);
            Assert.Contains("Agents cannot approve", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Run `sql-mcp approve`", message, StringComparison.Ordinal);
            Assert.DoesNotContain("you can run", message, StringComparison.OrdinalIgnoreCase);
        });
    }

    private string WriteProject(string json) => _temp.Write("project/.sql-mcp/config.json", json);

    private static ConfigFile Load(string path) => ConfigLoader.Load(path, _ => null);

    private EffectiveDatabase Effective(string projectPath)
    {
        var project = Load(projectPath);
        return Assert.Single(PermissionResolver.Resolve(null, project, _store.Read(project)));
    }
}
