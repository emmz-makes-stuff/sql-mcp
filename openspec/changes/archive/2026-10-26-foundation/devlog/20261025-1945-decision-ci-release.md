# §9 decisions: licence, push, CI trigger, release auth

<!-- Section: 9 · Tasks: 9.1–9.3 · Base: 1e472b5 -->

Emmz's answers:
- **Licence: MIT.** `LICENSE` reads "Copyright (c) 2026 Emmz". The csproj sets `PackageLicenseExpression` MIT,
  `Authors` Emmz, and `PackageProjectUrl`/`RepositoryUrl` to the GitHub repo. The README ends with a `## Licence`
  section.
- **9.1: the repo already existed**, public, created 2026-10-25 16:12, with `main` at `56061b5` (the planning
  commit). Emmz chose to **push `change/foundation` and open a PR later**, leaving `main` alone until the change is
  reviewed and merged. Pushed at `1e472b5` and verified with `git ls-remote` and `gh repo view`.
- **CI trigger: pull requests to `main` and pushes to `main` only.** This is narrower than D12's "push and PR".
  Pushes to `change/*` run nothing, so 9.2 is verified with a **draft PR** to `main`, which Emmz approved. It stays a
  draft until the whole-change review.
- **NuGet auth: `secrets.NUGET_USER` plus a `release` GitHub environment.** The nuget.org policy is restricted to
  that environment, and the environment can require Emmz's approval for each publish.
- **Actions are pinned to major tags:** `actions/checkout@v7` (v7.0.1), `actions/setup-dotnet@v6` (v6.0.0) and
  `NuGet/login@v1` (v1.2.0). These were the latest releases on 2026-10-25.
- **actionlint is now a dependency.** It has a row in `docs/dependencies.md` and a check in `make deps`. It's the
  only local check of `release.yml`, which can't run before a real tag.

My own choices, which Emmz approved before the commit:
- `release.yml` has a `gates` job, then a `publish` job (`needs: gates`, `environment: release`,
  `id-token: write`) that packs, logs in and pushes. Login comes just before the push, because the key lasts an hour.
- The push source is `https://api.nuget.org/v3/index.json`. The docs example uses the v2 package URL; both work.
- The API key goes to `dotnet nuget push` through `env:`, not `${{ }}` interpolated into the script.
- CI runs `make build`, `make format`, `make test` and `make itest`, in that order, so the logs show each
  `LABEL_EXIT` and a formatting failure shows before the slow itest. `validate` stays local, because it needs
  openspec.

Checked:
- From the NuGet/login README and `action.yml`: the input is `user`, the output is `NUGET_API_KEY`, and
  `id-token: write` is needed.
- From the nuget.org Trusted Publishing docs: the policy fields are owner, repo, workflow *file name only*, and an
  optional environment. A policy can start as "temporarily active for 7 days" until the first publish locks the
  repo IDs. The README has the setup steps.
- `dotnet pack` with `-p:Version="${GITHUB_REF_NAME#v}"` and `GITHUB_REF_NAME=v0.0.1-ci.1` produced
  `Emmz.SqlMcp.0.0.1-ci.1.nupkg`, whose nuspec has the MIT expression, authors, projectUrl and repository commit.

## Next

- Commit §9, push, open the draft PR, and run `gh run watch` on its CI run to verify 9.2.
