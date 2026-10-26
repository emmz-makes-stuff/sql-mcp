# Decision: `register` defaults to user scope, project scope writes an absolute `--project`, Gemini trust is respected

<!-- Section: 8 · Base: 6c91478 -->

Emmz's answers (before 8.5):
- **`--scope` defaults to `user`.** It writes no shared file. Claude's own default is `local` and Gemini's is
  `project`, so the strategies always pass `-s` explicitly. *Rejected:* defaulting to `project`; making `--scope`
  required.
- **Project scope adds `--project <absolute cwd>`, as in D10.** *Accepted cost:* the path is machine-specific in a
  usually committed file (`.mcp.json`, `.gemini/settings.json`), so `register` prints a note saying teammates with
  the checkout elsewhere need to register again. *Rejected:* leaving `--project` out, which relies on each
  client's working directory, unverified.
- **Gemini's untrusted-workspace refusal is passed on, never bypassed.** `register gemini --scope project` exits
  non-zero with Gemini's error and the snippet to add by hand. sql-mcp never sets `GEMINI_CLI_TRUST_WORKSPACE`.

Verified locally on 2026-10-25, with probes in the session scratchpad (Claude Code CLI, gemini 0.65.0, .NET 10 `dnx`):
- **`claude mcp add -s project probe -- echo serve --project /p`** writes `.mcp.json` with
  `type: "stdio"`, `command`, and `args` passed through unchanged. `claude mcp get <name>` exits 0 if the server
  exists and 1 if not.
- **`gemini mcp add` does not accept `--`.** It fails with "Not enough non-option arguments". Without `--`, the
  arguments after the command, `--project /p` included, go through to `args` unchanged.
- **In an untrusted folder,** Gemini fails with "Cannot modify settings in an untrusted workspace …".
- **`gemini mcp list`** prints `✓ name: command args (stdio) - Connected` lines, or `✗` for disconnected ones, and
  health-checks each server.
- **This `dnx` has no `--yes` option,** and `--interactive` defaults to false. Whether `dnx` forwards an option
  after the package id (`serve --project /p`) is unverified, because it needs a published package. Check it in
  §10 or after the first release.

## Next

- Build 8.5 to these answers.
