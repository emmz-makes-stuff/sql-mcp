# Decision: pooling stays off, a bad config is reported by the tools, list_databases is always offered

<!-- Section: 7 · Base: 72753f4 -->

Emmz's answers:
- **Connection pooling stays off** (`Pooling=false` in `SqlServerQueryExecutor`, from §6). I made that choice
  without asking, and a side agent flagged it to her after the §6 commit. She has now ratified it: closing the
  connection ends the server session, so "connection and transaction already closed" holds as written, and a
  failed rollback can't leave anything behind. *Accepted cost:* a full connect and login per query.
  *Rejected:* pooling with reworded spec and a ClearPool backstop; a per-database `pooling` setting.
- **When `serve` finds an invalid config file, it starts anyway** and logs the error to stderr. `list_databases`
  and every other tool return the config error (file and JSON path), and nothing runs against a database. *Why:*
  most clients show only "server failed to start", so an agent would never see what to fix. *Rejected:* exiting
  non-zero.
- **`list_databases` is always offered.** It reads only config, and it's how an agent finds out why something is
  denied. The filter by permitted group applies to `query`, `next_page` and `abandon`, and to future group tools.
  *Rejected:* offering no tools at all when no group is allowed.

Process note: from now on, any choice of my own that changes runtime behaviour or cost goes to Emmz as a question
**before** the section commit.

## Next

- Build §7, starting with task 7.1.
