# Whole-change self-review

<!-- Finishing step 1 · Range: 56061b5..86646f3 -->

Pre-flight: `DEPS_EXIT:0` (unsandboxed; the sandboxed run's gh and docker failures were sandbox denials),
`VALIDATE_EXIT:0`.

I read the MCP pipeline, the catalog and session, `serve`, the permission resolver, the approval store, the audit
log, the result store, the SQL Server executor, the registrations, `approve`, `ConfigTarget` and the grant
commands, all against the requirement list in the six delta specs.

Findings (both small):
1. `tests/SqlMcp.Tests/PlaceholderTests.cs` (`Assert.True(true)`) is §1 scaffolding. Delete it.
2. `ApprovalStore.Approve` writes to a temp file and renames it itself, instead of using
   `AtomicFile.WriteAllText`. Unlike `AtomicFile`, it leaves the `.tmp` file next to `approvals.json` if the write
   or rename fails. Switch it to `AtomicFile`.

Checked and fine:
- The audit gates: nothing is returned that wasn't logged.
- Approval fails closed on a missing or damaged store.
- The executor always rolls back, and doesn't pool connections.
- The loader validates against the schema embedded in the tool, so the schema and the code can't drift.
- The refusals that aren't audited (unknown or unavailable database) were Emmz's §7 decision. Not reopened.

Still open, and checkable only after the first release: `dnx --project`, and the shape of the OpenCode v2 config.

Emmz chose to **run the `code-review` skill first**. Its fixes and the two above then become one section,
`## 13. Review fixes`, to be agreed with her before it's built.

## Next

- Run `code-review` on `change/foundation`, if it hasn't run yet. Then put §13 to Emmz: the two findings above
  plus whatever the review confirms.
- Build §13 with the section loop (base commit, gates, one commit, hand-off).
- Then the change-done note, and offer `/opsx:archive`. Draft PR #1 stays a draft until then.
