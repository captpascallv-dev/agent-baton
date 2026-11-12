# macOS E: registration-order regression and Studio visual evidence

Base: `1027622`. Code/screenshots/docs commit: `ee2b3b3`.
Test platform: Intel macOS 15.7, .NET SDK 8.0.428. No model calls.

## Root cause and fix

The collector used the mutable `pendingAdapters.Count` to decide whether historical
metadata should wait behind registration scanning. When an early registration
matched the final live adapter, the set became empty. Later historical registrations
then consumed metadata budget before all registrations were read. Filesystem
enumeration order made the same bounded fixture behave differently.

Registration paths now have ordinal ordering before bounded partition/rotation.
A fixed per-pass priority flag preserves registration-first behavior after a live
match. Live identity metadata is still read immediately and validated; other
metadata remains deferred until registration traversal finishes. No extra content
reads, higher budget, weaker identity matching or private-history fallback was added.
The same change was applied to Studio's existing collector source copy. Windows
uses the same language-level ordering and priority semantics; no Windows-specific
process path or WPF code was changed. Windows execution still requires the Lead's
separate platform run.

The original test is a two-row theory: live registration first (`0-live`) and last
(`z-live`). That case no longer has MAC_PORT_TESTS conditionals. Both rows assert all
16 registration reads, exactly two live owner/request reads, adjacency immediately
after the live registration, the unchanged 18-read budget, unique live binding,
no historical metadata and no private binding-state write.

## Validation

Commands below use `<dotnet>`, `<pwsh>` and `<bridge>` for the preinstalled task
SDK/PowerShell and built Mac Studio bridge. Build environment specifies DOTNET_ROOT,
isolated DOTNET_CLI_HOME/NUGET_PACKAGES and canonical TMPDIR. Runs were serial.

| Command / check | Exit | Result / guard observation |
| --- | --- | --- |
| `capped_run.py --mem-gb 1.5 --timeout 300 -- <dotnet> test watch/src/CliWatch.MacTests/CliWatch.MacTests.csproj -c Release -m:1 -p:UseSharedCompilation=false --no-restore` | 0 | 234 passed, 0 failed, 0 runtime skipped; 59.2s, 0.40GB peak |
| `capped_run.py --mem-gb 1.5 --timeout 180 -- <dotnet> build studio/bridge/StudioBridge.csproj -c Release -m:1 -p:UseSharedCompilation=false --no-restore` | 0 | 0 warnings/errors; 31.4s, 0.38GB peak |
| `capped_run.py --mem-gb 1 --timeout 100 -- python3 studio/tools/mac-live-smoke.py --bridge <bridge> --pwsh <pwsh> --node <node> --output-dir <task-evidence>` | 0 | Rebuilt bridge: running_unknown, READY-while-alive remains running_unknown, exited becomes delivered/history; 12.8s, 0.11GB peak |
| `capped_run.py --mem-gb 1 --timeout 120 -- npm --prefix studio run test:live` | 0 | 29 tests passed; 1.4s, 0.16GB peak |
| `capped_run.py --mem-gb 1.5 --timeout 180 -- <dotnet> run --project studio/bridge/tests/CollectorBoundaryTests.csproj -c Release -p:UseSharedCompilation=false --no-restore` | 134 | Initial operator invocation omitted required synthetic fixture argument; failed at args.Single before tests, not a passing run |
| `capped_run.py --mem-gb 1 --timeout 90 -- <dotnet> studio/bridge/tests/bin/Release/net8.0/CollectorBoundaryTests.dll <fresh-canonical-fixture>` | 0 | Corrected invocation: 6/6 boundary groups passed; 1.6s, 0.04GB peak |
| Dedicated visual harness under `capped_run.py --mem-gb 1 --timeout 190` | 0 | Existing public fake Dispatch and scoped live server; Safari task count 1 to 0; 110.0s, 0.13GB peak |
| Window-only screenshot capture and inspection | 0 | Both files readable; no private content or real-session data in delivered images |
| `git diff --check` | 0 | No whitespace errors |

The Windows-only junction test row remains excluded at build time on Mac, not
counted as a passing test. Native Windows process inspection and WPF were not run.

## Screenshots and visual limits

- `studio/docs/images/mac-studio-running.png`: Safari, one current synthetic PI
  external task. Agent/session observation remains unknown by design.
- `studio/docs/images/mac-studio-idle.png`: same scoped live page after fake READY
  and exit, zero current external tasks. This is fixture idle, not proof that all
  user's real agents are idle.

Used Computer Use for Safari navigation and inspection, then `screencapture -l`
for only its verified window. The page rendered, Chinese labels were legible,
selection worked, and count changed. The captures show the existing light palette.
Floating agent chips partially cover the scene summary strip at this viewport;
automatic light/dark theme switching was not verified. No claim of complete visual
acceptance or Mac-specific cause is made. No system appearance settings were
changed. The WKWebView preview remains unverified; Safari supplied the evidence.
The browser had a brief input-formatting error while opening localhost; navigation
was corrected with paste. No unrelated user tabs were changed; the new test tab was
closed afterward. An initial window-ID discovery helper was run outside the guard;
the actual repeated Swift helper and all builds/tests used caps. No model or App
restart was involved.

The screenshot run used the existing D bridge before this change's rebuild; the
updated bridge was subsequently rebuilt and passed the three-stage HTTP fixture.
This distinction is retained rather than claiming screenshots exercise the new
registration-order case. The new first/last regression tests cover that code.

## Cleanup and boundaries

Stopped only the task-owned server/executor, closed the created Safari test tab,
and moved the task visual harness/fixture and boundary fixture to Trash. The standard
live smoke also recycled its own fixture. No wake-plugin/App configuration, pending
mailbox evidence, system/network settings, accounts, other repositories or forbidden
session files were touched. No real model or notification was called. README's
Chinese/English summaries and platform rows state tested scope and remaining gaps.
