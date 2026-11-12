# Mac port C2: one authorized wake retest

2026-11-12, branch `mac-port-c2`, base `79b6dc9310e4e1b3c8da33ddc418558fccff3178`.
**Result: fix loaded and rate-limited recovery exercised; real wake still failed.**
Classification: continuing native host-pipe closure after a fresh App restart,
not a missing configured connection and not a demonstrated policy-permission denial.
No accepted notification, receipt or actual Lead continuation was observed.

## Approval and scope

Pascal directly approved the plugin update, one normal App restart, and one real
wake test, and then confirmed that he had restarted the App. D had already been
committed and pushed before C2 began. This test used the existing original standby
thread and the one existing pending C delivery. No new READY, model dispatch,
account login/logout, quota read, private session-file access, UI bypass or
system/network change was performed. Setup and stop/evidence messages to the
original thread were explicit control messages, not claimed as plugin wakes.

## Update and loaded-code verification

- Fetched `fix/codex-pipe-closed-recovery` and created this branch from `79b6dc9`.
- Existing marketplace was local and already pointed at this checkout.
- Used the official CLI `codex plugin add codex-mail-wake@agent-lead-wake --json`.
  It returned installed plugin ID/version `codex-mail-wake@agent-lead-wake`, 0.5.1.
  The installation command ran under the repository cap: exit 0, 0.2s, peak <0.01GB.
- Version alone was not accepted as proof. Compared SHA-256 of the checkout's
  `codex/plugin/lib/official-tools.mjs` to the actual installed cache's same file.
  Before update the installed hash began `7563036b`; after update and again after
  the restart both matched exactly:
  `381ba36cea183c02c32457011c7554c672404f2b61e5a704d40e08603919149a`.
- The App's scoped startup log showed `codex_mail_wake status=ready` for the
  original standby thread at **15:20:37.225 UTC**, repeated ready at 15:20:43.061.
  Official `read_thread` and `send_message_to_thread` worked after the restart.
- Existing external configuration, ledger, pending delivery, receipts/locks and
  state directories were retained; no direct cache overwrite or state reset.

## Single-attempt chronology (UTC)

| Time | Evidence |
|---|---|
| 15:20:36 | Official control message started the original standby thread; model `gpt-6.1-sol`, xhigh |
| 15:20:37.225 | Host reported the plugin ready for that thread |
| 15:20:52.402 | Original owner enabled project `mac-c-wake` once |
| 15:20:53.232 | New plugin instance released its leader after the native pipe fault |
| 15:21:04 | Thread returned `MAC-C2-ARMED`; enabled=true but no real notification |
| 15:21:53.329 | `lastBridgeProbe` attempted the same-actor read-only recovery, approximately 60.1s after release |
| after probe | `lastBridgeProbe.status=failed`, same exact native error; no separate result timestamp was exposed |
| 15:22:31.333 | Original owner stopped the project; persisted enabled=false |
| 15:22:44 | Official thread reply confirmed stopped, no accepted notification or wake |

The original owner's pre-stop plugin status reported:

```
runtimeState = adapter_unavailable
adapterStatus = unavailable
adapter version = 26.1106.51428
lastBridgeProbe.status = failed
Error: Official MCP rejection: {"code":-32603,"message":"Codex app tools pipe closed"}
notificationAccepted = false
actualLeadContinuationVerified = false
```

The failure classification has changed correctly: this exact native rejection
now enters the 60-second recovery path. The read-only health check still fails.
The repair therefore does not establish a usable original launch-pipe connection
on this host. No substitute pipe, forged identity, immediate resend or second
watch activation was attempted. This is one test with its built-in read-only
recovery, not two independent real sends.

## Ledger and stop verification

The same single delivery is still pending. Its retained C outcome timestamp is **14:23:30.875 UTC**.
This old timestamp was not presented as a new C2 send result. The new C2 project
activation, instance leader release and recovery status provide the new evidence.
Direct task-owned state inspection after stop found:

- enabled=false, stoppedAtUtc=15:22:31.333 UTC;
- zero claims, send intents and receipts;
- outcome pending; notificationAccepted=false; actualLeadContinuationVerified=false;
- no `MAC-C2-WAKE-ACK` from a plugin-delivered turn.

The installed repaired plugin and external configuration remain in place. The
test watcher is stopped; all pending evidence is preserved for the maintainer.

## Remaining issue

Maintainer follow-up is the official adapter/host launch-pipe connection on Mac,
not the now-tested string classification. Ordinary official conversation tools
worked, while the plugin's own authorized same-launch adapter read failed.
This report does not infer why those two launch contexts differ, and contains no
private user paths, network details or session files. Do not mark real App wake
as passed from `ready`, `ARMED`, recovery attempted or a control-message reply.

## Supplemental offline checks

After stopping the real watcher, ran the unmodified public fake-host suite under
1 GB / 120s cap. It reached 23 passing cases then timed out (exit 124, 120s,
peak 0.31GB); this is **not a full-suite pass**. A separate targeted run of the
three changed recovery/rejection cases passed 3/3, exit 0, 9.7s, peak 0.16GB:
pipe-closed preflight recovery, uncertain send recovery, unrelated rejection.
These fake-host cases do not override the failed real Mac evidence above.
