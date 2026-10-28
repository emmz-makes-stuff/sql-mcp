using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using SqlMcp.Audit;
using SqlMcp.Permissions;

namespace SqlMcp.Introspection.SqlServer;

/// <summary>
/// Scripts a SQL Server schema with SMO's <see cref="Scripter"/> (design I5, ADR-0003). SMO is synchronous, so the work
/// runs on <see cref="Task.Run(Action, CancellationToken)"/>, and cancellation is checked between the batches it yields.
/// </summary>
/// <remarks>
/// What the spike and §4.3's probes found, and why the script is built in three parts:
/// <list type="number">
/// <item>Schemas and full-text catalogs first. <c>DiscoverDependencies</c> refuses schemas, and the tables' full-text
/// indexes need their catalog.</item>
/// <item>Everything else that has a create order, ordered by <c>DependencyWalker</c>. The walk adds the parents of what
/// it was given, and can name an object twice, so it is filtered back down to the candidates, first place kept.</item>
/// <item>DML triggers last, each scripted on its own. <c>ScriptingOptions.Triggers</c> stays off, because one encrypted
/// trigger makes SMO fail the whole table; and a trigger in the walk fails to script.</item>
/// </list>
/// <c>EnumScriptWithList</c> yields one batch per string and never a <c>GO</c>, whatever <c>ScriptBatchTerminator</c>
/// says, so the generator writes <c>GO</c> after each batch itself.
/// </remarks>
internal sealed class SmoScriptGenerator(TimeProvider time) : IScriptGenerator
{
    private readonly CatalogConnection _connections = new(time, "scripting");

    /// <summary>Design I5's options, less <c>Triggers</c> and <c>ScriptBatchTerminator</c> (see the remarks).</summary>
    private static ScriptingOptions Options() => new()
    {
        SchemaQualify = true,
        DriAll = true,
        Indexes = true,
        FullTextIndexes = true,
        ExtendedProperties = true,
        AnsiPadding = true,
        WithDependencies = false,
        ScriptData = false,
        Permissions = false,
        IncludeDatabaseContext = false,
        IncludeIfNotExists = false,
        ScriptOwner = false,
    };

    public Task<CatalogOutcome<IReadOnlyList<CatalogItem>>> GenerateAsync(
        string connectionString, IReadOnlyList<ObjectSummary>? objects, TextWriter writer, EffectiveLimits limits, CancellationToken cancellationToken) =>
        _connections.RunAsync<IReadOnlyList<CatalogItem>>(
            connectionString,
            limits,
            connection => Task.Run<IReadOnlyList<CatalogItem>>(() => Generate(connection, objects, writer, limits, cancellationToken), cancellationToken),
            cancellationToken,
            " Script fewer objects.",
            ex => ex is SmoException or SfcException ? (FailureKind.Other, $"scripting failed: {Messages(ex)}") : null);

    private static List<CatalogItem> Generate(
        SqlConnection connection, IReadOnlyList<ObjectSummary>? objects, TextWriter writer, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        var server = new Server(new ServerConnection(connection) { StatementTimeout = limits.TimeoutSeconds });
        Prefetch(server);
        var database = server.Databases[connection.Database];
        var scripter = new Scripter(server) { Options = Options() };
        database.PrefetchObjects(typeof(Table), scripter.Options);
        database.PrefetchObjects(typeof(View), scripter.Options);

        var plan = objects is null ? WholeDatabase(database) : Chosen(database, objects);
        var ordered = Walk(scripter, plan.Walked);
        var scripted = new List<CatalogItem>();
        Script(scripter, plan.First, writer, scripted, cancellationToken);
        Script(scripter, ordered, writer, scripted, cancellationToken);
        Script(scripter, plan.Triggers, writer, scripted, cancellationToken);
        return scripted;
    }

