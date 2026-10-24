# Public Codex 0.5.0 validation

2026-10-25 (Asia/Taipei), Windows, Node 24.19.0 and Python 3.12.10.

```text
npm --prefix codex/plugin test
node codex/tools/check-public.mjs
```

The full public suite passed: **35 tests, 0 failures, 0 skips**. It includes 13 results for the new reliability cases (including two nested revocation cases). The test process tree had a 2 GiB memory cap; observed peak was 0.32 GiB. The public structure check passed with version 0.5.0, six MCP tools, JavaScript/Python syntax checks and the existing literal user-path/session-ID exclusion checks.

| Changed behavior | Public-code evidence |
| --- | --- |
| Owner acknowledgment | Actual tool metadata is required; coordinator/foreign/mixed owners, duplicate or excessive IDs, missing IDs and extra fields are rejected before writes. Proof binds immutable notification content and current route owner; a reconstructed receiver suppresses the handled pending item while a new attempt still delivers. |
| Send/ack serialization | The same target OS lock makes an in-progress send/ack return busy. Existing sent, uncertain and committed central records, intents, receipts and outcomes remain byte-for-byte unchanged by a later handled acknowledgment. |
| Backlog batching | The synthetic host verifies all member intents and their exact batch manifest exist before send. Same-owner CLI/Bot items share one send, another owner is separate, every original prompt remains intact and every item has an independent central ack. |
| Bounds and UTF-8 | A full batch stays within 256KiB UTF-8, overflow remains unclaimed, and an oversized original remains unchanged as a singleton. A long non-ASCII report survives the real Python driver's streamed JSON output intact. |
| Recovery and uncertainty | Partial claim contention leaves unsent items pending. A durable batch receipt repairs missing per-item fanout after reconstruction without another send; an ambiguous batch never replays, while an independent new job works. Wrong owner/item or malformed batch evidence cannot supply a recovery receipt. |
| Authorization boundaries | Revocation during preflight leaves all items unclaimed; revocation after a claim retains it without sending. Revocation after batch submission still permits acknowledgment of that exact prior receipt. Existing delegation, lease, route, attachment and prelaunch-rejection regressions remain covered. |

Per-item assertions now locate the original notification within a batch instead of requiring one host call per item. Identity, original-report, snapshot, source and one-attempt checks are retained.

These tests use this public package's bundled ledger, synthetic MCP host, synthetic identities and fresh temporary state. No production mailbox, private configuration, real provider, real notification or shared App restart was used. Other operating systems, minimum runtime versions, fresh marketplace installation and a real Desktop delivery/continuation on 0.5.0 remain unverified. Earlier installation or private-deployment evidence does not replace those checks.

## Preserved 0.4.1 evidence

2026-10-17 (Asia/Taipei), Windows, Node 24.19.0 and Python 3.12.10.

```text
npm --prefix codex/plugin test
node codex/tools/check-public.mjs
```

Both commands passed. The receiver suite has 22 passing tests and no failures; the structure check verifies package/plugin/MCP version 0.4.1, the five existing tools, JavaScript syntax, Python syntax and absence of literal machine user paths/session IDs in this subtree. The test process tree was limited to 2 GiB; observed peak was 0.30 GiB.

| Changed behavior | Evidence |
| --- | --- |
| Request deadlines | Two real isolated fake-host sends waited 31 seconds with the default client: acceptance was acknowledged and refusal stayed uncertain, both single-attempt. A separate read timed out while an original send was pending; the send completed and the engine kept the connection. Confirmed fake-host process exit still triggered the original same-pipe health recovery. |
| Target serialization | Two Python lease processes contended for the same target; case-distinct targets remained independent on Windows. Killing the holder released the OS lock without deleting its file. Registered deliveries and coordinator faults shared the target gate. Preflight rechecked an active owner after contention; target-lease loss and watch revocation before claim prevented sending. |
| Legacy preflight | Only the known unsupported-history error entered official visible-status lookup. Exact local idle/notLoaded targets delivered; active/systemError, missing/wrong/remote targets, wrong kind, unknown/conflicting status and refusal remained pending. Watch withdrawal before or during fallback prevented claim/send. |
| Existing receiver behavior | The existing public tests still cover ledger boundaries, immutable claims/outboxes, exact receipt recovery, prelaunch rejection, relative attachments, explicit delegation/revocation, submitted-send receipts and multi-process leader handoff. |

