# Spec Delta

## MODIFIED Requirements

### Requirement: Describing tables and views
For a table or view, `describe_object` SHALL return its columns in order, each with its name, SQL type, nullability, default, and whether it is an identity or computed column (with the expression). A column's default SHALL be given as its expression and, separately, the name of its default constraint, including a name SQL Server generated. For a table it SHALL also return the primary key, unique and check constraints, indexes, triggers, the foreign keys it declares and the foreign keys that reference it.

#### Scenario: Table with keys
- **WHEN** an agent describes `dbo.orders`, which has an identity primary key, a foreign key to `dbo.users`, and is referenced by `dbo.order_lines`
- **THEN** the result gives the columns with types, the primary key, the foreign key to `dbo.users` with its columns, and the foreign key from `dbo.order_lines`

#### Scenario: Temporal table
- **WHEN** an agent describes a system-versioned table
- **THEN** the result names its history table and its period columns

#### Scenario: Named default
- **WHEN** an agent describes `dbo.users`, whose `created_at` column has `CONSTRAINT df_users_created DEFAULT (sysutcdatetime())`
- **THEN** that column gives the default `(sysutcdatetime())` and the default name `df_users_created`

#### Scenario: Default named by SQL Server
- **WHEN** an agent describes a table or table type whose column has a default declared without a name
- **THEN** that column gives the default's expression and the name SQL Server generated for it, unmarked

#### Scenario: Column without a default
- **WHEN** an agent describes a table whose column has no default
- **THEN** that column has neither a default nor a default name
