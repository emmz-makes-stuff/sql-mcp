# Spec Delta

## Purpose

Guarantees that each tool only ever runs statements of its own category, by parsing SQL and classifying it against an allowlist, and rejecting anything it can't classify with certainty.

## ADDED Requirements

### Requirement: Statement categories
Each SQL statement SHALL be classified into exactly one of: `query` (SELECT), `command` (INSERT, UPDATE, DELETE, MERGE), `ddl` (CREATE, ALTER, DROP, TRUNCATE), `execute` (EXECUTE of a stored procedure or function), or `rejected`.

#### Scenario: Each category is recognised
- **WHEN** the classifier is given `SELECT 1`, `UPDATE t SET a = 1`, `ALTER TABLE t ADD c int`, `TRUNCATE TABLE t` and `EXEC dbo.p`
- **THEN** they are classified as query, command, ddl, ddl and execute respectively

#### Scenario: Out-of-scope statements are rejected
- **WHEN** the classifier is given `GRANT SELECT ON t TO u`, `DENY ...`, `BACKUP DATABASE ...`, `USE otherdb` or `SET ...`
- **THEN** each is classified as rejected

### Requirement: Classification by parsing
Classification SHALL use a full parse of the statement for the target engine's SQL dialect. Any statement that does not parse without errors SHALL be rejected. Classification SHALL NOT rely on keyword or pattern matching.

#### Scenario: Syntax error
- **WHEN** the statement is `SELEC * FROM t`
- **THEN** it is rejected, with the parser's error position and message

#### Scenario: Keywords in comments and strings do not count
- **WHEN** the statement is `SELECT 'DROP TABLE t' AS x -- DELETE`
- **THEN** it is classified as query

### Requirement: Allowlist, fail closed
The classifier SHALL accept a statement into a category only if every syntax element it contains is on that category's allowlist. An unrecognised element SHALL cause rejection, naming the element.

#### Scenario: Unrecognised syntax
- **WHEN** a statement contains a syntax element that has no allowlist entry
- **THEN** it is rejected, and the reason names the element

### Requirement: Side-effecting reads are not queries
A SELECT SHALL be rejected (not classified as query) if it creates or changes state: `SELECT ... INTO`, the rowset functions `OPENQUERY`, `OPENROWSET` or `OPENDATASOURCE`, `NEXT VALUE FOR`, or table hints that take update or exclusive locks, or that escalate locking (UPDLOCK, XLOCK, TABLOCK, TABLOCKX, HOLDLOCK, SERIALIZABLE, PAGLOCK).

#### Scenario: SELECT INTO
- **WHEN** the statement is `SELECT * INTO t2 FROM t`
- **THEN** it is rejected, with a reason that says it creates a table

#### Scenario: Linked-server rowset
- **WHEN** the statement is `SELECT * FROM OPENQUERY(lnk, 'DELETE FROM x')`
- **THEN** it is rejected

#### Scenario: Sequence advance
- **WHEN** the statement is `SELECT NEXT VALUE FOR dbo.seq`
- **THEN** it is rejected

#### Scenario: Locking hint
- **WHEN** the statement is `SELECT * FROM t WITH (UPDLOCK)`
- **THEN** it is rejected

### Requirement: Batches must be homogeneous
A batch of several statements SHALL be accepted for a tool only if every statement is classified into that tool's category. Otherwise the whole batch SHALL be rejected, and nothing in it runs.

#### Scenario: Mixed batch
- **WHEN** the query tool is given `SELECT 1; DROP TABLE t`
- **THEN** the whole batch is rejected, with a reason naming statement 2 and its category

#### Scenario: Homogeneous batch
- **WHEN** the query tool is given `SELECT 1; SELECT 2`
- **THEN** the batch is accepted as query

### Requirement: Dynamic and indirect SQL
Statements whose effect can't be determined by parsing SHALL be rejected by every tool in v1: `EXEC('...')` or `EXECUTE (@sql)` with a string, and calls to `sp_executesql`.

#### Scenario: Dynamic SQL in a query
- **WHEN** the query tool is given `EXEC('SELECT 1')`
- **THEN** it is rejected, because dynamic SQL can't be classified

### Requirement: Classification is tied to the tool
Each tool SHALL run only statements classified into its own category. A statement of another category SHALL be rejected, with a reason that names the category found and the tool that would accept it.

#### Scenario: Wrong tool
- **WHEN** the query tool is given `DELETE FROM t`
- **THEN** it is rejected, with the reason "classified as command; use the command tool"
