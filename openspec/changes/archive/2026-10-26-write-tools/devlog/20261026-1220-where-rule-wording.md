# WHERE-rule wording: task 3.5 added

<!-- After §2 (cdddee4) · planning only -->

A side agent pointed out that `DELETE FROM dbo.users WHERE 1 = 1` passes the WHERE rule. That's already an
accepted risk in design.md, and the affected-rows cap is the backstop (enforced in §5). But the current reason
("DELETE needs a WHERE clause at …") reads like an instruction to satisfy, so a helpful agent may add `1 = 1`.

Emmz chose (2026-10-26) to **reword the reason**. She rejected both leaving it as it is and blocking tautologies,
which would be an arms race: `WHERE id > 0` does the same thing.
- **New task 3.5:** the reason asks for a filter that picks the rows to change. It doesn't name or suggest an
  always-true filter.
- design.md Risks now says so.
- Task 6.4's tool descriptions should use the same tone.

The only `WHERE 1 = 1` in the tests is in `ServeTests.Rejected_statement`, sent to the *query* tool as a wrong-tool
example. It isn't modelling a real write.

## Next

- **§3 (Classifier: DDL and execute), task 3.1:** see the §2-done note (20261026-1211) for the §3 notes. §3 now
  ends with 3.5, the reworded WHERE-rule reasons.
- Base commit for §3 is the commit that adds this note.
