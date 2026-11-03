# Codex Usage HUD MVP Technical Specification

Version: 1.3-continuation-risk
Date: 2026-08-12
Platform: Windows 10/11 x64
Runtime: self-contained .NET 8 WPF
UI language: Simplified Chinese

## 1. Product outcome

Deliver a low-resource, always-on-top Windows HUD that lets one person observe
official Codex quota windows and privacy-safe raw-token metadata across many
concurrent local Codex sessions. It must remain useful when Codex is minimized,
survive log/app/computer restarts without losing or doubling totals, and fail
open at the UI layer when one source is damaged or unavailable.

The default experience is an independent draggable HUD. An optional
`宠物伴随模式` may position the collapsed HUD beside the Codex pet when a valid
pet anchor is mechanically available; the HUD must remain independently
usable and must not disappear merely because the pet is hidden.

## 2. Explicit non-goals

No cloud sync, billing estimates, currency, complex charts, cross-platform
shell, browser-cookie sweep, login automation, prompt/response indexing,
message search, auto-update service, or deliberate service-tier changes.
No 19-provider catalogue. Direct Codex HTTP usage clients remain forbidden.

Pascal 2026-09-20 authority amends the previous "single Codex account only"
non-goal: five quota slots are in scope (two Codex homes, Cursor, Grok, Grok
Bot). Runtime may use existing local logins for those opted-in slots against
each provider's own quota endpoint. Agents still must not open credential
files or receive secret values. Direct Codex HTTP remains disabled.

Pascal 2026-09-28 adds one Claude subscription quota slot beside those five.
It is quota-only, default off, and not aggregated with Codex or any other
provider. It does not add Claude session, context, or token analysis, and it
does not open a general provider catalogue. Direct Codex HTTP remains disabled.

Pascal 2026-10-04 adds an opt-in local quota snapshot file. It is off by default,
writes only the allowlisted numbers already on the HUD board, and does not add
a provider, a timer, or any upload.

## 3. Solution shape

Create this structure unless a small naming adjustment is needed by tooling:

```text
CodexUsageHud.sln
src/
  CodexUsageHud.Core/       domain, parsers, state machines, SQLite
  CodexUsageHud.App/        WPF shell, tray, composition root
tests/
  CodexUsageHud.Tests/      unit and integration tests
tests/fixtures/             synthetic privacy-safe JSONL
scripts/                    build, publish, and acceptance helpers
docs/                       specification and acceptance evidence
dist/                       ignored/generated release output
.artifacts/                 ignored/generated test and evidence output
.tools/                     ignored/project-local .NET SDK and NuGet cache
```

Use `Microsoft.Data.Sqlite`; WPF may use `System.Windows.Forms.NotifyIcon` for
the tray. Keep parsing/domain/storage testable without creating a WPF window.
Use dependency injection only to the extent it improves source isolation and
tests; avoid a large framework.

## 4. Runtime read allowlist

Resolve `CODEX_HOME` from that environment variable when present, otherwise
`%USERPROFILE%\.codex`. Runtime adapters may read only:

- `sessions/**/*.jsonl`
- `archived_sessions/**/*.jsonl`
- `state_5.sqlite` through an allowlisted read-only query
- `session_index.jsonl` as a metadata fallback
- `.codex-global-state.json` only for the optional pet anchor fields
- the local Codex executable and its App Server stdin/stdout protocol

Never open prompt/response bodies, browser storage, shell history, or unrelated
Codex logs. Never enumerate or read any Concerto path. Agents must never open
`auth.json`, cookies, or token files even when runtime adapters may.

Pascal 2026-09-20: opted-in Cursor/Grok/Grok Bot adapters may read only the
exact necessary existing-login field at runtime (`cursorAuth/accessToken` in
Cursor `state.vscdb`, or the Grok CLI issuer `key` / root `access_token` in
`%USERPROFILE%\.grok\auth.json`) and keep it in memory for that request. Do
not log, persist, or display the value. A local `expires_at` on that Grok
access key is not logout. When the same issuer entry still has a refresh
grant, the HUD runs the official non-interactive `grok models` CLI so Grok's
own lock can rotate the file, then re-reads the access key. The HUD does not
POST `auth.x.ai`, write `auth.json`, or treat file expiry as a fake 0%/100%. Primary Codex quota still uses App Server, not extracted Codex tokens.
The second Codex slot stays quota-only. When its optional Codex home is blank,
it may read only the `openai-codex` OAuth entry from the existing PI login
store at runtime, keep access token and ChatGPT account id in memory, and GET
`https://chatgpt.com/backend-api/wham/usage` on the HTTPS allowlist with
redirects refused. When that slot has an explicit directory, the HUD instead
launches the installed Codex app-server with `CODEX_HOME` set only on that
child, then sends `initialize`, `initialized`, `account/rateLimits/read`, and
`account/read` with `refreshToken` false. A blank home remains PI. An invalid
directory or the same directory as the primary home is rejected and does not
fall back to PI. Agents must not open login files. Do not copy PI credentials
into Codex `auth.json`, refresh either store by a custom mechanism, or scan
unrelated homes.

