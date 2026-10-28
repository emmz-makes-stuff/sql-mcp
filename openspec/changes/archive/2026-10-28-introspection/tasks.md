# Tasks

## 1. SMO spike (gate)

- [x] 1.1 Add `Microsoft.SqlServer.SqlManagementObjects` (181.x, net10.0) to `Directory.Packages.props` and `SqlMcp.csproj`. Check that it resolves against the pinned `Microsoft.Data.SqlClient` 7.1.1 without a downgrade or a conflict, and that `make build` passes with warnings as errors (design I1). Verify by quoting `BUILD_EXIT:0` and the restored SqlClient version in the devlog note
- [x] 1.2 Add a spike integration test that creates a UDT, a table type, a temporal table and a partitioned table in the SQL Server 2025 container, scripts them with SMO `Scripter` on macOS, and asserts that each appears in the output. Verify that `make itest` passes. If it fails, stop with a `stopped-` devlog note asking about DacFx
- [x] 1.3 Record the change in the packed tool's size (`dotnet pack`, before and after) in the devlog note. Set ADR-0003's status to "Accepted". Verify that the ADR diff shows only the status line

## 2. Catalog model and list_objects

- [x] 2.1 Add `Introspection/CatalogModel.cs`: `ObjectType` with its wire names, `ObjectSummary`, `ObjectName`, and `ObjectDetails` with optional parts (design I2, I3). Also add `ICatalogReader`, and `CatalogReader` and `ScriptGenerator` in `EngineServices`. Verify with unit tests for the type-name round trip, and for the unknown-type error listing the valid types
- [x] 2.2 Add glob-to-`LIKE` translation, escaping `%`, `_` and `[` before mapping `*` and `?` (design I4). Verify with unit tests for `order*`, `a_b?`, `[x]*` and `100%`
- [x] 2.3 Implement `SqlServerCatalogReader.ListAsync`. It reads `sys.objects` (not shipped), user types, table types and database triggers, with approximate row counts, the schema, type and pattern filters through `LOWER()`, and `COUNT(*) OVER ()` with `TOP (@cap)`. Each command gets the database's timeout. Verify with an integration test against `Introspection/seed.sql` for "List a database", "System objects are left out", "Filter by type and pattern" and "More objects than the cap" (with a small cap), on a case-sensitive collation database too
- [x] 2.4 Add `RunIntrospectionAsync` to `ToolPipeline`, with the same refusal, lookup, grant, available, audit and response order as `RunAsync`. Change `IntrospectionEvent` to `(Tool, Database, Request, DurationMs, Succeeded, Error)` (design I7). Verify with pipeline tests using a fake reader for "Introspection denied" (no reader call, `denied` event), a successful list (`introspection` event without object data), and "Failed introspection" (timeout gives a failed event and the timeout message). `AuditEventShapeTests` must pass with the new shape
- [x] 2.5 Add the `list_objects` tool: its parameters (database, schema, types, pattern), its response `{objects, total, truncated, message?}`, its description (how to filter, and that visibility depends on the login), and its read-only annotation. Register it in `ToolCatalog` when introspection is allowed anywhere. Verify with catalog tests (offered, read-only, not offered when introspection is denied everywhere) and a response-shape test

## 3. describe_object

- [x] 3.1 Parse names with `TSqlParser.ParseSchemaObjectName`, refusing three and four parts, and implement `ResolveAsync`: an exact schema-qualified match, or a unique unqualified match. Otherwise give the ambiguous error (naming each candidate) or the not-found error (suggesting `list_objects`). Verify with unit tests for bracketed and quoted names and three-part refusal, and integration tests for "Ambiguous unqualified name" and "Unknown object"
- [x] 3.2 Implement `DescribeAsync` for tables and views: columns in order (type, nullability, default, identity, computed expression), the primary key, unique and check constraints, indexes, triggers, foreign keys both ways, and the temporal history table and period columns. Verify with integration tests for "Table with keys" and "Temporal table"
- [x] 3.3 Implement `DescribeAsync` for code objects: the definition from `sys.sql_modules`, or `{unavailable: encrypted | no permission}`, plus parameters in order and a function's return type and kind. Verify with integration tests for "Procedure", "Encrypted procedure", a scalar function and an inline table-valued function
- [x] 3.4 Implement `DescribeAsync` for sequences, synonyms, user-defined types and table types. Verify with integration tests for "Synonym", a sequence, a UDT and a table type
- [x] 3.5 Add the `describe_object` tool through `RunIntrospectionAsync`, with empty parts left out of the JSON, its description and its read-only annotation. Verify with a response-shape test (a view gives columns and a definition, and no keys) and a pipeline test that the audit event records the name and no details

## 4. Script generation and completeness

