using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlMcp.Audit;
using SqlMcp.Execution.SqlServer;
using SqlMcp.Permissions;

namespace SqlMcp.Introspection.SqlServer;

/// <summary>
/// Reads SQL Server's catalog views (design I4). Every query is fixed and parameterised, runs on its own unpooled
/// connection with the database's command timeout, and outside any transaction: nothing here writes.
/// </summary>
internal sealed class SqlServerCatalogReader(TimeProvider time) : ICatalogReader
{
    /// <summary>SqlClient reports a command timeout as error number -2.</summary>
    private const int TimeoutErrorNumber = -2;

    private const string ListTimeoutAdvice = " Ask for less: filter by schema, type or name.";

    /// <summary>
    /// Every user object as one row set: schema-scoped objects from <c>sys.objects</c>, user types from
    /// <c>sys.types</c>, and database triggers (no schema) from <c>sys.triggers</c>. The type codes map to the
    /// engine-neutral names; CLR procedures, functions, aggregates and triggers count as their T-SQL kind.
    /// <c>id</c> is what the describe queries look the object up by: the object id, a table type's table object id,
    /// or an alias type's user type id. <c>code</c> is <c>sys.objects.type</c>, or <c>TT</c>/<c>UT</c> for types.
    /// </summary>
    private const string ObjectsCte = """
        WITH objects AS (
            SELECT s.name AS schema_name, o.name,
                CASE o.type
                    WHEN 'U' THEN 'table' WHEN 'V' THEN 'view'
                    WHEN 'P' THEN 'procedure' WHEN 'PC' THEN 'procedure'
                    WHEN 'TR' THEN 'trigger' WHEN 'TA' THEN 'trigger'
                    WHEN 'SO' THEN 'sequence' WHEN 'SN' THEN 'synonym'
                    ELSE 'function'
                END AS type,
                CASE WHEN o.type = 'U'
                    THEN (SELECT SUM(p.rows) FROM sys.partitions AS p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1))
                END AS approx_rows,
                o.object_id AS id, CAST(o.type AS varchar(2)) AS code
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF', 'TR', 'TA', 'SO', 'SN')
              AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
            UNION ALL
            SELECT s.name, t.name, CASE WHEN t.is_table_type = 1 THEN 'table_type' ELSE 'type' END, NULL,
                COALESCE(tt.type_table_object_id, t.user_type_id), CASE WHEN t.is_table_type = 1 THEN 'TT' ELSE 'UT' END
            FROM sys.types AS t
            JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            LEFT JOIN sys.table_types AS tt ON tt.user_type_id = t.user_type_id
            WHERE t.is_user_defined = 1
            UNION ALL
            SELECT NULL, tr.name, 'trigger', NULL, tr.object_id, CAST(tr.type AS varchar(2))
            FROM sys.triggers AS tr
            WHERE tr.parent_class = 0 AND tr.is_ms_shipped = 0
        )
        """;

    /// <summary>
    /// The filters compare through <c>LOWER()</c>, so a case-sensitive database collation still matches without regard
    /// to case. <c>COUNT(*) OVER ()</c> is computed before <c>TOP</c>, so it counts every match. The type list is matched
    /// with <c>CHARINDEX</c>, not <c>STRING_SPLIT</c>, which a database below compatibility level 130 doesn't have.
    /// </summary>
    private const string ListSql = ObjectsCte + """
        SELECT TOP (@cap) schema_name, name, type, approx_rows, COUNT(*) OVER () AS total
        FROM objects
        WHERE (@schema IS NULL OR LOWER(schema_name) = LOWER(@schema))
          AND (@pattern IS NULL OR LOWER(name) LIKE LOWER(@pattern) ESCAPE '\')
          AND (@types IS NULL OR CHARINDEX(',' + type + ',', ',' + @types + ',') > 0)
        ORDER BY schema_name, name;
        """;