Pascal 2026-09-28: the opted-in Claude slot may read only `claudeAiOauth.accessToken`
and `expiresAt` from the existing Claude Code credential file
`%USERPROFILE%\.claude\.credentials.json`, or `%CLAUDE_CONFIG_DIR%\.credentials.json`
when that variable is set. When that file has no usable access token, the same
slot may fall back to the current Windows Claude Desktop profile only:
`%APPDATA%\Claude\config.json` and the sibling `Local State`, or the single
standard MSIX redirection
`%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\` when the roaming
pair is absent. It decrypts `oauth:tokenCacheV2` (or `oauth:tokenCache` only
when V2 is absent) in memory with the current-user DPAPI key from `os_crypt`
and reads one unexpired OAuth access token for `https://api.anthropic.com`.
It does not read refresh tokens, scan browsers, cookies, Credential Manager,
other Electron profiles, or transcripts, and it does not modify Desktop files.
If more than one Desktop account or profile is present, the slot is unavailable
instead of choosing one. The access value stays in memory for one GET
`https://api.anthropic.com/api/oauth/usage` with the `anthropic-beta: oauth-2025-04-20`
header. The host and path are fixed, redirects are refused, and the body is
bounded. There is no token refresh, no credential rewrite, no browser-cookie
scrape, no API-key billing, and no transcript or raw-token quota. An expired
local access token is `未连接`. Agents must not open those files.

For `state_5.sqlite`, inspect `PRAGMA table_info(threads)` and select only the
intersection of these columns: `id`, `rollout_path`, `created_at`,
`updated_at`, `cwd`, `model`, `reasoning_effort`, `source`, `agent_nickname`,
`agent_role`, `name`, `account_id`, `chatgpt_account_id`, and `user_id`.
Never select `title`, `first_user_message`, `preview`, or any unlisted column.
Account identity columns are metadata-only. Owner-confirmed primary-directory
binding is a distinct provenance from machine-verified per-row `account_id`.
Default session/continuation analysis shows that primary home minus classifiable
foreign rows. Do not empty the list when identity columns are absent, and do
not report owner-confirmed directory sessions as machine-verified. If the live
App identity later changes, do not silently rebind historical rows to the new
identity. Do not invent attribution from filenames, titles, token counts, parent
threads, or prompt/response content. Prefer
`name`, then `session_index`'s `thread_name`, then a shortened thread id for
display. Derive the project tag from the final directory name of `cwd`; do
not expose full paths in the UI.

## 5. Privacy-preserving JSONL reader

Unknown records must not be turned into .NET strings or retained whole.
Implement a streaming newline reader with these properties:

1. Read bytes from a shared-read file stream in bounded blocks.
2. Retain only a bounded candidate prefix while structurally determining the
   top-level event category. If it is not allowlisted, drain bytes through the
   next newline and discard them without UTF-8 decoding.
3. Allowlisted records are `session_meta` (thread id only), `turn_context`
   (model and approved settings only), and `event_msg` payload types
   `token_count`, `task_started`, `task_complete`, `thread_settings_applied`,
   and `context_compacted`. For `context_compacted`, extract only its empty
   structural boundary metadata; never inspect a compacted response item or
   encrypted/plaintext summary.
4. Candidate prefix limit: 64 KiB. Full allowlisted-record limit: 256 KiB.
   Over-limit or structurally changed records are skipped with a metadata-only
   error; they must not block subsequent lines.
5. Advance the durable offset only through the last complete newline. A
   partial tail is reread after the next append.
6. Never log a line, JSON fragment, string value outside approved fields, or
   exception message that could embed source content.

A `Utf8JsonReader`-based structural classifier is acceptable. Tests must prove
that an apparent `token_count` string inside an unknown message body does not
become an event.

## 6. Token event semantics

For a valid `event_msg/token_count`, parse both:

- `payload.info.total_token_usage`: cumulative snapshot
- `payload.info.last_token_usage`: latest model-call increment

Allowlisted components are `input_tokens`, `cached_input_tokens`,
`cache_read_input_tokens`, `cache_write_input_tokens`, `output_tokens`,
`reasoning_output_tokens`, and `total_tokens`, plus the context-window value
when present.

Canonical display math:

```text
cached_input = max(cached_input_tokens, cache_read_input_tokens)
raw_input    = max(input_tokens - cached_input, 0)
output       = output_tokens                 # includes reasoning
reasoning    = reasoning_output_tokens       # subset of output
total        = input_tokens + output_tokens
```

Cached input is a subset of input and reasoning is a subset of output; never
add displayed columns together. Preserve reported `total_tokens` only for an
anomaly check. Cache-write input may be retained as metadata but is not added
to canonical total.

### 6.1 Global event deduplication

Do not sum cumulative snapshots. Create a canonical SHA-256 fingerprint from:

