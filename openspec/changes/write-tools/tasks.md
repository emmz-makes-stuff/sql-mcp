# Tasks

## 1. Affected-rows cap in configuration

- [x] 1.1 Add `maxAffectedRows` to the schema (`integer`, 1 to 2147483647, with a description giving the default of 1000), to `LimitSettings`, `ConfigLoader.MapLimits` and `EffectiveLimits` (default 1000), and to `PermissionResolver`'s project-limit layering, where raising it is a widening (design W8). Verify with loader tests (valid value, 0, too large) and resolver tests for the "Project raises the cap" scenario
- [x] 1.2 Show the cap in `approve`'s diff (`maxAffectedRows 1000 → 5000`), in `config show`'s limits line, and in `list_databases`' limits. Verify with approve and `config show` command tests, and a pipeline test for the "Affected-rows cap is listed" scenario
- [x] 1.3 Document `maxAffectedRows` in the README's limits section and sample config. Verify the sample still loads, through the existing README sample test or a new loader test of that text

## 2. Classifier: command statements

- [x] 2.1 Make `SideEffectChecks` per category (design W5): `SELECT … INTO` and the rowset functions stay rejected everywhere, while locking hints and `NEXT VALUE FOR` are allowed in a command. Verify that every existing query row in `ClassifierTable` still passes, plus new rows for the "Sequence in an insert" scenario
- [x] 2.2 Extend the command allowlist with the query read allowlist plus joins, CTEs, subqueries, expressions and functions, `OUTPUT`/`OUTPUT INTO`, `MERGE` with `WHEN NOT MATCHED BY SOURCE`, and table hints. Each type comes with the table row that needs it. Verify with rows for "Update through a join", "CTE and OUTPUT", and a corpus of INSERT…SELECT, UPDATE…FROM, DELETE with a subquery, and MERGE
- [x] 2.3 Reject UPDATE and DELETE without a `WHERE` clause, with the reason "UPDATE needs a WHERE clause" or "DELETE needs a WHERE clause". Verify with rows for "Delete everything" and "Update with a filter", and a MERGE row that stays accepted
- [x] 2.4 Reject three- and four-part object names, and column references of four or more parts, in the command, ddl and execute categories, with the reason that writes may only name objects in the target database. Queries are unchanged. Verify with rows for "Write to another database" and a query row reading `otherdb.dbo.t` that stays accepted

## 3. Classifier: DDL and execute