    /// <summary>
    /// The objects one name could mean. Unlike the list filters, names compare as the database compares them, so on a
    /// case-sensitive database <c>dbo.Orders</c> and <c>dbo.orders</c> are different objects, as they are to SQL.
    /// </summary>
    private const string ResolveSql = ObjectsCte + """
        SELECT schema_name, name, type, id, code
        FROM objects
        WHERE name = @name AND (@schema IS NULL OR schema_name = @schema)
        ORDER BY schema_name, type;
        """;

    /// <summary>
    /// Everything a whole-database script accounts for (design I5). <c>class</c> is the <see cref="InventoryClass"/>:
    /// 0 scriptable, 1 security principal, 2 security object. Besides the listed objects: any other top-level user
    /// object (a rule, a standalone default, a queue…) by its <c>type_desc</c>, leaving out table types' own rows in
    /// <c>sys.objects</c>; user schemas (ids 5–16383 leave out dbo, guest, INFORMATION_SCHEMA, sys and the fixed-role
    /// schemas); and principals other than public, dbo, guest, INFORMATION_SCHEMA, sys and the fixed roles.
    /// Each principal holding explicit permissions gets a <c>permissions</c> entry, so the omitted list names them: the
    /// defaults every database has (a user's CONNECT, public's grants on system objects and on column key definitions)
    /// don't count.
    /// Encryption is read for <c>sys.objects</c> modules only, so an encrypted database trigger reads as unencrypted.
    /// </summary>
    private const string InventorySql = ObjectsCte + """
        , inventory AS (
            SELECT schema_name, name, type, 0 AS class,
                CAST(CASE WHEN code NOT IN ('TT', 'UT') AND OBJECTPROPERTY(id, 'IsEncrypted') = 1 THEN 1 ELSE 0 END AS bit) AS encrypted
            FROM objects
            UNION ALL
            SELECT s.name, o.name, LOWER(o.type_desc) COLLATE DATABASE_DEFAULT, 0, CAST(0 AS bit)
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0 AND o.parent_object_id = 0
              AND o.type NOT IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF', 'TR', 'TA', 'SO', 'SN', 'TT', 'S', 'IT')
              AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
            UNION ALL
            SELECT NULL, name, 'schema', 0, CAST(0 AS bit) FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383
            UNION ALL
            SELECT NULL, name, 'partition_function', 0, CAST(0 AS bit) FROM sys.partition_functions
            UNION ALL
            SELECT NULL, name, 'partition_scheme', 0, CAST(0 AS bit) FROM sys.partition_schemes
            UNION ALL
            SELECT SCHEMA_NAME(schema_id), name, 'xml_schema_collection', 0, CAST(0 AS bit)
            FROM sys.xml_schema_collections WHERE SCHEMA_NAME(schema_id) <> 'sys'
            UNION ALL
            SELECT NULL, name, 'assembly', 0, CAST(0 AS bit) FROM sys.assemblies WHERE is_user_defined = 1
            UNION ALL
            SELECT NULL, name, 'fulltext_catalog', 0, CAST(0 AS bit) FROM sys.fulltext_catalogs
            UNION ALL
            SELECT NULL, name, CASE type WHEN 'R' THEN 'role' WHEN 'A' THEN 'application_role' ELSE 'user' END, 1, CAST(0 AS bit)
            FROM sys.database_principals WHERE principal_id >= 5 AND is_fixed_role = 0
            UNION ALL
            SELECT DISTINCT NULL, pr.name, 'permissions', 1, CAST(0 AS bit)
            FROM sys.database_permissions AS p
            JOIN sys.database_principals AS pr ON pr.principal_id = p.grantee_principal_id
            WHERE NOT (p.class = 0 AND p.permission_name = 'CONNECT' AND p.state = 'G')
              AND NOT (pr.principal_id = 0 AND p.state = 'G' AND (p.class = 1 AND p.major_id < 0
                  OR p.class = 0 AND p.permission_name IN ('VIEW ANY COLUMN ENCRYPTION KEY DEFINITION', 'VIEW ANY COLUMN MASTER KEY DEFINITION')))
            UNION ALL
            SELECT NULL, name, 'certificate', 2, CAST(0 AS bit) FROM sys.certificates
            UNION ALL
            SELECT NULL, name, 'asymmetric_key', 2, CAST(0 AS bit) FROM sys.asymmetric_keys
            UNION ALL
            SELECT NULL, name, 'symmetric_key', 2, CAST(0 AS bit) FROM sys.symmetric_keys
            UNION ALL
            SELECT NULL, name, 'database_scoped_credential', 2, CAST(0 AS bit) FROM sys.database_scoped_credentials
        )
        SELECT schema_name, name, type, class, encrypted FROM inventory ORDER BY class, type, schema_name, name;
        """;

