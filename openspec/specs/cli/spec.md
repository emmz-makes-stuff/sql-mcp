# cli Specification

## Purpose

The `sql-mcp` command line lets a person run the server, set up a project, approve project config, edit configuration, and register the server with their coding agents, without editing JSON by hand.

## Requirements

### Requirement: serve
`sql-mcp serve` SHALL run the MCP server over stdio, using the configuration found from the current directory. It SHALL write nothing to stdout except MCP protocol messages. Diagnostics go to stderr.

#### Scenario: Started by a client
- **WHEN** an MCP client launches `sql-mcp serve` and sends `initialize`
- **THEN** the server responds over stdout and lists its tools

### Requirement: init
`sql-mcp init` SHALL create `.sql-mcp/config.json` (an empty database list, with a `$schema` reference) and `.sql-mcp/.gitignore` (containing `logs/`) in the current directory. It SHALL NOT overwrite an existing config file.

#### Scenario: Fresh project
- **WHEN** `sql-mcp init` runs in a directory without `.sql-mcp/`
- **THEN** both files are created, and the command exits 0

#### Scenario: Already initialised
- **WHEN** `.sql-mcp/config.json` already exists
- **THEN** it is left unchanged, the command says so, and exits 0

### Requirement: approve
`sql-mcp approve` SHALL show the access the project file widens, and SHALL record approval of the file's current content only after the user types the project directory's name at an interactive prompt. It SHALL refuse to run without an interactive terminal. There SHALL be no option to skip the prompt.

#### Scenario: Interactive approval
- **WHEN** the project file allows DDL on `localhost:1433/app`, and the user runs `sql-mcp approve` in a terminal and types the project directory's name
- **THEN** the approval is stored, and the next server start applies the DDL grant

#### Scenario: Wrong confirmation
- **WHEN** the user types anything other than the project directory's name
- **THEN** nothing is approved, and the command exits non-zero

#### Scenario: Nothing to approve
- **WHEN** the project file only narrows access
- **THEN** the command reports that nothing needs approval, and exits 0

#### Scenario: Agent tries to approve
- **WHEN** `sql-mcp approve` runs with stdin or stdout not attached to a terminal, as in an agent's shell
- **THEN** it exits non-zero without approving, and the error says "Approval must be done by a person in an interactive terminal. Ask the user to run `sql-mcp approve`."

#### Scenario: Skip flags are not accepted
- **WHEN** `sql-mcp approve --yes` (or any other flag meant to skip the prompt) is run
- **THEN** it fails as an unknown option, and nothing is approved

### Requirement: Config editing
The CLI SHALL provide commands to add and remove databases, and to allow or deny a tool group globally or per database, in either the user file (`--user`) or the project file (the default). Edits SHALL keep the file valid against the schema and SHALL keep existing comments.

#### Scenario: Add a database
- **WHEN** the user runs `sql-mcp db add "Server=localhost,1433;Database=app;User Id=sa;Password=${env:APP_PW}"`
- **THEN** the project config gains an entry identified as `localhost:1433/app`, and the command prints that identifier

#### Scenario: Allow a group
- **WHEN** the user runs `sql-mcp allow command --db localhost:1433/app`
- **THEN** the project file allows `command` for that database, and the command warns that the change has no effect until a person runs `sql-mcp approve` in an interactive terminal

#### Scenario: Show effective config
- **WHEN** the user runs `sql-mcp config show`
- **THEN** each database's effective permissions and their sources are printed, with secrets masked

### Requirement: register
`sql-mcp register <client>` SHALL add sql-mcp as a stdio MCP server to Claude Code (`claude`), Codex (`codex`), Gemini CLI (`gemini`) or OpenCode (`opencode`), at user or project scope (`--scope`). It SHALL use the client's own command where one exists, and edit the client's config file otherwise.

#### Scenario: Register with Claude Code
- **WHEN** the user runs `sql-mcp register claude --scope project`
- **THEN** Claude Code's project configuration contains a server named `sql-mcp` that launches `sql-mcp serve`

#### Scenario: Already registered
- **WHEN** the client already has a server named `sql-mcp`
- **THEN** the command reports it and leaves it unchanged, unless `--force` is given

#### Scenario: Client not installed
- **WHEN** the client's CLI is needed but can't be found
- **THEN** the command exits non-zero, and prints the configuration to add by hand

### Requirement: Exit codes
Every command SHALL exit 0 on success and non-zero on failure, with the error on stderr. No command except `approve` SHALL prompt.

#### Scenario: Invalid config
- **WHEN** `sql-mcp config show` runs against an invalid config file
- **THEN** it exits non-zero, and prints the validation error
