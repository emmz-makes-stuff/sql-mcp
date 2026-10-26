# The statement classifier

sql-mcp decides which tool may run a piece of SQL by parsing it, never by matching keywords (ADR-0002, design
D6). This page explains how the SQL Server classifier works, and how to let it accept syntax it rejects today.

## How a batch is classified

`SqlServerStatementClassifier` (`src/SqlMcp/Classification/SqlServer/`):

1. **Parse** the batch with ScriptDom's `TSql170Parser`. Any parse error rejects the whole batch, giving the
   parser's line, column and message. A `GO` token anywhere also rejects it: SqlClient can't run `GO`, and
   splitting batches silently would hide what runs.
2. **Pick a candidate category** for each top-level statement from `Allowlists.StatementCategories` (for example
   `SelectStatement` → query, `UpdateStatement` → command). A statement type that isn't in the map, such as
   GRANT, USE or SET, is rejected by every tool.
3. **Walk every node** in the statement. A `TSqlFragmentVisitor` override of `Visit(TSqlFragment)` sees each
   node, whatever its type. Each node must:
   - pass `SideEffectChecks`, the checks where the meaning depends on more than the node type: `SELECT … INTO`
     and rowset functions in every category, table hint *kinds* in a query, the write rules (see "Write
     categories"), and dynamic or indirect `EXEC`;
   - have a type on its category's allowlist (`Allowlists.For(category)`).

   The first failure rejects the statement, naming the element (through `Phrases`) and its position.
4. **Check the batch** against the tool (`ClassificationResult.ForTool`). Every statement must be in the tool's
   category. Otherwise the whole batch is rejected: "classified as command; use the command tool", or, for a
   batch, each statement that doesn't fit. The pipeline passes the categories whose group is allowed on the
   target database; when that category's group is denied there, the reason says so instead: "classified as
   command; the command tool is denied on this database".

Because the walk is an allowlist, syntax nobody has seen yet, including node types added by a future ScriptDom
version, is rejected until someone allows it on purpose. This fails closed.

## Letting the classifier accept new syntax

A type joins an allowlist **only alongside a test that needs it**.

1. Add a row to `tests/SqlMcp.Tests/Classification/ClassifierTable.cs`: under `Accepted` for a statement that
   should run, or under `Rejected` with a fragment of the expected reason. Run `make test`. The failure message
   names the node type that was rejected, for example
   `table sample clause (TableSampleClause) at line 1, column 15 is not allowed in query statements`.
2. Decide whether that node is safe in that category. For a **query**, ask: can it create or change state, take
   update or exclusive locks, reach another server, or run SQL that can't be seen when parsing? If so, it stays
   rejected. Give it a clear reason in `Phrases` or `SideEffectChecks`, and add a `Rejected` row.
3. If it is safe, add the type to the category's set in `Allowlists.cs`, in the group it belongs to. If its
   safety depends on a property (as table hints do), add a case to `SideEffectChecks` as well.
4. If the node must *never* be accepted in a query, add it to `AllowlistGuardTests.NeverInQueries`. If no
   category may accept it, add it to `NeverAnywhere`. A statement no tool may run goes in `NeverRun`.
5. Run `make gates`.

A new top-level statement type also needs an entry in `Allowlists.StatementCategories`, as well as its category's
allowlist. `AllowlistGuardTests` checks that the two stay consistent.

Every bug found in classification becomes a table row first.

## Write categories

The `command`, `ddl` and `execute` tools change the database, so their allowlists are deliberately narrow. Every
example below is a row in `ClassifierTable`.

### What each category accepts

- **command:** INSERT, UPDATE, DELETE and MERGE. Their reads use the whole query allowlist (the `Command` set
  starts with `.. Query`), plus the DML nodes: targets, `OUTPUT` and `OUTPUT INTO` a table, the MERGE actions
  (including `WHEN NOT MATCHED BY SOURCE`), and `NEXT VALUE FOR`. Table hints aren't kind-checked here, because
  the write takes those locks anyway.
  - Accepted: `UPDATE o SET o.status = 'late' FROM dbo.orders o JOIN dbo.customers c ON c.id = o.customer_id WHERE c.region = 'EU'`.
  - Accepted: `INSERT INTO dbo.t (id, name) VALUES (NEXT VALUE FOR dbo.seq, 'a')`.
  - Rejected: `OUTPUT … INTO @ids`. `DECLARE` is rejected, so a batch can never have a table variable.
- **ddl:** declarative objects only. That means tables, columns, constraints (keys, `DEFAULT`, `CHECK`), computed
  columns, indexes, views, schemas and sequences, plus `TRUNCATE TABLE`.
  - Defaults, `CHECK` constraints and computed columns may use only the `Scalars` set: names, literals, scalar
    expressions and predicates, with no subqueries. They may also use `NEXT VALUE FOR`, which a sequence-backed
    default advances only when a row is inserted.
  - **A view's SELECT is walked under the query category**, so a view can't hide what a query couldn't do.
    `CREATE VIEW dbo.v AS SELECT * FROM dbo.t WITH (UPDLOCK)` is rejected with "the view's SELECT is not a valid
    query: …".
  - `CREATE SCHEMA` is accepted only bare (`CREATE SCHEMA reporting`). `AUTHORIZATION` sets an owner, which is a
    security change. Schema elements must each be sent as their own statement. `ALTER SCHEMA … TRANSFER` is
    rejected, because moving an object between schemas can change who may use it.
  - **View and schema statements go alone.** SQL Server runs `CREATE VIEW`, `ALTER VIEW`, `CREATE OR ALTER VIEW`
    and `CREATE SCHEMA` only as the only statement in a batch (Msg 111), and ScriptDom enforces that as a syntax
    error. Such a batch is refused before anything is sent. The reason starts with, for example, "CREATE VIEW must
    be the only statement in its call: send it on its own", then gives the parser's errors.
- **execute:** `EXEC` of a user procedure, with parameters given by position or by name. Each value must be a
  literal, `NULL` or a negated number: `EXEC dbo.p NULL, -5, 1.5, N'x', 0x1F`. The parser already refuses
  expressions such as `1 + 2` as parameter values. The classifier refuses variables (`EXEC dbo.p @a = @x`) and
  return-value assignment (`EXEC @rc = dbo.p`).

### The write rules

- **WHERE rule:** UPDATE and DELETE need a `WHERE` clause. `DELETE FROM dbo.users` is rejected with "DELETE needs
  a WHERE clause that picks the rows to change".
  - Two cases are exempt. MERGE is limited by its `ON` clause. An UPDATE or DELETE whose target is a CTE of the
    same statement is filtered by the CTE's own query, as in
    `WITH x AS (SELECT a FROM dbo.t WHERE b = 1) UPDATE x SET a = 0`.
  - The rule catches a forgotten filter, not a deliberate one: `WHERE 1 = 1` passes it. The affected-rows cap
    (`maxAffectedRows`) is the real backstop. The reason never suggests an always-true filter.
- **Names rule:** a write may only name objects in the target database, because grants are given per database.
  In command, ddl and execute, a name with a database or server part (`INSERT INTO sales.dbo.orders …`,
  `EXEC remote.app.dbo.p`), or a column reference of four or more parts, is rejected. Queries are unchanged, so
  `SELECT a FROM otherdb.dbo.t` is still accepted, and so is a view's SELECT.
- **System-procedure rule:** execute refuses the `sys` schema and any procedure whose name starts with `sp_` or
  `xp_`, ignoring case. That covers `EXEC sp_addrolemember 'db_owner', 'bob'`, `EXEC sys.sp_configure`,
  `EXEC xp_cmdshell 'dir'` and `EXEC SP_WHO`. A user procedure named `sp_…` is refused too, because SQL Server
  resolves `sp_` names in `master` first.

### Why programmable objects are refused

`CREATE`, `ALTER` and `CREATE OR ALTER` of a procedure, function or trigger are refused, with the reason
"procedures, functions and triggers can't be created through sql-mcp". Their bodies run later, when something
calls or fires them. The classifier would only see the definition, not the run, so a body such as
`EXEC('DROP TABLE t')` could do anything. These statements, and database- and security-level DDL (`CREATE USER`,
`CREATE DATABASE`, roles, logins, `ALTER AUTHORIZATION`), are in `AllowlistGuardTests.NeverRun`.

### The procedure trust gap

An `execute` grant trusts the database's existing procedures (ADR-0002): the classifier checks the `EXEC`, never
the procedure body. Two consequences:
- A procedure that runs `SET NOCOUNT ON` hides its row counts, so the affected-rows cap can't see them.
- A procedure that issues its own `ROLLBACK` or `COMMIT` can end the transaction that sql-mcp opened, so its
  changes no longer depend on sql-mcp's all-or-nothing commit.

Grant `execute` only on databases whose procedures you trust.

## Known gaps

- A CLR or extended-procedure function called from a SELECT can have side effects outside the database. Query
  reads run in a transaction that is always rolled back, which undoes database writes but not external effects.
  ADR-0002 accepts this under the threat model.
- A three-part scalar function call, such as `otherdb.dbo.fn(x)`, gets past the names rule in a write. ScriptDom
  gives it the same shape as a method call on a column (`n.c.value(...)`, `geo.STDistance(...)`), so syntax can't
  tell them apart. A scalar function can only read, which `query` allows anyway (accepted 2026-10-26).