    /// <summary>A column's or parameter's type, as <see cref="SqlServerTypeNames.Format"/> takes it.</summary>
    private const string TypeColumns = "t.name, SCHEMA_NAME(t.schema_id), t.is_user_defined";

    private const string ColumnsSql = $"""
        SELECT c.name, {TypeColumns}, c.max_length, c.precision, c.scale, c.is_nullable, dc.definition, c.is_identity, cc.definition
        FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        WHERE c.object_id = @id
        ORDER BY c.column_id;
        """;

    private const string KeysSql = """
        SELECT kc.name, kc.type, i.type, c.name, ic.is_descending_key
        FROM sys.key_constraints AS kc
        JOIN sys.indexes AS i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE kc.parent_object_id = @id
        ORDER BY kc.name, ic.key_ordinal;
        """;

    private const string ChecksSql = """
        SELECT name, definition FROM sys.check_constraints WHERE parent_object_id = @id ORDER BY name;
        """;

    /// <summary>
    /// Indexes other than the heap and the ones behind keys. A columnstore index's columns all have key ordinal 0;
    /// in a rowstore index a column with key ordinal 0 that isn't included is the partitioning column SQL Server adds.
    /// </summary>
    private const string IndexesSql = """
        SELECT i.name, LOWER(REPLACE(i.type_desc, '_', ' ')), i.is_unique, i.filter_definition, i.type,
            c.name, ic.key_ordinal, ic.is_included_column, ic.is_descending_key
        FROM sys.indexes AS i
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = @id AND i.index_id > 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.is_hypothetical = 0
        ORDER BY i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
        """;

    private const string TriggersSql = """
        SELECT name FROM sys.triggers WHERE parent_id = @id ORDER BY name;
        """;

    /// <summary>Both directions at once: a key that references its own table is in both lists.</summary>
    private const string ForeignKeysSql = """
        SELECT fk.name,
            SCHEMA_NAME(pt.schema_id) + '.' + pt.name, SCHEMA_NAME(rt.schema_id) + '.' + rt.name,
            fk.delete_referential_action, fk.update_referential_action, pc.name, rc.name,
            CAST(CASE WHEN fk.parent_object_id = @id THEN 1 ELSE 0 END AS bit),
            CAST(CASE WHEN fk.referenced_object_id = @id THEN 1 ELSE 0 END AS bit)
        FROM sys.foreign_keys AS fk
        JOIN sys.objects AS pt ON pt.object_id = fk.parent_object_id
        JOIN sys.objects AS rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = @id OR fk.referenced_object_id = @id
        ORDER BY fk.name, fkc.constraint_column_id;
        """;

    private const string TemporalSql = """
        SELECT SCHEMA_NAME(h.schema_id) + '.' + h.name, sc.name, ec.name
        FROM sys.tables AS t
        JOIN sys.tables AS h ON h.object_id = t.history_table_id
        JOIN sys.periods AS p ON p.object_id = t.object_id
        JOIN sys.columns AS sc ON sc.object_id = t.object_id AND sc.column_id = p.start_column_id
        JOIN sys.columns AS ec ON ec.object_id = t.object_id AND ec.column_id = p.end_column_id
        WHERE t.object_id = @id AND t.temporal_type = 2;
        """;