- [x] 4.1 Add `IScriptGenerator` and `Completeness`: inventory minus scripted gives the omitted list, with the reasons `encrypted`, `security principal`, `security object` and `not scripted by the generator` (design I5). Verify with unit tests covering each reason, and an inventory that is fully scripted
- [x] 4.2 Implement `SqlServerCatalogReader.InventoryAsync`: user objects, user types, partition functions and schemes, XML schema collections, user assemblies and full-text catalogs, plus database principals, certificates, keys and database-scoped credentials for the omitted list only. Verify with an integration test against the seed that the user `app_reader` and the encrypted procedure are in the inventory
- [x] 4.3 Implement `SmoScriptGenerator`:
  - the `ServerConnection` gets the database's statement timeout, and the init fields are prefetched;
  - the scripting options are those in design I5;
  - encrypted modules are filtered out first, the rest are ordered with `DependencyWalker`, and `EnumScriptWithList` streams them into a `TextWriter`;
  - it runs on `Task.Run`, and checks cancellation between objects.

  Verify with an integration test that a whole-seed script names every non-omitted object, with each FK target created before the table that references it
- [x] 4.4 Support chosen objects: the walk is filtered down to the resolved set, with no dependencies added. Verify with an integration test for "Two tables" (`dbo.order_lines` chosen together with `dbo.orders` gives only those two, with `orders` first)

## 5. script_database tool, files and init

- [x] 5.1 Add `ScriptFiles` (design I6):
  - the project or fallback `scripts` directory;
  - the safe file name `<id>-<yyyyMMddTHHmmssZ>.sql`, with `-2`, `-3` and so on on a collision;
  - a `.sql.tmp` written then moved with no overwrite, and the temp file deleted on failure;
  - the header comment (identifier, server version, UTC time, sql-mcp version).

  Verify with unit tests for "Script file name", a collision, a failed write leaving no file, and a header holding no connection-string fragment
