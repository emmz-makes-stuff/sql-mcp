# Spec Delta

## MODIFIED Requirements

### Requirement: Statement categories
Each SQL statement SHALL be classified into exactly one of: `query` (SELECT), `command` (INSERT, UPDATE, DELETE, MERGE), `ddl` (CREATE, ALTER, DROP, TRUNCATE), `execute` (EXECUTE of a stored procedure or function), or `rejected`.

#### Scenario: Each category is recognised
- **WHEN** the classifier is given `SELECT 1`, `UPDATE t SET a = 1 WHERE id = 2`, `ALTER TABLE t ADD c int`, `TRUNCATE TABLE t` and `EXEC dbo.p`
- **THEN** they are classified as query, command, ddl, ddl and execute respectively

#### Scenario: Out-of-scope statements are rejected
- **WHEN** the classifier is given `GRANT SELECT ON t TO u`, `DENY ...`, `BACKUP DATABASE ...`, `USE otherdb` or `SET ...`
- **THEN** each is classified as rejected

## ADDED Requirements

### Requirement: Each tool runs only its own category
Each tool SHALL run only statements classified into its own category. A statement of another category SHALL be rejected, with a reason that names the category found and the tool that would accept it, and, when that tool's group is denied on the target database, says so.

#### Scenario: Wrong tool
- **WHEN** the query tool is given `DELETE FROM t WHERE id = 1`, and `command` is allowed on the database
- **THEN** it is rejected, with the reason "classified as command; use the command tool"

#### Scenario: Right tool is denied here
- **WHEN** the query tool is given `DELETE FROM t WHERE id = 1`, and `command` is denied on the database
- **THEN** it is rejected, with the reason "classified as command; the command tool is denied on this database"

### Requirement: Command statements
A `command` statement SHALL be accepted when its reads use only syntax a `query` accepts, extended with: the target of INSERT, UPDATE, DELETE or MERGE, joins and CTEs in the statement, an `OUTPUT` clause (including `OUTPUT INTO` a table), `NEXT VALUE FOR`, and table hints.

#### Scenario: Update through a join
- **WHEN** the classifier is given `UPDATE o SET o.status = 'late' FROM dbo.orders o JOIN dbo.customers c ON c.id = o.customer_id WHERE c.region = 'EU'`
- **THEN** it is classified as command

#### Scenario: CTE and OUTPUT
- **WHEN** the classifier is given `WITH old AS (SELECT TOP (100) * FROM dbo.jobs WHERE done = 1 ORDER BY id) DELETE FROM old OUTPUT deleted.id`
- **THEN** it is classified as command

#### Scenario: Sequence in an insert
- **WHEN** the classifier is given `INSERT INTO dbo.t (id, name) VALUES (NEXT VALUE FOR dbo.seq, 'a')`
- **THEN** it is classified as command

### Requirement: UPDATE and DELETE need a WHERE clause
An UPDATE or DELETE statement without a `WHERE` clause SHALL be rejected, with a reason that says a `WHERE` clause is required. MERGE is not affected, because its `ON` clause already limits it. Nor is an UPDATE or DELETE whose target is a common table expression of the same statement, because the CTE's query does the filtering.

#### Scenario: Delete everything
- **WHEN** the command tool is given `DELETE FROM dbo.users`
- **THEN** it is rejected, with a reason saying that DELETE needs a WHERE clause, and nothing is sent to the database

#### Scenario: Update with a filter
- **WHEN** the command tool is given `UPDATE dbo.users SET active = 0 WHERE last_login < '2025-01-01'`
- **THEN** it is classified as command

#### Scenario: Update through a filtering CTE
- **WHEN** the command tool is given `WITH x AS (SELECT a FROM dbo.t WHERE b = 1) UPDATE x SET a = 0`
- **THEN** it is classified as command

### Requirement: DDL covers declarative objects only
A `ddl` statement SHALL be accepted only when it creates, alters or drops a table, column, constraint, index, view, schema or sequence, or truncates a table. A view's SELECT SHALL itself be acceptable as a query. Procedures, functions, triggers, and database- or security-level objects SHALL be rejected.

#### Scenario: Index and view
- **WHEN** the classifier is given `CREATE INDEX ix_orders_customer ON dbo.orders (customer_id)` and `CREATE VIEW dbo.open_orders AS SELECT id FROM dbo.orders WHERE closed = 0`
- **THEN** both are classified as ddl

#### Scenario: Procedure body is not inspected, so it is refused
- **WHEN** the ddl tool is given `CREATE PROCEDURE dbo.p AS EXEC('DROP TABLE t')`
- **THEN** it is rejected, with a reason saying that procedures, functions and triggers can't be created through sql-mcp

#### Scenario: Security object
- **WHEN** the ddl tool is given `CREATE USER bob WITHOUT LOGIN` or `CREATE DATABASE other`
- **THEN** it is rejected

### Requirement: Execute runs user procedures only
An `execute` statement SHALL be accepted only for a procedure outside the `sys` schema whose name does not start with `sp_` or `xp_`, with parameters that are literals, `NULL`, or negated numbers, given by position or by name.

#### Scenario: User procedure
- **WHEN** the classifier is given `EXEC dbo.archive_orders @before = '2026-01-01', @limit = 500`
- **THEN** it is classified as execute

#### Scenario: System procedure
- **WHEN** the execute tool is given `EXEC sp_addrolemember 'db_owner', 'bob'`, `EXEC sys.sp_configure` or `EXEC xp_cmdshell 'dir'`
- **THEN** each is rejected, with a reason saying system and extended procedures can't be run

### Requirement: Writes stay in the target database
A `command`, `ddl` or `execute` statement SHALL be rejected if it names an object with a database or server part (a three- or four-part name), because grants are given per database.

#### Scenario: Write to another database
- **WHEN** the command tool is given `INSERT INTO sales.dbo.orders (id) VALUES (1)` for the database `localhost:1433/app`
- **THEN** it is rejected, with a reason saying that writes may only name objects in the target database

#### Scenario: Linked server procedure
- **WHEN** the execute tool is given `EXEC remote.app.dbo.p`
- **THEN** it is rejected

## REMOVED Requirements

### Requirement: Classification is tied to the tool
**Reason**: Every statement category now has a tool, so the "this server has no command tool yet" case can no longer happen. The rule itself carries on as "Each tool runs only its own category", which also says when the right tool is denied on the database.
**Migration**: None. Agents see "the command tool is denied on this database" where they would have seen "this server has no command tool yet".
