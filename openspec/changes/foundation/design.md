# Design

## Context

The repo is greenfield: no code yet. The motivation is in `proposal.md` (Why). The binding technology decisions are
recorded in `docs/adrs/ADR-0001` (runtime, packaging, MCP SDK, release) and `ADR-0002` (classifier and read-path
defences). `ADR-0003` (introspection) covers a later change and only shapes the extension points here. The
requirements are in `specs/*/spec.md`. On the development machine, .NET SDK 10.0.400 is installed, alongside an
11.0 RC that is the default.

## Goals / Non-Goals

**Goals:**
- Engine-specific code (SQL Server now, PostgreSQL in v2) sits behind narrow interfaces, so v2 adds implementations
  rather than reworking the core.
- Changes 2 (`write-tools`) and 3 (`introspection`) only add tools. Permissions, classification, auditing and the
  tool-registration pattern all land here.
- The classifier is testable without a database, and that test suite is the main safeguard against misclassification.

**Non-Goals:**
- Live reloading of configuration. The config is read when the server starts; changes need a restart (the specs
  say this for approval).
- Resisting deliberate tampering (see the proposal's non-goal).
- Multiple batches separated by `GO`. They are rejected (see D6).

## Decisions

### D1. Stack and gates
Per ADR-0001: C#, `net10.0`, `ModelContextProtocol` (stdio), `Microsoft.Data.SqlClient`, `ScriptDom`,
`System.CommandLine` 2.0.x, xUnit v3 on Microsoft Testing Platform, and `Testcontainers.MsSql`. Versions are pinned
centrally in `Directory.Packages.props`. `global.json` pins `"sdk": {"version": "10.0.400", "rollForward":
"latestFeature"}`, so the installed 11.0 RC is never chosen, and sets `"test": {"runner": "Microsoft.Testing.Platform"}`.
`Directory.Build.props` sets `TreatWarningsAsErrors`, `Nullable`, `ImplicitUsings`, and
`AnalysisLevel=latest-recommended`.

Gate commands (one stack, run from the repo root):

```
build:  dotnet build sql-mcp.slnx -c Release
test:   dotnet test --project tests/SqlMcp.Tests/SqlMcp.Tests.csproj -c Release
itest:  dotnet test --project tests/SqlMcp.IntegrationTests/SqlMcp.IntegrationTests.csproj -c Release
format: dotnet format sql-mcp.slnx --verify-no-changes
lint:   none separate (analyzers run in the build; warnings are errors)
```

`itest` needs a Docker daemon and uses `mcr.microsoft.com/mssql/server:2025-latest`. On Apple Silicon that means
OrbStack with Rosetta enabled. Integration tests are in their own project, so neither gate needs a trait filter.
(MTP fails with exit code 8 when a filter removes every test.)
*Rejected:* one test project split by trait. The filter syntax is fragile, and the exit-8 trap applies.

### D2. Project layout
```
src/SqlMcp/                     single PackAsTool project (PackageId Emmz.SqlMcp, ToolCommandName sql-mcp)
  Configuration/                file model, loading, JSONC editing, layering, approval, identity
  Permissions/                  groups, effective-permission resolution with provenance
  Classification/               IStatementClassifier; SqlServer/ (ScriptDom)
  Execution/                    IQueryExecutor; SqlServer/; result buffer store
  Audit/                        AuditLog (JSONL)
  Mcp/                          tool classes, tool registration filtered by permission
  Cli/                          System.CommandLine commands; Registration/ (one strategy per client)
  schema/config.schema.json     embedded, and published with the package
tests/SqlMcp.Tests/             unit: classifier table, config, permissions, paging, audit, CLI, registration
tests/SqlMcp.IntegrationTests/  Testcontainers SQL Server: execution, rollback, timeout, types, MCP end to end
```
Everything is in one assembly because it ships as one tool. The folders are boundaries: the engine-specific parts
are only `Classification/SqlServer` and `Execution/SqlServer`, both behind interfaces keyed by an `Engine` enum on
each database entry. *Rejected:* separate assemblies per layer. That adds packaging overhead and gives no benefit
for a single tool.

### D3. Configuration model, loading and validation
- Files are parsed with `System.Text.Json`, using `JsonCommentHandling.Skip` and `AllowTrailingCommas`.
- The embedded JSON Schema (draft 2020-12) is checked with **JsonSchema.Net** before mapping to the model, so errors
  report the JSON path (spec: configuration format). *Rejected:* validating only through the model's types, which
  gives poor error locations and needs a separate schema.
- File shape: `{ "$schema", "defaults": { "allow": [], "deny": [], "limits": {...} }, "databases": [ { "connectionString", "engine": "sqlserver", "allow", "deny", "limits" } ] }`.
  - The identity is computed from the connection string, not written in the file, so the two can't drift apart.
  - `engine` defaults to `sqlserver`.
- **Identity**: `SqlConnectionStringBuilder.DataSource` is parsed, accepting `tcp:` prefixes and `host,port`, and
  the port defaults to 1433. A named instance without a port is made unavailable. Hosts are lower-cased but never
  resolved, so `localhost` and `127.0.0.1` stay distinct. The identity is computed *before* `${env:}` substitution,
  so an unset variable doesn't hide the identity.
- **Env substitution**: `${env:NAME}` is substituted in the connection string only. Unset variables are collected
  per database, and the database is marked unavailable with a reason.

### D4. Layering and approval
Effective permissions are computed per database identity in this order, each step recording its source:

```
defaults --> user.defaults --> user.db --> project.defaults --> project.db
                                           (each step: narrowing always applied;
                                            widening only if the project file is approved)
```

- "Widening" means turning a denied group into an allowed one, or raising a limit, compared with the result *before*
  the project layer.
- A database that only the project file defines starts from defaults plus the user's defaults. Any group it allows
  beyond those also counts as widening.
- **Approval**: `approvals.json` sits next to the user config:
  `{ "<absolute canonical path of project config>": { "sha256": "...", "approvedAt": "..." } }`.
  It is approved only when the SHA-256 of the file's current bytes matches.
- **Self-approval guard**: `approve` requires `Console.IsInputRedirected` and `Console.IsOutputRedirected` both
  to be false. It shows the widening diff, then asks the user to type the project directory's name. There is no
  `--yes`. *Why:* an honest agent told "needs approval" tends to run the command it was pointed at, or find `--yes`
  in `--help`. Asking for a typed name, not a bare y/N, also stops a reflexive "y" through a pseudo-terminal. A
  shared `ApprovalMessages` constant holds the wording addressed to a person, used by the tools, the listing and
  the CLI.
  *Rejected:* keying on the git commit, because uncommitted edits would slip through.

### D5. Comment-preserving config edits
`System.Text.Json` drops comments when it rewrites a document, so CLI edits splice the text directly. A small JSONC
editor uses `Utf8JsonReader` token offsets to find a property or array value, replaces or inserts the text, then
re-parses and re-validates before writing. The new file is written to a temp file and renamed into place. The same
editor writes OpenCode's JSONC config (D10). *Rejected:* rewriting the whole file, which loses comments (spec: config
editing). *Rejected:* a third-party JSONC library, since none is mature on .NET.

