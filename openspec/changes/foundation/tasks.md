# Tasks

## 1. Repo scaffolding

- [x] 1.1 Add `global.json` (SDK 10.0.400, rollForward latestFeature, `test.runner` = Microsoft.Testing.Platform), `Directory.Build.props` (TreatWarningsAsErrors, Nullable, ImplicitUsings, AnalysisLevel latest-recommended), `Directory.Packages.props` (central versions for every package in design D1/D3/D10), `.editorconfig` and `.gitignore`; verify `dotnet --version` in the repo root reports 10.0.x
- [x] 1.2 Create `sql-mcp.slnx` with `src/SqlMcp` (PackAsTool, PackageId Emmz.SqlMcp, ToolCommandName sql-mcp, net10.0) and `tests/SqlMcp.Tests` / `tests/SqlMcp.IntegrationTests` (xunit.v3), each test project with one placeholder test; verify the build, test, itest and format gates from design D1 all exit 0
- [x] 1.3 Add a README stub that states purpose, the threat model (clumsy but honest agents) and the gate commands; verify the gate commands in it match design D1 exactly

## 2. Configuration

- [x] 2.1 Write `schema/config.schema.json` (defaults, databases, allow/deny groups, limits, engine) as an embedded resource, and implement loading with comments and trailing commas plus JsonSchema.Net validation that reports file and JSON path; verify with unit tests for the "Comments are accepted" and "Invalid file" scenarios
- [x] 2.2 Implement database identity from connection strings (`tcp:`, `host,port`, default 1433, lower-casing, rejecting a named instance without a port, no DNS resolution, duplicate detection within a layer); verify with unit tests for every scenario in the configuration spec's "Database identity" requirement
- [x] 2.3 Implement `${env:NAME}` substitution with an injectable environment, marking a database unavailable when a variable is unset, and a masking helper for any display of connection strings; verify with unit tests for "Variable is set", "Variable is missing" and "Secrets are not exposed"
- [x] 2.4 Implement file discovery (user path per OS, project `.sql-mcp/config.json` from cwd or `--project`); verify with unit tests on temp directories for the "Only a user file exists" and "Neither file exists" scenarios

## 3. Permissions, layering and approval

- [x] 3.1 Implement tool groups and effective-permission resolution with a source for each group (defaults → user defaults → user db → project defaults → project db, deny wins at the same level), plus limits resolution; verify with unit tests for each scenario in the permissions spec's "Default grants" and "Global and per-database allow and deny lists"
- [x] 3.2 Implement the approval store (`approvals.json`, canonical path → SHA-256) and the narrow/widen split, so that unapproved widening is ignored, and add the shared `ApprovalMessages` wording addressed to a person; verify with unit tests for "Project adds a database", "Project narrows a grant", "Unapproved widening is ignored", "Approved widening applies", "Editing the file invalidates approval" and "Denial wording is addressed to a person"

## 4. Statement classification

- [x] 4.1 Define `IStatementClassifier` and its result (category per statement, batch verdict, rejection reasons with positions); verify it compiles and the unit-test harness can run table-driven cases
- [x] 4.2 Implement ScriptDom parsing (TSql170Parser), rejecting parse errors and multiple batches separated by `GO`; verify with unit tests for "Syntax error", "Keywords in comments and strings do not count" and a `GO` case
- [x] 4.3 Implement the allowlist visitor and per-category allowlists for query, command, ddl and execute, with an unknown node rejecting and naming the element; verify with the classifier table covering "Each category is recognised", "Out-of-scope statements are rejected" and "Unrecognised syntax"
- [x] 4.4 Implement the side-effect checks (SELECT INTO, OPENQUERY/OPENROWSET/OPENDATASOURCE, NEXT VALUE FOR, locking hints) and dynamic SQL rejection (EXEC string, sp_executesql); verify with table cases for every scenario in "Side-effecting reads are not queries" and "Dynamic and indirect SQL"
- [x] 4.5 Implement batch homogeneity and the tool-to-category check with the "use the X tool" reason; verify with table cases for "Mixed batch", "Homogeneous batch" and "Wrong tool"
- [x] 4.6 Grow the query corpus (joins, CTEs, window functions, APPLY, PIVOT/UNPIVOT, JSON/XML functions, FOR SYSTEM_TIME, OPTION hints, TOP/OFFSET-FETCH, UNION/EXCEPT/INTERSECT, subqueries) until each case classifies as query, extending the allowlists only where a test needs it; verify the table passes, and document in ADR-0002's consequences or a `docs/classifier.md` how to add a node type

## 5. Audit log

- [x] 5.1 Implement `AuditLog` (session id, file naming, project dir or fallback dir, `.sql-mcp/.gitignore` creation, a lock-and-flush write per event, the unavailable flag) and typed events (session_start, session_end, executed, denied, failed, introspection) with no fields for values or secrets; verify with unit tests for every audit-log spec scenario except the git check, plus a test that serialises each event type and asserts no connection string or row value appears
- [x] 5.2 Verify the git-ignore behaviour with a unit test that runs `git init` in a temp dir, creates logs through `AuditLog`, and asserts `git status --porcelain` doesn't list them

## 6. Query execution

