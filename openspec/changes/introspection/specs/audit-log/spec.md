# Spec Delta

## ADDED Requirements

### Requirement: Introspection calls are recorded
Each introspection call that runs SHALL write an `introspection` event with the tool, the database, what was asked for (the filters, the object name, or the object list), the duration, and whether it succeeded. For `script_database`, the event SHALL also record the script's path, the counts by object type, and the number of objects omitted. It SHALL NOT record object definitions or the script text.

#### Scenario: Describe call
- **WHEN** an agent describes `dbo.orders` on `localhost:1433/app`
- **THEN** an `introspection` event records `describe_object`, the database, `dbo.orders` and the duration, and none of the columns or definitions returned

#### Scenario: Script call
- **WHEN** an agent writes a whole-database script that omits one user
- **THEN** an `introspection` event records `script_database`, the database, the script path, the counts by type, and one omitted object

#### Scenario: Failed introspection
- **WHEN** a `list_objects` call times out
- **THEN** an `introspection` event records the call as failed, with the reason
