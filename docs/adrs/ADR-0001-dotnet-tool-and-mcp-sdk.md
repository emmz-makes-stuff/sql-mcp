# ADR-0001: .NET 10 `dotnet` tool on the official MCP C# SDK

- Status: Accepted
- Date: 2026-10-25

## Context

sql-mcp gives coding agents (Claude Code, Codex, Gemini, OpenCode) controlled access to SQL databases, to replace
the unconstrained `mssql`/`pgsql` CLIs. Hard constraints: it is an MCP server, and it ships as a `dotnet` tool that
also carries CLI commands to register the server with those clients and to edit its config.

## Decision

- **Runtime and language:** C# on .NET 10 (LTS), `net10.0`. `global.json` pins SDK 10.0.x.
- **Packaging:** one `PackAsTool` project. NuGet package ID `Emmz.SqlMcp`, command `sql-mcp`. Clients launch it
  with `dnx Emmz.SqlMcp` or the installed `sql-mcp`.
- **MCP:** the official `ModelContextProtocol` SDK (2.3.0 at decision time) over the **stdio** transport. Each
  client launches its own server process, which also defines a session (one audit log, buffered results scoped to it).
- **CLI:** `System.CommandLine` 2.0.x (stable).
- **Release:** GitHub Actions builds and tests, then on a version tag publishes to nuget.org using Trusted
  Publishing (OIDC), so no long-lived API key is stored.

## Alternatives rejected

- Another language with a .NET shim: breaks the "ships as a `dotnet` tool" constraint in spirit and doubles the toolchain.
- Hand-written JSON-RPC: the official SDK is current, maintained, and targets net10.0.
- `System.CommandLine` 3.0: release candidate only. Spectre.Console.Cli: nothing it adds is needed.
- Package ID `sql-mcp`: free, but one character away from an existing, unrelated `sqlmcp` package.

## Consequences

- Users need the .NET 10 runtime (or `dnx`) to run it.
- PostgreSQL (v2) will use Npgsql. That decision belongs to the v2 change.