    /// <summary>
    /// The properties the candidate filters read, fetched with each collection rather than one round trip per object.
    /// </summary>
    private static void Prefetch(Server server)
    {
        server.SetDefaultInitFields(typeof(Schema), nameof(Schema.ID));
        foreach (var type in new[] { typeof(Table), typeof(SqlAssembly) })
        {
            server.SetDefaultInitFields(type, "IsSystemObject");
        }

        foreach (var type in new[] { typeof(View), typeof(StoredProcedure), typeof(UserDefinedFunction), typeof(Trigger), typeof(DatabaseDdlTrigger) })
        {
            server.SetDefaultInitFields(type, "IsSystemObject", "IsEncrypted");
        }
    }

    /// <summary>An object to script, and the name the completeness check knows it by.</summary>
    private sealed record Candidate(Urn Urn, CatalogItem Item);

    /// <summary>The three parts of the script, in the order they are written (see the remarks).</summary>
    private sealed record Plan(List<Candidate> First, List<Candidate> Walked, List<Candidate> Triggers);

    /// <summary>
    /// Every user object, less encrypted modules: SMO can't script them, and the completeness check names them. User
    /// schemas are ids 5–16383, as in the inventory.
    /// </summary>
    private static Plan WholeDatabase(Database database)
    {
        var first = new List<Candidate>();
        first.AddRange(database.Schemas.Cast<Schema>().Where(s => s.ID is >= 5 and <= 16383).Select(s => Of(s, null, CatalogItemTypes.Schema)));
        first.AddRange(database.FullTextCatalogs.Cast<FullTextCatalog>().Select(c => Of(c, null, CatalogItemTypes.FullTextCatalog)));

        var walked = new List<Candidate>();
        walked.AddRange(database.Assemblies.Cast<SqlAssembly>().Where(a => !a.IsSystemObject).Select(a => Of(a, null, CatalogItemTypes.Assembly)));
        walked.AddRange(database.XmlSchemaCollections.Cast<XmlSchemaCollection>().Where(x => x.Schema != "sys").Select(x => Of(x, x.Schema, CatalogItemTypes.XmlSchemaCollection)));
        walked.AddRange(database.PartitionFunctions.Cast<PartitionFunction>().Select(f => Of(f, null, CatalogItemTypes.PartitionFunction)));
        walked.AddRange(database.PartitionSchemes.Cast<PartitionScheme>().Select(p => Of(p, null, CatalogItemTypes.PartitionScheme)));
        walked.AddRange(database.UserDefinedDataTypes.Cast<UserDefinedDataType>().Select(t => Of(t, t.Schema, ObjectType.Type.ToWireName())));
        walked.AddRange(database.UserDefinedTypes.Cast<UserDefinedType>().Select(t => Of(t, t.Schema, ObjectType.Type.ToWireName())));
        walked.AddRange(database.UserDefinedTableTypes.Cast<UserDefinedTableType>().Select(t => Of(t, t.Schema, ObjectType.TableType.ToWireName())));
        walked.AddRange(database.Rules.Cast<Rule>().Select(r => Of(r, r.Schema, "rule")));
        walked.AddRange(database.Defaults.Cast<Default>().Select(d => Of(d, d.Schema, "default_constraint")));
        walked.AddRange(database.Sequences.Cast<Sequence>().Select(s => Of(s, s.Schema, ObjectType.Sequence.ToWireName())));
        walked.AddRange(database.Synonyms.Cast<Synonym>().Select(s => Of(s, s.Schema, ObjectType.Synonym.ToWireName())));
        walked.AddRange(UserTables(database).Select(t => Of(t, t.Schema, ObjectType.Table.ToWireName())));
        walked.AddRange(UserViews(database).Where(v => !v.IsEncrypted).Select(v => Of(v, v.Schema, ObjectType.View.ToWireName())));
        walked.AddRange(database.StoredProcedures.Cast<StoredProcedure>().Where(p => !p.IsSystemObject && !p.IsEncrypted).Select(p => Of(p, p.Schema, ObjectType.Procedure.ToWireName())));
        walked.AddRange(database.UserDefinedFunctions.Cast<UserDefinedFunction>().Where(f => !f.IsSystemObject && !f.IsEncrypted).Select(f => Of(f, f.Schema, ObjectType.Function.ToWireName())));
        walked.AddRange(database.UserDefinedAggregates.Cast<UserDefinedAggregate>().Select(a => Of(a, a.Schema, ObjectType.Function.ToWireName())));
        walked.AddRange(database.Triggers.Cast<DatabaseDdlTrigger>().Where(t => !t.IsSystemObject && !t.IsEncrypted).Select(t => Of(t, null, ObjectType.Trigger.ToWireName())));

        // An encrypted view is left out, so its triggers go with it; the completeness check names them.
        var triggers = UserTables(database).SelectMany(t => DmlTriggers(t.Triggers, t.Schema))
            .Concat(UserViews(database).Where(v => !v.IsEncrypted).SelectMany(v => DmlTriggers(v.Triggers, v.Schema)))
            .ToList();
        return new Plan(first, walked, triggers);
    }