### D6. Classifier (SQL Server)
- Parse with `TSql170Parser(initialQuotedIdentifiers: true)`. Any `ParseError` rejects the statement and reports
  its line, column and message.
- More than one batch (a `GO` separator) is rejected: SqlClient can't run `GO`, and splitting batches silently
  would hide what runs.
- **Allowlist walk**: a `TSqlFragmentVisitor` overrides `Visit(TSqlFragment)`, which is called for every node.
  - Each category has a set of allowed concrete node types. Any node outside the set rejects the statement, and
    the rejection names the node type, translated to a readable phrase where one is known.
  - Statements whose meaning depends on more than the node type get extra checks: `SelectStatement.Into`,
    `TableHint` kinds, `NextValueForExpression`, `OpenQueryTableReference`/`OpenRowsetTableReference`/
    `AdHocTableReference`, `ExecuteStatement` with a string (dynamic SQL), and `sp_executesql`.
- Each top-level statement gets a category, and the batch is accepted only if all statements share the tool's category.
- **The allowlists are built from tests.** The classifier test table starts with every scenario in the specs plus a
  T-SQL corpus covering joins, CTEs, window functions, APPLY, PIVOT, JSON and XML functions, temporal
  `FOR SYSTEM_TIME`, and `OPTION` hints. A type joins an allowlist only alongside a test that exercises it.