- [x] 6.1 Implement the result store (opaque handles, pages, multiple result sets, idle eviction using `TimeProvider`, release on the last page or abandon, dispose at session end); verify with unit tests for "Read every page", "Expired or unknown handle" and "Abandon"
- [x] 6.2 Implement the SQL Server query executor (ApplicationName, ReadCommitted transaction, CommandTimeout, buffering up to the row and byte caps across result sets, rollback in `finally`, value converters per design D7); verify with integration tests for rollback (an INSERT through the executor directly), "Timeout" (WAITFOR through the executor directly), "Result over the cap", "Connection released before paging" and "Types round-trip"

## 7. MCP server and tools

- [x] 7.1 Implement `serve`: Generic Host, stdio transport, all logs to stderr, config, permissions and audit wired in, and `session_start`/`session_end` events; verify with an integration test that starts the tool as a child process, sends `initialize` through the MCP SDK client, and gets a tool list (the "Started by a client" scenario)
- [x] 7.2 Implement the `list_databases` tool and the call pipeline (audit check → resolve db → permission → availability → classify → execute, auditing each outcome); verify with unit tests for "Listing shows provenance", "Unavailable database", "Denied group" and "Unknown identifier in a tool call", asserting that every unapproved-config denial uses `ApprovalMessages` and never invites the agent to run `approve`
- [x] 7.3 Implement the `query`, `next_page` and `abandon` tools with the guidance in their descriptions, and register tools only for permitted groups; verify with unit tests for "Agent reads the tool description" and "No database allows command", and an end-to-end integration test for "Successful query" and "Rejected statement" (including the audit events written)

## 8. CLI

- [x] 8.1 Implement the System.CommandLine root, exit-code handling, and `init`; verify with unit tests for "Fresh project", "Already initialised" and "Invalid config" (exit code non-zero)
- [x] 8.2 Implement the JSONC splice editor (design D5) with temp-file-and-rename writes; verify with round-trip unit tests that keep comments and trailing commas, and re-validate against the schema after each edit
- [x] 8.3 Implement `db add|remove`, `allow|deny` (with `--db` and `--user`) and `config show`, masking secrets; verify with unit tests for "Add a database", "Allow a group" and "Show effective config"
- [x] 8.4 Implement `approve` (refuse unless both stdin and stdout are a terminal, show the widening diff, require the project directory's name to be typed, no skip flag), with terminal detection behind an injectable abstraction for tests; verify with unit tests for "Interactive approval", "Wrong confirmation", "Nothing to approve", "Agent tries to approve" and "Skip flags are not accepted"
- [x] 8.5 Implement the `register` framework (process runner abstraction, launcher detection tool vs `dnx Emmz.SqlMcp@<version>`, `--scope`, `--force`, adding `--project` for project scope, printing the manual snippet on failure) plus the `claude` and `gemini` strategies; verify with unit tests against a fake runner for "Register with Claude Code", "Already registered" and "Client not installed", plus the gemini argument arrays
- [x] 8.6 Implement the `codex` strategy (CLI for user scope, Tomlyn edit of `.codex/config.toml` for project scope, plus the trust warning) and the `opencode` strategy (version detection, v1 and v2 JSONC shapes); verify with unit tests on fake runners and temp config files for both scopes and both OpenCode versions
- [x] 8.7 Document the CLI commands and the config format with a full example in the README; verify each documented command runs as written against a temp project, using the `init`, `db add`, `allow` and `config show` path

## 9. CI and release

- [x] 9.1 Create the GitHub repository and push `main` (with Emmz's confirmation of the org/name and visibility); verify `git remote -v` and `gh repo view`
- [x] 9.2 Add `.github/workflows/ci.yml` (ubuntu-latest, setup .NET 10, then the build, format, test and itest gates); verify a push runs green with `gh run watch`
- [x] 9.3 Add `.github/workflows/release.yml` (on a `v*` tag: gates, `dotnet pack -p:Version=${tag#v}`, `NuGet/login` OIDC, `dotnet nuget push`) after checking the current Trusted Publishing action and setup on nuget.org docs; verify with `actionlint` (or a workflow dry run), and record the nuget.org setup steps in the README

## 10. End-to-end check

- [x] 10.1 Pack the tool, install it from a local feed, run `sql-mcp init`, add the test container database, `register claude --scope project`, and from Claude Code list databases, run a constrained query, page it, and have a DELETE refused; verify the audit log has the matching executed and denied events and no row values

## 11. Review fixes: E2E findings

- [ ] 11.1 Write each audit event's type once, as `event`: the overridden `EventType` properties are serialised as a duplicate `event_type` despite `[JsonIgnore]` on the abstract base. Make it one non-virtual `[JsonIgnore]` property set through the base record's constructor; verify with a test that every event subtype serialises with `event` and without `event_type`
- [ ] 11.2 When a statement's category has no tool in this release, make the wrong-tool refusal say so ("classified as command; this server has no command tool yet") rather than "use the command tool"; add the scenario to `specs/statement-classification/spec.md`; verify with classifier and tool-pipeline tests, and that `make validate` stays green

## Workflow follow-up

- Run `/emmz:setup` before starting, to verify dependencies and add the Makefile gates and CLAUDE.md workflow section.
- Archive the change after review, then propose `write-tools`, followed by `introspection`.
