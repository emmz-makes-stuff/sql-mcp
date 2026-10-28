# Design

## Context

Foundation and write-tools left these extension points for this change:

- `ToolGroup.Introspection` exists, and is allowed by default (`PermissionResolver`).
- `IntrospectionEvent(Tool, Database, Target)` exists in `Audit/AuditEvents.cs`, but nothing writes it.
- `EngineServices` holds an engine's classifier and executor.
- `ToolCatalog` offers a group's tools when the group is allowed somewhere.
- `ProjectLayout.EnsureGitIgnore` writes `logs/` only when `.gitignore` is missing.
- `AuditLog.FallbackDirectory` gives the user-level state directory, `~/.local/state/sql-mcp/logs`.

ADR-0003 sets the approach: our own model filled from `sys.*` for the metadata tools, and SMO `Scripter` behind an
`IScriptGenerator` for the script, conditional on a spike. `Microsoft.SqlServer.SqlManagementObjects` is already
listed in `docs/dependencies.md` for this change, so adding it isn't a stop. The requirements are in
`specs/schema-introspection`, `specs/creation-script`, and the `audit-log` and `cli` deltas.

## Goals / Non-Goals

**Goals:**
- The model and both interfaces are engine-neutral, so PostgreSQL (v2) adds a catalog reader and a script generator
  and nothing else.
- SMO is used only inside the SQL Server script generator. The metadata tools never load it.
- The completeness check is a pure function, unit-tested without a database.

**Non-Goals:**
- Caching metadata between calls. Every call reads the catalog, so the results are never stale after a `ddl` call.
- Making the script runnable through the `ddl` tool. It holds procedures and `GO` separators, which `ddl` refuses
  by design. It is for sqlcmd, SSMS, or a migration tool.
- Cleaning up old script files.

## Decisions

### I1. The spike comes first, and gates the change
Section 1 adds the SMO package and one integration test. The test scripts a UDT, a table type, a temporal table and
a partitioned table from the SQL Server 2025 container, on macOS. It also checks:
- that SMO resolves against our pinned `Microsoft.Data.SqlClient` 7.1.1 without a downgrade or a version conflict;
- that `make build` still passes with warnings as errors;
- how much SMO adds to the packed tool.

