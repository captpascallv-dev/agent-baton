# MAC-PORT-C: POSIX detachment and offline verification

Status: implementation/offline verification delivered; real dispatch and App-plugin wake test NOT performed in this card. Intel macOS 15.7, 2026-11-12. Branch mac-port-c from main a357b2a. No main push or PR.

Implementation commit: `3dfa96bf5f006147d6a0ce24cba11cb46ffa4d8a`.

## Fix
POSIX Dispatch now launches Run-Agent.ps1 through a small Python standard-library helper using `start_new_session=True`, `close_fds=True`, and DEVNULL for stdin/stdout/stderr. No shell interpolation or dependency is added. The helper prints Popen.pid, which is the actual PowerShell Run-Agent PID recorded in job.json and WATCH. The original Windows ShellExecute block was compared with the base revision and is unchanged.

This fixes lifecycle rather than asking callers to keep dispatch alive. A capped Dispatch may now exit while its detached executor continues. Since that executor intentionally escapes the caller's group, its resource/timeout cap must be configured separately. Early wrapper startup failure may lack diagnostic output because streams are deliberately detached; normal Run-Agent logging and final records remain as before.

README lifecycle guidance now describes this correct behavior. Studio's existing Windows-only launcher now rejects non-Windows explicitly, before hitting Windows TCP/COM/node.exe APIs; its Windows path remains unchanged, including compatibility with Windows PowerShell OS detection.

## Regression evidence
Commands use PWSH_PATH/PATH pointing to the existing PowerShell 7.7.0 and are serial inside capped_run. No model/account/network access is performed by fixtures.

- Original implementation was tested before modification: both regressions FAILED, watcher `process-gone-without-ready`, return 4 instead of 0. Overall exit 1; 15.2s / 0.10GB.
- Strengthened final tests use a release marker: fake executor cannot write READY until AFTER caller cleanup. Paths contain spaces and Chinese characters. Running those tests against an extracted original Dispatch with `DISPATCH_SCRIPT=<old script>` again FAILED 2/2, overall exit 1, 15.9s / 0.11GB. Key output in both: `AssertionError: 4 != 0 ... state=process-gone-without-ready`.
- New implementation: `python3 -m unittest discover -s claude/tests -p test_detached.py -v`: PASS 2/2, exit 0, 12.4s / 0.11GB. Tests cover capped_run wrapping ONLY Dispatch, and explicit termination of the originating shell session. Both require waiter exit 0, READY, owner PID = host_pid = terminal PID, exit_code.txt 0 and terminal.json exit_code 0.
- `pwsh -NoProfile -File claude/tests/Smoke.ps1`: PASS 12 checks, exit 0, 94.5s / 0.19GB; stdin/pointer dispatch, resume, Unicode paths, pending state, reuse protection, watcher modes.
- `pwsh -NoProfile -File mailbox/tests/Smoke.ps1`: PASS 8 checks, exit 0, 31.9s / 0.17GB.
- `pwsh -File studio/Start-Studio.ps1 -NoOpen -Demo`: expected exit 1 with explicit Windows-only explanation, 2.1s / 0.08GB; no server or browser started.
- Python py_compile and git diff --check: exit 0.

Regression fixture cleanup uses native Trash on Mac. Tests clean up only their own uniquely identified fixture processes and private initiating process groups. No broad process kills or system settings changes.

