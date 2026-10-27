# ADR-0003: Introspection: our own metadata model, SMO for the creation script

- Status: Accepted
- Date: 2026-10-25

## Context

The introspection tools must expose structured metadata, and produce a creation script complete enough to rebuild
the database: schema objects, code objects, and the less common object types (UDTs, table types, synonyms,
partitioning, full-text, temporal tables, extended properties…). Security principals are excluded. The metadata
shape has to carry over to PostgreSQL in v2. The script must be in dependency order.

## Decision

- **Metadata tools:** an engine-neutral model of our own, filled by direct catalog queries (`sys.*`), and shaped for agents.
- **Creation-script tool:** an `IScriptGenerator` interface. For SQL Server it is implemented with
  `Microsoft.SqlServer.SqlManagementObjects` (SMO) `Scripter` (181.x, net10.0), whose dependency walker handles ordering.
- **Completeness check:** list every object in the catalog and compare it with what was scripted. Every object is
  either in the script or named in an "omitted" list with a reason. Nothing is dropped silently.
- **Acceptance:** a round-trip integration test. Script a seeded database, run the script against an empty one,
  introspect both, and compare.
- **Gate:** change 3 starts with a spike that runs SMO `Scripter` on macOS against a SQL Server 2025 container
  holding a UDT, a table type, a temporal table and a partitioned table. If it fails, we fall back to DacFx and revisit this ADR.

## Alternatives rejected

- Hand-written scripting for every object type: a long tail of ordering and correctness bugs, which is exactly the
  "complete" problem.
- DacFx as the primary choice: extraction fails on databases with broken references, it is heavier, and its model
  is awkward to expose to agents. It remains the fallback.
- SMO for the metadata tools as well: slow, round-trip heavy, and specific to SQL Server.

## Consequences

- SMO is a large dependency, used only by the script tool.
- PostgreSQL (v2) needs its own `IScriptGenerator`.