These are public-code tests with the repository's synthetic MCP host and temporary ledgers. No production plugin, real notification, provider, private inbox or shared App lifecycle was exercised. CLI marketplace installation was not repeated because package layout and dependencies did not change; the retained 0.4.0 installation evidence below is historical. Minimum runtimes, other operating systems and full real Desktop delivery/lifecycle remain unverified for 0.4.1.

## Preserved 0.4.0 evidence

# Public Codex 0.4.0 validation

Actual environment: Windows / Node v24.19.0 / Python 3.12.10 / Codex CLI 0.160.1. This run covers the public package, bundled ledger, fake official-tool dependency and isolated CLI installation. Earlier evidence below is retained as historical reference. Private deployment tests are separate and are not public Desktop acceptance.

From the repository root:

```text
npm --prefix codex/plugin test
node codex/tools/check-public.mjs
```

The public suite passed (15 tests, exit 0); the structure/syntax check passed (exit 0), with version 0.4.0 and five reception tools. The public commands use temporary synthetic ledger/state/outboxes and the repository's own fake MCP host, not copied official server code. No real pipe, model/provider, production account configuration or real notification was operated.

| Public consumer check | Observed result |
| --- | --- |
| Formal grant and caller boundary | Only actual metadata/executor-environment identity matching configured coordinator can grant/revoke. Both tools accept no arguments and use manifest prompt approval. No enabled all-watch means no grant. Duplicate valid grant leaves grant and epoch unchanged; lock contention changes nothing. Persistent delegation does not identify an ordinary tools/call. Malformed metadata does not fall back to environment. |
| Standard stdio reconstruction | First standard server explicitly receives coordinator start/grant, then exits through stdin EOF. A new standard server with no startup actor and no tools/call/hook uses the valid grant and its own synthetic launch pipe. Registered CLI and Bot READY deliveries go once to the exact original owner, with byte-identical claim prompt, original report and automatic label. State records persisted_delegation and the grant ID. |
| Fail-closed scope and precedence | No grant/old registrar alone, null/array/malformed records, bad schema/identity/source/roots/epoch and missing launch pipe cannot scan in an identity-less receiver. Launch actor and this instance's real tool actor retain priority. |
| Revoke, stop and projects | Revocation releases the delegated leader and prevents new collection; startup/status do not revive it. Stop-all disables the grant; a new all-watch epoch requires another explicit grant. Idempotent all-start preserves project opt-out. Held/closed routes remain paused. |
| Await boundaries | Revocation after OS lease/scan/read/claim, before intent/send submission and after bridge connect stops new effects. A central claim already taken is kept without sending. A delayed fake send already submitted before revoke retains the real stub receipt and completes ack; the next delivery stays pending. |
| Leader, receipts and faults | Two real stdio test instances without actor/tool calls share one OS leader and send each delivery once. Lease exit permits delegated standby takeover. Unknown sending stays manual_review_required; exact prior receipt completes only ack. Faults notify coordinator once, including an old registered owner's collector error after route handoff. |
| Preserved public behavior | Existing configuration, CLI/Bot original reports, scoped status, immutable registrations/outboxes, short driver locks/permanent claims, timeout recovery, crash-before-ack, prelaunch rejection and Windows relative attachment consumers still pass. |

Normal CLI installation was separately performed in a fresh temporary CODEX_HOME, without inherited launch pipe/actor, authentication or provider calls:

```text
codex plugin marketplace add <checkout> --json
codex plugin add codex-mail-wake@agent-lead-wake --json
codex mcp list --json
```

