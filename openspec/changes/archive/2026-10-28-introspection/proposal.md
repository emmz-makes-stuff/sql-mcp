# Proposal

## Why

The `introspection` group has been granted by default since foundation, but no tool uses it. To find out what a
database holds, an agent has to write its own `sys.*` queries through `query`, which is slow, easy to get wrong,
and gives no reliable way to get a script that rebuilds the schema. This is change 3 of v1, and ADR-0003 has
already settled the approach: our own metadata model for browsing, SMO for the creation script.

## What Changes

- **New `list_objects` tool.** Lists a database's user objects: tables, views, procedures, functions, triggers,
  sequences, synonyms, user-defined types and table types. It can filter by schema, object type and a name
  pattern. Each entry gives the schema, name and type, and an approximate row count for tables. Long lists are
  capped, with a message telling the agent to filter.
- **New `describe_object` tool.** Describes one object in an engine-neutral shape:
  - columns, with type, nullability, defaults, identity and computed expressions;
  - the primary key, unique and check constraints, foreign keys both ways, and indexes;
  - triggers on a table;
  - parameters and return type for procedures and functions;
  - the definition of views, procedures, functions and triggers. An encrypted definition is reported as
    unavailable, not left out silently.
- **New `script_database` tool.** Produces a T-SQL creation script, schema only, no data:
  - With no object list, it scripts the whole database in dependency order. It checks completeness against the
    catalog: every object is either in the script or named in an "omitted" list with a reason.
  - With an object list, it scripts just those objects, without their dependencies.
  - Security principals (users, roles, permissions) are left out, and are named in the omitted list.
- **Scripts go to a file, not through MCP.** The script is written to `.sql-mcp/scripts/<database>-<UTC time>.sql`.
  The tool returns the path, the counts by object type, and the omitted list. `scripts/` is git-ignored like
  `logs/`. Without a project directory, scripts go in the user-level state directory, as the audit log does.
- **All three tools are in the `introspection` group.** They are offered when introspection is allowed on at
  least one database, and the permission is checked on every call. They run fixed catalog queries, never agent
  SQL, so they don't go through the classifier. All three are annotated read-only. `script_database` writes a
  local file, but only a new, git-ignored one, so it is annotated read-only too (Emmz's call, 2026-10-26).
- **Audit:** each call writes an `introspection` event with the tool, the database and what was asked for. For a
  script, the event also records the file path, the object counts and the omitted count.
- **`sql-mcp init`** writes `scripts/` into `.sql-mcp/.gitignore` as well as `logs/`.

## Capabilities

### New Capabilities
- `schema-introspection`: the `list_objects` and `describe_object` tools. Covers the engine-neutral metadata model,
  filtering, the list cap, and how unavailable definitions are reported.
- `creation-script`: the `script_database` tool. Covers whole-database and chosen-object scripts, dependency order,
  the completeness check and omitted list, writing the script to a git-ignored file, and what it returns.

### Modified Capabilities
- `audit-log`: what an `introspection` event records.
- `cli`: `init` also git-ignores `scripts/`.

## Impact

- **Code:**
  - a new `Introspection/` area: the metadata model, an `ICatalogReader` with a SQL Server implementation over
    `sys.*`, and an `IScriptGenerator` with an SMO implementation;
  - `Mcp/` (`ToolCatalog`, `ToolPipeline`, `ToolResponses`) for the three tools;
  - `Audit/` (`IntrospectionEvent` gains the script details);
  - `Cli/InitCommand` and `Configuration/ProjectLayout` for the scripts directory.
- **Dependencies:** `Microsoft.SqlServer.SqlManagementObjects`, already listed in `docs/dependencies.md` for change
  3. ADR-0003 makes it conditional on a spike, which comes first: SMO `Scripter` on macOS against SQL Server 2025
  with a UDT, a table type, a temporal table and a partitioned table. If the spike fails, we stop and revisit the
  ADR (DacFx is the fallback).
- **Tests:** unit tests for the model, the response shapes and the completeness check. Integration tests seed a
  database with every supported object type. They include ADR-0003's round trip: script the seeded database, run
  the script against an empty one, introspect both, and compare.
- **Docs:** the README's tool section, which currently says introspection tools come in a later release.
- **Out of scope:**
  - data in the script (`INSERT`s);
  - scripting chosen objects together with their dependencies;
  - security principals and permissions;
  - server-level objects (logins, linked servers, Agent jobs);
  - PostgreSQL, which is v2 and needs its own catalog reader and script generator.
