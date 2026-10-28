# Follow-up: human verification after 0.1.0-alpha.1

Written after the archive. `0.1.0-alpha.1` is published (tag on `f7802c2`, release run 40713437146), and the README
PR is #4.

On 2026-10-28, Emmz ran a smoke test in a separate Claude Code session against a real SQL Server 2019 database. It
passed:
- `list_databases` and `list_objects`: 234 tables. Approximate row counts were right on two very large tables.
- `describe_object` on a schema-qualified name and an unqualified one. It gave columns, the identity column, a
  default, a clustered PK, an index, and foreign keys both ways.
- `query` paging: `next_page`, `abandon`, and an expired handle.
- Refusals: DELETE (command denied), `SELECT … INTO #t`, `EXEC sp_who`, and a batch with a DELETE after a SELECT.
  Each said nothing was sent.
- `script_database` on two tables. The order was right, and the header named `0.1.0-alpha.1+f7802c2` and SQL Server
  15.0.

From it:
- Default constraint names are missing from `describe_object`. That's now the change `describe-default-names`.
- The agent saw that scripts land in `.sql-mcp/scripts/` in the working tree. The server already git-ignores them
  (the creation-script spec, "Scripts are git-ignored"), but that folder wasn't a git repo, so this wasn't checked
  there.

Still to verify by hand (from the archived `tasks.md` "Workflow follow-up"):
- In Claude Code, the three introspection tools show as read-only.
- `.sql-mcp/.gitignore` lists `scripts/` after a `script_database` call (`cat .sql-mcp/.gitignore`).
- Gemini, Codex and OpenCode: the server loads, and `list_objects`, `describe_object` and `script_database` work.
- In each of the four clients: a `script_database` call whose `objects` list mixes a plain name and a
  `{"name": …, "type": …}` entry. Its items schema is `anyOf` without `additionalProperties`, for model APIs with
  partial JSON Schema support (Gemini especially).

## Next

- Emmz runs the checks above. Any failure becomes a new OpenSpec change.
