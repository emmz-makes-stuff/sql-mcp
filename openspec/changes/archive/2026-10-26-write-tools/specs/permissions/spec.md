# Spec Delta

## MODIFIED Requirements

### Requirement: list_databases tool
The system SHALL provide a `list_databases` tool. For each configured database it returns the identifier, whether it is available, the reason if not, the effective allowed and denied groups each with its source, and the result cap, page size, timeout and affected-rows cap.

#### Scenario: Listing shows provenance
- **WHEN** a project file's widening has not been approved
- **THEN** the affected groups are listed as denied, with the source "project config not approved"

#### Scenario: Unavailable database
- **WHEN** a database's connection string references an unset environment variable
- **THEN** it is listed as unavailable, with that reason, and without any part of the connection string

#### Scenario: Affected-rows cap is listed
- **WHEN** `localhost:1433/app` sets `maxAffectedRows` to 50
- **THEN** its listing gives an affected-rows cap of 50
