# Spec Delta

## ADDED Requirements

### Requirement: Write outcomes are recorded
For a write tool, an `executed` event SHALL mean the batch was committed, and SHALL record the rows affected by each statement. A `failed` event SHALL mean the batch was rolled back, and SHALL record why: an error, a timeout, or the affected-rows cap. When sql-mcp can't know whether the batch was committed, the `failed` event SHALL record that the outcome is unknown.

#### Scenario: Committed write
- **WHEN** a `command` batch of two statements affecting 3 and 5 rows is committed
- **THEN** an `executed` event records the tool, database, SQL, classification, and rows affected of 3 and 5

#### Scenario: Write rolled back by the cap
- **WHEN** a `command` batch is rolled back because it affected more rows than the affected-rows cap
- **THEN** a `failed` event records that the cap rolled it back, with the cap and the rows the batch affected, and no `executed` event is written

#### Scenario: Write with an unknown outcome
- **WHEN** an `execute` batch runs a procedure that ends sql-mcp's transaction with its own COMMIT or ROLLBACK
- **THEN** a `failed` event records that the outcome is unknown, and no `executed` event is written
