# Cross-platform core regression project

This net8.0 project links the existing CliWatch.Tests sources and the pure C#
presentation helpers. It does not modify the Windows test or WPF projects.

Excluded build inputs (not test successes): `DetailsView.xaml.cs`,
`HudView.xaml.cs`, both XAML views, and the Windows desktop App. Native Windows
process inspection and WPF rendering are not exercised by this project.

Existing tests are linked with `MAC_PORT_TESTS` defined only in this project.
One Windows-only data row is explicitly **not included**: `broken = "reparse"`
in `CliWatchPromptFileTests.Broken_prompt_identity_does_not_bind`, which uses
`cmd.exe /c mklink /J`. The other seven data rows remain. This exclusion is not
a test success. The expected included case count is 234.

The Mac build uses POSIX fake PI entry paths (including a space to retain quoted
argument coverage), keeping all identity/model/artifact assertions. The Windows
constants remain unchanged. The registration-budget regression now runs the
same assertions on both platforms, with live registration ordinally first and
last. The shared collector sorts registration paths and retains registration-first
priority throughout the pass, including after the live adapter is bound. Both
cases require all 16 registrations and only the live owner/request metadata,
within the unchanged 18-read budget, with unique identity and privacy assertions.

Use a fresh canonical temporary root (for example, one created directly under
`/private/tmp`) as TMPDIR. The identity checks intentionally reject symlink
ancestors; macOS `/var` and `/tmp` are symlinks. Do not disable those checks to
accommodate a noncanonical fixture root.

Run serially with the repository memory/time guard, an isolated DOTNET_CLI_HOME,
and the required .NET 8 SDK. These tests do not launch model clients. Existing
fixture cleanup is scoped to freshly created temporary roots.

## Fake Dispatch integration (no model calls)

`watch/tests/mac_smoke.py` and `studio/tools/mac-live-smoke.py` create isolated
fixtures using the public Dispatcher and a fake Node executor. They observe
running, READY while still alive, and exited/history, and reject any accidental
acceptance claim. The shared projector intentionally keeps the running phase
while the CLI remains alive; READY is shown as an artifact with an explanatory
blocker. After exit, delivery becomes complete; acceptance remains unapproved (unknown/pending).
The scripts reject a synthetic private-body sentinel in exported snapshots,
stop only their own app/server/executor processes, and recycle their fixture.
They do not test a real model or no-READY failure; those are separate cases.

Final Mac core run: 234 passed, 0 failed, 0 runtime skipped (234 included).
The Windows-only junction row remains excluded at build time, not counted as a
success. Windows native inspection/WPF must still be verified on Windows.