- *Rejected:* a denylist visitor, because it fails open on unseen syntax (ADR-0002).

### D7. Query execution
- `SqlConnection` is opened with `ApplicationName=sql-mcp` so DBAs can see where sessions come from.
- `BeginTransaction(ReadCommitted)`, then the command with `CommandTimeout` from the database's limits, then
  `ExecuteReader`. Rows are read into a buffer until the row or byte cap is reached, every result set is read,
  and then the transaction is rolled back in a `finally` block and the connection is closed.
- Several result sets (`SELECT 1; SELECT 2`) are buffered in order, and the caps count across all of them. Each
  page says which result set and row range it holds.
- Values are written with `GetFieldValue`, using a converter per SQL type:
  - `decimal` and `numeric` as strings, so no precision is lost;
  - date and time types as ISO 8601 at full precision;
  - binary as base64, and `uniqueidentifier` as a string.
  - CLR UDT columns (geometry, geography, hierarchyid) are read as raw bytes with base64 and a type note, because
    SqlClient can't deserialise them without the CLR assembly.
- The **result store** maps an opaque handle (a random 128-bit value, base64url) to a buffer and a last-touched
  time. A timer evicts handles idle for longer than 10 minutes, and the whole store belongs to the session.
- Defaults are 1000 rows, 5 MB, 100 rows per page and 30 s timeout, and each database can override them.
- *Rejected:* holding the reader between pages (ADR-0002).

### D8. MCP hosting and tool exposure
- Generic Host with `AddMcpServer().WithStdioServerTransport()`. All logging goes to stderr: the Console provider
  with `LogToStandardErrorThreshold = Trace`.
- Tools are classes with `[McpServerTool]` attributes and are registered explicitly. A tool class is added only if
  its group is allowed on at least one database, which gives the spec's "only permitted tools are offered".
- Every call checks permission again, in this order:

```
tool call --> audit ok? --> resolve db --> group allowed? --> available? --> classify --> execute
                 |              |               |                 |             |            |
               error      error(known ids)  denied(audit)  error(reason)  denied(audit)  executed/failed(audit)
```

- Tool descriptions are constants holding the agent guidance from the spec. The current limits are added when the
  tool is registered.

### D9. Audit log
- One `AuditLog` per process. A session id (UUID v7) and the file are opened at start-up. The project directory is
  `.sql-mcp/` under the current directory, or under `--project <dir>` if given. The fallback is
  `~/.local/state/sql-mcp/logs` (`%LOCALAPPDATA%\sql-mcp\logs` on Windows).
- Each event is written under a lock: serialise, append a line, flush.
- If the file can't be opened, a flag is set and every tool returns "audit log unavailable" (spec). Fail closed:
  nothing runs that can't be logged.
- Events are typed records. There is deliberately no field that can carry row values or connection strings.
  `introspection` is defined now and used in change 3.

### D10. CLI and client registration
- System.CommandLine commands: `serve [--project <dir>]`, `init`, `approve` (no skip flag),
  `db add|remove`, `allow|deny <group> [--db <id>] [--user]`, `config show`, and
  `register <client> [--scope user|project] [--launcher tool|dnx] [--force]`.
- **The launch command** written into each client is `sql-mcp serve` if the tool is on PATH, and otherwise
  `dnx Emmz.SqlMcp@<this version> serve`. `--launcher` overrides that choice. Project-scope registrations add
  `--project <repo root>`, so config discovery doesn't depend on which directory the client starts the server in.
- One registration strategy per client. Each runs the client CLI with an argument array (never a shell string),
  as verified on 2026-10-25:

