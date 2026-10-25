# Spec Delta

## Purpose

Lets agents run read-only queries against a configured database and read the results page by page, without leaving changes, locks or open connections behind.

## ADDED Requirements

### Requirement: query tool
The system SHALL provide a `query` tool that takes a database identifier and SQL text, checks the `query` permission, classifies the SQL, and runs it only if every statement is a query.

#### Scenario: Successful query
- **WHEN** an agent calls `query` with `localhost:1433/app` and `SELECT id, name FROM dbo.users WHERE id < 10`
- **THEN** it receives the first page of rows with column names and types, the number of rows buffered, whether the result was truncated, and a result handle if more pages remain

#### Scenario: Rejected statement
- **WHEN** an agent calls `query` with `DELETE FROM dbo.users`
- **THEN** nothing is sent to the database, and the error gives the classification and the reason

### Requirement: Queries run in a transaction that is always rolled back
Each query call SHALL run inside a transaction that is rolled back once the results have been read, whether the query succeeded or failed.

#### Scenario: Hidden side effect is undone
- **WHEN** anything run during a query call changes data in the database
- **THEN** the change is rolled back before the call returns, and no other session ever sees it

### Requirement: Capped, buffered results
The system SHALL read rows into an in-memory buffer up to the database's row cap (default 1000) and byte cap (default 5 MB), then release the connection before returning. When a cap is reached, the result SHALL be marked truncated and SHALL tell the agent to narrow the query.

#### Scenario: Result over the cap
- **WHEN** a query would return 50,000 rows and the cap is 1000
- **THEN** 1000 rows are buffered, `truncated` is true, and the message says to add filters, select fewer columns, or use TOP

#### Scenario: Connection released before paging
- **WHEN** a query returns its first page and more pages remain
- **THEN** the database connection and transaction are already closed

### Requirement: Paging
Results SHALL be returned in pages of the database's page size (default 100 rows). A `next_page` tool SHALL take a result handle and return the next page, and SHALL report when the last page has been returned. Once the last page is served, the handle SHALL be released.

#### Scenario: Read every page
- **WHEN** a result buffers 250 rows with a page size of 100, and the agent calls `next_page` twice
- **THEN** it receives rows 101-200 and then rows 201-250, the second marked as the last page

#### Scenario: Expired or unknown handle
- **WHEN** an agent calls `next_page` with a handle that is unknown or already released
- **THEN** the call fails with an error saying to run the query again

### Requirement: Abandoning a result
An `abandon` tool SHALL release a result handle and its buffer at once. Handles SHALL also be released after an idle timeout (default 10 minutes), and when the session ends.

#### Scenario: Abandon
- **WHEN** an agent calls `abandon` with an open handle
- **THEN** the buffer is released, and later `next_page` calls with that handle fail

### Requirement: Query timeout
Each query SHALL be subject to the database's command timeout (default 30 seconds). A query that times out SHALL be cancelled and rolled back, and the agent SHALL get an error saying it timed out.

#### Scenario: Timeout
- **WHEN** a query runs longer than the timeout
- **THEN** it is cancelled, nothing is buffered, and the error says it timed out and gives the limit

### Requirement: Tool descriptions steer towards constrained queries
The `query` tool's description SHALL tell agents to select only the columns they need, to filter, and to limit rows, and SHALL state the database's caps.

#### Scenario: Agent reads the tool description
- **WHEN** a client lists the server's tools
- **THEN** the `query` description includes guidance to use explicit column lists, WHERE clauses and TOP, and states the default caps

### Requirement: Result values are serialised faithfully
Rows SHALL be returned with each column's name and its SQL type. Values SHALL be represented without loss: NULL stays distinct from an empty string, decimals keep their precision, and binary data is base64 encoded.

#### Scenario: Types round-trip
- **WHEN** a query returns a NULL, a `decimal(38,10)`, a `datetime2(7)` and a `varbinary` value
- **THEN** the result shows NULL as null, the decimal with all its digits, the datetime with 7 fractional digits, and the binary as base64