    /// <summary>
    /// The objects named, each with its own triggers, and nothing they depend on (spec: scripting chosen objects).
    /// Encrypted modules, and the triggers of an encrypted view, are left out, for the completeness check to name. A
    /// name the catalog reader resolved but SMO can't find (dropped in between, say) is an error rather than a silent
    /// gap, and so is a DML trigger without its <see cref="ObjectSummary.Parent"/>.
    /// </summary>
    private static Plan Chosen(Database database, IReadOnlyList<ObjectSummary> objects)
    {
        var walked = new List<Candidate>();
        var triggers = new List<Candidate>();
        foreach (var o in objects)
        {
            var wire = o.Type.ToWireName();
            switch (o.Type)
            {
                case ObjectType.Table:
                    var table = Find(database.Tables[o.Name, o.Schema], o);
                    walked.Add(Of(table, o.Schema, wire));
                    triggers.AddRange(DmlTriggers(table.Triggers, o.Schema));
                    break;
                case ObjectType.View:
                    var view = Find(database.Views[o.Name, o.Schema], o);
                    if (!view.IsEncrypted)
                    {
                        walked.Add(Of(view, o.Schema, wire));
                        triggers.AddRange(DmlTriggers(view.Triggers, o.Schema));
                    }

                    break;
                case ObjectType.Procedure:
                    AddUnlessEncrypted(Find(database.StoredProcedures[o.Name, o.Schema], o), p => p.IsEncrypted);
                    break;
                case ObjectType.Function:
                    if (database.UserDefinedAggregates[o.Name, o.Schema] is { } aggregate)
                    {
                        walked.Add(Of(aggregate, o.Schema, wire));
                    }
                    else
                    {
                        AddUnlessEncrypted(Find(database.UserDefinedFunctions[o.Name, o.Schema], o), f => f.IsEncrypted);
                    }

                    break;
                case ObjectType.Trigger when o.Schema is null:
                    AddUnlessEncrypted(Find(database.Triggers[o.Name], o), t => t.IsEncrypted);
                    break;
                case ObjectType.Trigger:
                    // A trigger on an encrypted view goes with the view, as in a whole-database script.
                    var (owner, ownerEncrypted) = Owner(database, o);
                    var trigger = Find(owner[o.Name], o);
                    if (!trigger.IsEncrypted && !ownerEncrypted)
                    {
                        triggers.Add(Of(trigger, o.Schema, wire));
                    }

                    break;
                case ObjectType.Sequence:
                    walked.Add(Of(Find(database.Sequences[o.Name, o.Schema], o), o.Schema, wire));
                    break;
                case ObjectType.Synonym:
                    walked.Add(Of(Find(database.Synonyms[o.Name, o.Schema], o), o.Schema, wire));
                    break;
                case ObjectType.Type:
                    walked.Add(Of((ScriptSchemaObjectBase?)database.UserDefinedDataTypes[o.Name, o.Schema] ?? Find(database.UserDefinedTypes[o.Name, o.Schema], o), o.Schema, wire));
                    break;
                case ObjectType.TableType:
                    walked.Add(Of(Find(database.UserDefinedTableTypes[o.Name, o.Schema], o), o.Schema, wire));
                    break;
                default:
                    throw new InvalidOperationException($"No script for object type {o.Type}.");
            }

            void AddUnlessEncrypted<T>(T found, Func<T, bool> encrypted)
                where T : NamedSmoObject
            {
                if (!encrypted(found))
                {
                    walked.Add(Of(found, o.Schema, wire));
                }
            }
        }

        // A trigger named alongside its table or view is in the list twice; script it once.
        return new Plan([], walked, [.. triggers.DistinctBy(c => c.Urn.ToString(), StringComparer.Ordinal)]);
    }

