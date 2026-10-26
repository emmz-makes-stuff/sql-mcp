# Decision: how `allow`/`deny`, `--db` lookups, approval warnings and `config show` behave

<!-- Section: 8 · Base: 6c91478 -->

Emmz's answers (before 8.3):
- **`allow` and `deny` move a group between lists.** `allow ddl --db X` removes `ddl` from X's `deny` list in the
  same file and level, then adds it to `allow`. `deny` does the reverse. Other levels and the other file are left
  alone. *Why:* deny wins at the same level, so only adding would silently do nothing. *Rejected:* add and warn;
  refuse.
- **A `--db` that isn't in the target file is an error with a hint.** This applies to `allow`, `deny` and
  `db remove`: exit 1, naming the other file if the database is there, and suggesting `--user` or `sql-mcp db add`.
  *Rejected:* copying the entry across, which can put a literal password into a shared project file.
- **The approval warning follows every project-file write.** After writing, permissions are resolved against
  `approvals.json`. If any widening is ignored, it's listed on stderr with `ApprovalMessages`, and the exit code is
  still 0. This covers `allow`, `db add`, and any edit that lapses an earlier approval (every edit changes the
  hash). *Rejected:* warning only when `allow` widens.
- **`config show` is text only**, one block per database: connection (masked by `ConnectionStringMasker.MaskRaw`),
  availability, allowed and denied groups with sources, limits, and a not-approved note. Agents use
  `list_databases` for JSON. *Rejected:* adding `--json`.

## Next

- Build 8.3 to these answers.
