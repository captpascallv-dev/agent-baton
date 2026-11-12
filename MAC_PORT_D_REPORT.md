# Mac port D delivery

2026-11-12. Branch `mac-port-d`, base `54a5a05`, implementation `8d5a243`.
Delivery is **partial acceptance**, not an all-green claim: one shared-core
regression and Studio visual QA remain open. No real model or quota call,
plugin/config update, Codex App operation, or private session access in this card.

## Delivered

- Native SwiftUI/AppKit CLI Watch ordinary window, light/dark, current/history,
  independent axes, unknown/stale presentation. It reads the shared C#
  Collector → Normalizer → Projector JSON, including shared current-list rules,
  artifacts and blocker text. It does not recreate business state in Swift.
- Mac shared CLI and process adapter. Explicit task-directory configuration;
  birth-matched owner PID roots and descendants only, not host-wide argv search.
  Process presence is not work/delivery/acceptance. IO is unavailable, so
  activity is honestly unknown; `ps` cannot preserve arbitrary argv quoting.
- Studio Mac .NET bridge built against shared projects; foreground launcher,
  local HTTP, no private runtime/session overlay. Windows targets/branches
  remain intact. A Mac-only POSIX path check fixes missing artifact identity.
- HUD Chinese presentation and six-slot synthetic light/dark screenshots.
  No fresh account/quota read.
- Per-read scope gates cover indirect references, output remains independent,
  each snapshot publication rechecks symlink ancestors. These checks are not
  a hostile concurrent-writer sandbox; check/open and PID sampling races remain.

## Serial evidence

Intel macOS 15.7, .NET 8.0.428, Swift 6.1.2, PowerShell 7.7. All builds/tests
were serial, under sampled RSS/time guards (1–1.5 GB, typically 180–300 sec).
Sampling is not a hard kernel memory guarantee.

| Check | Result |
|---|---|
| Final CLI build | exit 0, 0 warnings/errors; 11.2s, peak 0.33 GB |
| CLI boundary self-test | exit 0; final prior boundary build 1.1s, 0.04 GB |
| Final native Watch build | exit 0; 14.2s, 0.31 GB |
| Watch + Studio bridge same fake Dispatch lifecycle | exit 0; 35.5s, 0.10 GB |
| Final Studio bridge build | exit 0, 0 warnings/errors; 15.7s, 0.38 GB |
| Studio bridge self-test | exit 0; includes unknown/current/history/private-field checks |
| Studio linked identity | 30 assertions pass |
| Studio collector boundary | 6/6 groups pass |
| Studio Node live/config tests | 30/30 pass |
| Studio Vite build | pass, 4572 modules; no lockfile change |
| Studio actual scoped collector + HTTP, fake Dispatch | exit 0; 11.6s, 0.11 GB |
| Existing shared Watch tests on Mac | **232 pass, 1 fail**, 233 included; 33s, 0.42 GB |
| HUD core six-slot demo/native UI | pass, simulated only; native build 14.2s, 0.30 GB |
| Studio own WKWebView visual preview | **not verified**, two attempts failed to report loaded within 20s |

The three-stage integration used the public Dispatcher and a task-owned fake
Node CLI, not a model. Running → READY while alive → exited was observed.
The shared phase correctly stays `running_unknown` while CLI is alive, even
with READY present; the READY artifact and blocker explain this. After exit,
phase is `delivered`, current=false, acceptance=pending, never accepted/pass.
Synthetic task/report-body sentinel was absent from exported JSON/HTTP.
Each attempt stops only its own processes and recycles its own fixture.

First integration failure was a test output placed outside the bridge's declared
root (correctly rejected). Second incorrectly expected delivered phase before
process exit; test corrected to shared semantics, not production weakened.
Studio preview failure did not stop independent HTTP lifecycle testing. No
Studio screenshot was fabricated or substituted with a mock image.

## Shared test failure retained

`MailboxCollectorTests.Live_prompt_adapter_registration_precedes_historical_evidence`
reveals a native enumeration-order/budget difference. After an early live
registration is matched, later historical registrations can consume metadata
budget: the strong expected live-only metadata subsequence has 2 entries,
actual has 5. Budget/identity/private-history assertions were not removed to
force green. This remains a follow-up collector scheduling issue.

Mac fixture paths are POSIX (including quoted-space coverage); Windows originals
remain conditional. The Mac test no longer assumes unspecified filesystem
ordering, but retains the actual metadata sequence and business assertions.
One Windows `cmd.exe /c mklink /J` data row is explicitly not included; it is
not a pass or runtime skip. WPF code-behind/UI and Windows native probes were
not executed on Mac. No Windows behavioral regression claim is made here.

## Screenshots and reproducibility

- `watch/docs/images/mac-watch-light.png`, `mac-watch-dark.png`: own native
  window only, synthetic task; verified readable with honest unknown states.
- `hud/docs/images/mac-hud-demo-light.png`, `mac-hud-demo-dark.png` and matching
  menubar crops: own UI only, synthetic slots; no private account content.
- Build/run instructions: `watch/README.md`, `watch/macos/README.md`,
  `watch/src/CliWatch.Cli/README.md`, `studio/README.md`.
- Lifecycle helpers: `watch/tests/mac_smoke.py`, `studio/tools/mac-live-smoke.py`.
  Wrap in `guards/capped_run.py`, pass SDK/CLI/pwsh paths explicitly. The optional
  WKWebView helper is diagnostic source, not a verified browser replacement.

Not covered: unrestricted native session collection, real providers, real App
wake, ARM Mac, packaged/notarized distribution, WPF, Studio visual rendering,
long-running idle load, or corrected enumeration-order regression. C2 plugin
retest is a separate subsequent task; it was not mixed into D.
