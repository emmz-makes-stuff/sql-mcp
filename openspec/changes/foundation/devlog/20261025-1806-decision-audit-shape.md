# Decision: audit event shape, and no error messages in `failed`

<!-- Section: 5 · Base: f5456ba -->

Emmz's answer: a **`failed` event records the error number, severity, state and kind** (sql, timeout,
connection), and **never the message text**.

Why: SQL Server messages can echo row values (for example "Conversion failed when converting the varchar value
'alice@example.com' …"), and the spec bans result data from the log. The agent still gets the full message in
the tool result.

*Rejected:*
- the full message, which can leak data;
- a scrubbed message, which is heuristic, so a value the scrubber misses would leak.

Choices of my own, stated to Emmz in the plan:
- the file name is compact UTC with no colons, so it's Windows-safe: `20261025T180512Z-<pid>.jsonl`;
- fields are snake_case, and every line has `ts`, `event` and `session_id` (UUID v7);
- `session_start` carries `log_path` and `pid`, which covers "the file path is recorded in the log's first
  event".

## Next

- Build §5, starting with task 5.1.
