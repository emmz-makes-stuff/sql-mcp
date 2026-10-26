# Proposal

## Why

Coding agents (Claude Code, Codex, Gemini, OpenCode) reach SQL databases through the `mssql`/`pgsql` CLIs, which run
anything the login allows. Nothing limits an agent to the kind of statement it ought to be running. sql-mcp is an
MCP server that gives agents **controlled** access instead. This first change builds the base of that control:
configuration, permissions, statement classification, read-only querying, auditing, and the CLI that installs it.
These are enough to replace the CLIs for read-only work against SQL Server.

## What Changes

- New `dotnet` tool `Emmz.SqlMcp` (command `sql-mcp`) that runs an MCP server over stdio.
- **Layered configuration:** a user-level and a project-level JSON file, with a published JSON Schema.
  - Each database is identified as `host:port/database`, and its connection string can reference environment variables (`${env:NAME}`).
  - The project file can always narrow permissions. It can widen them only after the user approves it with `sql-mcp approve`, and that approval lapses whenever the file changes.
- **Permissions:** per-database allow/deny lists for the tools, on top of global defaults. By default Query and introspection are allowed; Command, DDL and Execute are denied.
- **Statement classification:** every SQL statement is parsed with Microsoft's T-SQL parser and classified as Query, Command, DDL, Execute, or rejected.
  - Statements are checked against an allowlist. Anything unparseable, ambiguous or unrecognised is rejected (fail closed).
  - This change covers every category, because Query's safety depends on recognising everything that isn't a Query.
- **MCP tools:** `list_databases` (each database with its effective permissions, and why), and `query` with `next_page` and `abandon`.
  - Queries run in a transaction that is always rolled back.
  - Results are read into a capped buffer and paged from it.
  - Each database sets its own cap, page size and timeout. A truncated result says so.
- **Audit log:** one JSON Lines file per session under `.sql-mcp/logs/` (git-ignored). It records executed statements, denied attempts and introspection calls, and never result data.
- **CLI:** `serve`, `init`, `approve`, commands to edit the config, and `register` to add the server to Claude Code, Codex, Gemini or OpenCode.
- **Repo and CI:** solution scaffolding, gates, GitHub Actions CI, and a tag-triggered release to nuget.org through Trusted Publishing.

**Non-goal:** v1 does not resist an agent that deliberately tampers with its own config or approvals. The threat
model is clumsy but honest agents.

**Out of scope:**
- The Command, DDL and Execute tools (change `write-tools`).
- Introspection and the creation script (change `introspection`).
- PostgreSQL (v2).
- GRANT/REVOKE.

## Capabilities

### New Capabilities

- `configuration`: config file locations and format, layering of user and project files, approval of project files, environment variable substitution, and database identity (`host:port/database`).
- `permissions`: default grants, per-database allow/deny lists, resolving effective permissions, and the `list_databases` tool.
- `statement-classification`: parsing T-SQL, classifying each statement into a category against an allowlist, and rejecting statements fail-closed.
- `query-execution`: the `query`, `next_page` and `abandon` tools, the rolled-back transaction, capped buffering, paging, timeouts, and the truncation signal.
- `audit-log`: the per-session JSONL log, its event types, where it lives, and what it must never contain.
- `cli`: the `serve`, `init`, `approve`, config-editing and `register` commands.

### Modified Capabilities

None. This is the first change.

## Impact

- New code: `src/SqlMcp`, `tests/SqlMcp.Tests`, `tests/SqlMcp.IntegrationTests`, `sql-mcp.slnx`, `global.json`,
  `Directory.Build.props`, `Directory.Packages.props`, and `.github/workflows/`.
- Dependencies: ModelContextProtocol, Microsoft.Data.SqlClient, Microsoft.SqlServer.TransactSql.ScriptDom,
  System.CommandLine, xunit.v3 and Testcontainers.MsSql (see `docs/dependencies.md` and ADR-0001/0002).
- External systems: SQL Server (2025 for tests), and the client config of Claude Code, Codex, Gemini and OpenCode
  (written by `register`).
- Needs a GitHub remote and nuget.org Trusted Publishing set up before the first release.
