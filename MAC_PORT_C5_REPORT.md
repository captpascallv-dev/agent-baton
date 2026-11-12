# macOS C5 official-launcher retest

## Verdict

**The real Mac wake did not pass.** The installed candidate uses the official macOS launcher, but the host still rejected its peer with `missing-code-signing-identity`. This is not proof that the launcher selected the intended signed runtime. No signature setting, caller identity, or launch pipe was changed to work around the refusal.

## Candidate and offline checks

- Candidate/base of this report branch: `6d38d3e64a969264659020be16959f17b27a9c84`, based on `278fe82`.
- Branch: `mac-port-c5`. Report only; no plugin-code modifications.
- Targeted public tests (macOS resolves; standard stdio process restart; pipe-closed; unrelated RPC; one read deadline; revocation after leader): **7 passed, 0 failed**, exit 0, 27.0 seconds, 0.31 GB peak.
- `check-public.mjs`: exit 0, 1.7 seconds, 0.07 GB peak; 13 Node syntax checks and 3 Python AST checks passed, public-path/session-ID checks passed.
- Each check used `guards/capped_run.py`, 1 GB cap, 180-second timeout. No full-suite claim.

## Installation and reload

Pascal directly approved installing/running this candidate, one normal App restart and one real wake attempt. Installation used the supported `codex plugin add codex-mail-wake@agent-lead-wake --json` workflow (exit 0). Version remains 0.5.1; version alone was not treated as evidence.

Installed files matched the candidate byte-for-byte:

| File | SHA256 |
| --- | --- |
| `lib/official-tools.mjs` | `664e631649f8240741c985e9ac466b390489e7ac97a72673c8a2f87eef1109e1` |
| `.mcp.json` | `d24ab05552308673a6c8f97772aa203efa40359fffdea46ad1b661e4c9068ce7` |
| `server.mjs` | `d71d81dcb29e62aff4c65fb5176645d81897ac9cb811b1507ea47c1ecc94465b` |

The installed entry is normal `server.mjs`, not C3's diagnostic entry. Pascal reported completing the requested normal restart. Host log records `codex_mail_wake ready` at 2026-11-12 16:37:17.750 UTC and 16:37:22.938 UTC. The original standby chat's supported history shows the normal start-watch tool completed once, followed by the control response `MAC-C5-ARMED`. This response is explicitly **not** a plugin wake ACK.

## Single attempt and three evidence layers

The original owner, project, original pending item, external configuration and ledger were retained. No new READY, dispatch, or independent model task was created. The original owner's control turn enabled the project exactly once and ended. The existing bounded read-only recovery was allowed once; no stop/start retry was performed.

1. **Original-owner preflight: failed.** Host authorization rejected the candidate peer.
2. **Exact original-owner notification receipt: absent.** No notification acceptance was established.
3. **Actual original Lead continuation/expected ACK: absent.** Supported thread history showed only the explicit arming and cleanup control turns, not a plugin-delivered continuation.

Host authorization events in the attempt window:

| UTC timestamp, 2026-11-12 | Event | Safe reason |
| --- | --- | --- |
| 16:37:36.863 | `dynamic_app_tools_peer_rejected` | `missing-code-signing-identity` |
| 16:38:38.097 | `dynamic_app_tools_peer_rejected` | `missing-code-signing-identity` |

The second event is approximately 61.2 seconds after the first, consistent with the existing bounded recovery. No alternate socket probe was performed. No `dynamic_app_tools_peer_authorization_failed` or `dynamic_app_tools_socket_error` event was found in the inspected attempt interval.

The persisted original outcome still has its older timestamp, 2026-11-12 14:23:30.875 UTC, status `pending`, `notificationAccepted=false`, `actualLeadContinuationVerified=false`, with preflight pipe-closed reason. That older outcome is **not** presented as a newly written C5 result. The current host events above are the C5 refusal evidence.

## Boundaries and unresolved questions

No sessions directory, private conversation files, credentials, personal networking information, raw pipe paths or environment dumps were accessed/shared. No UI restriction was bypassed. No signing changes, validation disablement, alternate pipe, identity impersonation, login/logout or unrelated configuration changes were made. The candidate remains installed.

The offline fixtures prove launcher selection and fail-closed dependency validation, not successful signed-runtime selection in this App launch. The actual runtime chosen by the official launcher and why it still lacks an accepted signing identity remain unverified. This report makes no claim that launcher selection alone resolves native peer authorization.

## Cleanup verification

The original owner called status, stop-watch, and status through the supported plugin tools. Before stopping, runtimeState was `adapter_unavailable`, adapterStatus `unavailable` (official tools version 26.1106.51428). lastBridgeProbe attempted at 2026-11-12 16:38:37.749 UTC and failed with RPC -32603, `Codex app tools pipe closed`; no independent result timestamp was provided. Status before/after stop showed notificationAccepted=false and actualLeadContinuationVerified=false, with no receipt returned. Persisted project enabled=false; stopped at 2026-11-12T16:39:17.708Z. Pending/outcome and all existing evidence were preserved. No further attempt was made.
