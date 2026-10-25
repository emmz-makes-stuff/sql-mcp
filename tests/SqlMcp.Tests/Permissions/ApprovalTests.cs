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

        Assert.False(_store.IsApproved(path, Load(path).Sha256));
    }

    [Fact]
    public void Approval_is_stored_against_the_path_and_hash()
    {
        var path = WriteProject("""{ "databases": [] }""");
        var file = Load(path);

        _store.Approve(path, file.Sha256);

        Assert.True(_store.IsApproved(path, file.Sha256));
        var stored = JsonNode.Parse(File.ReadAllText(_store.Path))!.AsObject();
        var approval = Assert.Single(stored);
        Assert.Equal(ApprovalStore.CanonicalPath(path), approval.Key);
        Assert.Equal(file.Sha256, approval.Value!["sha256"]!.GetValue<string>());
        Assert.NotNull(approval.Value!["approvedAt"]);
    }

    [Fact]
    public void Editing_the_file_invalidates_approval()
    {
        const string original = """
            { "databases": [ { "connectionString": "Server=sqlhost;Database=Sales", "allow": ["ddl"], "deny": ["introspection"] } ] }
            """;
        var path = WriteProject(original);
        _store.Approve(path, Load(path).Sha256);
        Assert.True(Effective(path).IsAllowed(ToolGroup.Ddl));

        // Any change at all, even whitespace.
        File.WriteAllText(path, original + " ");

        var db = Effective(path);
        Assert.False(db.IsAllowed(ToolGroup.Ddl));
        Assert.Equal(PermissionSource.ProjectConfigNotApproved, db.Groups[ToolGroup.Ddl].Source);
        Assert.False(db.IsAllowed(ToolGroup.Introspection));
    }

    [Fact]
    public void Approving_again_replaces_the_old_hash()
    {
        var path = WriteProject("""{ "databases": [] }""");
        _store.Approve(path, Load(path).Sha256);
        File.WriteAllText(path, """{ "databases": [ ] }""");
        var edited = Load(path);

        _store.Approve(path, edited.Sha256);

        Assert.True(_store.IsApproved(path, edited.Sha256));
        Assert.Single(JsonNode.Parse(File.ReadAllText(_store.Path))!.AsObject());
    }

    [Fact]
    public void A_damaged_store_approves_nothing_and_is_not_overwritten()
    {
        var path = WriteProject("""{ "databases": [] }""");
        Directory.CreateDirectory(Path.GetDirectoryName(_store.Path)!);
        File.WriteAllText(_store.Path, "{ not json");

        Assert.False(_store.IsApproved(path, Load(path).Sha256));
        var ex = Assert.Throws<ConfigException>(() => _store.Approve(path, Load(path).Sha256));
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

        _store.Approve(throughLink, Load(path).Sha256);

        Assert.True(_store.IsApproved(path, Load(path).Sha256));
        Assert.Equal(ApprovalStore.CanonicalPath(path), ApprovalStore.CanonicalPath(throughLink));
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
        return Assert.Single(PermissionResolver.Resolve(null, project, _store.IsApproved(projectPath, project.Sha256)));
    }
}