## Other background-process audit
| Location | Finding / action |
|---|---|
| claude/bin/Dispatch.ps1 | Required caller-independent lifetime; fixed POSIX session detachment. |
| studio/Start-Studio.ps1 server launches | Existing entry point depends on Windows COM, TCP cmdlets, node.exe and WindowStyle. It is not a working Mac launcher. Added explicit non-Windows refusal rather than pretending detach alone ports it. Node entry point remains available. |
| codex/plugin/lib/app-mailbox.mjs | Driver and lease children are pipe-bound; lease must end when parent stdin ends. Do not detach. |
| codex/plugin/lib/official-tools.mjs | MCP child transport belongs to host; explicit close cleanup. Do not detach. |
| studio/server/live-server.mjs | Bridge has parent-pid and stop cleanup; must follow host, not become a daemon. |
| acp/acp_client.py | Already creates new session for controlled group teardown, not caller-surviving service. Forced parent death/orphan cleanup is a separate lifecycle risk; no unrelated refactor in this card. |
| watch/Start-CliWatch.ps1 | Explicit Windows-only entry. Probe is waited on; Windows interactive GUI uses system launch. No Mac branch to fix. |
| mailbox/Claude waiter scripts | Foreground watchers do not spawn executors; caller controls background execution. No detachment needed. |

## Real dispatch
NOT RETESTED after this fix. Pascal's previous one-shot retry permission was consumed by card B and was not reused. No Codex model invocation was made for card C.

## Plugin: read-only preparation only
- Repository marketplace/plugin instructions and example config reviewed.
- Official installed dependency resolver was called without starting its server. It validated codex-app-tools manifest/server version **26.1106.51428**. This proves installed prerequisite validity, not host pipe availability or notification delivery.
- No marketplace addition, plugin installation/enabling, configuration writing, App restart, test conversation creation, notification sending or model wake performed. No existing plugin changed; therefore no rollback is needed for this card.
- Future installation destination is the user's normal Codex plugin cache/marketplace, with task-owned ledger and state roots. A real coordinator/Lead ID and host-provided launch pipe are still required; none is invented or persisted from another session.
- Mac Codex App UI remains unavailable through Computer Use. No AppleScript/AX/screenshot fallback is used. Private sessions are not accessed, directly or through scripts.
- If authorized later, verify notification using official supported conversation tools and exact ledger intent/receipt/outcome; do not read private session files as a workaround. A transport receipt alone cannot prove the Lead actually responded.
- A possible human step AFTER explicit authorization/setup: open one test Lead in the Mac App and supply its verified identity through supported tools. Do not create this or cause model use during the current card.

## Remaining limits
Real post-fix dispatch, actual plugin install/restart/wake, and any Mac App UI interaction remain unperformed. Linux code path uses standard POSIX APIs but was not executed on Linux here. Windows runtime suite was not rerun on Windows; source branch unchanged was verified. Do not treat this offline delivery as full plugin-wake acceptance.

## Owner-authorized live continuation

Pascal subsequently explicitly authorized one post-fix tiny dispatch, installing the repository wake plugin, one normal App restart, and one real wake test.

### Post-fix real dispatch: PASS
Exactly one `gpt-6.1-sol` / `xhigh` / default service-tier (Fast off) tiny job was dispatched. Only Dispatch was inside its 1GB/45s guard; the executor separately used 1.5GB/180s. Dispatch exited 0 in 2.3s; the executor survived independently. `hello.txt` matched `MAC-C-SMOKE-OK\n` byte-for-byte and READY.json declared completed with the expected deliverables. The local waiter exited 0 (24.2s/0.01GB). After the owner's App restart/reconnection, the original job's `exit_code.txt` and `terminal.json` both reported 0, with host_pid = owner.pid = terminal.pid. No repeat dispatch was made. This resolves the missing runner exit-record check from card B.

### Authorized plugin installation and restart
The supported `codex plugin marketplace add .` and `codex plugin add codex-mail-wake@agent-lead-wake` both exited 0. Installed version 0.5.1 in the normal Codex plugin cache under marketplace agent-lead-wake. No other plugin was changed. The owner reported manually restarting the App and creating a conversation titled “待命”; supported list/read APIs verified that conversation, not filesystem session records. Direct App UI was not bypassed.

A new dedicated plugin default config (none previously existed) points only at task-owned test ledger/state directories with this verified Lead as coordinator. One fake delivery was registered. Exact private local paths and conversation identity are intentionally omitted here. Installation remains as requested; later rollback can use the supported plugin remove command, and only this task-created config can be moved to Trash after stopping its watcher. Preserve receipts and claims until testing is reconciled; do not remove other plugin state.

