# Stopped: CI can't install .NET (aka.ms → dotnetorigin 409)

<!-- Section: 9 · Tasks: 9.2 · Base: 1e472b5 -->

State:
- §9 is committed as `6222620`, pushed, with **9.1–9.3 still unticked**. Draft PR #1
  (https://github.com/emmz-makes-stuff/sql-mcp/pull/1) is open from `change/foundation` to `main`.
- `actionlint` passes (`ACTIONLINT_EXIT:0`), and locally `GATES_EXIT:0`. The decisions are in
  `20261025-1945-decision-ci-release.md`.

What blocked:
- CI run 40227599494 failed in `actions/setup-dotnet@v6`, on attempts 1 and 2: "Could not find `.NET Core SDK` with
  version = 10.0.403". No gate ran.
- setup-dotnet turns `global.json` (10.0.400, `latestFeature`) into `install-dotnet.sh --channel 10.0`. The
  script downloads through `aka.ms/dotnet/10.0.4xx/dotnet-sdk-linux-x64.tar.gz`, which redirects (301) to
  `dotnetorigin.blob.core.windows.net/...`. **That host returns 409 for every version I tried, 10.0.402
  included.**
- The same tarballs return 200 from `builds.dotnet.microsoft.com` and `dotnetcli.azureedge.net`.
- 10.0.403 and runtime 10.0.14 were released today (2026-10-25), so this is probably a Microsoft-side redirect
  fault on release day, not our workflow. CLAUDE.md's two-honest-attempts rule applies.

Question for Emmz:
- **Wait and rerun.** No change; `gh run rerun 40227599494 --failed` once aka.ms is fixed.
- **Pin an exact version.** `dotnet-version: 10.0.403` in both workflows: an exact version makes the script use
  the feed URLs (which work) instead of the aka.ms link. It needs bumping on each SDK release, and drifts from
  `global.json`.
- **Something else.**

## Next

- Ask Emmz the question above, then rerun or edit and watch: `gh run watch <id> --exit-status`.
- On a green run: tick 9.1–9.3, write the §9 section-done note, `git commit --amend` the §9 commit, and
  `git push --force-with-lease` (the branch is Emmz's own draft PR). Then hand off, and §10.1 is next.
