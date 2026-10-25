# sql-mcp

An MCP server that gives coding agents (Claude Code, Codex, Gemini, OpenCode) **controlled** access to SQL
databases, instead of the `mssql`/`pgsql` CLIs, which run anything the login allows.

- Every statement is parsed and classified as Query, Command, DDL or Execute against an allowlist. Anything
  unparseable or unrecognised is rejected.
- Each database has its own allow and deny lists for those groups. By default only Query and introspection are
  allowed.
- Queries run in a transaction that is always rolled back, and results are capped and paged.
- Every executed or denied statement is written to a per-session audit log, which never holds result data.

It ships as the `dotnet` tool `Emmz.SqlMcp` (command `sql-mcp`). SQL Server is supported first. PostgreSQL is planned.

> Status: under construction. The package isn't on nuget.org yet.

## Install

```
dotnet tool install --global Emmz.SqlMcp
```

Or run it without installing, through `dnx` (.NET 10): `dnx Emmz.SqlMcp serve`. `sql-mcp register` uses `dnx` in
the launch command when `sql-mcp` isn't on your PATH.

## Quick start

From your project's root directory:

```
sql-mcp init
sql-mcp db add 'Server=localhost,1433;Database=app;User Id=sa;Password=${env:APP_PW};TrustServerCertificate=true'
sql-mcp config show
sql-mcp register claude --scope project
```

Use **single quotes** around the connection string, in bash, zsh and PowerShell alike: in double quotes, each of
those shells expands `${env:APP_PW}` itself, so the file gets an empty password. With single quotes, sql-mcp stores
the reference, and the server reads the variable when it connects, so the password never lands in the file.

The database is now available to Claude Code as `localhost:1433/app`, for `query` and `introspection` only. To let
the agent change data there:

```
sql-mcp allow command --db localhost:1433/app
```

That edits the project config, which widens access, so it has no effect until **you** approve it in your own
terminal:

```
sql-mcp approve
```

## Commands

Every command exits 0 on success and 1 on failure, with errors on stderr. Only `approve` ever prompts.

| Command | What it does |
|---|---|
| `sql-mcp serve [--project <dir>]` | Runs the MCP server over stdio. MCP clients start this; you don't run it yourself. Reads config from `<dir>` (default: the current directory). |
| `sql-mcp init` | Creates `.sql-mcp/config.json` (no databases, with a `$schema` reference) and `.sql-mcp/.gitignore` (ignoring `logs/`). Never overwrites an existing config. |
| `sql-mcp db add '<connection string>' [--user]` | Adds a database and prints its identifier (`host:port/database`). Refuses a literal password in the project config, which is usually committed: use `Password=${env:NAME}`, or `--user`. |
| `sql-mcp db remove <id> [--user]` | Removes a database. |
| `sql-mcp allow <group> [--db <id>] [--user]` | Allows a tool group for every database in the file, or for one with `--db`. Takes the group off the deny list at the same level. |
| `sql-mcp deny <group> [--db <id>] [--user]` | Denies a tool group, the same way. |
| `sql-mcp config show` | Prints each database's effective permissions, where each comes from, and its limits. Literal passwords are masked. |
| `sql-mcp approve` | Shows what the project config widens, and approves it once you type the project directory's name. Must be run by a person in an interactive terminal. It has no `--yes` option, by design. |
| `sql-mcp register <client> [--scope user\|project] [--launcher tool\|dnx] [--force]` | Adds sql-mcp as an MCP server to `claude`, `codex`, `gemini` or `opencode`. Scope defaults to `user`. If the client can't be updated, it prints the config to add by hand. |

The tool groups are `query`, `introspection`, `command` (INSERT, UPDATE, DELETE, MERGE), `ddl` and `execute`.
This release has the `query` tool, plus `list_databases`, `next_page` and `abandon`. The other groups' tools come
in later releases.

Edits go to the project's `.sql-mcp/config.json`, or with `--user` to the user config. They keep comments and
formatting, and are checked against the schema before anything is written.

`register --scope project` writes the client's shared project file, with `--project <this directory>` in the launch
command. Teammates whose checkout is somewhere else need to run it themselves. Codex reads a project's
`.codex/config.toml` only once the project is trusted. Gemini won't write project settings in an untrusted
workspace, and sql-mcp doesn't bypass that check.

## Configuration

There are two files, and both are optional:

- **User config:** `~/.config/sql-mcp/config.json` (`%APPDATA%\sql-mcp\config.json` on Windows).
- **Project config:** `.sql-mcp/config.json` in the project directory.

Both are JSON. Comments and trailing commas are allowed. A full example:

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/emmz-makes-stuff/sql-mcp/main/src/SqlMcp/schema/config.schema.json",

  // Applied to every database in this file, before its own settings.
  "defaults": {
    "allow": ["query", "introspection"],
    "deny": ["execute"],
    "limits": {
      "maxRows": 1000,        // most rows buffered per query
      "maxBytes": 5242880,    // most bytes buffered per query (5 MB)
      "pageSize": 100,        // rows per page
      "timeoutSeconds": 30    // command timeout
    }
  },

  "databases": [
    {
      // The identifier (localhost:1433/app) comes from the connection string.
      "connectionString": "Server=localhost,1433;Database=app;User Id=sa;Password=${env:APP_PW};TrustServerCertificate=true",
      "engine": "sqlserver",  // the default, and the only engine so far
      "allow": ["command"],
      "deny": ["ddl"],
      "limits": { "maxRows": 5000 }
    }
  ]
}
```

How the effective permissions are worked out:

- **Defaults:** `query` and `introspection` are allowed; `command`, `ddl` and `execute` are denied.
- **Order:** each layer overrides the one before it: the user file's `defaults`, then its database entry, then the
  project file's `defaults`, then its database entry. Where allow and deny at the same level name the same group,
  deny wins.
- **Approval:** anything in the project file that **narrows** access always applies. Anything that **widens** it
  (allowing a denied group, or raising a limit) applies only while the file is approved, and any change to the file
  lapses the approval. `config show` and the agent's `list_databases` say what's waiting for approval.
- **Secrets:** `${env:NAME}` is substituted in connection strings when the server connects. A database whose
  variable isn't set is listed as unavailable.

Every executed or denied statement goes to a per-session JSON Lines audit log, in `.sql-mcp/logs/` (or
`~/.local/state/sql-mcp/logs/` when the project has no `.sql-mcp/`). The log never holds result data.

## Threat model

sql-mcp guards against **clumsy but honest agents**: an agent that runs a `DELETE` when it was meant to look, or
picks the wrong database, or doesn't notice that a statement has side effects. It does not try to resist an agent
that deliberately tampers with its own configuration or approvals. A project config can always narrow permissions,
but widening them needs a person to run `sql-mcp approve` in their own terminal.

## Development

Requires the .NET 10 SDK (10.0.400 or later; `global.json` pins it) and, for integration tests, a Docker daemon.
The gate commands, run from the repo root:

```
build:  dotnet build sql-mcp.slnx -c Release
test:   dotnet test --project tests/SqlMcp.Tests/SqlMcp.Tests.csproj -c Release
itest:  dotnet test --project tests/SqlMcp.IntegrationTests/SqlMcp.IntegrationTests.csproj -c Release
format: dotnet format sql-mcp.slnx --verify-no-changes
lint:   none separate (analyzers run in the build; warnings are errors)
```

`make gates` runs all of them. `itest` uses `mcr.microsoft.com/mssql/server:2025-latest`; on Apple Silicon that
needs OrbStack with Rosetta enabled.
