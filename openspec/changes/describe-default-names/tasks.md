## 1. Default constraint names in describe_object

- [x] 1.1 Read each column's default constraint name with its definition (`dc.name` in the columns query), and carry it on the column as `DefaultName`, given only when the column has a default. Verify that `describe_object`'s JSON has `default_name` next to `default`, and leaves both out for a column without a default, with a unit test in `IntrospectionPipelineTests`
- [x] 1.2 Give the seed a default SQL Server names itself: an unnamed default on a table-type column (`dbo.id_list` or `crm.address`), and one on a table column if no seeded table has one. Verify that the whole-database script, inventory and round-trip itests still pass, with their expected counts updated only where the new defaults change them
- [x] 1.3 Integration tests: `dbo.orders` gives `default_name` `df_orders_number`, and `dbo.users` gives `df_users_created`. The unnamed defaults from 1.2 give a non-empty generated name that matches `sys.default_constraints`. Verify with `make itest`
- [x] 1.4 README: if it lists what `describe_object` returns for a column, add the default's name. Verify with `make gates`

## Workflow follow-up

- Whole-change self-review, the change-done devlog note, and an offer of `code-review` before archiving.
- `/opsx:archive` once the review is done or declined.
