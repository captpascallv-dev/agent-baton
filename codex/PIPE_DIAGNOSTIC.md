# Mac C3: one connect-only launch-pipe diagnostic

This temporary branch is based on `79b6dc9`. It replaces the `codex_mail_wake`
MCP entry with `diagnostic-server.mjs` and exposes only
`diagnose_app_tools_pipe({})`. The plugin identity and forwarded environment
names are unchanged. The receiver never starts: no configuration/ledger load,
watch, claim, official adapter child, protocol request or notification send.
Do not merge this diagnostic configuration into the normal reception branch.

The goal is to locate the failing layer in C2. C2 confirmed the classification
fix and the 60-second recovery attempt, but both original preflight and recovery
received `Codex app tools pipe closed`. The first failure happened before the
Lead finished its turn. This diagnostic does not presume the cause.

## Execution card for the existing Mac owner

Small P executes; Q7 coordinates the result. Use the existing standby chat and
task-owned installation/checkout from C2. Its project remains stopped and its
single delivery remains pending. This is a diagnostic, not another wake test.

1. Prepare this diagnostic branch in the task checkout, preserving any existing
   work. The maintainer supplies its exact commit with the handoff. Keep the
   existing external config, ledger and receiver state unchanged.
2. If installation is authorized, use the same supported CLI installation as
   C2: `codex plugin add codex-mail-wake@agent-lead-wake --json`. Check the actual
   installed `.mcp.json` entry is `./diagnostic-server.mjs` and that the diagnostic
   file matches the checkout. Version 0.5.1 alone does not identify this candidate.
3. Use the host's supported reload workflow. If a new shared App restart is
   required, Q7 must arrange Pascal's authorization/window for that operation;
   C2's completed one-restart permission does not authorize another restart.
   Do not invent a reload command or work around missing tool discovery.
4. In the existing standby chat, verify the newly loaded plugin exposes
   `diagnose_app_tools_pipe`, then invoke it **once with `{}`** through the normal
   MCP tool surface. Actual caller metadata/environment is supplied by the host.
   Do not run a standalone shell probe or copy a pipe from another process.
5. Return the sanitized JSON and whether the actual diagnostic entry was loaded.
   Do not call a second time or restart to get another attempt. A missing pipe,
   refused connection or discovery failure is a useful result to report.
6. After collecting the result, restore the normal plugin entry from `79b6dc9`
   using the supported installation workflow when that restoration is authorized.
   Any needed App restart follows the same explicit window rule. Keep the watch
   stopped. If immediate restoration cannot be loaded, report that the temporary
   plugin currently offers diagnostics only; do not claim reception is restored.

No new READY, executor/model dispatch, account access, private session read,
raw environment dump, alternate endpoint, socket-file deletion or system setting
change is part of this card. A control message invoking the diagnostic is not a
plugin-delivered wake.

## Output and interpretation

The tool returns only these fields:

| Field | Meaning |
| --- | --- |
| `pipePresent` | A nonempty launch variable exists after the same trimming used by the official adapter. |
| `pathCharacters`, `pathBytes` | Unicode character count and UTF-8 byte count of that trimmed path. No path text or fingerprint is returned. |
| `isSocket`, `statErrorCode` | On Mac/POSIX, `stat` follows the configured path and reports socket type or a sanitized error code. Windows named pipes report null / `NOT_APPLICABLE_WIN32`. |
| `connect.attempted` | Whether the tool attempted its one connection. A missing variable means false. |
| `connect.outcome`, `connect.errorCode` | Connection/close observation and sanitized error code; no native error message is returned. |

There is a fixed 2-second connection deadline. After connect, the tool observes
for at most 250 ms, then closes its own connection. It sends **zero application
bytes** and discards any peer bytes without recording them. A concurrent or later
call in the same process is rejected without another connection.

- `connect_error`: the connection did not establish; use the error code together
  with stat and byte length. Length alone does not prove a platform limit.
- `connect_timeout`: no successful connect within two seconds.
- `closed_before_connect`: closure occurred before a connect event.
- `closed_after_connect` / `error_after_connect`: connect established, then the
  peer closed or errored during the observation window.
- `connected_no_close_observed`: connect established and no close was observed
  within 250 ms. This says nothing about permission or acceptance of an RPC.

This samples the **new host-launched instance of the same plugin definition**,
not the environment of an already-exited C2 process. No single connect-only probe
can prove a one-client restriction or why the host rejects an actual framed tool
request. In particular, Unix-domain sockets are not classified as single-client
by this diagnostic. More testing, if needed, requires a separate scoped decision.

## Maintainer checks

2026-11-12 (Asia/Taipei), Windows / Node 24.19.0. Ran:

```text
node --test codex/tests/pipe-diagnostic.test.mjs
```

Five tests passed, exit 0, under a 1 GiB / 40-second cap (observed peak 0.08 GiB).
They use synthetic endpoints and a real local named-pipe server to check missing
variables/paths, path redaction, zero application bytes, peer close, single-use
including concurrent requests, identity/argument checks, and the actual stdio
entry advertising no reception tools. The production reception suite is
unchanged and was not rerun. Its `check-public.mjs` expects the normal receiver
entry, so it is not the acceptance command for this temporary configuration.

Mac socket stat/connect and real host launch remain unexecuted by the maintainer.
No current production plugin, real notification, watch or shared App lifecycle
was changed by these checks.
