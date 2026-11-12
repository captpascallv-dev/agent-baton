# Portable quota snapshot CLI

.NET 8 console frontend for the existing HUD Core parser and schema-1 mapper.
It does not use WPF and adds no third-party packages. Run commands from the
repository root; replace `dotnet` with a user-local SDK path if needed.

## Offline first

```sh
dotnet run --project hud/src/CodexUsageHud.Cli -- --self-test
dotnet run --project hud/src/CodexUsageHud.Cli -- --demo --output-dir /tmp/hud-demo
dotnet run --project hud/src/CodexUsageHud.Cli -- --fixture fixture.json --output-dir /tmp/hud-fixture
```

Demo and fixture modes never discover or launch Codex and require an explicit
output directory. Use a separate scratch directory, not a production snapshot
location. Fixture input is an app-server quota JSON response, not a snapshot.
Both offline modes mark `hud_version` with `-demo`.

Exactly one of `--demo`, `--fixture FILE`, or `--live` is required for collection.
One sample is the default. Add `--watch` to sample every 60 seconds; Ctrl+C stops
it. Each successful sample writes one compact JSON line to stdout and replaces
`quota-snapshot.json` through a temporary file in the same directory. This avoids
partially written JSON for readers; it is not a promise of crash/power-loss durability.

## Live collection is an explicit opt-in

**Do not use `--live` until actual Codex quota access has been authorized.**
The implementation exists, but offline validation is not evidence of live access
working. Live collection launches the discovered Codex app-server and requests
rate limits only, not account identity or a coding task. That process may use the
user's existing authentication; `--live` is not a sandbox or an approval mechanism.

After authorization, run `dotnet run --project hud/src/CodexUsageHud.Cli -- --live`.
Optional `--codex /absolute/path/to/codex` selects an executable; otherwise existing
Core discovery checks configured overrides, macOS desktop app bundles, and PATH.
Only select a trusted official executable. No executable found produces an
unavailable snapshot. `--output-dir DIR` can override the default live location:

- macOS: `~/Library/Application Support/CodexUsageHUD`
- Other systems: `LocalApplicationData/CodexUsageHUD`

## Snapshot contract and validation

Schema remains `1`, with `written_at_utc`, `hud_version`, and the existing six
public slots. Only Codex primary is enabled; other slots are disabled. The Core
mapper allowlists pool names and fields. Unknown percent/reset values stay null,
not zero. No account identity, email, token, or source path belongs in output.
The SwiftUI viewer reads this file independently; this CLI does not start it.

`--self-test` is portable and offline. It covers parser/mapping, schema and disabled
slots, private sentinel exclusion, unknown-pool exclusion, null fields, invalid
JSON/unavailable output, replacement and cancellation cleanup. Its file tests use
an owned temporary directory and remove it afterward. It does not invoke Codex.

The existing Windows/WPF test suite is separate and is **not run by this command**.
Do not report a portable self-test pass as a Windows suite or live-integration pass.
CLI exits 0 on success/cancellation, 2 for argument errors, and 1 for other failures.
Failure messages deliberately omit private paths and upstream exception details.