If any of these fail, that is a stop: write a `stopped-` devlog note and ask about DacFx (ADR-0003's fallback).
If they pass, ADR-0003's status becomes plain "Accepted".

### I2. Layout and interfaces
```
src/SqlMcp/Introspection/
  CatalogModel.cs          ObjectType, ObjectSummary, ObjectDetails (+ per-type parts), ObjectName
  ICatalogReader.cs        ListAsync(filter), ResolveAsync(names), DescribeAsync(name), InventoryAsync()
  IScriptGenerator.cs      GenerateAsync(objects?, TextWriter) → what was scripted
  Completeness.cs          inventory − scripted → omitted, with reasons
  ScriptFiles.cs           directory, file name, temp-then-move, .gitignore
  SqlServer/SqlServerCatalogReader.cs   fixed, parameterised sys.* queries
  SqlServer/SmoScriptGenerator.cs       SMO Scripter
```
`EngineServices` gains `CatalogReader` and `ScriptGenerator`.
*Rejected:* putting the catalog reads in `IQueryExecutor`. Its contract is "run a classified batch in a transaction".
Catalog reads are fixed queries that return a typed model, not rows to buffer and page.

### I3. Object types and the model
The type names agents see are `table`, `view`, `procedure`, `function`, `trigger`, `sequence`, `synonym`, `type` and
`table_type`. A function also carries its kind (scalar, inline table-valued, table-valued, or CLR), but the kind is not
a separate type. Database-level DDL triggers have no schema, and are listed without a `schema` key. `ObjectDetails` is
one record with optional parts (columns, keys, indexes, foreign keys, triggers, parameters, definition, sequence,
synonym target, base type, temporal). Only the parts that apply to the type are filled in, and empty parts are left
out of the JSON. A definition is either text or `{ "unavailable": "<reason>" }`. The reason is `encrypted`; `clr`
for a CLR routine, which has no T-SQL definition; or `no permission` when `sys.sql_modules.definition` is NULL for any
other reason. A database trigger isn't in `sys.objects`, so `OBJECTPROPERTY` can't say whether it is encrypted. Its
NULL definition counts as `encrypted` when the login has VIEW DEFINITION on the database, and as `no permission`
otherwise. `describe_object` takes an optional type, because types are named apart from other objects: a table and a
table type can share a `schema.name`.

### I4. Catalog reads
- One connection per call, opened from the same effective connection string the executor builds. Every command
  gets the database's timeout. No transaction is used: nothing here writes, and no agent SQL runs.
- Objects come from `sys.objects` with `is_ms_shipped = 0`, plus `sys.types` (`is_user_defined = 1`) for `type` and
  `table_type`, plus `sys.triggers` with `parent_class = 0` for database triggers.
- The approximate row count is `SUM(rows)` from `sys.partitions` for `index_id IN (0, 1)`.
- The total and the cap come from one query, using `COUNT(*) OVER ()` and `TOP (@cap)`.
- The glob becomes a `LIKE` pattern. `%`, `_` and `[` are escaped first, then `*` becomes `%` and `?` becomes `_`.
  Both sides are compared through `LOWER()`, so a case-sensitive database collation still matches without regard
  to case. The type list is matched with `CHARINDEX`, not `STRING_SPLIT`, which needs compatibility level 130.
- Names are parsed with ScriptDom's `TSqlParser.ParseSchemaObjectName`, which handles brackets and quoted names.
  A name with three or four parts is refused, as the write tools refuse them.
- An unqualified name is looked up across all schemas: no match means "not found", and more than one means
  "ambiguous", naming each candidate.

### I5. Script generation (SMO)
- `ServerConnection` is built on a `SqlConnection` with the effective connection string, and its
  `StatementTimeout` is the database's timeout. `Server.SetDefaultInitFields` prefetches the properties the
  scripter needs, so it doesn't make a round trip per object.
- `ScriptingOptions`: `SchemaQualify`, `DriAll`, `Indexes`, `FullTextIndexes`, `ExtendedProperties` and
  `AnsiPadding`. `ScriptData`, `Permissions`, `IncludeDatabaseContext`, `IncludeIfNotExists`, `ScriptOwner` and
  `WithDependencies` are off. No `CREATE DATABASE` is emitted.
  - `Triggers` is off too. One encrypted DML trigger makes SMO fail its whole table, and a trigger URN inside the
    walk fails to script, so DML triggers are scripted on their own (below).
  - `ScriptBatchTerminator` isn't used. `EnumScriptWithList` yields one batch per string and never a `GO`, whatever
    it says, so the generator writes `GO` after each batch.
- Whole database: list the candidate URNs, leaving out encrypted modules (SMO throws on them). The script is
  written in three parts, each streamed with `EnumScriptWithList` into the writer:
  1. schemas and full-text catalogs, since `DiscoverDependencies` refuses schemas and full-text indexes need their
     catalog;
  2. everything else that has a create order, ordered by `DependencyWalker`. The walk adds parents that weren't
     asked for and repeats some nodes, so it is filtered back down to the candidates, first place kept;
  3. the non-encrypted DML triggers of every table and non-encrypted view, last. An encrypted view's triggers are
     left out with it, and omitted as `on an encrypted view`.
- Chosen objects: the same walk, filtered down to the chosen set. This keeps the FK order between them (for
  example `orders` before `order_lines`) without adding any dependency. A chosen table or view brings its own DML
  triggers, in part 3; a chosen trigger goes there too, found through the table or view that resolution names as its
  parent. Encrypted chosen modules are left out, and the omitted list names them. Their inventory comes from
  `ICatalogReader.ResolveAsync`, not the whole-database inventory: the chosen objects and their own DML triggers, with
  encryption and parents, so an encrypted trigger of a chosen table is named too (§8.3).
- The generator returns the items it scripted. For a whole database, `Completeness` compares them with
  `ICatalogReader.InventoryAsync`, which covers:
  - `sys.objects`, user types, partition functions and schemes, XML schema collections, user assemblies, and
    full-text catalogs;
  - for the omitted list only: database principals, certificates, keys and database-scoped credentials.

  Anything in the inventory that wasn't scripted is omitted, with one of these reasons: `encrypted`,
  `on an encrypted view`, `security principal`, `security object`, or `not scripted by the generator` for anything
  SMO skipped.
- SMO is synchronous. It runs on `Task.Run`, and the cancellation token is checked between the objects
  `EnumScriptWithList` yields.

### I6. Script files
- Directory: `<project>/.sql-mcp/scripts/`, as the audit log chooses its own: only when `.sql-mcp/` exists and the
  file can be created there. Otherwise, a `scripts` folder next to the audit log's fallback
  (`~/.local/state/sql-mcp/scripts`, or `%LOCALAPPDATA%\sql-mcp\scripts`).
- File name: the identifier with every character outside `[A-Za-z0-9._-]` replaced by `_`, then `-`, then the UTC
  time as `yyyyMMddTHHmmssZ`, then `.sql`. For example, `localhost_1433_app-20261026T140509Z.sql`. If the name is
  taken, `-2`, `-3` and so on are added.
- The script is written to `<name>.sql.tmp`, then moved with `overwrite: false`. The name used is the first whose
  `.sql` and `.sql.tmp` are both free, and the `.sql.tmp` is created with `CreateNew`. If another file takes the name
  before the move, the next free name is used. On any failure or cancellation the temp file is deleted in a
  `finally` block.
- The header comment is written by us, not SMO. It holds the identifier, `SERVERPROPERTY('ProductVersion')`, the
  UTC time and the sql-mcp version, and never the connection string.
- `.gitignore`: `ProjectLayout` gets `EnsureGitIgnoreLines("logs/", "scripts/")`, which creates the file or adds
  the missing lines and keeps the rest. `init` and the audit log's directory creation use the same content, so a
  new project gets both lines from the start.

### I7. Tool flow, responses and audit
- `ToolPipeline` gets one `RunIntrospectionAsync` flow, shared by the three tools, following the order `RunAsync`
  uses:
  1. the audit-unavailable refusal;
  2. an unknown database;
  3. a grant check, which writes a `denied` event;
  4. an unavailable database;
  5. the arguments (names, types, the objects list), refused unlogged if invalid, as an unknown database is;
  6. the call itself;
  7. the `introspection` event;
  8. the response.

  The arguments come after the grant, so a call on a denied database is audited as denied whatever it passed.

  There's no classifier step, because no agent SQL runs.
- `IntrospectionEvent` becomes `(Tool, Database, Request, DurationMs, Succeeded)`, and a failure adds `Kind` (a
  `FailureKind`, with the new `UnresolvedName` for a name that matched no object or several) and `ErrorNumber`. As
  with `FailedEvent`, the message text is never logged, because it can echo values. For a script it adds
  `ScriptPath`, `Counts` and `OmittedCount`. `Request` is the filters as `name=value` entries, the object name, or
  the object list.
- If the audit write fails after a script file has been written, the file is deleted and the agent gets the
  audit-unavailable error. Nothing is handed out unlogged.
- Responses are snake_case JSON, like the existing ones:
  - `list_objects` returns `{ objects: [{schema, name, type, approx_rows?}], total, truncated, message? }`;
  - `script_database` returns `{ path, bytes, counts: {table: 12, …}, omitted: [{schema, name, type, reason}] }`.
- Annotations: all three are read-only, and use the default `Create` options. Emmz decided on 2026-10-26 that
  `script_database` is read-only too. It never changes the database, and it only ever writes a new, git-ignored
  file, so clients can run it without asking, as they run the other two.
  *Rejected:* `ReadOnly = false` with `Destructive = false`. That label is stricter under MCP's definition, but
  clients would ask for approval on every script, or block it in read-only modes.
- `ToolCatalog` offers all three whenever `introspection` is allowed on any database. They don't add `next_page`
  or `abandon`.

### I8. Tests
- Unit tests:
  - glob-to-LIKE escaping;
  - name parsing, and the ambiguous and not-found messages;
  - file naming and collisions;
  - `.gitignore` line merging;
  - `Completeness`;
  - the response shapes;
  - tool offering and annotations;
  - the pipeline's audit order, with fake readers and generators.
- Integration tests: a seed script (`tests/SqlMcp.IntegrationTests/Introspection/seed.sql`) creates every
  supported object type, an encrypted procedure, a user, and a database DDL trigger. Then:
  - assert on `list_objects` and `describe_object` results;
  - the round trip: script the seeded database, create an empty second database in the same container, run the
    script there (the test helper splits it on `GO`), and compare `list_objects` plus `describe_object` for every
    object, leaving out row counts and the omitted objects.

## Risks / Trade-offs

- **[SMO needs a different `Microsoft.Data.SqlClient` from the one we pin]** → The spike checks this first. Central
  pinning decides the version. A conflict we can't resolve is a stop.
- **[SMO makes the tool package much bigger]** → Accepted by ADR-0003. The spike records the size in its devlog note.
- **[A login without `VIEW DEFINITION` sees only part of the database]** → The completeness check can only compare
  against what the login can see, so a hidden object is missing from both the inventory and the script. The tool
  descriptions say that visibility depends on the login. We can't detect this from inside the database.
- **[SMO is slow on very large databases]** → Prefetching the init fields helps. Each statement has the database's
  timeout, and cancellation stops the script between objects. A whole-database script of a big schema can still
  take minutes, and that is accepted.
- **[Constraint names the system generated differ between databases]** → SMO scripts the existing names
  explicitly, so the round trip keeps them. The test would catch it if it didn't.
- **[Definitions can hold secrets such as hard-coded passwords in a procedure]** → An agent with `introspection` can
  already read them through `describe_object`. Script files are git-ignored, and the audit log never records them.
- **[Script files pile up]** → Accepted. They are small text files, the agent is told their paths, and the user can
  delete the directory.

## Migration Plan

- This change is additive. Configs and permissions are unchanged, and `introspection` is already allowed by default,
  so the new tools appear on upgrade. That is the intended v1 behaviour.
- An existing `.sql-mcp/.gitignore` gains `scripts/` the first time a script is written there.
- Rollback is the previous release. Script files left behind are plain files and harmless.
