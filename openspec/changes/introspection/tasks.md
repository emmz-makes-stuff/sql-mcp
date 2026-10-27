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

- [ ] 3.1 Parse names with `TSqlParser.ParseSchemaObjectName`, refusing three and four parts, and implement `ResolveAsync`: an exact schema-qualified match, or a unique unqualified match. Otherwise give the ambiguous error (naming each candidate) or the not-found error (suggesting `list_objects`). Verify with unit tests for bracketed and quoted names and three-part refusal, and integration tests for "Ambiguous unqualified name" and "Unknown object"
- [ ] 3.2 Implement `DescribeAsync` for tables and views: columns in order (type, nullability, default, identity, computed expression), the primary key, unique and check constraints, indexes, triggers, foreign keys both ways, and the temporal history table and period columns. Verify with integration tests for "Table with keys" and "Temporal table"
- [ ] 3.3 Implement `DescribeAsync` for code objects: the definition from `sys.sql_modules`, or `{unavailable: encrypted | no permission}`, plus parameters in order and a function's return type and kind. Verify with integration tests for "Procedure", "Encrypted procedure", a scalar function and an inline table-valued function
- [ ] 3.4 Implement `DescribeAsync` for sequences, synonyms, user-defined types and table types. Verify with integration tests for "Synonym", a sequence, a UDT and a table type
- [ ] 3.5 Add the `describe_object` tool through `RunIntrospectionAsync`, with empty parts left out of the JSON, its description and its read-only annotation. Verify with a response-shape test (a view gives columns and a definition, and no keys) and a pipeline test that the audit event records the name and no details

## 4. Script generation and completeness

- [ ] 4.1 Add `IScriptGenerator` and `Completeness`: inventory minus scripted gives the omitted list, with the reasons `encrypted`, `security principal`, `security object` and `not scripted by the generator` (design I5). Verify with unit tests covering each reason, and an inventory that is fully scripted
- [ ] 4.2 Implement `SqlServerCatalogReader.InventoryAsync`: user objects, user types, partition functions and schemes, XML schema collections, user assemblies and full-text catalogs, plus database principals, certificates, keys and database-scoped credentials for the omitted list only. Verify with an integration test against the seed that the user `app_reader` and the encrypted procedure are in the inventory
- [ ] 4.3 Implement `SmoScriptGenerator`:
  - the `ServerConnection` gets the database's statement timeout, and the init fields are prefetched;
  - the scripting options are those in design I5;
  - encrypted modules are filtered out first, the rest are ordered with `DependencyWalker`, and `EnumScriptWithList` streams them into a `TextWriter`;
  - it runs on `Task.Run`, and checks cancellation between objects.

  Verify with an integration test that a whole-seed script names every non-omitted object, with each FK target created before the table that references it
- [ ] 4.4 Support chosen objects: the walk is filtered down to the resolved set, with no dependencies added. Verify with an integration test for "Two tables" (`dbo.order_lines` chosen together with `dbo.orders` gives only those two, with `orders` first)

## 5. script_database tool, files and init

- [ ] 5.1 Add `ScriptFiles` (design I6):
  - the project or fallback `scripts` directory;
  - the safe file name `<id>-<yyyyMMddTHHmmssZ>.sql`, with `-2`, `-3` and so on on a collision;
  - a `.sql.tmp` written then moved with no overwrite, and the temp file deleted on failure;
  - the header comment (identifier, server version, UTC time, sql-mcp version).

  Verify with unit tests for "Script file name", a collision, a failed write leaving no file, and a header holding no connection-string fragment
- [ ] 5.2 Replace `EnsureGitIgnore` with `EnsureGitIgnoreLines("logs/", "scripts/")`, which adds the missing lines and keeps the others. Use it from `init`, from the audit log's directory creation, and before writing a script. Verify with unit tests for a missing file, a `logs/`-only file ("Project initialised before this change"), and a file with extra user lines, plus the updated `init` "Fresh project" command test
- [ ] 5.3 Add the `script_database` tool through `RunIntrospectionAsync`:
  - names are resolved before anything is written;
  - the response is `{path, bytes, counts, omitted}`, without the script text;
  - the audit event records the path, the counts and the omitted count, and if the audit write fails the file is deleted;
  - the description says schema only, a file at the returned path, omitted objects listed, `GO` separators, and visibility depending on the login;
  - it is annotated read-only (Emmz's decision, 2026-10-26).

  Verify with pipeline tests for "Script denied", "Unknown name in the list", "Result", "Script call", and the audit-failure deletion
- [ ] 5.4 Add the round-trip integration test: script the seeded database, create an empty database in the same container, run the script there (split on `GO`), then compare `list_objects` and `describe_object` for every object, leaving out row counts and the omitted objects. Verify that `make itest` passes with "Script rebuilds the schema", "Encrypted procedure", "Database users" and "Failed script leaves no file" (cancelled part way through) covered
- [ ] 5.5 Update the README: replace "Introspection tools come in a later release" with a section on the three tools, the scripts directory, and what is omitted. Note SMO as now used in `docs/dependencies.md`. Verify that the README's sample config test still passes and `make gates` is green

## Workflow follow-up

- Whole-change self-review, the change-done devlog note, and an offer of `code-review` before archiving.
- Human verification: register the server with real Claude Code, run `list_objects`, `describe_object` and `script_database` against a real database, and confirm that the client treats all three as read-only.
- `/opsx:archive` once the review is done or declined.
