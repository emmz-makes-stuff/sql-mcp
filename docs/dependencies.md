# Dependencies

<!-- Maintained by /emmz:architecture; verified by `make deps`. -->

| Tool | Why | Version | Check | Auth check | Install / auth |
|---|---|---|---|---|---|
| dotnet | build, test, format, pack (net10.0; `global.json` pins SDK 10.0.x so an installed 11 preview isn't picked up) | SDK >=10.0.400, <11 | `dotnet --list-sdks \| grep -q '^10\.0\.[4-9]'` | — | `brew install --cask dotnet-sdk` (or https://dot.net) |
| git | source control | any | `git --version` | — | `brew install git` |
| openspec | change tracking | >=1.15 | `openspec --version` | — | `npm i -g @fission-ai/openspec` |
| gh | GitHub remote, PRs, Actions release workflow | any | `gh --version` | `gh auth status` | `brew install gh`; `gh auth login` |
| actionlint | lints `.github/workflows/*.yml`; the only local check of the release workflow, which can't run before a real tag | any | `actionlint --version` | — | `brew install actionlint` |
| docker | `itest` gate: SQL Server 2025 container through Testcontainers | engine >=24 | `docker --version` | `docker info` | macOS: `brew install orbstack`, then `orb config set rosetta true` (the mssql image is amd64-only) |

NuGet packages (restored by `dotnet restore`, no separate install): ModelContextProtocol, Microsoft.Data.SqlClient,
Microsoft.SqlServer.TransactSql.ScriptDom, System.CommandLine, JsonSchema.Net (config validation), Tomlyn (Codex project registration),
Microsoft.SqlServer.SqlManagementObjects (`script_database` script generation, ADR-0003),
xunit.v3, Testcontainers.MsSql. Versions are pinned in `Directory.Packages.props`.

Release publishing uses nuget.org Trusted Publishing from GitHub Actions (OIDC), so there's no local NuGet login or
long-lived API key.