```text
schema-version | thread-id |
total(input,cached,cache_read,cache_write,output,reasoning,total) |
last(input,cached,cache_read,cache_write,output,reasoning,total) |
context-window
```

Semantic v2 identity uses the same seven independent total/last/context fields
(`input`, `cached_input`, `cache_read_input_tokens`, `cache_write`, `output`,
`reasoning`, `total`) on both tuples plus context-window presence; missing is
distinct from explicit zero. Raw thread fingerprints remain durable metadata;
lineage consumption uniqueness is `(parent-tree root, semantic identity)` and
does not replace the thread-scoped fingerprint.

Normalize missing numeric fields distinctly from zero. Exclude timestamp,
path, and byte offset so that the same snapshot replayed later or copied to an
archive remains a duplicate. Insert the fingerprint under a SQLite unique
constraint before any quota-window filter or aggregate update. Only the first
accepted fingerprint contributes `last_token_usage` to totals. Later matches
may increment a duplicate-observation counter but never tokens.

The normal trusted path requires both cumulative and last snapshots. If a new
schema supplies last usage without cumulative usage, persist a degraded
metadata observation keyed by file identity and byte offset, do not silently
treat it as globally reliable, and surface a schema warning.

### 6.2 Turn and session aggregates

Use the latest structural `turn_context`/task boundary to assign a monotonic
turn key per thread. Aggregate all unique model-call increments in that turn
for `最近一轮增量`. If no reliable turn boundary exists, show the latest event
as `最近事件（降级）` rather than asserting a turn.

`会话累计` is the sum of canonical own events for that thread after parent-tree
semantic uniqueness. `父工作合计` adds descendant canonical events. `本额度周期全部会话合计`
is the sum of canonical sample event timestamps within the mechanically determined
current quota window. Lineage canonicalization happens after thread-scoped
fingerprint insert and before quota-window filtering, so an ancestor snapshot
replayed onto a child thread during a new window does not count again.

### 6.3 Context continuation reference

Treat `event_msg/context_compacted` as the preferred completed compaction
boundary. The post-compaction baseline is the first reliable non-zero token
sample later in the same source generation. If that event is absent, a
same-turn high/zero/lower sequence may provide a labeled heuristic fallback.
Never infer a boundary from cumulative raw-token volume alone.

Lineage migration, root reselection, and explicit rebuild reconstruct eligible
`context_compacted` boundaries from durable `structural_events` together with
canonical token samples. Explicit evidence keeps priority over heuristics.
Reconstruction does not require deleted rollout logs. Inherited or noncanonical
ancestor evidence cannot create a false child baseline; genuine child-specific
boundaries remain attributed once.

Persist only boundary identity/time/source order, baseline token/window/model,
detection source, and the reliable turn/token work between adjacent comparable
baselines. Compare only observations with the same model and context window;
model/window changes reset sample sufficiency. Prefer three or more explicit
observations; use heuristic observations only as a separate group and never mix
the two groups into one trend.

The UI exposes the recent-three baseline median, latest-vs-earliest baseline
trend, and latest interval vs prior interval runway trend. It deliberately does
not expose current context occupancy or compaction count. Fewer than three
comparable observations is unavailable. No metric may claim that content was
forgotten.

Structural continuation guidance is an explainable six-level hierarchy. The post-compaction
baseline sets the base grade: `<25% = S`, `25%–<35% = A+`, `35%–<45% = A-`,
`45%–<55% = B+`, `55%–<65% = B-`, and `>=65% = C`. The baseline trend is the
second-order signal: `-3` to `+3` percentage points is stable, a larger increase
worsens one grade, and a larger decrease improves one grade. Runway is supporting
evidence only. Turn and compression-interval raw-token changes are meaningful at
`20%`; both improving may improve one grade, both shrinking may worsen one grade,
and split signals do not change the grade. A meaningful baseline trend takes
precedence over runway, and the final grade may differ from the baseline grade by
at most one level. No single runway metric may trigger a recommendation.

Grade actions are: `S` light/continue confidently, `A+` healthy/continue, `A-`
continue while watching the baseline trend, `B+` continue but prepare a handoff,
`B-` move to a continuation conversation after finishing the current complete
work package, and `C` move as soon as practical without opening a new large work
package in the current conversation. The UI shows the final grade, action, base
grade, trend state, and combined runway state.

The structural grade is not a semantic-reliability score. Add a local-relative
long-session risk using cumulative canonical raw tokens, aggregated turn count,
and the last 48 hours of reliable-timestamp token/turn activity. Compare only
primary sessions with the same model and require at least ten candidates. Raw
tokens never act alone: both lifetime volume and turns must be high relative to
the local median and rank distribution before this risk imposes a `B+` or `B-`
minimum grade.