- [x] 5.2 Replace `EnsureGitIgnore` with `EnsureGitIgnoreLines("logs/", "scripts/")`, which adds the missing lines and keeps the others. Use it from `init`, from the audit log's directory creation, and before writing a script. Verify with unit tests for a missing file, a `logs/`-only file ("Project initialised before this change"), and a file with extra user lines, plus the updated `init` "Fresh project" command test
- [x] 5.3 Add the `script_database` tool through `RunIntrospectionAsync`:
  - names are resolved before anything is written;
  - the response is `{path, bytes, counts, omitted}`, without the script text;
  - the audit event records the path, the counts and the omitted count, and if the audit write fails the file is deleted;
  - the description says schema only, a file at the returned path, omitted objects listed, `GO` separators, and visibility depending on the login;
  - it is annotated read-only (Emmz's decision, 2026-10-26).

  Verify with pipeline tests for "Script denied", "Unknown name in the list", "Result", "Script call", and the audit-failure deletion
- [x] 5.4 Add the round-trip integration test: script the seeded database, create an empty database in the same container, run the script there (split on `GO`), then compare `list_objects` and `describe_object` for every object, leaving out row counts and the omitted objects. Verify that `make itest` passes with "Script rebuilds the schema", "Encrypted procedure", "Database users" and "Failed script leaves no file" (cancelled part way through) covered
- [x] 5.5 Update the README: replace "Introspection tools come in a later release" with a section on the three tools, the scripts directory, and what is omitted. Note SMO as now used in `docs/dependencies.md`. Verify that the README's sample config test still passes and `make gates` is green

## 6. Review fixes: compatibility, design drift and test gaps

- [x] 6.1 Replace `STRING_SPLIT` in `list_objects`' type filter with a match that works at every compatibility level (`STRING_SPLIT` needs level 130). Verify with an integration test that filters by type on a database at compatibility level 120
- [x] 6.2 Bring `design.md` in line with what was built: I3's `clr` reason; I5's options (`Triggers` and `ScriptBatchTerminator` off), the three-part script and how chosen objects take their triggers; I7's event fields (`Kind`, `ErrorNumber`). Verify that `make validate` passes
- [x] 6.3 Fix the "write tool)'s" doc comment in `ToolCatalog.cs`, and delete `SmoSpikeTests` (the round-trip and generator tests now cover it). Verify that `make build` passes
- [x] 6.4 Fill the test gaps: an integration test for a catalog read that times out for real (blocked by uncommitted DDL), and `git status` not showing a script in "Project initialised before this change" (already covered by `AuditGitIgnoreTests`; nothing to add). Verify that `make gates` is green

## 7. Review fixes: correctness (code-review, 2026-10-28)

- [x] 7.1 Validate arguments after the shared checks: `RunIntrospectionAsync` takes a validate step that runs after audit-ok, database resolution, the grant check (with its `denied` event) and availability, as design I7 orders them. A null inside `types` is refused with the "not an object type" message, not thrown. Verify with pipeline tests: a bad name or type on a denied database is audited as denied; with the audit log unavailable, the refusal comes first; `types: ["table", null]` gives the error
- [x] 7.2 Group `describe_object`'s foreign keys by `object_id`, not by name, and order them by it. Verify with an integration test in which keys in two schemas share a name and both reference one table
- [x] 7.3 Tell a sys.objects object and a type with the same `schema.name` apart: an optional `type` argument on `describe_object`, and a type per entry in `script_database`'s `objects`; the ambiguity message suggests it. Add the spec delta (schema-introspection "describe_object tool"; creation-script "Scripting chosen objects") and update the tool descriptions. Verify with unit tests for the message and an integration test with table `x.address` and table type `x.address`
- [x] 7.4 In a whole-database script, leave out the DML triggers of encrypted views, which are themselves left out, and name them in the omitted list. Verify with a generator integration test: an encrypted view with an INSTEAD OF trigger, and a script that runs on an empty database
- [x] 7.5 For chosen objects, keep the chosen tables' and views' own DML triggers in the narrowed inventory, so an encrypted one is named as omitted (closes the §5 accepted gap; update the README note). Verify with a pipeline test and the generator integration test
- [x] 7.6 Report an encrypted database DDL trigger as `encrypted` in `describe_object` and in the omitted list (closes the §3 accepted gap). First probe the catalog for an encryption signal for database triggers; if there is none, stop and ask. Verify with an integration test seeding an encrypted database trigger, and that `make gates` is green

## 8. Review fixes: performance and cleanup (code-review, 2026-10-28)

- [x] 8.1 Pull out one SQL Server connection helper, shared by `SqlServerCatalogReader` and `SmoScriptGenerator`: the effective connection string, open, and turning failures into `CatalogFailed` (invalid string, connection, timeout -2, SQL), with one timeout constant. Verify that the existing reader and generator failure tests pass unchanged
- [x] 8.2 Find a chosen DML trigger through its parent (the reader resolves `parent_object_id`), not by loading every table's and view's triggers; for a whole database, prefetch triggers instead of one query per parent. Verify that the generator tests and the round trip pass
- [x] 8.3 For chosen objects, build the narrowed inventory from what resolution returns (with encryption and the chosen tables' triggers), instead of running the whole-database inventory. Verify with the §7.5 tests and a pipeline test asserting that `InventoryAsync` isn't called for chosen objects, and that `make gates` is green
- [x] 8.4 Make `script_database`'s `objects` items schema `anyOf: [string, {name, type?}]` instead of `oneOf`, and drop `additionalProperties: false` from it. `ParseObjectsEntry` still refuses extra keys. Both changes are for model APIs with partial JSON Schema support, Gemini especially (Emmz, 2026-10-28). Update the human-verification line to match. Verify with the updated `ToolCatalogTests` schema test, and that `make gates` is green

## 9. Review fixes: chosen triggers and messages (code-review of §7–§8, 2026-10-28)

- [x] 9.1 Script a trigger once when both it and its table or view are chosen: remove duplicate URNs from the generator's trigger list, as `Walk` does for walked candidates. Verify with a generator itest naming `dbo.orders` and `dbo.orders_status` (one `CREATE TRIGGER`, one scripted item), and a round trip of that script
- [x] 9.2 A chosen DML trigger on an encrypted view is left out, as it is in a whole-database script: resolution gives the trigger's parent with its encryption, the generator skips it, and the omitted list names it `on an encrypted view`. Verify with a generator itest
- [x] 9.3 A DML trigger whose table or view resolution can't name (a parent outside the catalog query, such as one marked `is_ms_shipped`) fails with a message that says so, not "was not found when scripting began". Verify with a unit-level generator test using a parentless trigger summary
- [x] 9.4 When the candidates for an ambiguous name include an object and a type with the same name in one schema, the message also says to give `type`, even when other schemas match too. Verify with a `NameResolution` unit test (mixed schemas, plus a table and a table type in one schema)

## 10. Review fixes: batch resolution and types (code-review of §7–§8, 2026-10-28)

- [x] 10.1 Resolve every name in one query: pass the names as a delimited list (no OPENJSON, which needs compatibility level 130), return each candidate tagged with its name's position, and keep `NameResolution.Problem` per name. Verify that the resolve, describe and compatibility-level itests pass unchanged, with a new itest resolving many names, among them duplicates and an ambiguous one
- [x] 10.2 Hold a DML trigger's parent kind as an `ObjectType`, not a wire-name string, so the generator's table-or-view switch is checked by the compiler. Verify with the generator tests
- [x] 10.3 Make `IntrospectionCall<T>` a ready/refused pair, so a call with neither a read nor an error can't be built, and drop `call.Error!`. Verify with the introspection pipeline tests and `make gates`

## Workflow follow-up

- Whole-change self-review, the change-done devlog note, and an offer of `code-review` before archiving.
- Human verification: register the server with real Claude Code, run `list_objects`, `describe_object` and `script_database` against a real database, and confirm that the client treats all three as read-only. In Claude Code, Gemini, Codex and OpenCode, also check that the server loads, and make a `script_database` call with an `objects` list that mixes a plain name and a `{"name": …, "type": …}` entry: its items schema is `anyOf: [string, {name, type?}]` without `additionalProperties` (§7.3, §8.4), chosen because some model APIs (Gemini especially) support JSON Schema only in part.
- `/opsx:archive` once the review is done or declined.