All commands exited 0. Installation reported version 0.4.0 and discovered codex_mail_wake using the installed local cache. Installed package and plugin versions, manifest approval modes, standard stdio initialize and tools/list all agreed: five tools, zero-argument grant/revoke schemas. An installed service call without actual identity refused grant and created no delegation. The active user profile was unchanged. This confirms CLI package registration, installation and discovery, not actual Desktop loading. Raw output stays outside the public tree.

Still unverified for this public package: full Desktop/App shutdown and reload after grant, host eager loading, a new real delivery on the new real pipe, official-tool/notLoaded send, original Lead continuation, minimum runtimes and Ubuntu/macOS execution/locking. A persistent grant cannot load an unloaded MCP service or supply a missing launch pipe. Successful private grant/leader recovery or a read-only bridge probe does not prove this full lifecycle. Receipt acceptance is transport evidence, not reading or business acceptance.

## Preserved 0.3.2 evidence

# Public Codex 0.3.2 validation

Actual environment: Windows / Node v24.19.0 / Python 3.12.10. Legal relative deliverables were already resolved from the registered outbox. This version rejects ambiguous Windows drive-relative and root-relative anchors before join, and adds one real CLI consumer regression. The 0.3.1 and 0.3.0 sections below stay as earlier evidence. They are not recounted as this run. Private adapter trials are separate and are not included here.

From the repository root:

```text
node --test codex/tests/public.test.mjs
node codex/tools/check-public.mjs
```

The public suite completed with **8 passed, 0 failed**, exit 0. The structure check then passed, exit 0: version 0.3.2, 11 Node syntax checks, 3 Python AST checks, three reception tools, explicit configuration, bundled runtime dependencies, and no literal machine user paths or session IDs. The new test launched the ledger CLI with an independent spawn working directory. Nested, non-ASCII, and spaced relative attachments were snapshotted from the registered outbox rather than a same-named file in that working directory. On this Windows run, same-drive drive-relative paths (`C:name`, `C:sub/file` using the fixture volume letter) and a forward-slash root-relative path were rejected and were not read as attachments. Parent paths, absolute paths, and directory deliverables were still rejected. A normal delivery was claimed and acknowledged to the original fixture owner. Existing sent and sending records, including an existing snapshot, were left byte-for-byte unchanged. The process-global working directory was not changed.

No provider, real notification, installation, App operation, or publication was part of this check. Local raw output stays outside this tree. Ubuntu, macOS, and other Windows layouts were not executed, so those platforms remain unverified. Passing this Windows fixture run does not prove marketplace install or Desktop loading.

The maintainer separately verified normal Codex CLI marketplace registration, installation reporting version 0.3.2, and MCP discovery in an isolated CODEX_HOME. The active user profile was unchanged. This confirms package registration and discovery, not real Desktop loading or provider execution.

## Preserved 0.3.1 evidence

# Public Codex 0.3.1 targeted validation

Actual environment: Windows / Node24.19.0 / Python3.12.10. Only the file-only public prelaunch contract changed; earlier 0.3.0 reception evidence below is reused, not recounted as new evidence.

```text
node --test --test-name-pattern='public prelaunch rejection' codex/tests/public.test.mjs
node codex/tools/check-public.mjs
```

The targeted public check passed (1 test, exit0): matching trusted-wrapper metadata becomes one labelled blocked launch-failure notification to the original owner; diagnostic content is not inlined; wrong owner/path/possibly-started declarations do not create READY; existing normal READY, partial report, permanent claim and retired registration are preserved; repeated scan/helper does not change sent/READY, and the same job's new attempt in a fresh outbox receives independently. No provider, actual notification, installation or App operation occurred. Local raw output remains excluded from publication.

This proves the explicit file contract and conversion, not a provider-specific launch adapter or the truth of an arbitrary wrapper assertion. Silent exit/timeout still needs proper executor/adapter terminal evidence or Lead handling. Minimum runtimes, other platforms, real Desktop/official tools and full App lifecycle remain unverified here.