Actual semantic drift is manual because detecting it would require reading message
content. Persist only thread id, one of `occasional` or `repeated`, and observation
time; absence means `unassessed`, never zero drift. Occasional drift imposes a
`B-` minimum and repeated/obvious drift imposes `C`. The composite continuation
grade is the worst of structural grade, long-session minimum, and manual drift
minimum. The UI must expose all three inputs so the composite is explainable.

## 7. File identity, offsets, rotation, and restart

Persist a source identity composed of thread id, Windows volume serial, and
128-bit file id obtained with a shared-read handle. If file-id APIs fail,
degrade to canonical path + creation time + allowlisted session id and record
the degraded identity; never hash message-bearing file contents.

Required state-machine behavior:

- Append with the same identity: resume at the durable complete-line offset.
- Path changes with the same identity: update the path and retain the offset.
- Same path with a new identity: create a new source generation and read it
  from zero.
- Same identity becomes shorter than its offset: mark truncation, increment
  generation, and rescan from zero; event fingerprints prevent double count.
- Active log copied/moved to archive: discover both roots; copied content may
  be reread but global fingerprints prevent double count.
- Restart: reload offsets, file identities, fingerprints, and aggregates from
  SQLite before scanning.

Initial historical indexing runs in bounded background batches and displays
`索引中`; it must not freeze the HUD. Subsequent append scans default to eight
seconds. A single pass should have a byte/time budget and yield cancellation.

## 8. Session metadata and status

Per session expose: safe display name/short id, role/nickname, project tag,
model, reasoning effort where useful, service tier, last activity, and status.
Latest rollout context overrides stale database model metadata.

Status rules:

- `运行中`: latest accepted task-start is newer than task-complete and the
  source has activity within 120 seconds.
- `未知`: a task appears open but the source is stale beyond 120 seconds.
- `空闲`: task-complete closes the latest start, or there is no open task.

`运行中会话` only means a session whose mechanically derived status is
`运行中`; an idle session is never counted merely because it had activity in
the last 15 minutes. `当前会话` is a user-pinned row; until pinned it is the
most recently active visible session and must be labeled as inferred in its
tooltip.

Service tier resolution:

1. Use an explicit allowlisted rollout service-tier field when present.
2. Otherwise, use the App Server model catalog only if it mechanically shows
   that model's default service tier is null/default; display
   `Standard（默认）`.
3. Explicit `priority`/`fast` is displayed as `Fast` and never enabled by the
   HUD.
4. If evidence is insufficient, display `不可用`.

## 9. Official quota adapter

Codex quota remains app-server only. The application must not implement a
direct Codex HTTP usage client. Discover a local `codex.exe` from configured
override, the npm package's native binary, PATH, or the installed Codex Desktop
package. Prefer a native binary; use `codex.cmd`/`codex.bat` only as
compatibility fallbacks. Launch a short-lived child process:

```text
codex -s read-only -a never app-server
```

The primary Codex slot sets `CODEX_HOME` only on that child process environment
so quota and `account/read` identity share the same authenticated profile. The
secondary slot does the same only when its optional home is set; otherwise it
uses the PI usage endpoint above. `account/rateLimits/read` prefers
`rateLimitsByLimitId` independent buckets (`usedPercent`, `windowDurationMins`,
`resetsAt`, optional `planType`). Legacy `rateLimits` is used only when that
map is absent. Do not infer a model bucket or a plan entitlement that the
response did not return. Missing identity invalidates any previous association;
do not combine one account's quota with another's identity. Do not change the
parent process environment or copy logins. HTTP provider bodies are bounded streaming reads with stable
sanitized error codes. Rail and panel remaining percentages name the exact
window or pool; they are never averaged.

Pascal 2026-09-20 authority additionally allows opt-in, runtime-only quota
adapters for Cursor, Grok and Grok Bot that use existing local logins against
fixed HTTPS hosts/paths, with no redirects, bounded timeouts, in-memory
credentials, and no credential logging. Missing login is `未连接`, never a fake
percentage. Codex HTTP remains disabled. Grok weekly billing remains
`GET https://cli-chat-proxy.grok.com/v1/billing?format=credits`. Access-token
expiry with a remaining refresh grant is `grok_renewal_unavailable` until the
official CLI refresh succeeds, `grok_login_revoked` when the grant is gone, and
`grok_login_expired` only when no refresh grant exists. The Grok slot may wait
up to 35s for that CLI refresh; the 500ms board publish budget is unchanged.

