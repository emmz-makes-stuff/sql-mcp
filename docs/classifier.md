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
   - pass `SideEffectChecks`, the checks where the meaning depends on more than the node type: `SELECT … INTO`,
     table hint *kinds* in a query, and dynamic or indirect `EXEC`;
   - have a type on its category's allowlist (`Allowlists.For(category)`).

   The first failure rejects the statement, naming the element (through `Phrases`) and its position.
4. **Check the batch** against the tool (`ClassificationResult.ForTool`). Every statement must be in the tool's
   category. Otherwise the whole batch is rejected: "classified as command; use the command tool", or, for a
   batch, each statement that doesn't fit.

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
4. If the node must *never* be accepted in a query, add it to `AllowlistGuardTests.NeverInQueries`.
5. Run `make gates`.

A new top-level statement type also needs an entry in `Allowlists.StatementCategories`, as well as its category's
allowlist. `AllowlistGuardTests` checks that the two stay consistent.

Every bug found in classification becomes a table row first.

## Known gap

A CLR or extended-procedure function called from a SELECT can have side effects outside the database. Query reads
run in a transaction that is always rolled back, which undoes database writes but not external effects. ADR-0002
accepts this under the threat model.