- [x] 3.1 Extend the DDL allowlist to create, alter and drop tables, columns, constraints (including defaults, CHECK and computed columns, using the scalar-expression allowlist), indexes, views, schemas and sequences. Walk a view's SELECT under the query category. Accept `CREATE SCHEMA` only without elements or `AUTHORIZATION`. Verify with rows for "Index and view", a view whose SELECT has `WITH (UPDLOCK)` (rejected), and the ALTER TABLE forms
- [x] 3.2 Give procedures, functions and triggers (`CREATE`, `ALTER`, `CREATE OR ALTER`) the reason "procedures, functions and triggers can't be created through sql-mcp" through `Phrases`. Reject database- and security-level DDL. Verify with rows for "Procedure body is not inspected, so it is refused" and "Security object"
- [x] 3.3 Restrict execute to user procedures: no `sys` schema, no names starting `sp_`/`xp_` (ignoring case), and parameters that are literals, `NULL` or negated numbers, by position or by name. Verify with rows for "User procedure" and "System procedure", plus a variable-parameter row that stays rejected
- [x] 3.4 Add a "Write categories" section to `docs/classifier.md`. It covers what each write category accepts, the WHERE, names and system-procedure rules, why programmable objects are refused, and the procedure trust gap (NOCOUNT, a procedure's own ROLLBACK). Update `AllowlistGuardTests` if new top-level statement types were added. Verify that `AllowlistGuardTests` passes and the doc's examples match table rows
- [x] 3.5 Reword the WHERE-rule reasons so they ask for a filter that picks the rows to change, e.g. "DELETE needs a WHERE clause that picks the rows to change (at line 1, column 1)", without suggesting or naming an always-true filter (2026-10-26 decision, after a side-agent note that `WHERE 1 = 1` passes the rule). Verify with the existing "Delete everything" and UPDATE rows, updated to the new wording

## 4. Wrong-tool reasons per database

- [x] 4.1 Make the pipeline pass `ForTool` the categories whose group is allowed on the target database. Replace "this server has no X tool yet" with "the X tool is denied on this database", and remove `ToolCatalog.Categories` (design W6). Verify with pipeline tests for "Wrong tool" and "Right tool is denied here"

## 5. Write execution

- [ ] 5.1 Add `BatchMode` (`ReadOnly`, `Write`) to `IQueryExecutor.ExecuteAsync`, and `RowsAffected` to `QuerySucceeded`. Read-only behaviour is unchanged. Verify that the existing executor integration tests pass unchanged
- [ ] 5.2 In `Write` mode: run `SET XACT_ABORT ON` first, collect `RowsAffected` from `StatementCompleted`, commit after every result is read, and on any failure roll back and say nothing was committed (design W1, W2). Verify with integration tests for "Successful command", "Error part-way through a batch" (with a constraint violation in statement 2), "Timeout", and a check from a second connection that a commit is visible and a rollback isn't
- [ ] 5.3 In `Write` mode, drain rather than cancel when a result set reaches the row or byte cap, so the batch completes and commits marked truncated (design W3). Verify with an integration test for "Large OUTPUT" (row cap 1000, 1500 rows through OUTPUT, all committed)
- [ ] 5.4 Enforce `maxAffectedRows`: cancel as soon as the running total passes the cap, roll back, and return `QueryFailed` with `FailureKind.AffectedRowsCap`, the cap and the total (design W4). Verify with integration tests for "Over the cap" and "Within the cap", and a two-statement batch where the second statement passes the cap and the first is rolled back too
- [ ] 5.5 Verify result sets from writes with integration tests for the "OUTPUT clause" and "Procedure result set" scenarios. The procedure is created in the test container's setup, not through the ddl tool

## 6. Tools, responses and audit

- [ ] 6.1 Factor the pipeline's query flow into one `RunAsync` (tool, category, group, mode), and add the `command`, `ddl` and `execute` methods on top of it, so every tool audits in the same order. Verify that existing pipeline tests pass, plus new ones for "Statement of another category" and "Group denied" on each write tool
- [ ] 6.2 Extend the audit events. `ExecutedEvent` gains `RowsAffected` for writes. `FailedEvent` gains `FailureKind.AffectedRowsCap` and nullable `Cap` and `Affected`. A write is logged `executed` only once committed. Verify with pipeline tests for "Committed write" and "Write rolled back by the cap", and `AuditEventShapeTests` still passing
- [ ] 6.3 Shape the write response: the query response plus `committed` and `rows_affected`, with pages and a handle only when there are result sets. Error text says nothing was committed. Verify with response tests for a write with and without result sets
- [ ] 6.4 Register the write tools in `ToolCatalog`, each only when its group is allowed somewhere, annotated not read-only, destructive, not idempotent and not open-world. The descriptions say all or nothing, give the affected-rows cap and ask for narrow changes. They also say that a change over the cap runs in full before it is undone, so the agent should count matching rows with `query` first (design Risks). `execute`'s description says the cap counts only rows the server reports. Offer `next_page` and `abandon` whenever `query`, `command` or `execute` is offered. Verify with catalog tests for "Client sees a destructive tool" and the permissions spec's "No database allows command"
- [ ] 6.5 Update the README: the tools section (command, ddl and execute, all or nothing, the cap, the WHERE rule, what DDL and execute accept) and the default-grants text. Add the advice to keep `timeoutSeconds` short on databases that allow `command`, because the cap undoes an oversized change only after it has run, and its rollback holds locks and isn't bounded by the timeout. Verify that the README's statements match the tool descriptions and the specs

## 7. End-to-end check

- [ ] 7.1 Through the real MCP server over stdio in an integration test, with `command`, `ddl` and `execute` allowed on the test container: create a table with `ddl`, insert and update with `command`, run a procedure with `execute`, and read the result with `query`. Check that the audit log has the matching `executed` events with rows affected. Verify that the test passes in `make itest`
- [ ] 7.2 **Human verification (stop and ask):** in a real Claude Code session registered with `sql-mcp register claude --scope project`, with `command` allowed on a scratch database, have the agent update rows, hit the WHERE rule and the affected-rows cap, and confirm Claude Code treats the write tools as destructive. Record the result in a devlog note

## Workflow follow-up

- Run the whole-change review and offer `code-review` before archiving.
- Archive the change, then propose `introspection`.
- Consider whether `query` should also refuse other-database names (design Risks). That would be a separate change.