Claude subscription quota is a separate allowlisted GET, not a Codex or
Cursor pool. When the body contains a `limits` array, that array is the
current reading: `session` / `five_hour` is the five-hour window, `weekly_all`
/ `weekly` / `seven_day` is the overall weekly window, and `weekly_scoped`
entries are shown only when `scope.model.display_name` is present. `percent`
and legacy `utilization` are the same used-percent unit; remaining is
`100 - used`. `is_active: false` means the window is not the one currently
binding the request. A valid `session` / `five_hour`, overall weekly, or named
`weekly_scoped` entry is still shown as its own window, using only the server
percent and reset. It is not labeled as no allowance, and a weekly value is
not inferred from the five-hour window. An unknown kind, an unnamed scope,
conflicting values for the same window, or a used percent outside `[0, 100]`
is omitted. If the array yields no usable window,
the reader falls back to the legacy top-level `five_hour`, `seven_day`, and
`seven_day_*` objects. A missing or non-numeric window is omitted, never
rendered as 0% or 100%. A missing or unparseable reset is not invented from a
duration. `extra_usage` credit fields are not subscription quota. HTTP 401/403
is `未连接` and is not retried inside the read. It clears any previous Claude
windows for that slot. An unsupported body stays unavailable. After a
successful Claude quota read, HTTP 429, timeout, network failure, and HTTP
5xx keep the last successful five-hour, weekly, and model windows, the
original observation time, and the verified identity, marked `更新延迟`. They
do not invent a new observation or percentage. With no successful observation
yet, the slot stays unavailable. Disabling the slot, a configuration or
account change, unusable or ambiguous credentials, and identity rejection
also clear the previous windows. Expired resets are not shown as available;
a weekly window that has not reset stays separate. The Claude reader does not
write its own quota cache. The only quota file is the opt-in snapshot in section 11,
and that file stays unwritten until its setting is on.

Claude automatic successful reads wait 5 minutes. HTTP 429 then waits 5, 10,
and 20 minutes, capped at 30 minutes, and a manual refresh waits out that
cooldown as well. Other Claude transport failures keep the shared 15-second
backoff that doubles up to 2 minutes. A Claude observation stays live for 6
minutes, so the 5-minute cadence is not labeled stale. Other providers keep
the 60-second cadence, 75-second freshness, and 15-second backoff. No Claude
Code process is launched and no model call is made.

Diagnostics name the source as `claude_code_file` or `claude_desktop` and do
not include the access token.

Do not pass a service-tier override. Use JSON-RPC over stdin/stdout:

1. `initialize` with the HUD name/version
2. `account/rateLimits/read`
3. optional identity `account/read` if the helper supports it; unknown identity
   is unlabeled rather than assumed
4. model catalog read only when needed for default-tier evidence

Use a bounded startup timeout (10 s), per-request timeout (5 s), cancellation,
and process-tree termination. Do not retain stdout lines beyond the matching
response. Quota polling defaults to 60 seconds; countdown rendering uses the
local clock and does not repoll each second.

`never` is the non-interactive approval policy; it does not weaken the separate
`read-only` sandbox. An App Server process that exits or closes stdout before
`initialize`/`account/rateLimits/read` is classified as an early-exit/EOF
compatibility failure rather than mislabeled as a timeout.

Select the main Codex bucket by stable limit id/name, not by array position.
If selection is ambiguous, show unavailable. Preserve additional model buckets
as observations but do not mix them into the primary collapsed display.

For the selected window:

```text
remainingPercent = clamp(100 - usedPercent, 0, 100)
cycleStart        = resetsAt - windowDurationMins
```

Store every valid official observation. Passive `rate_limits` values already
present in allowlisted rollout token events may serve only as a timestamped
fallback marked `本机最后观测/陈旧`. Never estimate quota from raw tokens.

If remaining quota increases by at least 15 percentage points or another
window-identity change indicates a substantial jump, record
`疑似 reset 信号` with before/after metadata. Do not label it a confirmed TIBO
or reset event.

## 10. SQLite contract

Default database: `%LOCALAPPDATA%\CodexUsageHUD\usage.db`. Tests and
diagnostics must support an explicit data-directory override inside the
project. The published ZIP does not contain `ISOLATED_PREVIEW.marker`. Ordinary
startup without `--data-dir` uses the LocalAppData database and the default
Codex home; `--data-dir` / `--codex-home` / `--isolated-preview` are QA or
preview overrides and do not change published defaults. A markered preview
directory must supply both overrides before it may start. Provider live
diagnostics are a test helper (`--provider-live-diagnostic`), not a default
application launch path, and must not print credentials or private bodies. Enable WAL, foreign keys, a bounded busy timeout, migrations, and
short single-writer transactions.

At minimum model these durable concepts:

- schema/migration version
- source files: identity, current path, thread, generation, complete offset,
  length/write observation, health/error code
- event fingerprints with first/last seen and duplicate count
- token samples with approved components, turn key, source offset, model/tier,
  event time, observation time, and confidence
