# Stopped: §7.2 needs Emmz inside a real Claude Code session

<!-- Section: 7 · Tasks: 7.1, 7.2 · Base: 5aa1edd -->

**7.1 is built and green, but uncommitted and unticked** (the stop rule):
- `ServeTests.Writes_through_each_write_tool` in `tests/SqlMcp.IntegrationTests/Mcp/ServeTests.cs`.
  - The project config allows command, ddl and execute. The test creates the procedure itself, since ddl refuses
    procedures.
  - Through stdio, the test: lists 7 tools; `ddl` CREATE TABLE gives `rows_affected: []`; `command` INSERT 3 plus
    UPDATE 1 gives `[3,1]`; `execute` of the procedure gives `[1]`; `query` reads the three rows back.
  - The audit's `executed` events are ddl, command, execute and query, with those `rows_affected`. The query's is
    absent.
- `InitializeAsync`'s config write and approval moved into `WriteApprovedConfig(JsonObject database)`, so the write
  test can re-approve a widened config. The other ServeTests are unchanged.
- Gates: `BUILD_EXIT:0`, `TEST_EXIT:0` (578), `ITEST_EXIT:0` (41), `FORMAT_EXIT:0`, `VALIDATE_EXIT:0`,
  `GATES_EXIT:0`.

Prepared for 7.2. Everything lives under the session scratchpad,
`E=/private/tmp/claude-501/-Users-rendle-github-emmz-sql-mcp/f60848f3-77d8-457b-902e-70e7248ec4e0/scratchpad/e2e`:
- **Package and tool:** `Emmz.SqlMcp.0.2.0-e2e.1.nupkg` was packed into `$E/feed`, then installed with
  `--tool-path "$E/tools"`. `--version` prints `0.2.0-e2e.1+5aa1edd…`, so 7.1's test-only change isn't in it.
- **Container:** `sql-mcp-e2e` (`mssql/server:2025-latest`) on **localhost:14333**. The sa password is in
  `$E/sa-password`.
  - Database `app` has `dbo.orders(id, customer, status, total)`: 1500 rows, of which 1200 are `pending` (ids
    1–1200) and 300 are `shipped`.
- **Project:** `$E/project`. `init` gave `INIT_EXIT:0`, `db add` gave `DBADD_EXIT:0` (`localhost:14333/app`), and
  `allow command --db localhost:14333/app` gave `ALLOW_EXIT:0`. `config show` says it isn't approved: command is
  denied until Emmz runs `sql-mcp approve`. The cap is the default, 1000 affected rows.

Why stop: CLAUDE.md lists running `sql-mcp approve`, and working inside real Claude Code, as human verification.

Recipe for Emmz, in a separate terminal:
```
E=/private/tmp/claude-501/-Users-rendle-github-emmz-sql-mcp/f60848f3-77d8-457b-902e-70e7248ec4e0/scratchpad/e2e
cd "$E/project"
export PATH="$E/tools:$PATH"
export APP_PW="$(cat "$E/sa-password")"
sql-mcp approve
sql-mcp config show
sql-mcp register claude --scope project
claude
```
In that Claude Code session, approve the project's `sql-mcp` server when asked, then:
1. `Call the sql-mcp list_databases tool and show me the result.`
2. `With the sql-mcp command tool on localhost:14333/app, run exactly: UPDATE dbo.orders SET status = 'cancelled' WHERE id IN (1, 2, 3)`
3. `With the sql-mcp command tool on localhost:14333/app, run exactly: DELETE FROM dbo.orders`
4. `With the sql-mcp command tool on localhost:14333/app, run exactly: UPDATE dbo.orders SET status = 'archived' WHERE status = 'pending'`
5. Run `/mcp`, open sql-mcp, then its tools, then `command`. Note what it shows for the annotations
   (destructive, read-only), and whether the permission prompts in steps 2–4 looked different from `query`'s.

Then `/exit`, and paste the `approve` and `config show` output plus what Claude reported for each step.

Expected:
- 1: `localhost:14333/app` is available, with command allowed (from the project config) and the cap 1000.
- 2: `committed: true`, `rows_affected: [3]`.
- 3: refused before anything is sent, by the WHERE rule (DELETE needs a WHERE clause).
- 4: 1197 rows match, which is over the cap. The batch is rolled back and the agent is told nothing was committed,
  with the cap and the count. The agent may count with `query` first, as the description asks. That's fine.
- 5: `command` shows as destructive and not read-only.

On PASS:
- Check the newest `$E/project/.sql-mcp/logs/*.jsonl`: `executed` for step 2 (`rows_affected: [3]`), `denied` for
  step 3, and `failed` for step 4 (`cap: 1000`, `affected: 1197`). No row values (`customer-<n>`) anywhere.
- In the container, check that `status` counts are cancelled 3, pending 1197 and shipped 300.
- Tick 7.1 and 7.2, write the §7 section-done note, commit, and hand off. Then the whole-change review.
- Then clean up: `docker rm -f sql-mcp-e2e`, and leave the scratchpad alone.

On FAIL: record which step failed and what Claude reported, then agree with Emmz whether to fix it in §7 or in a
review-fix section. Repack as `0.2.0-e2e.2`, reinstall to `$E/tools`, and rerun the recipe.

## Next

- Emmz ran steps 1–4 at 15:46Z (Claude Code v2.1.310). All four matched the expected results. The audit log
  (`20261026T154605Z-82438.jsonl`) and the table counts (cancelled 3, pending 1197, shipped 300) check out, with 0
  `customer-` matches.
- **Still waiting for step 5** (the `/mcp` annotations for `command`). 7.2 isn't ticked until that's in.
- Finding: the cap message's "had affected N rows when it was stopped" (`SqlServerQueryExecutor.cs:284`) made the
  agent think 1197 was a lower bound. Emmz's ruling: commit §7 as it is, then add `## 8. Review fixes: cap message`
  to tasks.md. The new wording says the count is exact for the statements that ran, and that later statements
  didn't run.
- The container `sql-mcp-e2e` is left running on purpose, for the recipe.
- The working tree has 7.1's `ServeTests.cs` change and this note, both uncommitted on purpose.
