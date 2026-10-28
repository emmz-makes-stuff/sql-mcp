# audit-log Specification

## Purpose

Keeps a complete, machine-readable record of everything agents asked sql-mcp to do, both what ran and what was refused, without ever recording the data that came back.

## Requirements

### Requirement: One log per session
Each server process SHALL write one audit log file named `<UTC start time>-<process id>.jsonl` in `.sql-mcp/logs/` under the project directory. If there is no project directory, or it can't be written to, the log SHALL go in the user-level state directory instead.

#### Scenario: Project directory present
- **WHEN** the server starts in a directory that contains `.sql-mcp/`
- **THEN** a new log file is created in `.sql-mcp/logs/`, named with the start time in UTC and the process id

#### Scenario: No project directory
- **WHEN** the server starts in a directory without `.sql-mcp/`
- **THEN** the log is written to the user-level state directory, and the file path is recorded in the log's first event

### Requirement: Log format
Every line SHALL be one JSON object with at least: a UTC timestamp, the event type, and the session id. Events SHALL be written and flushed as they happen.

#### Scenario: Readable while running
- **WHEN** a query has just completed
- **THEN** its event is already in the file, as one complete JSON line

### Requirement: Events recorded
The log SHALL record:
- `session_start` and `session_end`, including the server version and the config files loaded;
- `executed`: the tool, database, SQL text, classification, duration, and rows returned or affected;
- `denied`: the tool, database, SQL text if any, and the reason;
- `failed`: an execution error;
- `introspection`: an introspection call.

#### Scenario: Denied attempt
- **WHEN** a query is rejected because it is classified as command
- **THEN** a `denied` event records the tool, database, SQL and the reason

#### Scenario: Permission denial
- **WHEN** a call is refused because the tool group is denied on that database
- **THEN** a `denied` event records the reason and where it came from

### Requirement: No result data or secrets in the log
The log SHALL NOT contain any result row values, connection strings, passwords, or the values of resolved environment variables.

#### Scenario: Query with results
- **WHEN** a query returns rows
- **THEN** the `executed` event records how many rows, and none of their values

### Requirement: Logs are git-ignored
`sql-mcp init` SHALL create `.sql-mcp/.gitignore` containing `logs/`. The server SHALL create the same file when it creates `.sql-mcp/logs/` and the file is missing.

#### Scenario: Log directory in a repo
- **WHEN** the server creates `.sql-mcp/logs/` in a git repository
- **THEN** `git status` does not show the log files

### Requirement: Logging failures are not silent
If the audit log can't be written, the server SHALL refuse to run tools, and SHALL return an error saying the audit log is unavailable.

#### Scenario: Log directory not writable
- **WHEN** neither the project log directory nor the fallback directory can be written
- **THEN** every tool call fails with an "audit log unavailable" error, and nothing runs against a database

### Requirement: Write outcomes are recorded
For a write tool, an `executed` event SHALL mean the batch was committed, and SHALL record the rows affected by each statement. A `failed` event SHALL mean the batch was rolled back, and SHALL record why: an error, a timeout, or the affected-rows cap. When sql-mcp can't know whether the batch was committed, the `failed` event SHALL record that the outcome is unknown.

#### Scenario: Committed write
- **WHEN** a `command` batch of two statements affecting 3 and 5 rows is committed
- **THEN** an `executed` event records the tool, database, SQL, classification, and rows affected of 3 and 5

#### Scenario: Write rolled back by the cap
- **WHEN** a `command` batch is rolled back because it affected more rows than the affected-rows cap
- **THEN** a `failed` event records that the cap rolled it back, with the cap and the rows the batch affected, and no `executed` event is written

#### Scenario: Write with an unknown outcome
- **WHEN** an `execute` batch runs a procedure that ends sql-mcp's transaction with its own COMMIT or ROLLBACK
- **THEN** a `failed` event records that the outcome is unknown, and no `executed` event is written

### Requirement: Introspection calls are recorded
Each introspection call that runs SHALL write an `introspection` event with the tool, the database, what was asked for (the filters, the object name, or the object list), the duration, and whether it succeeded. For `script_database`, the event SHALL also record the script's path, the counts by object type, and the number of objects omitted. It SHALL NOT record object definitions or the script text.

#### Scenario: Describe call
- **WHEN** an agent describes `dbo.orders` on `localhost:1433/app`
- **THEN** an `introspection` event records `describe_object`, the database, `dbo.orders` and the duration, and none of the columns or definitions returned

#### Scenario: Script call
- **WHEN** an agent writes a whole-database script that omits one user
- **THEN** an `introspection` event records `script_database`, the database, the script path, the counts by type, and one omitted object

#### Scenario: Failed introspection
- **WHEN** a `list_objects` call times out
- **THEN** an `introspection` event records the call as failed, with the reason