- lineage metadata on token samples: live semantic identity/material (nullable
  total tuple, nullable last tuple, context-window, missing-versus-zero, no
  thread id), SHA-256 identity matching stored material, a deterministic v1
  legacy compatibility alias for schema-v9 rows, resolved parent-tree root, and
  canonical flag. Consumption uniqueness is `(root thread id, semantic identity)`;
  earliest reliable `(event time, source, generation, offset, id)` is canonical.
  Schema v9 rows are rebuilt from allowlisted stored token columns without
  requiring deleted logs; equivalent later live ancestor replay matches migrated
  v9 through a bounded, non-transitive legacy alias that pairs the legacy
  exact-group winner with at most one live v2 identity. The paired live
  representative stays discoverable after compatibility demotion, later inserts,
  restart, root reassignment, and full rebuild, so additional distinct live
  missing/zero/presence identities remain canonical. Pairing follows existing
  canonical order and moves reversibly when an earlier representative arrives.
  Combined exact-election and compatibility-overlay flag changes for one insert
  are applied as a net canonical delta: a sample both promoted and retired has
  no aggregate effect. Merging session metadata preserves an existing
  `parent_thread_id` when the incoming value is omitted; an explicit
  `ClearSessionParent` write is the distinguished parent-removal path. Inserting a
  previously missing parent through rollout `CommitScan`, or otherwise changing
  root-relevant session membership, marks lineage unavailable and schedules the
  same deterministic rebuild used by other late-parent paths. Identity,
  alias, or root changes re-elect both the old and new groups so a group never
  has zero or two winners. Missing, malformed, or cyclic `parent_thread_id`
  chains remain isolated roots. Context and lifecycle consumers rebuild from
  durable metadata: canonical samples including persisted model, plus explicit
  `context_compacted` rows in `structural_events`. Derived context replay is
  lossless and idempotent across lineage migration, reparent, rebuild, and restart.
- safe session metadata and derived status
- quota observations and suspected-reset events
- metadata-only parser/source errors
- local UI settings such as window position, collapse state, filters, pinned
  thread, optional pet companion mode, and `quota_snapshot_export` (`1` or `0`,
  missing means off, stored in the existing `ui_settings` table with no new migration)
- manual session drift assessment containing only thread id, enum level, and timestamp

Use integer token columns and checked/saturating aggregation. Add indexes for
thread/time, quota window/time, and source identity. No table may contain
message body, prompt, response, raw JSON, token/credential, browser data, or
full exception dumps.

## 11. UI contract

The borderless WPF window supports optional always-on-top, dragging, DPI awareness, and remembers
position. Pointer reveal of an auto-hidden docked card raises it above overlapping ordinary and
topmost windows without activation or keyboard focus, and does not change the saved always-on-top
preference. That temporary raise ends when the card hides if always-on-top is off. Closing hides to the system tray; tray commands are Show/Hide,
Refresh, Settings, and Exit. Exit cancels workers and closes SQLite cleanly.

The top-level HUD window is opaque so Windows can use ClearType text rendering.
Do not apply bitmap effects or fractional layout transforms to containers that
own text. Use native DWM corner rounding, fixed text hinting, integer layout
rounding, readable minimum label sizes, and sufficient muted-text contrast.

Collapsed form shows one line equivalent to:

```text
剩余 84% · 6天18时 · 运行中 3 · 本周期 12.4M raw tokens
```

The compact visual uses an opaque light card, deep-green normal state,
readable Chinese typography and a horizontal remaining-quota bar. The default
collapsed rail is 252 logical pixels wide, with six labelled slots. Its height
follows the measured slot list and scrolls when the work area is shorter. The
top rail is one row of those six slots, up to 1100 logical pixels wide. Claude
is labelled distinctly and does not feed Codex session analysis. The previous
compact card (224 by 324; top 660 by 80) remains a settings option. Both retain
quota, reset, settings, window-topmost, tray and expand controls. Iconography
uses original letter marks and Windows system assets, not copied third-party icons.

Expanded form contains:

1. overview: used/remaining, official reset time, countdown, running count,
   all-session cycle aggregate and quota source. The data-status drawer retains
   the running-session cycle aggregate and source freshness; the running
   aggregate never uses those sessions' all-history totals.
2. horizontal filters: recent (default) / running / all / orphan history
3. name-first session list with source/project/model, status, work total and
   expandable child tasks. The inspector retains full identity/metadata and
   precise latest-turn and session-lifetime input/cache/output/reasoning/total
   columns. Long values have ellipsis and full-value tooltips; the complete
   thread ID is selectable under session information.
4. pinned-first activity or work-token sorting; no project-group button
5. data-status drawer with recent quota/reset/error events
6. permanent note: `raw token 与平台额度不是同一单位，不能直接换算`
7. expandable selected-session continuation risk: structural grade, local-relative long-session
   risk, three-state manual drift control, and worst-case composite recommendation

Color is used only for normal, below 20%, below 10%, and unavailable/stale.
Below 20% must be conspicuous; below 10% stronger. Provide readable text/icon
state so color is not the sole signal.

Optional pet companion mode parses only `electron-avatar-overlay-open` and
the approved mascot/anchor bounds from `.codex-global-state.json`. Validate
coordinates, support negative-coordinate monitors and mixed DPI, and position
the collapsed HUD near but not over the pet. On invalid/hidden state, retain
the last safe independent HUD position rather than hiding.

### Opt-in local quota snapshot

