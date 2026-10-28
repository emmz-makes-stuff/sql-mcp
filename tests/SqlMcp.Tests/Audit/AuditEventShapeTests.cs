using System.Text.Json.Nodes;
using SqlMcp.Audit;

namespace SqlMcp.Tests.Audit;

/// <summary>
/// Guards the spec's "no result data or secrets in the log": no event type has a field that could hold a row
/// value, a connection string or an error message.
/// </summary>
public class AuditEventShapeTests
{
    /// <summary>Every field an audit event may have. Adding one means deciding it can't carry data or secrets.</summary>
    private static readonly HashSet<string> AllowedFields =
    [
        "Version", "ConfigFiles", "LogPath", "Pid",
        "Tool", "Database", "Sql", "Classification", "DurationMs", "Rows", "Truncated", "RowsAffected",
        "Reason", "Source",
        "Kind", "ErrorNumber", "Severity", "State", "Cap", "Affected",
        "Request", "Succeeded", "ScriptPath", "Counts", "OmittedCount",
    ];

    private static readonly HashSet<Type> AllowedTypes =
    [
        typeof(string), typeof(int), typeof(int?), typeof(long), typeof(long?), typeof(bool),
        typeof(IReadOnlyList<string>), typeof(IReadOnlyList<int>), typeof(IReadOnlyDictionary<string, int>),
        typeof(FailureKind), typeof(FailureKind?),
    ];

    public static TheoryData<string> EventTypes => new(
        typeof(AuditEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(AuditEvent)) && !t.IsAbstract)
            .Select(t => t.Name));

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void Event_fields_cannot_carry_values_or_secrets(string typeName)
    {
        var type = typeof(AuditEvent).Assembly.GetTypes().Single(t => t.Name == typeName);

        foreach (var property in type.GetProperties().Where(p => p.Name is not "EventType" and not "EqualityContract"))
        {
            Assert.Contains(property.Name, AllowedFields);
            Assert.Contains(property.PropertyType, AllowedTypes);
        }
    }

    [Fact]
    public void All_six_event_types_exist()
    {
        Assert.Equal(
            ["DeniedEvent", "ExecutedEvent", "FailedEvent", "IntrospectionEvent", "SessionEndEvent", "SessionStartEvent"],
            EventTypes.Select(row => row.Data).Order());
    }

    [Fact]
    public void Serialised_events_contain_no_connection_string_or_row_value()
    {
        using var temp = new TempDirectory();
        using var log = AuditLog.Open(temp.Path, Path.Combine(temp.Path, "logs"), new FakeTime(DateTimeOffset.UnixEpoch), 1);
        const string connectionString = "Server=sqlhost;Database=Sales;Password=hunter2";
        const string rowValue = "alice@example.com";

        foreach (var auditEvent in OneOfEach())
        {
            var line = log.Serialise(auditEvent);
            Assert.DoesNotContain(connectionString, line, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal);
            Assert.DoesNotContain(rowValue, line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Serialised_events_carry_their_type_once_as_event()
    {
        using var temp = new TempDirectory();
        using var log = AuditLog.Open(temp.Path, Path.Combine(temp.Path, "logs"), new FakeTime(DateTimeOffset.UnixEpoch), 1);

        var events = OneOfEach();
        Assert.Equal(EventTypes.Select(row => row.Data).Order(), events.Select(e => e.GetType().Name).Distinct().Order());
        foreach (var auditEvent in events)
        {
            var line = JsonNode.Parse(log.Serialise(auditEvent))!.AsObject();
            Assert.Equal(auditEvent.EventType, line["event"]!.GetValue<string>());
            Assert.False(line.ContainsKey("event_type"), $"{auditEvent.GetType().Name} also has event_type");
        }
    }

    private static AuditEvent[] OneOfEach() =>
    [
        new SessionStartEvent("1.0.0", ["/p/.sql-mcp/config.json"], "/p/.sql-mcp/logs/x.jsonl", 1),
        new SessionEndEvent("1.0.0", ["/p/.sql-mcp/config.json"]),
        new ExecutedEvent("query", "sqlhost:1433/sales", "SELECT email FROM users", ["query"], 1, 1, false),
        new ExecutedEvent("command", "sqlhost:1433/sales", "UPDATE users SET email = 'x' WHERE id = 1", ["command"], 1, 0, false, [1]),
        new DeniedEvent("query", "sqlhost:1433/sales", "DELETE FROM users", "classified as command; use the command tool"),
        new FailedEvent("query", "sqlhost:1433/sales", "SELECT CAST(email AS int) FROM users", FailureKind.Sql, 245, 16, 1, 1),
        new FailedEvent("command", "sqlhost:1433/sales", "UPDATE users SET email = 'x' WHERE id > 0", FailureKind.AffectedRowsCap, null, null, null, 1) { Cap = 1000, Affected = 5000 },
        new IntrospectionEvent("list_objects", "sqlhost:1433/sales", ["schema=dbo", "pattern=user*"], 1, Succeeded: true),
        new IntrospectionEvent("list_objects", "sqlhost:1433/sales", [], 1, Succeeded: false) { Kind = FailureKind.Timeout, ErrorNumber = -2 },
        new IntrospectionEvent("script_database", "sqlhost:1433/sales", [], 1, Succeeded: true)
        {
            ScriptPath = "/p/.sql-mcp/scripts/sqlhost_1433_sales-20261026T140509Z.sql",
            Counts = new Dictionary<string, int> { ["table"] = 12 },
            OmittedCount = 1,
        },
    ];
}
