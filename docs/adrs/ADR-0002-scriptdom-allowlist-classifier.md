# ADR-0002: Classify T-SQL with ScriptDom against an allowlist, and fail closed

- Status: Accepted
- Date: 2026-10-25

## Context

Each tool (Query, Command, DDL, Execute) may run only its own statement category, and each database grants tools
separately. Query and introspection are allowed by default; the rest are denied. A misclassified statement is a
security failure. **Threat model (v1): clumsy but honest agents.** v1 does not try to resist an agent that
deliberately tampers with its own config or approvals. It does guard against **helpful self-approval**: an honest
agent told that access "needs approval" may run `sql-mcp approve` itself. So `approve` requires an interactive
terminal and a typed confirmation, offers no skip flag, and every denial message tells the agent to ask the user
rather than run the command. Editing `approvals.json` directly still counts as deliberate tampering, and stays out of scope. Each database has **one connection string** in v1, so we
cannot rely on per-tier, least-privilege logins.

## Decision

1. **Parser:** `Microsoft.SqlServer.TransactSql.ScriptDom` (Microsoft's T-SQL parser, MIT, 180.x, `TSql170Parser`
   for SQL Server 2025 syntax). Any parse error means the statement is rejected.
2. **Classifier:** a visitor over the full syntax tree that checks every statement and relevant node against an
   **allowlist** per category. Anything not recognised, whether syntax we don't know or syntax added in a future
   ScriptDom version, is rejected. A batch is accepted only if *every* statement belongs to the tool's category.
   A Query rejects `SELECT ... INTO`, `OPENQUERY`/`OPENROWSET`/`OPENDATASOURCE`, `NEXT VALUE FOR`, and
   locking or updating table hints.
3. **Categories:** Query = SELECT. Command = INSERT/UPDATE/DELETE/MERGE. DDL = CREATE/DROP/ALTER/TRUNCATE.
   Execute = EXEC of procedures and functions. GRANT/REVOKE/DENY and anything else are rejected by every tool.
4. **Defence in depth:** Query and introspection reads run inside a transaction that is **always rolled back**.
   Results are read into a **capped buffer** and the connection is released before the agent pages through them.
   The cap and page size are set per database, a truncated result says so, and tool descriptions steer agents
   towards constrained queries.
5. The classifier covers **every category from the first change**, because Query's safety depends on recognising
   everything that isn't a Query.

## Alternatives rejected

- Keyword or regex matching: cannot meet "never misclassify".
- General-purpose SQL parsers: weak T-SQL dialect coverage, which would mean more rejections or, worse, misreads.
- Denylist classification: fails open on syntax nobody has seen yet.
- Holding the reader or transaction open between pages: ties up connections and shared locks while the agent thinks.

## Consequences

- One known gap remains: CLR or extended-procedure functions called from a SELECT. The rollback covers
  database-local writes, but not external side effects. This is documented, and acceptable under the threat model.
- PostgreSQL (v2) needs its own parser that meets the same contract, plus `READ ONLY` transactions. That is decided in v2.
- The classifier's statement-table test suite is the main safeguard against misclassification and grows with every bug found.