Settings include "导出本机额度快照". The checkbox is off for a new install and
when `quota_snapshot_export` is missing or any value other than `1`. Turning it
on may write the board already in memory; it does not start a provider read.
Each later quota publication, while the setting stays on, writes the same
complete six-slot board the HUD is showing. Session indexing alone does not
write the file. Ageing that the panel applies between publications appears on
the next publication.

The file is `%LOCALAPPDATA%\CodexUsageHUD\quota-snapshot.json`, beside `usage.db`
in the HUD data directory (`--data-dir` included). The writer reuses one owned
temp, `quota-snapshot.json.tmp`, in that directory and replaces the snapshot
only while the setting is still on, so a reader sees the previous complete
document or the new one. A failed or interrupted write does not create another
temp name; the next write overwrites that same temp. The writer does not delete
unrelated files. Turning the setting off drops a queued replace. The previous
file may remain; its
`written_at_utc`, each window's `observed_at_utc`, and `state` say how fresh it
is. File mtime is not a freshness signal. IO errors are contained and do not
change refresh, tray, docking, or provider reads.

Schema is `1`. UTF-8 JSON, no BOM. Root fields are `schema`, `written_at_utc`,
`hud_version`, and `slots`. `hud_version` is the App assembly version
`major.minor.build` (candidate `1.1.3`). `written_at_utc` is the replace time.
`slots` always has these six entries, in this order:

| `slot` | `label` |
| --- | --- |
| `codex_primary` | Codex主 |
| `codex_secondary` | Codex备 |
| `cursor` | Cursor |
| `grok` | Grok |
| `grok_bot` | Bot |
| `claude` | Claude |

Labels are those fixed names. A renamed slot label in settings is not written.
A slot missing from the in-memory board is `unavailable`.

`state` is `ok`, `stale`, `unavailable`, or `disabled`. `disabled` is a slot the
board marks disabled, and its `windows` array is empty. `ok` is a live reading
that is not update-delayed. `stale` is an aged reading or a retained older
reading; Claude `UpdateDelayed` is always `stale` and keeps the original
successful `observed_at_utc`. `unavailable` is not connected, not configured,
no allowance, identity collision, unverified, or a failed read without a
retained reading. That slot's `windows` array is empty, so a failure cannot
wipe another slot.

A window object has `pool`, `window_minutes`, `remaining_percent`,
`resets_at_utc`, and `observed_at_utc`. Missing minutes, percent, or reset are
JSON `null`. They are not replaced with 0, 100, or a guessed time.
`observed_at_utc` is the successful observation time, not `written_at_utc`.
`remaining_percent` is present only when that window still has a usable
allowance. It stores the raw double. The panel rounds that official value to at
most two displayed decimal places, and a whole number stays a whole number.
Known pools, and only these, are exported; any other window id is
left out instead of copied:

| Slot | Source window id | `pool` |
| --- | --- | --- |
| Codex主 or Codex备 | `primary`, `primary_window` | `codex_primary_window` |
| Codex主 or Codex备 | `secondary`, `secondary_window` | `codex_secondary_window` |
| Codex主 or Codex备 | `weekly` | `codex_weekly` |
| Codex主 or Codex备 | `codex`, `codex-main`, `codex_main` | `codex` |
| Codex主 or Codex备 | `codex/secondary`, `codex-main/secondary`, `codex_main/secondary` | `codex_secondary_window` |
| Cursor | `autoPercentUsed` | `cursor_auto` |
| Cursor | `apiPercentUsed` | `cursor_api` |
| Cursor | `团队共用` | `cursor_team` |
| Cursor or Bot | `shared_weekly` | `x_shared_weekly` |
| Grok | `weekly` | `grok_weekly` |
| Bot | `sand` | `grok_bot` |
| Claude | `five_hour` | `claude_five_hour` |
| Claude | `seven_day` | `claude_seven_day` |
| Claude | `seven_day_` plus the HUD's validated model slug | `claude_` plus that id |

Windows stay separate. The file does not collapse them into the single smallest
glance percent. The same pool can appear more than once; `window_minutes` and
`resets_at_utc` keep the entries distinct.

`x_shared_weekly` is written only when `get-sand-usage-status` returns boolean
`isUnified: true`. The Cursor slot and the Bot slot may each contain that pool.
They are two views of one Sand allowance and must not be added together. The
window uses that response's `usagePercent`, `currentPeriodStart` and
`nextResetTimestampUtc`; monthly `autoPercentUsed` / `apiPercentUsed` are not
substituted. Explicit `isUnified: false` keeps `cursor_auto`, `cursor_api`,
`cursor_team` and Bot `sand` as `grok_bot`. If `isUnified` is missing or not a
boolean, Cursor does not export a monthly pool from `usage-summary`. Bot can
still export legacy `grok_bot` when the Sand body itself has a legacy allowance
and no explicit unified flag. `grok_weekly` remains the Grok credential's own
billing window. A similar percent or reset does not make it the same allowance,
and the file does not copy or add it to `x_shared_weekly`. On one paid seat, that
weekly allowance is also what eligible first-party Grok and Build use. A separate
source, a different display name, or a whole-number Grok percent does not make a
second budget, and a matching reset does not let one login replace another.
The detail and status text use “Cursor/Grok/Bot 共享周额度”. The narrow rail caption is
“共享周额度”, beside the slot name, so the full phrase is not clipped. The slot
detail popup shows one wrapped sentence under the window rows. Cursor and Bot
show it when the shared weekly window is present. Grok's sentence says that its
weekly number is the official read for that login, not an extra allowance and
not a copy of the Cursor number.

