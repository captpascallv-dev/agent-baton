# Codex Usage HUD — package acceptance summary

Build: `1.1.3` — opt-in local quota snapshot, default off. The accepted v1.1.2 behavior remains: optional Claude subscription quota, an explicit secondary Codex CLI home, and no-focus edge reveal. The five original slots remain; Claude is a sixth slot and defaults off.

This summary describes the published interface and its validation scope. It
contains no real local usage, session identifiers, credentials or machine paths.

## Interface

- Opaque light surfaces, deep-green normal state, explicit Chinese font
  selection, ClearType and layout rounding. No whole-panel bitmap effects or
  fractional text scaling.
- A quota overview, horizontal session filters, a name-first virtualized
  conversation list and a selected-session inspector replace the old sidebar.
- The inspector places recent-turn and session-lifetime values in separate
  columns. Precise counts and complete identifiers remain accessible; overly
  long text is ellipsized with complete-value tooltips.
- Continuation details and manual drift controls are expandable. Their
  underlying calculations and meanings are unchanged.
- Vertical compact card remains 224 by 324 logical pixels; the top bar is 660 by 80.
  The default collapsed layout is a six-slot rail, 252 logical pixels wide, with
  height measured from the six provider rows and capped to the current work area
  (scroll appears only when that cap is tighter than the content). The top rail
  prefers 1100 by 78 and stacks to a 96-high layout near 640; it does not force a
  640 minimum that would overflow a narrower work area. The previous card remains
  available in settings.
- Fullscreen increases the inspector width and font sizes directly, keeping
  native text rendering. Restoring returns to the prior window bounds.
- The tray uses a dedicated system-font mark instead of a shrunken screenshot
  of the entire compact card.

## Quota slots

The rail keeps the five original slots and adds one optional Claude subscription
slot. Claude defaults off. Enabling it does not change the other slots' saved
choices, and the ZIP does not write anyone's enabled-slot choices.

- Primary Codex uses the local App Server `account/rateLimits/read`. Session
  analysis stays on the owner-confirmed primary home.
- The second Codex slot is quota-only. A blank home still reads PI's existing
  ChatGPT/Codex (`openai-codex`) login against `chatgpt.com/backend-api/wham/usage`
  and does not require a second `CODEX_HOME`. An explicit directory selects that
  directory's Codex CLI app-server quota instead, with no PI fallback.
- Cursor, Grok and Grok Bot default off. After the user enables a slot, the HUD
  reads only the necessary existing-login field and calls that service's own
  quota endpoint. Missing login is 未连接, never a fake 0% or 100%.
- Claude is quota-only and defaults off. A transient failure for the same account
  keeps the last observation and is marked 更新延迟. Valid weekly and model windows
  stay visible when `is_active` is false. Existing Claude Code login is preferred;
  when that file has no usable access token, the already accepted fallback uses
  one unexpired token from the current Claude Desktop profile. There is no Claude
  session analysis and no credential refresh.
- Grok access-key expiry with a remaining refresh grant runs the official
  installed `grok models` CLI so Grok can lock and rewrite its own login file.
  The HUD does not write `auth.json` and does not treat a file timestamp as
  logout.
- Published packages do not contain `ISOLATED_PREVIEW.marker`, local launchers,
  or anyone's enabled-slot choices. Double-clicking the EXE uses
  `%LOCALAPPDATA%\CodexUsageHUD` and the default Codex home. `--data-dir` and
  `--codex-home` are packaging/QA overrides only.

## Focused verification

v1.0.5 retained seven HUD-focused checks covering display semantics, native WPF
construction and runtime interactions, a 1,000-session source with rapid
filtering/sorting and 30 collapse/expand pairs, left/right/top docking,
9-pixel handle restoration, and off-screen recovery. Synthetic captures are
not live quota observations.

v1.1.0 adds focused provider and Grok-renewal checks (expiry then renew, reread,
single-flight, timeout/failure vs revocation, billing 401 retry, unsupported
login shape, isolated-preview marker gating). Grok live renewal was verified
on the maintainer machine with sanitized status/expiry/window/percent fields
only. That local install is separate from this public package. Physical
multi-monitor DPI soak and overnight soak were not claimed for this release.

v1.1.1 adds focused synthetic PI renewal coverage (seven targeted checks) and
retains an accepted local Grok cache-invalidation fix. PI native `auth check`
recovered an expired local login; a later sanitized quota read returned Live
with two windows. A real credential was not force-expired to exercise the HUD
renewal branch. The local compact WPF rail was not visually targetable by the
automation, so its text was not independently confirmed on-screen.

