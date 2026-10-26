# write-execution Specification

## Purpose

Lets an agent change data and schema through the `command`, `ddl` and `execute` tools, one granted category at a time. Each batch commits all or nothing, and the number of rows it may change is bounded.

## Requirements

### Requirement: Write tools
The system SHALL provide `command`, `ddl` and `execute` tools. Each takes a database identifier and SQL text, checks its own group's permission, classifies the SQL, and runs it only if every statement is in the tool's category.

#### Scenario: Successful command
- **WHEN** `command` is allowed on `localhost:1433/app` and an agent calls `command` with `UPDATE dbo.users SET active = 0 WHERE id = 7`
- **THEN** the update is committed, and the agent receives the rows affected by each statement

#### Scenario: Statement of another category
- **WHEN** an agent calls `command` with `DROP TABLE dbo.users`
- **THEN** nothing is sent to the database, and the error gives the classification and the reason

#### Scenario: Group denied
- **WHEN** `ddl` is denied on `localhost:1433/app` and an agent calls `ddl` on it
- **THEN** no connection is opened, and the error says `ddl` is denied there and why

### Requirement: Writes commit all or nothing
Each write tool call SHALL run in one transaction. The transaction SHALL be committed only if every statement in the batch succeeds; otherwise the whole batch SHALL be rolled back, and the agent SHALL be told that nothing was changed.

#### Scenario: Error part-way through a batch
- **WHEN** an agent calls `command` with `INSERT INTO t VALUES (1); INSERT INTO t VALUES ('not a number')` and the second statement fails
- **THEN** the first insert is rolled back too, and the error says that no change was committed

#### Scenario: Timeout
- **WHEN** a write runs past the database's command timeout
- **THEN** it is cancelled and rolled back, and the error says it timed out and that nothing was changed

### Requirement: An unknown outcome is not reported as rolled back
When sql-mcp can't know whether a write batch was committed, because a procedure in it ended sql-mcp's transaction with its own COMMIT or ROLLBACK, or because the commit itself failed, the agent SHALL be told that the outcome is unknown and to check with `query` before running it again, never that nothing was changed.

#### Scenario: A procedure ends the transaction
- **WHEN** an agent calls `execute` on a procedure that inserts a row and then runs `ROLLBACK` or an extra `COMMIT`
- **THEN** the error says some of the batch may have been committed and to check with `query` before running it again, and the audit records that the outcome is unknown

### Requirement: Affected-rows cap
Each database SHALL have an affected-rows cap, `maxAffectedRows` (default 1000), configured and layered like the other limits. A write batch whose statements together affect more rows than the cap SHALL be rolled back, and the agent SHALL be told the cap and to narrow the change. Every row count the server reports SHALL count towards the cap, including the rows a procedure's `SELECT` returns.

#### Scenario: Over the cap
- **WHEN** the cap is 1000 and an agent calls `command` with an `UPDATE` whose `WHERE` matches 5000 rows
- **THEN** the update is rolled back, and the error gives the cap, the rows the batch affected, and says to narrow the `WHERE` clause or split the change

#### Scenario: Within the cap
- **WHEN** the cap is 1000 and a `command` batch affects 999 rows in total
- **THEN** it is committed

#### Scenario: A procedure's SELECT counts
- **WHEN** the cap is 1000 and an agent calls `execute` on a procedure that updates 600 rows and then selects them
- **THEN** the batch is rolled back, because the update and the select together report 1200 rows

#### Scenario: Project raises the cap
- **WHEN** the project file raises `maxAffectedRows` and has not been approved
- **THEN** the lower cap still applies, and `list_databases` says the raise is waiting for approval

### Requirement: Write results
A write tool SHALL return the rows affected by each statement. Any result sets the batch produces (from an `OUTPUT` clause, or a `SELECT` in a procedure) SHALL be returned with the same row cap, byte cap, paging and handles as `query`.

#### Scenario: OUTPUT clause
- **WHEN** an agent calls `command` with `DELETE FROM dbo.jobs OUTPUT deleted.id WHERE done = 1` and 3 rows are deleted
- **THEN** the result reports 3 rows affected, and returns the 3 deleted ids as a result set

#### Scenario: Procedure result set
- **WHEN** an agent calls `execute` with `EXEC dbo.archive_orders @before = '2026-01-01'` and the procedure ends with a `SELECT`
- **THEN** the result returns that result set, paged like a query result

### Requirement: A truncated result does not cancel the write
When a result set from a write reaches the row or byte cap, the remaining rows SHALL be read and discarded so the batch runs to completion. The write SHALL still commit, and the result SHALL be marked truncated.

#### Scenario: Large OUTPUT
- **WHEN** the row cap is 1000 and a committed `command` returns 1500 rows through `OUTPUT`
- **THEN** 1000 rows are buffered, the result is marked truncated, and all 1500 changes are committed

### Requirement: Write tool descriptions
Each write tool's description SHALL say that the batch commits all or nothing, give the affected-rows cap, and tell the agent to make each change as narrow as it can. The write tools SHALL be marked to MCP clients as not read-only and as destructive.

#### Scenario: Client sees a destructive tool
- **WHEN** a client lists the server's tools and `command` is allowed somewhere
- **THEN** the `command` tool is annotated as not read-only and destructive, and its description states the affected-rows cap
