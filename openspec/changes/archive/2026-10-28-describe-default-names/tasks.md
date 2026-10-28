## 1. Default constraint names in describe_object

- [x] 1.1 Read each column's default constraint name with its definition (`dc.name` in the columns query), and carry it on the column as `DefaultName`, given only when the column has a default. Verify that `describe_object`'s JSON has `default_name` next to `default`, and leaves both out for a column without a default, with a unit test in `IntrospectionPipelineTests`
- [x] 1.2 Give the seed a default SQL Server names itself: an unnamed default on a table-type column (`dbo.id_list` or `crm.address`), and one on a table column if no seeded table has one. Verify that the whole-database script, inventory and round-trip itests still pass, with their expected counts updated only where the new defaults change them
- [x] 1.3 Integration tests: `dbo.orders` gives `default_name` `df_orders_number`, and `dbo.users` gives `df_users_created`. The unnamed defaults from 1.2 give a non-empty generated name that matches `sys.default_constraints`. Verify with `make itest`
- [x] 1.4 README: if it lists what `describe_object` returns for a column, add the default's name. Verify with `make gates`

## 2. Review fixes: test precision and wording

- [x] 2.1 The round trip blanks a system-named default only as the value of `default_name`, not any `DF__…` string in the JSON. Verify with `make itest`
- [x] 2.2 The reader builds `ColumnDetails` with named arguments, so `Default` and `DefaultName` can't be swapped silently. Verify with `make build`
- [x] 2.3 The spec's "Named default" scenario uses a seeded named default (`dbo.users.created_at`, `df_users_created`). Verify with `make validate`
- [x] 2.4 Rewrap the seed's header comment to the width of the lines around it. Verify with `make gates`

## Workflow follow-up

- Whole-change self-review, the change-done devlog note, and an offer of `code-review` before archiving.
- `/opsx:archive` once the review is done or declined.
