# Decision: `init` writes the raw GitHub `$schema` URL on main; Invalid config is tested in two steps

<!-- Section: 8 · Base: 6c91478 -->

Emmz's answers (before 8.1):
- **`$schema` URL:** `https://raw.githubusercontent.com/emmz-makes-stuff/sql-mcp/main/src/SqlMcp/schema/config.schema.json`.
  `origin` already points at `git@github.com:emmz-makes-stuff/sql-mcp.git`, so this no longer waits on 9.1. The
  schema gets a matching `$id`. A test keeps `$id` and `ProjectLayout.SchemaUrl` in step.
  - Editors only resolve the URL once the repo is public.
  - *Rejected:* a URL pinned to the release tag, because dev builds would point at a tag that doesn't exist.
  - *Rejected:* no `$schema` until §9, which departs from the spec.
- **"Invalid config" (Exit codes) is tested in two steps.** In 8.1 it goes through the shared `CliAction` handler,
  with a real invalid file loaded by `ConfigDiscovery`. Section 8.3 adds the same scenario against `config show`.

Shape of 8.1, as agreed:
- `CliContext` holds the directories and environment. Output goes through `InvocationConfiguration.Output` and
  `Error`.
- `CliAction.Set` turns `CliException`, `ConfigException`, `IOException` and `UnauthorizedAccessException` into
  their message on stderr and exit 1. Anything else falls through to System.CommandLine's handler.
- `init` with an existing config leaves the config untouched, but still creates a missing `.gitignore`.

## Next

- Continue §8 with task 8.2, the JSONC splice editor (D5).