The document is built from those fields only. It does not contain custom labels,
account keys, identity hashes, config fingerprints, source or error text,
delay text, paths, prompts, responses, or authentication data. Raw token totals
are not converted into quota, and a percent jump is not reported as a reset.

Consumers should treat `disabled` and `unavailable` as having no usable numbers,
treat `stale` numbers as the last retained observation, and ignore a file whose
`written_at_utc` or `observed_at_utc` is older than the decision they are making.
`null` means the HUD did not have that value.

## 12. Reliability and resource behavior

All file/App Server/SQLite work runs off the dispatcher. Use cancellation and
contain failures per source. A malformed log, locked database, missing field,
or App Server timeout changes one source to unavailable/degraded and records a
safe error; it must not crash or freeze the window.

Default cadence:

- UI countdown: 1 second, no I/O
- append/session refresh: 8 seconds
- quota refresh: 60 seconds plus manual refresh; a successful automatic Claude read waits 5 minutes
- source discovery: 30 seconds or when metadata changes

Avoid FileSystemWatcher as the sole truth source; periodic reconciliation is
required. Keep buffers pooled/bounded and do not keep rollout files open
between scans.

## 13. Required automated fixtures and tests

Create at least these three named synthetic fixture groups:

1. `cumulative-sequential.jsonl`: cumulative snapshots plus last increments;
   unique increment sum must equal the terminal cumulative total.
2. `duplicate-stale-replay.jsonl`: exact duplicate at a later timestamp and an
   older cumulative snapshot interleaved; duplicates/stale replay must not add.
3. `rotation/`: active file, copied archive, replacement/truncation, and a
   partial final line; moving/copying/restarting must not add twice.

Also test:

- cached/reasoning subset math and overflow protection
- complete-line offsets and corrupt/oversized-line recovery
- event-like text embedded in an unknown message body is ignored
- missing/changed fields become degraded/unavailable without exceptions
- SQLite migration and two consecutive index runs preserve identical totals
- active/archive discovery and same-path new-file identity
- quota parsing, bucket selection, countdown, staleness, and suspected reset
- status transitions and stale open-task -> unknown
- session metadata query contains no forbidden column names
- long-session ranking, manual drift override, clear/restart persistence, and rapid-click single-flight behavior
- lineage missing-versus-zero tuples and context, v9 plus equivalent live replay,
  re-election after identity/root change, independent lineage-cycle oracle
  defect injection, and exact session/turn/latest/frontier/bucket/cycle/context/
  lifecycle/parent-work/render values
- a high-entropy secret/message sentinel in an unknown record never appears in
  database rows, app logs, diagnostics, or rendered view models

Tests must not open the real `auth.json` or make direct network requests.

## 14. Real-machine and UI acceptance

Provide metadata-only diagnostic commands/scripts that:

1. select at least two recent real rollout files without printing messages
2. calculate each thread's deduplicated component totals
3. compare them to mechanically reconstructed rollout evidence
4. index into an isolated acceptance database, stop, restart, and prove totals
   are unchanged
5. inspect every text/blob value in that acceptance database and HUD-owned log
   for the privacy sentinel

Lead acceptance will additionally exercise: drag, fold/unfold, topmost, close
to tray, restore, manual refresh, unavailable state, threshold presentation,
multi-monitor/DPI positioning where available, and launch from the packaged
artifact. Automated tests are evidence, not substitutes for this path.

## 15. Build and packaging

Because no system .NET SDK is installed, bootstrap an official .NET 8 SDK into
`.tools/dotnet` only. Pin the resolved SDK version in `global.json`; set
`DOTNET_CLI_HOME`, `NUGET_PACKAGES`, and build artifacts to project-local
ignored paths, and opt out of CLI telemetry. Do not require administrator
rights or change machine-wide PATH/registry during build.

Provide `scripts/build.ps1`, `scripts/test.ps1`, and `scripts/publish.ps1`.
Publish self-contained `win-x64`, single-file where WPF/native SQLite permits,
with trimming disabled. The final `dist/CodexUsageHUD-win-x64` must launch on
this machine without an installed .NET runtime. Include a ZIP and SHA-256
manifest. Unsigned/SmartScreen limitations must be stated honestly.

README must document data sources, exact token field/subset meanings, quota
source and fallback, privacy boundary, storage paths, known limits, controls,
how to verify numbers, how to remove app-owned data, and that raw tokens cannot
be converted to platform quota.