    /// <summary>
    /// A chosen DML trigger's table or view, found through the parent the reader resolved rather than by loading every
    /// table's and view's triggers, and whether it is an encrypted view.
    /// </summary>
    private static (TriggerCollection Triggers, bool Encrypted) Owner(Database database, ObjectSummary trigger)
    {
        switch (trigger.Parent)
        {
            case { Type: ObjectType.Table } parent:
                return (Find(database.Tables[parent.Name, parent.Schema], trigger).Triggers, false);
            case { Type: ObjectType.View } parent:
                var view = Find(database.Views[parent.Name, parent.Schema], trigger);
                return (view.Triggers, view.IsEncrypted);
            default:
                throw new SmoException(
                    $"'{trigger.Schema}.{trigger.Name}' (trigger) is on a table or view that sql-mcp doesn't list, such as one marked as shipped by Microsoft, so it can't be scripted.");
        }
    }

    private static T Find<T>(T? found, ObjectSummary wanted)
        where T : SqlSmoObject =>
        found ?? throw new SmoException($"'{(wanted.Schema is null ? wanted.Name : $"{wanted.Schema}.{wanted.Name}")}' ({wanted.Type.ToWireName()}) was not found when scripting began.");

    private static IEnumerable<Table> UserTables(Database database) => database.Tables.Cast<Table>().Where(t => !t.IsSystemObject);

    private static IEnumerable<View> UserViews(Database database) => database.Views.Cast<View>().Where(v => !v.IsSystemObject);

    private static IEnumerable<Candidate> DmlTriggers(TriggerCollection triggers, string? schema) =>
        triggers.Cast<Trigger>().Where(t => !t.IsSystemObject && !t.IsEncrypted).Select(t => Of(t, schema, ObjectType.Trigger.ToWireName()));

    private static Candidate Of(NamedSmoObject smo, string? schema, string type) => new(smo.Urn, new CatalogItem(schema, smo.Name, type));

    /// <summary>The candidates in create order: the walk adds parents and repeats some, so keep only candidates, once each.</summary>
    private static List<Candidate> Walk(Scripter scripter, List<Candidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var byUrn = candidates.GroupBy(c => c.Urn.ToString(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var tree = scripter.DiscoverDependencies(candidates.Select(c => c.Urn).ToArray(), DependencyType.Parents);
        var ordered = new List<Candidate>();
        foreach (var node in scripter.WalkDependencies(tree))
        {
            if (byUrn.Remove(node.Urn.ToString(), out var candidate))
            {
                ordered.Add(candidate);
            }
        }

        // The walk should return every candidate; anything it didn't goes last rather than missing.
        ordered.AddRange(candidates.Where(c => byUrn.ContainsKey(c.Urn.ToString())).DistinctBy(c => c.Urn.ToString()));
        return ordered;
    }

    private static void Script(Scripter scripter, List<Candidate> candidates, TextWriter writer, List<CatalogItem> scripted, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var batch in scripter.EnumScriptWithList(candidates.Select(c => c.Urn).ToArray()))
        {
            writer.WriteLine(batch.TrimEnd());
            writer.WriteLine("GO");
            cancellationToken.ThrowIfCancellationRequested();
        }

        scripted.AddRange(candidates.Select(c => c.Item));
    }

    /// <summary>SMO wraps the cause in "Script failed for Server…"; the agent needs the whole chain.</summary>
    private static string Messages(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message.Trim());
        }

        return string.Join(" ", messages.Distinct());
    }
}
