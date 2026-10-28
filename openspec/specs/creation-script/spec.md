# creation-script Specification

## Purpose

Gives agents a T-SQL script that recreates a database's schema, complete and in dependency order, and says exactly what it leaves out and why.

## Requirements

### Requirement: script_database tool
The system SHALL provide a `script_database` tool in the `introspection` group. It takes a database identifier and an optional list of object names, and writes a T-SQL script that creates the schema, with no data. It SHALL check the `introspection` permission on every call, and SHALL NOT change the database.

#### Scenario: Script denied
- **WHEN** an agent calls `script_database` on a database where `introspection` is denied
- **THEN** no connection is opened, no file is written, and the error says introspection is denied and why

### Requirement: Whole-database script
With no object list, the script SHALL create every user object in the database, in an order in which each object is created after the objects it depends on. It SHALL run in an existing, empty database, and SHALL NOT create the database itself.

#### Scenario: Script rebuilds the schema
- **WHEN** a database holding tables with foreign keys, views, procedures, functions, triggers, sequences, synonyms, a user-defined type, a table type, a temporal table and a partitioned table is scripted, and the script is run against an empty database
- **THEN** it runs without errors, and `list_objects` and `describe_object` give the same results for both databases

### Requirement: Nothing is left out silently
For a whole-database script, every user object in the catalog SHALL be either in the script or named in an omitted list with the reason. Users, roles and permissions SHALL always be omitted, and named in the list.

#### Scenario: Encrypted procedure
- **WHEN** the database holds a procedure created `WITH ENCRYPTION`
- **THEN** the procedure is not in the script, and the omitted list names it with the reason that its definition is encrypted

#### Scenario: Database users
- **WHEN** the database has the user `app_reader`
- **THEN** the script creates no user, and the omitted list names `app_reader` with the reason that security principals are not scripted

### Requirement: Scripting chosen objects
With an object list, the script SHALL create only the objects named, with their own constraints, indexes and triggers, and SHALL NOT add the objects they depend on. Each entry SHALL be a name, or a name with an object type. Names SHALL be resolved as `describe_object` resolves them, with the type when one is given. If any name can't be resolved, the call SHALL fail without writing a file. An own trigger that can't be scripted SHALL be named in the omitted list.

#### Scenario: Object and type with the same name
- **WHEN** an agent scripts `{name: crm.address, type: table_type}`, and both the table `crm.address` and the table type `crm.address` exist
- **THEN** the script creates the table type and not the table

#### Scenario: Encrypted trigger on a chosen table
- **WHEN** an agent scripts `dbo.orders`, which has a trigger created `WITH ENCRYPTION`
- **THEN** the script creates the table without that trigger, and the omitted list names the trigger with the reason that its definition is encrypted

#### Scenario: Two tables
- **WHEN** an agent scripts `dbo.orders` and `dbo.order_lines`
- **THEN** the script creates those two tables with their keys and indexes, and nothing else

#### Scenario: Unknown name in the list
- **WHEN** one of the names given doesn't exist
- **THEN** no file is written, and the error names the object that wasn't found

### Requirement: Script is written to a file
The script SHALL be written to `.sql-mcp/scripts/` in the project directory, or to the user-level state directory when there is no project directory. The file name SHALL be built from the database identifier and the UTC time, with characters that are unsafe in file names replaced, and SHALL NOT overwrite an existing file.

#### Scenario: Script file name
- **WHEN** an agent scripts `localhost:1433/app` at 2026-10-26 14:05:09 UTC in a project directory
- **THEN** the script is written to a new `.sql-mcp/scripts/` file whose name contains `localhost`, `1433`, `app` and that time, and ends `.sql`

#### Scenario: Failed script leaves no file
- **WHEN** scripting fails or is cancelled part way through
- **THEN** no script file, partial or complete, is left in the scripts directory, and the error says what failed

### Requirement: Script file header
The script SHALL start with a comment giving the database identifier, the server version, the UTC time it was made, and the sql-mcp version. It SHALL NOT contain a connection string or any secret.

#### Scenario: Header
- **WHEN** a script is written
- **THEN** its first lines are a comment naming the database, server version, time and sql-mcp version, and no part of the connection string appears anywhere in the file

### Requirement: Scripts are git-ignored
When it writes to `.sql-mcp/scripts/`, the server SHALL make sure `.sql-mcp/.gitignore` lists `scripts/`, creating the file or adding the line if it is missing, and leaving the file's other lines as they are.

#### Scenario: Project initialised before this change
- **WHEN** `.sql-mcp/.gitignore` contains only `logs/` and a script is written
- **THEN** the file contains both `logs/` and `scripts/`, and `git status` does not show the script

### Requirement: What script_database returns
The tool SHALL return the script's full path, its size, the number of objects scripted by object type, and the omitted list. It SHALL NOT return the script text.

#### Scenario: Result
- **WHEN** a whole-database script of 12 tables, 3 views and 2 procedures is written, and one user is omitted
- **THEN** the result gives the path, the size, counts of 12, 3 and 2 by type, and the omitted user with its reason

### Requirement: script_database description
The tool's description SHALL say the script holds schema only, that it is written to a file the agent should read from the returned path, that a whole-database script lists everything it omits, and that only objects the database login can see are scripted. The tool SHALL be marked to MCP clients as read-only: it never changes the database, and the only file it writes is a new, git-ignored script.

#### Scenario: Agent reads the description
- **WHEN** a client lists the server's tools on a database where introspection is allowed
- **THEN** `script_database` is listed, marked read-only, and its description says the script goes to a file and holds no data