    /// <summary>
    /// The definition, and why it's missing when it is. <c>OBJECTPROPERTY</c> has no answer for a database trigger,
    /// which isn't in <c>sys.objects</c>, so an encrypted one reads as "no permission".
    /// </summary>
    private const string DefinitionSql = """
        SELECT m.definition,
            CAST(COALESCE(OBJECTPROPERTY(x.id, 'IsEncrypted'), 0) AS bit),
            CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.assembly_modules AS a WHERE a.object_id = x.id) THEN 1 ELSE 0 END AS bit)
        FROM (VALUES (@id)) AS x (id)
        LEFT JOIN sys.sql_modules AS m ON m.object_id = x.id;
        """;

    /// <summary>Parameter 0 is a function's return value.</summary>
    private const string ParametersSql = $"""
        SELECT p.parameter_id, p.name, {TypeColumns}, p.max_length, p.precision, p.scale, p.is_output, p.has_default_value
        FROM sys.parameters AS p
        JOIN sys.types AS t ON t.user_type_id = p.user_type_id
        WHERE p.object_id = @id
        ORDER BY p.parameter_id;
        """;

    private const string SequenceSql = $"""
        SELECT {TypeColumns}, s.precision, s.scale,
            CONVERT(varchar(40), s.start_value), CONVERT(varchar(40), s.increment),
            CONVERT(varchar(40), s.minimum_value), CONVERT(varchar(40), s.maximum_value), s.is_cycling
        FROM sys.sequences AS s
        JOIN sys.types AS t ON t.user_type_id = s.user_type_id
        WHERE s.object_id = @id;
        """;

    private const string SynonymSql = """
        SELECT base_object_name FROM sys.synonyms WHERE object_id = @id;
        """;

    /// <summary>A CLR type has no T-SQL base type, so its base type is left out.</summary>
    private const string AliasTypeSql = """
        SELECT b.name, NULL, CAST(0 AS bit), t.max_length, t.precision, t.scale, t.is_nullable
        FROM sys.types AS t
        LEFT JOIN sys.types AS b ON b.user_type_id = t.system_type_id AND t.is_assembly_type = 0
        WHERE t.user_type_id = @id;
        """;

