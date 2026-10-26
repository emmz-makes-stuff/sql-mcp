# Design

## Context

Foundation (archived as `2026-10-26-foundation`) built the read path end to end:
- The classifier already sorts every statement into all five categories (ADR-0002, decision 5).
- `ToolPipeline` runs audit, then database, permission, availability, classification and execution, in that order.
- `SqlServerQueryExecutor` opens a connection without pooling, starts a `ReadCommitted` transaction, streams result
  sets against the row and byte caps with `SequentialAccess`, and always rolls back.
- `ResultStore` holds pages behind handles.

What's missing for writes:
- **The command, DDL and execute allowlists are seeds only.** Command has no joins, CTEs, `OUTPUT` or functions.
  DDL covers `CREATE`/`DROP TABLE`, `ALTER TABLE … ADD` and `TRUNCATE`. Execute takes only literal parameters.
- **Nothing commits.** The executor's one mode is "roll back".
- **Refusals for an unoffered category** say "this server has no command tool yet". That stops being true here.

Motivation is in proposal.md. The behaviour is in the four delta specs.

## Goals / Non-Goals

**Goals:**
- One execution path for reads and writes. They differ only in the final commit or rollback, in how a result cap is
  handled, and in the affected-rows check, so the connection, transaction, streaming and value code are not
  duplicated.
- All or nothing holds even for T-SQL errors that SQL Server treats as statement-level by default.
- The allowlists stay test-built (foundation D6). Every type added to an allowlist comes with a classifier table
  row that needs it.

**Non-Goals:**
- Stopping a single statement part-way when it passes the affected-rows cap. SQL Server only reports a count once
  the statement completes, so the cap rolls back after the fact.
- Getting row counts that a procedure hides with `SET NOCOUNT ON` (see Risks).
- A CLI command for limits. Limits are still edited in the config file. `approve`, `config show` and
  `list_databases` already handle limits generically.

## Decisions