The maintainer separately verified normal Codex CLI marketplace registration, plugin installation reporting version 0.3.1, and MCP discovery in an isolated CODEX_HOME. The active user profile was unchanged. This confirms package registration and discovery, not real Desktop loading or provider execution.

## Preserved 0.3.0 evidence

# Public Codex 0.3.0 validation

Actual local environment: Windows, Node v24.19.0, Python 3.12.10. This record covers only the independent public subtree. No installed production plugin, real mailbox, model/provider, real notification, App lifecycle or automation was operated by these checks.

From the repository root:

```text
npm --prefix codex/plugin test
node codex/tools/check-public.mjs
```

The public test suite completed with **6 passed, 0 failed**, exit 0. A final targeted configuration/Windows case-identity check also passed, exit 0. The structure check passed: 11 Node syntax checks, 3 Python AST checks, version 0.3.0, exactly three reception tools, explicit configuration, and no literal machine user paths/session UUIDs. Local raw test output is excluded from publication; this summary and the relative commands are the public evidence.

| Actual public check | Observed result |
| --- | --- |
| Configuration/identity | Missing fields refuse startup. Explicit config overrides the current-user default file; missing/default partial config fails closed. CODEX_HOME/HOME/USERPROFILE are isolated temporary directories. No pipe or actor means no scan; real metadata can bind a missing actor, with no target/model override. |
| Windows file identity | Demo/demo project opt-outs remain separate. Case-distinct registered projects have distinct lowercase delivery IDs and intact original registration records. Long adapter IDs use bounded filesystem keys; no report/prompt digests are calculated in the polling path. |
| CLI/Bot READY and original prompt | Both sources use the same READY/REPORT/outbox contract. Reports and deliverables are snapshotted. The sent prompt exactly equals the claimed central prompt, includes the automatic delivery label and original report, and goes to the original owner with actual actor metadata. notLoaded is exercised by the fake host. |
| Registration/path/claim/ack | Same registration is idempotent; different job/attempt cannot reuse the canonical outbox. Owner change is explicit, old registration remains immutable. Escaping deliverables and damaged single jobs remain errors while a good delivery is collectable. Permanent claim cannot be taken twice; exact-owner ack is idempotent after sent. |
| Continued reception/dedup | Later READY changes for a consumed attempt do not send again. A new attempt in a fresh outbox sends independently. The default ledger uses READY only: it does not synthesize completion or failed delivery from executor PID exit. |
| OS leader and scoped faults | Only the holder scans; a real lease child exit releases the OS lock and another instance takes over without deleting lock files. An active owner stays pending; two simulated hours produce one scoped fault, then notLoaded permits delivery. Three observations of one registered collector error produce one fault; invalid unregistered files do not invent a target. |
| Uncertain/history/reconnect | Send timeout preserves sending/intent and is not resent. Same-launch-pipe health read rejection prevents scan and respects the simulated 60-second rate limit. Correct actual actor verification resumes an independent delivery. Missing historical central data becomes visible uncertain/fault without blocking another ready item; fault attempts remain single. |
| Real test-process reconstruction | One test Node process exits17 after an exact receipt is durable and before ack. A new standard stdio server instance completes only ack; send count remains1. This is a test process with a fake host, not an App restart. Current-user dependency discovery accepts a valid fake installed manifest and rejects an outside-cache override. |

Tests create fresh OS temporary directories and verify the exact resolved target before recursive cleanup. There are no published mailbox snapshots, private test evidence, user IDs or official server copies. The installable plugin bundles the default ledger; the repository CLI is only an entry to that same module.

Not yet locally verified: Ubuntu/macOS runtime and OS locking, minimum Node22/Python3.10 runtimes, actual Desktop loading of the public plugin/default config, real official-tool/notLoaded notifications, original Lead continuation, eager/autostart behavior, or full App update/close/reopen/new-pipe recovery. The maintained test code is portable; passing Windows fake-host tests does not prove those environments. Marketplace registration/publication acceptance is handled separately by the repository maintainer.