| Client | User scope | Project scope | Notes |
|---|---|---|---|
| `claude` | `claude mcp add -s user sql-mcp -- <launch>` | `claude mcp add -s project …` (writes `.mcp.json`) | Remove first with `claude mcp remove -s <scope> sql-mcp` when `--force` is given |
| `codex` | `codex mcp add sql-mcp -- <launch>` (always `~/.codex/config.toml`) | Edit `.codex/config.toml` → `[mcp_servers.sql-mcp]` with **Tomlyn**, keeping comments | Warn that a project config loads only for trusted projects |
| `gemini` | `gemini mcp add -s user sql-mcp <launch>` | `gemini mcp add -s project …` | `add` upserts. Always pass `-s`, because the default is project |
| `opencode` | Edit `~/.config/opencode/opencode.json(c)` | Edit `opencode.json(c)` | Detect the major version with `opencode --version`. v1 writes `mcp.sql-mcp = {type:"local", command:[...], enabled:true}`; v2 writes `mcp.servers.sql-mcp` with `disabled:false` omitted. The v1 CLI is an interactive wizard, so the file is edited instead. Uses the JSONC editor (D5) |

- "Already registered" is detected through `claude mcp get`, `codex mcp get`, `gemini mcp list`, or by reading
  the file.
- *New dependency:* **Tomlyn**, needed for Codex project scope. It parses to a syntax tree that keeps comments.

### D11. Testing
- **Unit:** the classifier table (several hundred cases, as data-driven `[Theory]`), layering and approval cases,
  identity parsing, JSONC editing round trips, paging and eviction (with a fake clock via `TimeProvider`), audit
  event shape (asserting that no values leak), and registration strategies against a fake process runner.
- **Integration:** one SQL Server 2025 container per test assembly, through an xUnit assembly fixture. It covers:
  - rollback, tested by calling the executor directly (below the classifier) with an `INSERT` and checking the row
    is gone afterwards. A T-SQL function can't write, so this can't be triggered through the `query` tool;
  - timeouts, tested by calling the executor directly with `WAITFOR DELAY` and a short timeout;
  - type fidelity, caps, and multiple result sets;
  - end to end: start `sql-mcp serve` as a child process and drive it with the MCP SDK client.

### D12. CI and release
- `.github/workflows/ci.yml` runs on push and PR, on `ubuntu-latest`. It sets up .NET 10, then runs the build,
  format, test and itest gates. Docker is available on that runner.
- `.github/workflows/release.yml` runs on a `v*` tag. It runs the gates, then
  `dotnet pack -c Release -p:Version=${tag#v}`, exchanges the GitHub OIDC token for a short-lived key using
  `NuGet/login`, and pushes with `dotnet nuget push`.
- Trusted Publishing has to be set up on nuget.org (owner, repo, workflow file) before the first tag. The repo has
  no GitHub remote yet; creating it is a task.

## Risks / Trade-offs

- [The allowlist is too narrow at first, so legitimate queries are rejected] → The rejection names the node type.
  The test corpus is built up front (D6). Widening an allowlist is a small, test-backed change.
- [ScriptDom gains a new node type that changes meaning] → It is rejected until allowlisted, which fails closed by
  design. Renovate or Dependabot updates run the full classifier table.
- [A CLR or extended-proc function called from SELECT has an external side effect] → Accepted under the threat
  model, and documented in ADR-0002. Rollback covers database-local writes.
- [Clients launch the server from an unexpected directory, so the wrong project config, or none, loads] →
  Project-scope registration passes `--project`. The `session_start` event records which config files were loaded,
  and `list_databases` names both config locations.
- [Running SQL Server under Rosetta is slow, which makes local `itest` slow] → One container per assembly. CI runs
  it natively on x64.
- [Client CLIs change their flags (OpenCode has already changed between v1 and v2)] → One strategy class per
  client, unit-tested against a fake runner. Any failure prints the snippet to add by hand.
- [An agent edits `approvals.json` directly] → That is deliberate tampering, outside the threat model. Self-approval
  through the CLI is the accidental route, and it is blocked.
- [Inside Claude Code, `! sql-mcp approve` may not count as an interactive terminal] → Accepted. The user approves
  in their own terminal window, and that friction is intended.
- [Failing closed on the audit log stops all work when the disk is full] → Intentional. The error is explicit.
- [A large row cap times a wide row size uses a lot of memory] → The byte cap (5 MB) bounds each buffer, and idle
  eviction bounds how long it is kept.

## Migration Plan

Greenfield, so there is nothing to migrate. Release order:
1. Create the GitHub repo and push.
2. Configure nuget.org Trusted Publishing for `Emmz.SqlMcp` and reserve the `Emmz.` prefix if wanted.
3. Tag `v0.1.0`.

Rollback means unlisting the NuGet version.
