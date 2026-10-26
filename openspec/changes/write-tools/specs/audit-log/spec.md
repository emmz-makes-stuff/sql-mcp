# Spec Delta

## MODIFIED Requirements

### Requirement: Events recorded
The log SHALL record:
- `session_start` and `session_end`, including the server version and the config files loaded;
- `executed`: the tool, database, SQL text, classification, duration, and rows returned or affected. For a write tool, `executed` means the batch was committed, and it records the rows affected by each statement;
- `denied`: the tool, database, SQL text if any, and the reason;
- `failed`: an execution error. For a write tool, `failed` means the batch was rolled back, and it records why: an error, a timeout, or the affected-rows cap;
- `introspection`: an introspection call.

#### Scenario: Denied attempt
- **WHEN** a query is rejected because it is classified as command
- **THEN** a `denied` event records the tool, database, SQL and the reason

#### Scenario: Permission denial
- **WHEN** a call is refused because the tool group is denied on that database
- **THEN** a `denied` event records the reason and where it came from

#### Scenario: Committed write
- **WHEN** a `command` batch of two statements affecting 3 and 5 rows is committed
- **THEN** an `executed` event records the tool, database, SQL, classification, and rows affected of 3 and 5

#### Scenario: Write rolled back by the cap
- **WHEN** a `command` batch is rolled back because it affected more rows than the affected-rows cap
- **THEN** a `failed` event records that the cap rolled it back, with the cap and the rows the batch affected, and no `executed` event is written
