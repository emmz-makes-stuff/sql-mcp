# Spec Delta

## MODIFIED Requirements

### Requirement: init
`sql-mcp init` SHALL create `.sql-mcp/config.json` (an empty database list, with a `$schema` reference) and `.sql-mcp/.gitignore` (containing `logs/` and `scripts/`) in the current directory. It SHALL NOT overwrite an existing config file.

#### Scenario: Fresh project
- **WHEN** `sql-mcp init` runs in a directory without `.sql-mcp/`
- **THEN** both files are created, and the command exits 0

#### Scenario: Already initialised
- **WHEN** `.sql-mcp/config.json` already exists
- **THEN** it is left unchanged, the command says so, and exits 0
