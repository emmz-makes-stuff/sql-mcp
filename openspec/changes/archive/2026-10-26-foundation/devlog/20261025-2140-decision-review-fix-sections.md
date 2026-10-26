# Decision: review-fix sections §13–§16

<!-- Finishing step 3 · HEAD: 86646f3 (nothing built yet) -->

`code-review` on `change/foundation` reported 10 findings. I checked each against the code, and all hold up.
Together with my two self-review findings (see `20261025-2110-whole-change-self-review.md`), they are now
§13–§16 in `tasks.md`, all agreed with Emmz.

Emmz's answers:
- **Finding 1 (security): a project-only database needs approval.**
  - The hole: the "Project adds a database" scenario made a database that only the project file defines
    available with `query`, and no approval. Its connection string gets `${env:…}` filled in, so an unapproved,
    checked-in config could send a user's secret (or an Integrated Security handshake) to any host.
  - A side agent pointed out the cost of whole-file approval: any edit, including a `git pull`, would take the
    database offline. So Emmz chose **approval per database, by a hash of its raw connection string**. Other
    edits to the file don't revoke it. A changed host, login or env reference does. Grant and limit widenings
    stay tied to the whole file's hash.
  - The not-approved reason has to say plainly that a person runs `sql-mcp approve` in a terminal, because it
    will be seen often.
  - This changes the spec (`specs/configuration/spec.md`) and design D4. That is the scope change Emmz agreed to.
- **Finding 6:** a byte cap per value, read with `SequentialAccess`, and correct design.md's memory risk row.
- **Finding 7:** `AtomicFile` keeps a symlinked target.
- **Finding 10:** at most **20 live result handles**, fixed (not configurable). The least recently used handle
  is evicted. A per-session byte budget was rejected.
- **The clear-cut fixes are agreed as described:**
  - escaped exceptions get audited (finding 2);
  - cancel before disposing the reader (finding 5);
  - the schema's integer limits (finding 3);
  - `approve` survives two `(invalid)` entries (finding 4);
  - `ApprovalStore` uses `AtomicFile` (finding 8 and my own);
  - `IsOnPath` uses `ProgramLookup` (finding 9);
  - `PlaceholderTests` is deleted.

Ordering: security first (§13), then the errors that escape (§14), memory bounds (§15), and cleanup (§16). Each
is one section and one commit, with a hand-off after each.

## Next

- Build **§13, task 13.1**: approval per database in `ApprovalStore` and `PermissionResolver`, plus the spec and
  D4 updates. Note the base commit first.
- Neither devlog note from this session (this one and the self-review note) is committed yet. Both go into the
  §13 commit, with the `tasks.md` edit.
