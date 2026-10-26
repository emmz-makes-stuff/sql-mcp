# Proposal

## Why

Foundation shipped the `query` tool. Permissions for `command`, `ddl` and `execute` exist in config but no tool
runs them, so an agent that needs to change data or schema still falls back to an unconstrained CLI, which is
exactly what sql-mcp is meant to replace. The classifier, permissions, approval and audit are already in place, so
this change only adds the three write tools and the guards a write needs and a read didn't.

## What Changes

- **New `command`, `ddl` and `execute` tools.** Each is offered only when its group is allowed on at least one
  database. Each checks the permission again, classifies the batch, and runs it only if every statement is in the
  tool's category.
- **Writes commit, all or nothing.** Each call runs in one transaction. It commits only if every statement succeeds.
  An error, a timeout or the affected-rows cap rolls the whole batch back.
- **Affected-rows cap.** A new per-database limit, `maxAffectedRows` (default 1000), layered and approved like the
  other limits. A batch that affects more rows is rolled back, and the agent is told to narrow it.
- **Results from writes.** Each call reports the rows affected per statement. Result sets (from `OUTPUT` clauses,
  or `SELECT`s in a procedure) come back through the same capped buffer and paging as `query`.
- **Classifier allowlists widen to real-world T-SQL:**
  - command: joins, CTEs, subqueries, expressions and functions, `OUTPUT`, `NEXT VALUE FOR`, and table hints;
  - ddl: declarative objects only, meaning tables, columns, constraints, indexes, views, schemas and sequences,
    plus `TRUNCATE`;
  - execute: procedure calls with literal, `NULL`, negative and named parameters.
- **New classifier refusals for write tools:**
  - `UPDATE` and `DELETE` without a `WHERE` clause are rejected;
  - procedures, functions and triggers are not `ddl`, because their bodies run later, unseen by the classifier;
  - `execute` refuses system and extended procedures: the `sys` schema, and names starting `sp_` or `xp_`;
  - write tools refuse three- and four-part names, which point at another database or a linked server.
- **Wrong-tool messages** name the tool, and say when that tool's group is denied on the database. "This server has
  no command tool yet" goes away.
- **`list_databases`** shows the affected-rows cap.
- **Audit:** `executed` means committed, and it records the rows affected per statement. A rolled-back write is
  `failed`, with the reason it was rolled back.

## Capabilities

### New Capabilities
- `write-execution`: the `command`, `ddl` and `execute` tools. Covers committing all or nothing, the affected-rows
  cap, rows affected and result sets, timeouts, and their tool descriptions.

### Modified Capabilities
- `statement-classification`: what each write category accepts, the new refusals (no `WHERE`, programmable
  objects, system procedures, other-database names), and wrong-tool messages now that every category has a tool.
- `permissions`: `list_databases` reports the affected-rows cap.
- `audit-log`: what `executed` and `failed` mean for a write.

## Impact

- **Code:**
  - `Mcp/` (`ToolCatalog`, `ToolPipeline`, `ToolResponses`);
  - `Execution/` (a write path beside `IQueryExecutor`, plus SQL Server's implementation);
  - `Classification/SqlServer/` (allowlists, `SideEffectChecks`, `Phrases`);
  - `Permissions/` (`EffectiveLimits`), `Configuration/` (`maxAffectedRows` in the model, loader and schema);
  - `Audit/` (the events).
- **Tests:** the classifier statement table grows the most. Integration tests cover commit, rollback, the cap, and
  `OUTPUT` and procedure result sets against SQL Server 2025.
- **Config:** one new optional limit. Existing files stay valid. There are no new dependencies.
- **Docs:** the README's tool and limit sections, and `docs/classifier.md` (write categories).
- **Out of scope:**
  - procedures, functions and triggers as DDL;
  - output parameters and return values from `execute` (they need `DECLARE`, which is rejected);
  - functions run through `execute` (call them from `query`);
  - `SELECT … INTO`, which stays rejected;
  - database-level and security DDL (`CREATE DATABASE`, logins, users, roles);
  - introspection, which is change 3.
