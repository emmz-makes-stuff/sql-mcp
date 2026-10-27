# Spec Delta

## Purpose

Lets agents find out what a database holds, and how each object is defined, without writing catalog queries of their own and without being able to change anything.

## ADDED Requirements

### Requirement: list_objects tool
The system SHALL provide a `list_objects` tool that takes a database identifier and lists the database's user objects: tables, views, procedures, functions, triggers, sequences, synonyms, user-defined types and table types. Each entry SHALL give the schema, name and object type, and for a table its approximate row count. System objects SHALL NOT be listed.

#### Scenario: List a database
- **WHEN** an agent calls `list_objects` with `localhost:1433/app`, which holds the table `dbo.users`, the view `sales.open_orders` and the procedure `dbo.archive_orders`
- **THEN** all three are listed with their schema, name and type, and `dbo.users` has an approximate row count

#### Scenario: System objects are left out
- **WHEN** an agent lists any database
- **THEN** no object from the `sys` or `INFORMATION_SCHEMA` schemas, and no object marked as shipped with SQL Server, is listed

### Requirement: Filtering the object list
`list_objects` SHALL accept an optional schema, an optional list of object types, and an optional name pattern in which `*` matches any run of characters and `?` matches one character. Matching SHALL ignore case. Only objects that match every filter given SHALL be listed.

#### Scenario: Filter by type and pattern
- **WHEN** an agent calls `list_objects` with the type `table` and the pattern `order*`
- **THEN** only tables whose names start with "order", in any case, are listed

#### Scenario: Unknown object type
- **WHEN** an agent passes the type `package`
- **THEN** the call fails with an error that lists the valid object types

### Requirement: Object list is capped
`list_objects` SHALL return at most the database's row cap of entries. When more objects match, the result SHALL be marked truncated, SHALL give the number of matching objects, and SHALL tell the agent to filter by schema, type or name.

#### Scenario: More objects than the cap
- **WHEN** 3,000 objects match and the row cap is 1000
- **THEN** 1000 entries are returned, `truncated` is true, the total of 3,000 is given, and the message says to filter

### Requirement: describe_object tool
The system SHALL provide a `describe_object` tool that takes a database identifier and an object name, and returns the object's type and the details for that type. The name SHALL be `schema.name`, with or without brackets. An unqualified name SHALL be accepted only when exactly one object has that name.

#### Scenario: Ambiguous unqualified name
- **WHEN** an agent calls `describe_object` with `orders`, and both `dbo.orders` and `archive.orders` exist
- **THEN** the call fails with an error naming both, and asking for a schema-qualified name

#### Scenario: Unknown object
- **WHEN** an agent calls `describe_object` with `dbo.nothing_here`
- **THEN** the call fails with an error saying no such object exists, and suggesting `list_objects`

### Requirement: Describing tables and views
For a table or view, `describe_object` SHALL return its columns in order, each with its name, SQL type, nullability, default, and whether it is an identity or computed column (with the expression). For a table it SHALL also return the primary key, unique and check constraints, indexes, triggers, the foreign keys it declares and the foreign keys that reference it.

#### Scenario: Table with keys
- **WHEN** an agent describes `dbo.orders`, which has an identity primary key, a foreign key to `dbo.users`, and is referenced by `dbo.order_lines`
- **THEN** the result gives the columns with types, the primary key, the foreign key to `dbo.users` with its columns, and the foreign key from `dbo.order_lines`

#### Scenario: Temporal table
- **WHEN** an agent describes a system-versioned table
- **THEN** the result names its history table and its period columns

### Requirement: Describing code objects
For a view, procedure, function or trigger, `describe_object` SHALL return its definition. For a procedure or function it SHALL also return its parameters in order, each with name, SQL type, direction and whether it has a default, and a function's return type. A definition that can't be read SHALL be reported as unavailable with the reason, never left out silently.

#### Scenario: Procedure
- **WHEN** an agent describes `dbo.archive_orders`, which takes `@before date`
- **THEN** the result gives the parameter `@before` of type `date`, input, and the procedure's definition

#### Scenario: Encrypted procedure
- **WHEN** an agent describes a procedure created `WITH ENCRYPTION`
- **THEN** the result gives its parameters, and says its definition is unavailable because it is encrypted

### Requirement: Describing other object types
For a sequence `describe_object` SHALL return its type, start, increment, bounds and whether it cycles. For a synonym, it SHALL return the object it points to. For a user-defined type, the base type and nullability. For a table type, its columns and constraints, as for a table.

#### Scenario: Synonym
- **WHEN** an agent describes the synonym `dbo.customers` that points at `crm.customers`
- **THEN** the result gives the target `crm.customers`

### Requirement: Introspection is read-only and permission-checked
The introspection tools SHALL be in the `introspection` group, and SHALL check it on every call as other tools do. They SHALL run only fixed catalog queries, never SQL from the agent, and SHALL NOT change the database. They SHALL be subject to the database's command timeout, and SHALL be marked to MCP clients as read-only.

#### Scenario: Introspection denied
- **WHEN** an agent calls `list_objects` on a database where `introspection` is denied
- **THEN** no connection is opened, and the error says introspection is denied and why

#### Scenario: Catalog query times out
- **WHEN** a catalog read runs longer than the database's timeout
- **THEN** it is cancelled, and the error says it timed out and gives the limit

### Requirement: Introspection tool descriptions
The introspection tool descriptions SHALL tell the agent to use `list_objects` with filters to find objects and `describe_object` for details, and that only objects the database login can see are shown.

#### Scenario: Agent reads the descriptions
- **WHEN** a client lists the server's tools on a database where introspection is allowed
- **THEN** `list_objects` and `describe_object` are listed, marked read-only, and their descriptions say how to filter and that visibility depends on the login