### W1. One batch runner, two modes
- `IQueryExecutor.ExecuteAsync` gains a `BatchMode`: `ReadOnly` (today's behaviour) or `Write`. Both modes return
  the same outcome types. `QuerySucceeded` gains `RowsAffected` (a list, empty for reads).
- In `Write` mode, the executor:
  - runs the batch;
  - drains results (W3);
  - checks the cap (W4);
  - commits, and returns `QuerySucceeded` only once the commit returns.
  - If the commit fails, it returns `QueryFailed`.
- *Rejected:* a separate `IWriteExecutor`. It would duplicate opening the connection, starting the transaction,
  streaming values and rolling back, which §14 and §15 of foundation took care to get right.

### W2. All or nothing: `SET XACT_ABORT ON`
- Before a write batch, the executor runs `SET XACT_ABORT ON` on the same connection, as a separate command. The
  classifier rejects `SET`, so an agent can't turn it off.
- With it on, any run-time error dooms the transaction and aborts the batch. Without it, errors such as a
  constraint violation fail only their statement, and the batch carries on.
- On any `SqlException`, the executor rolls back if the transaction is still alive, using the existing
  `RollBackAsync`, which tolerates a transaction that is already gone. The agent is told that nothing was committed.
- *Rejected:* relying only on "any exception, then roll back". SqlClient sometimes raises a statement-level error
  only when the reader moves on, after later statements have already run. Rolling back still undoes them, but
  `XACT_ABORT` also stops them running at all.

### W3. A capped result drains instead of cancelling
- For a read, hitting the row or byte cap cancels the command (foundation's 2026-10-25 decision).
- For a write, cancelling would abort the batch. So in `Write` mode, once a cap is hit, the executor stops
  buffering and keeps calling `ReadAsync`/`NextResultAsync` without reading values, until the batch completes.
- `SequentialAccess` means skipped columns are never pulled into memory. The command timeout still bounds the
  drain.
- Cancellation stays for exceptions and for the affected-rows cap (W4).

### W4. Counting affected rows
- `SqlCommand.StatementCompleted` gives the `RecordCount` of each completed statement, including statements inside
  procedures and triggers, unless `NOCOUNT` hides them. The runner adds each count to `RowsAffected` as it arrives.
- As soon as the running total passes `maxAffectedRows`, the runner cancels the command, so later statements never
  run. It then rolls back and returns `QueryFailed` with the new `FailureKind.AffectedRowsCap`.
- The failure message gives the cap and the total, and says to narrow the `WHERE` clause or split the change.
- `TRUNCATE` reports no count, so it never trips the cap. It is DDL, granted separately.
- A row count arrives only when the server flushes its output: a full packet, or the end of the batch. A batch with
  little output has usually run in full by then, so the cancel stops later statements only when the output was
  flushed early (a large result, or a procedure's `RAISERROR … WITH NOWAIT`). The rollback undoes it either way.
  The cancel is sent from inside the `StatementCompleted` handler: from another thread it waits until the batch
  ends (2026-10-26 §5).
- A `SELECT` reports its count just like a change does, and SqlClient doesn't say which kind of statement a count
  came from. So in `execute`, rows a procedure's `SELECT` returns count in `RowsAffected` and towards the cap
  (Emmz's ruling, 2026-10-26). This errs on the strict side. In `command`, the only result sets come from
  `OUTPUT`, whose count is the change's own.
- *Rejected:*
  - `SET ROWCOUNT`, which silently does part of the change;
  - counting with a dry run first, which doubles the cost and races with other writers.

### W5. Classifier: per-category walk, with the query rules reused
- **Command** = the query allowlist plus the DML nodes: targets, `OUTPUT`/`OUTPUT INTO`, `MERGE` actions, the
  `NEXT VALUE FOR` expression, and table hints.
  - `SideEffectChecks` becomes per category. `SELECT … INTO` and the rowset functions (`OPENQUERY`, `OPENROWSET`,
    `OPENDATASOURCE`, ad hoc names) stay rejected everywhere.
  - Locking hints and `NEXT VALUE FOR` are allowed in a command, because a write takes those locks and advances
    sequences anyway.
- **WHERE rule:** an `UpdateSpecification` or `DeleteSpecification` with a null `WhereClause` is rejected with
  "UPDATE needs a WHERE clause" or "DELETE needs a WHERE clause". `WHERE CURRENT OF` is a cursor, and stays
  rejected, because cursors aren't on any allowlist.
  - A target that names a CTE of the same statement is exempt, because the CTE's query does the filtering
    (2026-10-26 decision; the "CTE and OUTPUT" scenario needs it). The affected-rows cap still applies. The check
    sits on `UpdateStatement`/`DeleteStatement`, which hold the CTEs.
- **Names rule:** in the write categories, a `SchemaObjectName` with a `DatabaseIdentifier` or `ServerIdentifier`
  is rejected. So is a column reference with four or more parts. Queries are unchanged (see Risks).
- **DDL**:
  - table, column, constraint, index, view, schema and sequence statements, plus `TRUNCATE`;
  - defaults, `CHECK` constraints and computed columns reuse the query's scalar-expression allowlist;
  - a view's `SelectStatement` is walked under the query category, so a view can't hide what a query couldn't do;
  - `CREATE SCHEMA` is accepted only without schema elements and without `AUTHORIZATION`, because ownership is a
    security change. `ALTER SCHEMA … TRANSFER` is rejected for the same reason: through ownership chaining, the
    move can change who may use the object (2026-10-26).
  - Defaults may use `NEXT VALUE FOR`, which a default advances only on insert. `ALTER TABLE … [NO]CHECK
    CONSTRAINT` and `ALTER INDEX … REBUILD | REORGANIZE | DISABLE` (with no `WITH` options) are accepted
    (2026-10-26).
  - `CreateProcedureStatement`, `CreateFunctionStatement`, `CreateTriggerStatement` and their `ALTER`/`CREATE OR
    ALTER` forms get a dedicated reason through `Phrases`. Without it they would fall through as generic
    unrecognised syntax.
- **Execute:**
  - The procedure name must have no database or server part (the names rule). Its schema must not be `sys`, and
    its base name must not start with `sp_` or `xp_`, ignoring case.
  - Parameters may be literals, `NULL`, or a `UnaryExpression` negating a numeric literal. Variables stay
    rejected, because `DECLARE` is rejected.
- Each new node type joins an allowlist only with a `ClassifierTable` row that needs it, and `docs/classifier.md`
  gets a "write categories" section.

### W6. Wrong-tool reasons per database
- `ClassificationResult.ForTool(tool, withTools)` keeps its shape. The pipeline now passes the set of categories
  whose group is allowed **on the target database**, instead of the static `ToolCatalog.Categories`.
- The "no tool yet" wording becomes "the <name> tool is denied on this database".
- `ToolCatalog.Categories` is removed.

### W7. Tools, annotations and responses
- `ToolCatalog.Build` adds `command`, `ddl` and `execute`, each only if its group is allowed somewhere. They are
  annotated `ReadOnly = false`, `Destructive = true`, `Idempotent = false`, `OpenWorld = false`.
- `next_page` and `abandon` are offered whenever any tool that can return rows is offered (`query`, `command` or
  `execute`).
- The pipeline's query flow becomes `RunAsync(tool, category, group, mode, …)`, and `QueryAsync` and the three
  write methods call it, so the audit order is the same for every tool.
- The response is the query response shape plus `committed: true` and `rows_affected: [3, 5]`. It has `pages` and a
  handle only when there are result sets.
- Descriptions are constants with the spec's guidance, and the cap is added when the tool is registered, as for
  `query`.

### W8. Limit and audit plumbing
- `maxAffectedRows`:
  - in `LimitSettings`, `EffectiveLimits` (default 1000) and the schema (`integer`, 1 to Int32.MaxValue);
  - in `ProjectLimit` layering, as a widening when it goes up;
  - in `approve`'s diff, in `config show`, and in `LimitsInfo` for `list_databases`.
- `ExecutedEvent` gains `RowsAffected` (a list, omitted for reads).
- `FailedEvent` gains `FailureKind.AffectedRowsCap`, plus nullable `Cap` and `Affected` counts. Counts carry no row
  values, so `AuditEventShapeTests` still holds.

## Risks / Trade-offs

- **[A `NOCOUNT ON` procedure hides its row counts, so the cap can't see them]** → Accepted. An `execute` grant
  trusts the database's procedures, as ADR-0002 already trusts what a procedure does. The `execute` description
  says the cap counts every row count the server reports, including rows a procedure's `SELECT` returns.
- **[The cap prevents permanent damage, not disruption]**
  - SQL Server reports a statement's row count only once the statement completes, so a mistaken 10-million-row
    `DELETE` runs in full before the cap can roll it back.
  - While it runs and while it rolls back, it holds locks (often escalated to the whole table) and grows the
    transaction log.
  - The command timeout bounds the forward work but not the rollback: SQL Server must undo everything it did, which
    can take about as long again, and no client timeout stops that.
  - On a shared database that is an outage, even though nothing is lost.
  - Nothing on the client side can prevent it: there is no reliable row count before execution, and rewriting the
    agent's SQL (adding `TOP`) would change its meaning.
  - → Mitigations:
    - the WHERE rule;
    - the descriptions tell the agent to count matching rows with `query` first, and say that an oversized change
      runs in full before it is undone;
    - the README advises keeping `timeoutSeconds` short on databases that allow `command`.
- **[A procedure that runs its own `ROLLBACK` ends our transaction, and its later statements commit on their
  own]** → `XACT_ABORT` aborts the batch at error 266 when the procedure returns. Anything the procedure did after
  its own `ROLLBACK` is inside the trusted procedure. Documented in `docs/classifier.md`. The agent is told the
  outcome is unknown, never that nothing was committed (code-review fix 10.1): on error 266, 3902 or 3903, or when
  the commit finds the transaction gone. A failed commit is reported the same way.
- **[`WHERE 1 = 1` gets past the WHERE rule]** → Intended. The rule catches a forgotten filter, not a deliberate
  one (ADR-0002's threat model). The affected-rows cap still applies. The rejection reason asks for a filter that
  picks the rows to change, so an agent isn't nudged towards an always-true one (task 3.5, 2026-10-26). Blocking
  tautologies was rejected as an arms race: `WHERE id > 0` does the same.
- **[Queries can still read other databases with three-part names]** → Out of scope here: the specs limit the names
  rule to writes. A follow-up can decide whether `query` should keep within the target database too. A view's
  SELECT is held to the names rule, because creating the view is a write (code-review fix 10.2).
- **[A three-part scalar function call, `otherdb.dbo.fn(x)`, gets past the names rule in a write]** → Accepted
  (2026-10-26). ScriptDom gives it the same shape as an XML or CLR method call on a column (`n.c.value(...)`,
  `geo.STDistance(...)`), so syntax can't tell them apart. A scalar function can only read, which `query` allows
  anyway. Tables, views, table-valued functions, sequences and procedures with a database or server part are all
  caught. `docs/classifier.md` records the gap (task 3.4).
- **[DDL inside the transaction can block other sessions on schema locks for the length of the call]** → The call
  is short, and the timeout bounds it.

## Migration Plan

- This is additive. Existing configs stay valid, and `maxAffectedRows` is optional.
- The only change visible to agents is in refusal text: "this server has no command tool yet" becomes "classified
  as command; the command tool is denied on this database" (or "use the command tool").
- Rollback is the previous release. No stored state changes shape.