v1.1.2 packages the accepted source after public v1.1.1. It does not replay the
full automated catalog and does not treat the older catalog count as a product
PASS. Accepted behavior covered here: the Claude slot stays off until enabled;
a same-account transient failure keeps the last observation and is marked
更新延迟; valid weekly and model windows stay visible when `is_active` is false.
The second Codex slot uses an explicitly configured CLI home when one is saved;
an empty home keeps the PI login, with no fallback, account mixing, or secondary
conversation analysis. Auto-hidden left, right, and top reveal raises the card
above overlapping windows without taking keyboard focus. The pin control and the
saved always-on-top preference stay unchanged, and hide still respects that
preference. Synthetic native product-event focus and z-order evidence was
accepted for that raise. The maintainer personally confirmed the installed
right-edge experience. This summary does not claim a new physical hover, a live
provider read, an overnight soak, or a multi-monitor soak.

v1.1.3 adds a settings checkbox, "导出本机额度快照", stored as
`quota_snapshot_export` in the existing `ui_settings` table. Missing or any value
other than `1` is off, for both new and existing databases. While on, each quota
publication writes schema `1` to `quota-snapshot.json` in the HUD data directory.
The file lists all six fixed slots and keeps Codex, Claude, and Cursor windows
separate. States are `ok`, `stale`, `unavailable`, and `disabled`. Null stays null.
Claude update-delayed rows stay `stale` and keep the original observation time.
The writer reuses one owned temp, `quota-snapshot.json.tmp`, and replaces the
snapshot only if the switch is still on. It does not allocate a new temp per
attempt or delete unrelated files. The Codex app-server windows `codex` and
`codex/secondary` stay separate (`codex` and `codex_secondary_window`); an
unknown limit id is omitted. A failed slot does not clear the others, and a
failed write does not stop refresh. The snapshot does not add a provider, a
credential read, or an upload.
Focused synthetic checks cover the allowlist, the six slots, nulls, the toggle,
atomic readers, and an isolated IO failure. Those checks are not a live quota
comparison and not a full-suite replay.

The same version keeps one shared weekly pool when the provider returns boolean
`isUnified` true. Cursor and Bot are two views of that pool and are not added.
Grok keeps its own official weekly read on its own credential. A whole-number
percent and a fractional percent are not two budgets, and one login is not copied
onto another. Displayed percents use the official value to at most two decimal
places; integers stay integers. Snapshot `remaining_percent` stays the raw double.
The slot detail shows one sentence under the window rows for that shared window,
and a separate sentence on Grok. This release preparation checks that shared-pool
behavior, the snapshot, and the percent presentation with synthetic fixtures. It
does not rerun the earlier accepted catalog and does not read a live provider.

A green test result does not replace the real window and interaction checks.

## Package and operating boundaries

The existing publishing script builds a self-contained Windows x64 application,
scans the package for private paths and identifiers, verifies its internal
manifest, starts and exits an isolated instance, extracts the ZIP and checks
the SHA-256 sidecar. A source build or this document alone is not proof that a
particular package passed those steps; retain the publishing result alongside
the artifact.

A normal portable upgrade is: exit the old HUD from the tray, unpack the new ZIP
over the program files, and open the EXE again. Existing data and settings under
`%LOCALAPPDATA%\CodexUsageHUD` are not deleted. The ZIP contains no installer,
no authentication reset, no data migration, and no enabled-provider choices.

Local installation and public GitHub publication are separate operations.

Existing safeguards remain: one HUD writer per data directory; second launch
restores the first instance; source failures degrade to stale/unavailable;
startup, topmost and docking preferences persist; exit closes the runtime.

Quota and reset data for the current Codex App account come from the local
Codex App Server read-only method. Raw tokens never estimate platform quota
or billing. Official quota can be Live while `account/read` identity is still
missing; the primary popup then says 主目录已确认绑定 and does not claim
per-row machine verification. Nested ChatGPT `account.email` is hashed only in
the email namespace for equality, never as account_id history proof. Session
analysis defaults to the owner-confirmed primary directory minus classifiable
foreign rows. If the live App identity later changes, historical rows are not
rebound to the new identity. The HUD never changes service tier and never reads
message bodies or browser storage. Runtime may keep an in-memory copy of an
enabled provider's necessary login field for that request only. The v1.0.2
lineage-aware deduplication and current-cycle filtering are retained.

The automated catalog was previously recorded at 127 checks. That historical
count is evidence from earlier releases, not a product PASS for v1.1.2 or v1.1.3. This
release preparation did not rerun the full suite.

## Limits

Windows 10/11 x64 only. The package is unsigned, so Windows SmartScreen may
prompt. Automated display checks do not represent every physical monitor,
multi-monitor DPI combination or hardware hot-plug event. Real quota freshness
depends on each enabled provider's installed client and current local login.
Grok renewal requires the official Grok CLI; a missing binary or failed refresh
is shown as unavailable, not as a fake Live percentage.