### Wake setup blocked at host MCP startup (diagnosed)
The official supported send/read-thread APIs were used on the verified standby
conversation to request project-scoped watch setup. It reported that
start_app_mailbox_watch and app_mailbox_status were absent, so it did not start
watching. The one fake outbox has NO READY marker; no notification or wake reply
is claimed.

The host application log records initialization connection closure for
codex_mail_wake at 14:04:19, 14:04:54, 14:05:03 and 14:05:17 UTC. The complete
test configuration was created at 14:06:58 UTC, after the newly created Lead was
identified. The server loads/validates mandatory configuration before responding
to initialize; absent configuration exits immediately. Current loadConfig
validation passes. A bounded standalone read-only initialize/tools-list check
of the installed server exited 0, with six expected tools and no stderr. It did
not call start-watch or supply invented actor metadata.

Thus the initial host discovery failed before the config existed; current
standalone startup works. Host reconnect/reload is still required for actual
watch setup. Available CLI MCP management and supported App tools exposed no
reconnect operation. No second restart, uninstall/reinstall loop, private
session read or UI bypass was attempted. The owner coordinator is handling the
smallest supported next step. A successful App restart alone is not a successful
wake test.

Official troubleshooting reference reviewed:
https://developers.openai.com/plugins/deploy/troubleshooting
It recommends separating server/discovery/client issues; it does not establish
an available Desktop reconnect command for this installation.

## Authorized wake continuation, 2026-11-12 14:21–14:25 UTC
The owner manually restarted the App again. New App process start was 14:21:16 UTC;
Mac reattachment succeeded. The host reported codex_mail_wake ready at 14:23:00
and 14:23:05. In the existing user-created standby conversation, supported App
send/read APIs verified actual start_app_mailbox_watch and app_mailbox_status
calls, followed by WAKE-C-ARMED. The project permit was enabled at 14:23:16.
No additional executor dispatch or quota read occurred.

Exactly one synthetic outbox READY was created at 14:23:29 UTC with marker
MAC-C-WAKE-ONE. The receiver collected its snapshot, but its original-owner
preflight failed at 14:23:30 with:

`Official MCP rejection: {"code":-32603,"message":"Codex app tools pipe closed"}`

The outcome is pending/read_unavailable, notificationAccepted=false and
actualLeadContinuationVerified=false. No delivery claim, send intent, receipt,
or MAC-C-WAKE-ACK was found. This is a real attempted wake blocked BEFORE send,
not a successful wake and not an uncertain already-submitted notification.

The same conversation successfully executed stop_app_mailbox_watch and status
at 14:24:34, reporting watchEnabled=false. All registered pending/snapshot/state
and the stopped permit remain intact; nothing was deleted or resent. The
standby conversation remains idle. Installed plugin and configuration remain.

### Diagnosis boundary and minimal reproduction
The outer supported App read/send interface and plugin tool calls work. The
failure is specifically the plugin's child official-tools adapter using its
original host launch pipe during background read_thread preflight. Installation
and initial configuration are no longer the blocker. This run observed one
concrete closure; it does NOT prove a permanent host-wide outage or establish
whether the pipe would recover on its own. The receiver released leadership.
The implementation has rate-limited read-only recovery on the same original
pipe, but restarting the watch could subsequently send this still-pending item,
so it was not used as an unapproved retry after stopping.

Reproduce only with a separately authorized test: installed 0.5.1, valid config,
registered synthetic outbox, real original Lead starts project watch, finishes
its turn, then a single READY is written. Compare native read_thread success
with adapter preflight result. Investigate the official launch-pipe lifetime
and adapter supported integration, without copying another thread's pipe,
faking identity, reading private sessions, or bypassing App UI restrictions.
No extra user restart or new conversation is justified by this evidence alone.
