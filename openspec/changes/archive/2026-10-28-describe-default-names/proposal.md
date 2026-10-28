# Proposal

## Why

`describe_object` gives a column's default only as its expression, such as `('N')`, not the constraint's name, such
as `DF_SRM_ENTITIES_DELETED`. `script_database` does emit the name. So an agent that reads a table through
`describe_object` and then writes an `ALTER TABLE … DROP CONSTRAINT` has to guess the name, or query `sys.*` itself.
This came up in a smoke test against a real database on 2026-10-28, the first run after the `0.1.0-alpha.1` release.
Every other named constraint (primary key, unique, check, foreign key) already comes back with its name.

## What Changes

- Each column with a default gets a new `default_name` field: the default constraint's name, next to the existing
  `default` expression. This applies to the columns of tables and of table types.
- The field is additive. `default` keeps its string shape, and a column without a default has neither field (null
  fields are left out, as now). Emmz's call, 2026-10-28.
- A name SQL Server generated, such as `DF__orders__status__3B75D760` (always the case for a table type's
  columns), is returned as it is, unflagged. Emmz's call, 2026-10-28.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `schema-introspection`: the "Describing tables and views" requirement adds the default constraint's name to each
  column that has a default.

## Impact

- `SqlServerCatalogReader.ColumnsSql`: read `dc.name` alongside `dc.definition`.
- `ColumnDetails` (`CatalogModel.cs`), and its JSON in `describe_object`'s response: a new `default_name`.
- Tests: the describe itests against the seeded database (a named default, a system-named default, a table type's
  column), and the response serialisation unit tests.
- README: describe what `describe_object` returns for a column, if the README lists its fields.
- No new dependencies, no config changes, no change to `script_database`.
