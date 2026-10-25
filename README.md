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

> Status: under construction. Installation, the CLI and the config format will be documented here when they land.

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
