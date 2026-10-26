# Decision: classifier allowlist scope and hint policy

<!-- Section: 4 · Base: 00e8ede -->

Emmz's answer before §4: the **command, ddl and execute allowlists cover the spec cases plus basics** in this
change:
- command: simple INSERT, UPDATE, DELETE and MERGE;
- ddl: CREATE TABLE, DROP TABLE, ALTER TABLE ADD and TRUNCATE;
- execute: EXEC of a named procedure with parameters.

Each has table tests. `write-tools` grows them with its own corpus. *Rejected:* the spec scenarios only, which is
too thin to show the categories work. Also rejected: building them out broadly now, which is write-tools' job and
would make §4 much larger.

Table hints in a **query** are allowlisted by kind, not denylisted (fail closed):
- allowed: NOLOCK, READUNCOMMITTED, READCOMMITTED, READCOMMITTEDLOCK, REPEATABLEREAD, SNAPSHOT, ROWLOCK, NOEXPAND,
  NOWAIT, READPAST, FORCESEEK, FORCESCAN and INDEX(…);
- everything else is rejected, including the spec's UPDLOCK, XLOCK, TABLOCK, TABLOCKX, HOLDLOCK, SERIALIZABLE and
  PAGLOCK.

`EXEC … AT <linked server>` and `EXEC @variable` are rejected as indirect, alongside the spec's `EXEC('…')` and
`sp_executesql`.

## Next

- Build §4, starting with task 4.1.
