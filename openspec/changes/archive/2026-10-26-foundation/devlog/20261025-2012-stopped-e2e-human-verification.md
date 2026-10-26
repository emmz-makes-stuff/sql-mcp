# Stopped: §10.1 needs Emmz inside a real Claude Code session

<!-- Section: 10 · Tasks: 10.1 · Base: 099fc39 -->

Prepared. Everything lives under the session scratchpad,
`E=/private/tmp/claude-501/-Users-rendle-github-emmz-sql-mcp/44e20e7d-2ff6-4ffc-bd6e-0682aa403792/scratchpad/e2e`:
- **Package and tool:** `Emmz.SqlMcp.0.1.0-e2e.1.nupkg` packed into `$E/feed`, then installed with
  `dotnet tool install --tool-path "$E/tools"` (not `--global`, so Emmz's own tools are untouched). `--version`
  prints `0.1.0-e2e.1+099fc39…`.
- **Container:** `sql-mcp-e2e` (`mssql/server:2025-latest`) on **localhost:14333**. The sa password is in
  `$E/sa-password`.
  - Database `app` has `dbo.orders(id, customer, status, total)`: 300 rows, of which **250** are
    `status = 'shipped'`.
- **Project:** `$E/project`.
  - `sql-mcp init` gave `INIT_EXIT:0`.
  - `db add 'Server=localhost,14333;Database=app;User Id=sa;Password=${env:APP_PW};TrustServerCertificate=true'`
    gave `DBADD_EXIT:0` and the id `localhost:14333/app`.
  - `config show` gave `SHOW_EXIT:0`, `available: yes`, with query and introspection allowed by default and
    command, ddl and execute denied by default.
- **Smoke test** in a copy (`$E/smoke`, so the project's audit log stays clean): `serve` over stdio answered
  `initialize` and listed `list_databases`, `query`, `next_page` and `abandon`. `list_databases` returned the
  database as available.

Why stop: CLAUDE.md lists registering with, and working inside, real Claude Code as human verification.

Recipe for Emmz, in a separate terminal:
```
E=/private/tmp/claude-501/-Users-rendle-github-emmz-sql-mcp/44e20e7d-2ff6-4ffc-bd6e-0682aa403792/scratchpad/e2e
cd "$E/project"
export PATH="$E/tools:$PATH"
export APP_PW="$(cat "$E/sa-password")"
sql-mcp register claude --scope project
cat .mcp.json
claude
```
In that Claude Code session, approve the project's `sql-mcp` server when asked, then send these prompts:
1. `Call the sql-mcp list_databases tool and show me the result.`
2. `With the sql-mcp query tool on localhost:14333/app, run exactly: SELECT id, customer, total FROM dbo.orders WHERE status = 'shipped' ORDER BY id — tell me the row count, whether it was truncated, and the result handle.`
3. `Call sql-mcp next_page with that handle until it reports the last page, and tell me the id range of each page.`
4. `With the sql-mcp query tool on localhost:14333/app, run exactly: DELETE FROM dbo.orders WHERE id = 1`
Then `/exit`, and paste the `register` output plus what Claude reported for each step.

Expected:
- 1: one database, `localhost:14333/app`, available, with query allowed.
- 2: 250 rows buffered, not truncated, first page ids 1–… (100 rows), with a handle.
- 3: two more pages; the second is marked as the last page.
- 4: refused as a `command` statement on a database where `command` is denied, and the row isn't deleted.

On PASS:
- Check the newest `$E/project/.sql-mcp/logs/*.jsonl`: an executed event for the SELECT, a denied event for the
  DELETE, and no row values anywhere in the file.
- Check `SELECT COUNT(*) FROM dbo.orders` is still 300.
- Tick 10.1, write the §10 section-done note, commit, hand off. Then the whole-change review
  (`git diff 56061b5..HEAD`).
- Then clean up: `docker rm -f sql-mcp-e2e`, and leave the scratchpad alone.

On FAIL: record which step failed and what Claude reported in a note, fix it as part of §10 with tests, repack as
`0.1.0-e2e.2`, reinstall to `$E/tools`, and rerun the recipe.

## Next

- Waiting for Emmz's result from the recipe above. Then follow "On PASS" or "On FAIL".
- The container `sql-mcp-e2e` is left running on purpose, for the recipe.