    public Task<CatalogOutcome<ObjectList>> ListAsync(string connectionString, ObjectFilter filter, EffectiveLimits limits, CancellationToken cancellationToken) =>
        ReadAsync(connectionString, limits, async connection =>
        {
            await using var command = Command(connection, ListSql, limits);
            command.Parameters.Add(new SqlParameter("@cap", SqlDbType.Int) { Value = limits.MaxRows });
            command.Parameters.Add(Text("@schema", filter.Schema));
            command.Parameters.Add(Text("@pattern", filter.Pattern is null ? null : Glob.ToLike(filter.Pattern)));
            command.Parameters.Add(Text("@types", filter.Types.Count == 0 ? null : string.Join(",", filter.Types.Select(t => t.ToWireName()))));

            var objects = new List<ObjectSummary>();
            var total = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                objects.Add(new ObjectSummary(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetString(1),
                    ParseType(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3)));
                total = reader.GetInt32(4);
            }

            return new ObjectList(objects, total, total > objects.Count);
        }, cancellationToken, ListTimeoutAdvice);

    public Task<CatalogOutcome<IReadOnlyList<ObjectSummary>>> ResolveAsync(
        string connectionString, IReadOnlyList<ObjectName> names, EffectiveLimits limits, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<ObjectSummary>>(connectionString, limits, async connection =>
        {
            var resolved = await ResolveAllAsync(connection, names, limits, cancellationToken).ConfigureAwait(false);
            return [.. resolved.Select(r => r.Summary).Distinct()];
        }, cancellationToken);

    public Task<CatalogOutcome<ObjectDetails>> DescribeAsync(string connectionString, ObjectName name, EffectiveLimits limits, CancellationToken cancellationToken) =>
        ReadAsync(connectionString, limits, async connection =>
        {
            var found = (await ResolveAllAsync(connection, [name], limits, cancellationToken).ConfigureAwait(false))[0];
            var describer = new Describer(connection, found.Id, limits, cancellationToken);
            var summary = found.Summary;
            var details = new ObjectDetails(summary.Schema, summary.Name, summary.Type);
            return summary.Type switch
            {
                ObjectType.Table or ObjectType.View or ObjectType.TableType => await describer.RelationAsync(details).ConfigureAwait(false),
                ObjectType.Procedure => await describer.RoutineAsync(details, found.Code).ConfigureAwait(false),
                ObjectType.Function => await describer.RoutineAsync(details, found.Code).ConfigureAwait(false),
                ObjectType.Trigger => details with { Definition = await describer.DefinitionAsync().ConfigureAwait(false) },
                ObjectType.Sequence => details with { Sequence = await describer.SequenceAsync().ConfigureAwait(false) },
                ObjectType.Synonym => details with { Target = await describer.SynonymTargetAsync().ConfigureAwait(false) },
                ObjectType.Type => await describer.AliasTypeAsync(details).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"No description for object type {summary.Type}."),
            };
        }, cancellationToken);

    public Task<CatalogOutcome<IReadOnlyList<InventoryItem>>> InventoryAsync(string connectionString, EffectiveLimits limits, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<InventoryItem>>(connectionString, limits, async connection =>
        {
            await using var command = Command(connection, InventorySql, limits);
            var items = new List<InventoryItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new InventoryItem(
                    new CatalogItem(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), reader.GetString(2)),
                    (InventoryClass)reader.GetInt32(3),
                    reader.GetBoolean(4)));
            }

            return items;
        }, cancellationToken);

    public Task<CatalogOutcome<string>> ServerVersionAsync(string connectionString, EffectiveLimits limits, CancellationToken cancellationToken) =>
        ReadAsync(connectionString, limits, async connection =>
        {
            await using var command = Command(connection, "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128));", limits);
            return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }, cancellationToken);

    /// <summary>An object a name resolved to, with what the describe queries need to find it.</summary>
    private sealed record Resolved(ObjectSummary Summary, int Id, string Code);

    /// <summary>
    /// Resolves every name on one connection. If any fails, throws <see cref="UnresolvedNameException"/> with every
    /// problem, one per line, which <see cref="ReadAsync{T}"/> turns into an outcome.
    /// </summary>
    private static async Task<List<Resolved>> ResolveAllAsync(SqlConnection connection, IReadOnlyList<ObjectName> names, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        var resolved = new List<Resolved>();
        var problems = new List<string>();
        foreach (var name in names)
        {
            await using var command = Command(connection, ResolveSql, limits);
            command.Parameters.Add(Text("@schema", name.Schema));
            command.Parameters.Add(Text("@name", name.Name));
            var candidates = new List<Resolved>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var summary = new ObjectSummary(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), ParseType(reader.GetString(2)), null);
                    candidates.Add(new Resolved(summary, reader.GetInt32(3), reader.GetString(4)));
                }
            }

            if (NameResolution.Problem(name, [.. candidates.Select(c => c.Summary)]) is { } problem)
            {
                problems.Add(problem);
            }
            else
            {
                resolved.Add(candidates[0]);
            }
        }

        return problems.Count == 0 ? resolved : throw new UnresolvedNameException(string.Join(Environment.NewLine, problems));
    }

    /// <summary>The describe queries for one object, on one open connection.</summary>
    private sealed class Describer(SqlConnection connection, int id, EffectiveLimits limits, CancellationToken cancellationToken)
    {
        /// <summary>A table, view or table type: every relational part, empty parts left null.</summary>
        public async Task<ObjectDetails> RelationAsync(ObjectDetails details)
        {
            var keys = await RowsAsync(KeysSql, r => (Name: r.GetString(0), Type: r.GetString(1).Trim(), Clustered: r.GetByte(2) == 1, Column: Column(r, 3, 4))).ConfigureAwait(false);
            var foreignKeys = await RowsAsync(ForeignKeysSql, r => (
                Name: r.GetString(0), Table: r.GetString(1), Referenced: r.GetString(2), OnDelete: Action(r.GetByte(3)), OnUpdate: Action(r.GetByte(4)),
                Column: r.GetString(5), ReferencedColumn: r.GetString(6), Declares: r.GetBoolean(7), IsReferenced: r.GetBoolean(8))).ConfigureAwait(false);
            var foreignKeyDetails = foreignKeys
                .GroupBy(k => k.Name)
                .Select(g => (Key: g.First(), Details: new ForeignKeyDetails(
                    g.Key, g.First().Table, [.. g.Select(k => k.Column)], g.First().Referenced, [.. g.Select(k => k.ReferencedColumn)], g.First().OnDelete, g.First().OnUpdate)))
                .ToList();

            return details with
            {
                Columns = NullIfEmpty(await ColumnsAsync().ConfigureAwait(false)),
                PrimaryKey = Keys(keys, "PK").FirstOrDefault(),
                UniqueConstraints = NullIfEmpty(Keys(keys, "UQ")),
                CheckConstraints = NullIfEmpty(await RowsAsync(ChecksSql, r => new CheckDetails(r.GetString(0), r.GetString(1))).ConfigureAwait(false)),
                Indexes = NullIfEmpty(await IndexesAsync().ConfigureAwait(false)),
                Triggers = NullIfEmpty(await RowsAsync(TriggersSql, r => r.GetString(0)).ConfigureAwait(false)),
                ForeignKeys = NullIfEmpty([.. foreignKeyDetails.Where(k => k.Key.Declares).Select(k => k.Details)]),
                ReferencedBy = NullIfEmpty([.. foreignKeyDetails.Where(k => k.Key.IsReferenced).Select(k => k.Details)]),
                Temporal = (await RowsAsync(TemporalSql, r => new TemporalDetails(r.GetString(0), r.GetString(1), r.GetString(2))).ConfigureAwait(false)).FirstOrDefault(),
                Definition = details.Type == ObjectType.View ? await DefinitionAsync().ConfigureAwait(false) : null,
            };

            static List<KeyDetails> Keys(IEnumerable<(string Name, string Type, bool Clustered, string Column)> rows, string type) =>
                [.. rows.Where(k => k.Type == type).GroupBy(k => k.Name).Select(g => new KeyDetails(g.Key, [.. g.Select(k => k.Column)], g.First().Clustered))];
        }

        /// <summary>
        /// A procedure or function: parameters, the definition, and for a function its kind, return type and any result
        /// columns. Whether a T-SQL parameter has a default is only in the definition (<c>sys.parameters</c> records
        /// it for CLR routines alone), so it is read from there, and is unknown when the definition is unavailable.
        /// </summary>
        public async Task<ObjectDetails> RoutineAsync(ObjectDetails details, string code)
        {
            var definition = await DefinitionAsync().ConfigureAwait(false);
            var clr = code is "PC" or "FS" or "FT" or "AF";
            var defaults = definition is Definition.Text text ? ParameterDefaults(text.Value) : null;
            var rows = await RowsAsync(ParametersSql, r => (
                Id: r.GetInt32(0), Name: r.GetString(1), Type: TypeName(r, 2), Output: r.GetBoolean(8), ClrDefault: r.GetBoolean(9))).ConfigureAwait(false);
            var parameters = rows
                .Where(p => p.Id > 0)
                .Select(p => new ParameterDetails(
                    p.Name,
                    p.Type,
                    p.Output ? "output" : "input",
                    clr ? p.ClrDefault : defaults?.GetValueOrDefault(p.Name.ToUpperInvariant())))
                .ToList();
            var withParameters = details with { Parameters = NullIfEmpty(parameters), Definition = definition };
            if (details.Type != ObjectType.Function)
            {
                return withParameters;
            }

            var tableValued = code is "IF" or "TF" or "FT";
            return withParameters with
            {
                Kind = code switch
                {
                    "FN" => "scalar",
                    "IF" => "inline_table_valued",
                    "TF" => "table_valued",
                    _ => "clr",
                },
                ReturnType = tableValued ? "table" : rows.FirstOrDefault(p => p.Id == 0).Type,
                Columns = tableValued ? NullIfEmpty(await ColumnsAsync().ConfigureAwait(false)) : null,
            };
        }

        public async Task<Definition> DefinitionAsync()
        {
            var row = (await RowsAsync(DefinitionSql, r => (Text: r.IsDBNull(0) ? null : r.GetString(0), Encrypted: r.GetBoolean(1), Clr: r.GetBoolean(2))).ConfigureAwait(false)).Single();
            return row switch
            {
                { Text: { } value } => new Definition.Text(value),
                { Encrypted: true } => new Definition.Unavailable(Definition.Encrypted),
                { Clr: true } => new Definition.Unavailable(Definition.Clr),
                _ => new Definition.Unavailable(Definition.NoPermission),
            };
        }

        public async Task<SequenceDetails> SequenceAsync() =>
            (await RowsAsync(SequenceSql, r => new SequenceDetails(
                SqlServerTypeNames.Format(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetBoolean(2), 0, r.GetByte(3), r.GetByte(4)),
                r.GetString(5),
                r.GetString(6),
                r.GetString(7),
                r.GetString(8),
                r.GetBoolean(9))).ConfigureAwait(false)).Single();

        /// <summary>
        /// <c>base_object_name</c> is stored bracketed, such as <c>[crm].[customers]</c>; agents get the plain parts,
        /// as <c>describe_object</c> takes them.
        /// </summary>
        public async Task<string> SynonymTargetAsync()
        {
            var stored = (await RowsAsync(SynonymSql, r => r.GetString(0)).ConfigureAwait(false)).Single();
            using var reader = new StringReader(stored);
            var parsed = new TSql170Parser(initialQuotedIdentifiers: true).ParseSchemaObjectName(reader, out var errors);
            return errors.Count > 0 || parsed is null
                ? stored
                : string.Join(".", new[] { parsed.ServerIdentifier, parsed.DatabaseIdentifier, parsed.SchemaIdentifier, parsed.BaseIdentifier }
                    .Where(i => i is not null).Select(i => i!.Value));
        }

        public async Task<ObjectDetails> AliasTypeAsync(ObjectDetails details)
        {
            var row = (await RowsAsync(AliasTypeSql, r => (Type: r.IsDBNull(0) ? null : TypeName(r, 0), Nullable: r.GetBoolean(6))).ConfigureAwait(false)).Single();
            return details with { BaseType = row.Type, Nullable = row.Nullable };
        }

        private Task<List<ColumnDetails>> ColumnsAsync() =>
            RowsAsync(ColumnsSql, r => new ColumnDetails(
                r.GetString(0),
                TypeName(r, 1),
                r.GetBoolean(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.GetBoolean(9) ? true : null,
                r.IsDBNull(10) ? null : r.GetString(10)));

        private async Task<List<IndexDetails>> IndexesAsync()
        {
            var rows = await RowsAsync(IndexesSql, r => (
                Name: r.GetString(0), Kind: r.GetString(1), Unique: r.GetBoolean(2), Filter: r.IsDBNull(3) ? null : r.GetString(3), Columnstore: r.GetByte(4) is 5 or 6,
                Column: Column(r, 5, 8), KeyOrdinal: r.GetByte(6), Included: r.GetBoolean(7))).ConfigureAwait(false);
            return [.. rows.GroupBy(i => i.Name).Select(g =>
            {
                var first = g.First();
                var keys = g.Where(c => c.Columnstore || c.KeyOrdinal > 0).Select(c => c.Column).ToList();
                var included = g.Where(c => !c.Columnstore && c.Included).Select(c => c.Column).ToList();
                return new IndexDetails(first.Name, first.Kind, first.Unique, keys, NullIfEmpty(included), first.Filter);
            })];
        }

        private async Task<List<T>> RowsAsync<T>(string sql, Func<SqlDataReader, T> map)
        {
            await using var command = Command(connection, sql, limits);
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(map(reader));
            }

            return rows;
        }

        /// <summary>A type from the columns <see cref="TypeColumns"/> names, followed by max length, precision and scale.</summary>
        private static string TypeName(SqlDataReader r, int at) =>
            SqlServerTypeNames.Format(r.GetString(at), r.IsDBNull(at + 1) ? null : r.GetString(at + 1), r.GetBoolean(at + 2), r.GetInt16(at + 3), r.GetByte(at + 4), r.GetByte(at + 5));

        private static string Column(SqlDataReader r, int name, int descending) => r.GetBoolean(descending) ? $"{r.GetString(name)} DESC" : r.GetString(name);

        private static string? Action(byte action) => action switch
        {
            1 => "cascade",
            2 => "set_null",
            3 => "set_default",
            _ => null,
        };
    }

    /// <summary>
    /// Which parameters of the routine in <paramref name="definition"/> have a default, by upper-cased name. Null if
    /// the definition doesn't parse, so the answer is unknown rather than wrong.
    /// </summary>
    internal static Dictionary<string, bool>? ParameterDefaults(string definition)
    {
        using var reader = new StringReader(definition);
        var fragment = new TSql170Parser(initialQuotedIdentifiers: true).Parse(reader, out var errors);
        if (errors.Count > 0)
        {
            return null;
        }

        var visitor = new ParameterVisitor();
        fragment.Accept(visitor);
        return visitor.Defaults;
    }

    private sealed class ParameterVisitor : TSqlFragmentVisitor
    {
        public Dictionary<string, bool> Defaults { get; } = new(StringComparer.Ordinal);

        public override void Visit(ProcedureParameter node) => Defaults.TryAdd(node.VariableName.Value.ToUpperInvariant(), node.Value is not null);
    }

    private static List<T>? NullIfEmpty<T>(List<T> items) => items.Count == 0 ? null : items;

    private static ObjectType ParseType(string wire) =>
        ObjectTypes.TryParse(wire, out var type) ? type : throw new InvalidOperationException($"The catalog query returned an unknown object type '{wire}'.");

    /// <summary>A name that resolved to no object or several. The message is for the agent and names every problem.</summary>
    private sealed class UnresolvedNameException(string message) : Exception(message);

    /// <summary>
    /// Opens a connection, runs <paramref name="read"/>, and turns SqlClient's failures into outcomes, as the
    /// executor does: an unparseable connection string or a failed open is a connection failure, error -2 a timeout.
    /// </summary>
    private async Task<CatalogOutcome<T>> ReadAsync<T>(
        string connectionString, EffectiveLimits limits, Func<SqlConnection, Task<T>> read, CancellationToken cancellationToken,
        string timeoutAdvice = "")
    {
        var started = time.GetTimestamp();
        TimeSpan Elapsed() => time.GetElapsedTime(started);

        string effective;
        try
        {
            effective = SqlServerQueryExecutor.EffectiveConnectionString(connectionString);
        }
        catch (ArgumentException ex)
        {
            return new CatalogFailed<T>(FailureKind.Connection, $"invalid connection string: {ex.Message}", null, Elapsed());
        }

        await using var connection = new SqlConnection(effective);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            return new CatalogFailed<T>(FailureKind.Connection, $"could not connect: {ex.Message}", ex.Number, Elapsed());
        }

        try
        {
            var value = await read(connection).ConfigureAwait(false);
            return new CatalogSucceeded<T>(value, Elapsed());
        }
        catch (UnresolvedNameException ex)
        {
            return new CatalogFailed<T>(FailureKind.UnresolvedName, ex.Message, null, Elapsed());
        }
        catch (SqlException ex) when (ex.Number == TimeoutErrorNumber)
        {
            return new CatalogFailed<T>(
                FailureKind.Timeout,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"reading the catalog timed out after {limits.TimeoutSeconds} seconds (this database's command timeout), so it was cancelled.{timeoutAdvice}"),
                ex.Number,
                Elapsed());
        }
        catch (SqlException ex)
        {
            return new CatalogFailed<T>(FailureKind.Sql, $"reading the catalog failed: {ex.Message}", ex.Number, Elapsed());
        }
    }

    private static SqlCommand Command(SqlConnection connection, string sql, EffectiveLimits limits) =>
        new(sql, connection) { CommandTimeout = limits.TimeoutSeconds };

    private static SqlParameter Text(string name, string? value) =>
        new(name, SqlDbType.NVarChar, 4000) { Value = (object?)value ?? DBNull.Value };
}
