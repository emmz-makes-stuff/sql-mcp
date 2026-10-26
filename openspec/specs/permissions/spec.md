# permissions Specification

## Purpose

Decides which tools an agent may use against each configured database, and makes the outcome, and the reasons for it, visible to the agent.

## Requirements

### Requirement: Tool groups
Permissions SHALL be granted per tool group: `query`, `introspection`, `command`, `ddl` and `execute`. There SHALL be no permission for statements outside these groups (for example GRANT, REVOKE or DENY), and no tool may run them.

#### Scenario: Unknown group in config
- **WHEN** a config grants `admin`
- **THEN** loading fails with a schema error naming `admin`

### Requirement: Default grants
Unless configuration says otherwise, `query` and `introspection` SHALL be allowed, and `command`, `ddl` and `execute` SHALL be denied, on every database.

#### Scenario: Database with no permission settings
- **WHEN** a database entry has no allow or deny lists
- **THEN** its effective permissions are query and introspection only

### Requirement: Global and per-database allow and deny lists
A config file SHALL be able to set global allow and deny lists that override the defaults, and per-database allow and deny lists that override the global ones. Where both lists at the same level name a group, deny SHALL win.

#### Scenario: Per-database allow
- **WHEN** the global settings deny `command` and the entry for `sqlhost:1433/sales` allows `command`
- **THEN** `command` is allowed on `sqlhost:1433/sales` and denied elsewhere

#### Scenario: Deny wins at the same level
- **WHEN** one database entry lists `ddl` in both allow and deny
- **THEN** `ddl` is denied on that database

### Requirement: Permission checks happen before execution
Every tool call SHALL check the tool's group against the database's effective permissions before connecting or parsing. A denied call SHALL return an error naming the database, the group, and why it is denied (default, user config, project config, or project config not approved).

#### Scenario: Denied group
- **WHEN** an agent calls a tool in a group that is denied on the target database
- **THEN** no connection is opened, and the error says which group is denied and why

### Requirement: list_databases tool
The system SHALL provide a `list_databases` tool. For each configured database it returns the identifier, whether it is available, the reason if not, the effective allowed and denied groups each with its source, and the result cap, page size, timeout and affected-rows cap.

#### Scenario: Listing shows provenance
- **WHEN** a project file's widening has not been approved
- **THEN** the affected groups are listed as denied, with the source "project config not approved"

#### Scenario: Unavailable database
- **WHEN** a database's connection string references an unset environment variable
- **THEN** it is listed as unavailable, with that reason, and without any part of the connection string

#### Scenario: Affected-rows cap is listed
- **WHEN** `localhost:1433/app` sets `maxAffectedRows` to 50
- **THEN** its listing gives an affected-rows cap of 50

### Requirement: Only permitted tools are offered
The system SHALL expose MCP tools only for groups that are allowed on at least one configured database. Permission is still checked again on every call.

#### Scenario: No database allows command
- **WHEN** no database allows `command`
- **THEN** no command tool appears in the server's tool list
