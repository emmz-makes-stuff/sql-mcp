# configuration Specification

## Purpose

Defines where sql-mcp reads its database definitions and settings, how the user-level and project-level files combine, and how the user stays in control of anything a project file grants.

## Requirements

### Requirement: Configuration file locations
The system SHALL read an optional user-level file at `~/.config/sql-mcp/config.json` (`%APPDATA%\sql-mcp\config.json` on Windows), and an optional project-level file at `.sql-mcp/config.json` in the directory the server or CLI is started from.

#### Scenario: Only a user file exists
- **WHEN** the user-level file defines a database and there is no project file
- **THEN** that database is available, with the user-level settings

#### Scenario: Neither file exists
- **WHEN** the server starts with no configuration files
- **THEN** it starts successfully, and `list_databases` returns an empty list with a message naming both file locations

### Requirement: Configuration format and schema
Configuration files SHALL be JSON. Comments and trailing commas are allowed. The system SHALL publish a JSON Schema for the format, and SHALL reject a file that does not match it with an error naming the file, the location within it, and the problem.

#### Scenario: Comments are accepted
- **WHEN** a config file contains `//` comments and trailing commas
- **THEN** it loads without error

#### Scenario: Invalid file
- **WHEN** a config file sets a tool permission to an unknown tool name
- **THEN** loading fails with an error naming the file, the JSON path, and the unknown name

### Requirement: Database identity
Each database SHALL be identified as `host:port/database`, written in lower case, with the port always included. The system SHALL accept that identifier in tool arguments and SHALL reject two entries that resolve to the same identifier within one layer.

#### Scenario: Port is always included
- **WHEN** a connection string targets `sqlhost` with no explicit port and database `Sales`
- **THEN** the database is identified as `sqlhost:1433/sales`

#### Scenario: Named instance without a port
- **WHEN** a connection string targets `sqlhost\SQLEXPRESS` with no port
- **THEN** that database is unavailable, with the reason that a named instance needs an explicit port

#### Scenario: Host names are not resolved
- **WHEN** one entry targets `localhost` and another targets `127.0.0.1`, both on port 1433 with the same database
- **THEN** they are two different identities, `localhost:1433/app` and `127.0.0.1:1433/app`

#### Scenario: Duplicate identity
- **WHEN** one config file has two entries that both resolve to `sqlhost:1433/sales`
- **THEN** loading fails with an error naming both entries

#### Scenario: Unknown identifier in a tool call
- **WHEN** an agent calls a tool with `otherhost:1433/sales` and no entry has that identity
- **THEN** the call fails with an error listing the known identifiers

### Requirement: Environment variable substitution
Connection strings SHALL support `${env:NAME}` references, resolved from the server's environment when the configuration loads. A reference to an unset variable SHALL make that database unavailable, with an error naming the variable. It SHALL NOT be replaced with an empty string.

#### Scenario: Variable is set
- **WHEN** a connection string contains `Password=${env:SALES_PW}` and `SALES_PW` is set
- **THEN** the connection uses that variable's value

#### Scenario: Variable is missing
- **WHEN** `SALES_PW` is not set
- **THEN** the database is listed as unavailable, with the reason "environment variable SALES_PW is not set", and calls against it fail

#### Scenario: Secrets are not exposed
- **WHEN** a database is listed, or appears in an error or a log
- **THEN** the resolved connection string and its credentials do not appear

### Requirement: Layering of user and project files
When both files exist, the system SHALL merge them per database identity. Project-level entries add new databases or adjust existing ones. Settings that **narrow** access (denying a tool, or lowering the result cap, page size or timeout) SHALL always apply.

#### Scenario: Project adds a database
- **WHEN** the project file defines `localhost:1433/app`, the user file does not, and the database has been approved
- **THEN** `localhost:1433/app` is available, with the default grants

#### Scenario: Project narrows a grant
- **WHEN** the user file allows Command on `sqlhost:1433/sales` and the project file denies it
- **THEN** Command is denied on `sqlhost:1433/sales`

### Requirement: Project widening requires approval
A project file setting that **widens** access beyond the user-level and default grants (allowing a tool that would otherwise be denied, or raising a limit) SHALL apply only while the project file is approved. Approval SHALL be stored at user level against the file's path and a fingerprint of its content.

#### Scenario: Unapproved widening is ignored
- **WHEN** the project file allows DDL on `localhost:1433/app` and has not been approved
- **THEN** DDL stays denied, and `list_databases` gives the reason "project config grants ddl but is not approved. Ask the user to run `sql-mcp approve` in their own terminal. Agents cannot approve."

#### Scenario: Denial wording is addressed to a person
- **WHEN** any tool call, listing or CLI warning reports that a project file isn't approved
- **THEN** the message tells the agent to ask the user, and never presents `sql-mcp approve` as a command for the agent to run

#### Scenario: Approved widening applies
- **WHEN** the user runs `sql-mcp approve` in the project and restarts the server
- **THEN** DDL is allowed on `localhost:1433/app`

#### Scenario: Editing the file invalidates approval
- **WHEN** an approved project file is changed in any way
- **THEN** its widening settings are ignored again until it is re-approved, and its narrowing settings still apply

### Requirement: Project-only databases require approval
A database that only the project file defines SHALL be unavailable, and SHALL NOT be connected to, until it is approved. Its approval SHALL be stored at user level against a fingerprint of its connection string as written, before environment substitution.

#### Scenario: Unapproved project database
- **WHEN** the project file defines `evil.example:1433/x` with `Password=${env:GITHUB_TOKEN}`, the user file does not, and it has not been approved
- **THEN** it is listed as unavailable with the reason "project config adds this database but is not approved. Ask the user to run `sql-mcp approve` in their own terminal. Agents cannot approve.", and a query against it fails without connecting

#### Scenario: Approval survives other edits
- **WHEN** an approved project-only database's connection string is unchanged but another part of the project file is edited (for example by `git pull` or `sql-mcp deny`)
- **THEN** the database stays available, while the file's grant and limit widenings are ignored until it is re-approved

#### Scenario: Changed connection string
- **WHEN** an approved project-only database's connection string is changed in any way
- **THEN** the database is unavailable again until it is re-approved

#### Scenario: Approval shows what the database reads
- **WHEN** the user runs `sql-mcp approve` and the project file defines an unapproved database whose connection string uses `${env:APP_PW}`
- **THEN** the diff lists that database as added, reading `${env:APP_PW}`, and never shows the variable's value
