# Data format

Company facts live in a git repository you own. `Onestead` reads that repository. It does not keep a second copy of the facts.

Files are UTF-8 Markdown. A file the reader loads starts with YAML front matter between two lines that contain only `---`. The writers save files with LF line endings. The reader turns CRLF into LF before it parses. A bare CR is left as it is.

The repository has to contain a `.git` directory. `onestead init` creates that repository. See [Getting started](getting-started.md).

Section titles, project states, app kinds, and skill origins below are Chinese because the program looks for those exact words. A later release is planned to make them translatable. The prose under the headings can be any language.

One member id is reserved for the local web app. MCP and the CLI reject it. That id is `owner_key` in the config file, or `ONESTEAD_OWNER`. The environment variable wins. There is no compiled default. If it is missing, the process errors. See [Configuration](deploy.md#configuration).

## Directory layout

The reader loads Markdown only. It does not walk into nested folders, except one level of `projects/<id>/`.

```text
FORMAT.md                      optional handbook, type handbook
handbook/<id>.md               handbook
assets/<id>.md                 asset
decisions/<id>.md              decision
lessons/<id>.md                lesson
people/<key>.md                person
home/<owner>.md                home page, state active
home/<owner>-draft.md          home page draft
todos/<id>.md                  todo
apps/<id>.md                   app
skills/<id>.md                 skill note
projects/<id>/project.md       project
projects/<id>/status.md        progress log, no front matter
projects/<id>/status/<YYYY-MM>.md   older progress moved out of status.md, by month
projects/<id>/links.md         optional links, no front matter
```

`FORMAT.md` at the repository root is loaded as a handbook when the file exists. Its `id` comes from its own front matter.

A project directory without `project.md` does not become a project. If `status.md` or `links.md` is present anyway, the reader still loads that file and marks it as a problem.

## Front matter

The top of a loaded file looks like this:

```yaml
---
id: shop-site
type: project
title: Shop site
sensitivity: internal
---
```

Writers emit one key per line. A short list or a short map inside a value is written on one line. A date is `YYYY-MM-DD`. A clock time is quoted, so YAML does not turn `14:30` into a number of minutes.

The reader checks the front matter when it loads a file. The writers check more, and they are what refuse a bad write. A file you edit by hand can hold values the tools would refuse. The reader still loads it when the checks in this section pass. When they fail, the file is kept and marked with a problem. Assigned readers and anonymous readers do not see a problem file. A full-access reader does.

### Fields every loaded file needs

| Field | Required | Values |
| --- | --- | --- |
| `id` | yes | Non-empty string. For a project, it must equal the directory name. |
| `type` | yes | Must match the folder: `handbook`, `asset`, `decision`, `lesson`, `person`, `home`, `todo`, `app`, `skill`, or `project`. |
| `sensitivity` | yes | `public`, `internal`, or `restricted`. |
| `title` | no | Readers fall back to `id`, then to the path. Writers usually set it. |

`project-status` and `project-links` files have no front matter. In memory they carry the project's front matter, including its `id` and `sensitivity`.

If `sensitivity` is `restricted`, `visible_to` is required. It is a list of strings. An empty list is valid. Any other sensitivity does not need `visible_to`. See [Who can read what](#who-can-read-what).

`access_note` is an optional string. Readers show it as a reminder. It does not grant or deny access.

### Handbook and asset

Path: `handbook/<id>.md`, `assets/<id>.md`, and the optional root `FORMAT.md`.

Beyond the common fields, the reader does not require more. The web app can create both kinds. On that path the id is 2 to 41 characters: a lowercase letter or digit, then lowercase letters, digits, or hyphens. `title` is required there and is a single line of at most 80 characters. The body is at most 60,000 characters.

`update_page(kind, page_id, body, title=None, signed_as=None)` exposes these same limits to full-access writers. `kind` is `handbook` or `asset`; a missing page is created and requires `title`. Updates preserve every other front-matter field, including sensitivity, `visible_to`, and custom fields, and replace only title, body, and `updated`. Assigned writers and remote read-only identities cannot use it; writable full-access remote agents can. Each changed page is one attributed, reversible git commit. The pinned rules in `handbook/company.md` require the owner's explicit direction before editing. CLI: `onestead page-update handbook company --text-file rules.md [--title "Company rules"]`.

### Project

Path: `projects/<id>/project.md`.

| Field | Required | Values |
| --- | --- | --- |
| `state` | on create | On create: `进行中`, `维护中`, `构想中`, `暂停`. Default `构想中`. The web editor and full-access `update_project_summary` also offer `已结束` and `历史`. The reader does not check `state`. |
| `goal` | on create | One line, at most 200 characters. |
| `roles` | yes to pass the reader | A list. Each item is a map with string `role` and string `who`. Both may be empty only if you never save through the reader check: the reader rejects an item that is missing either string. |
| `summary_as_of` | no | Date `YYYY-MM-DD`. Create sets it to today. Updating the `现状` section sets it to today. |
| `updated` | no | Date the writers set on save. |
| `authority` | no | A map. The reader uses `label` and `path` in the summary banner. Remote readers are shown the label, not the path. No tool writes this field. |
| `paths` | no | Absolute directories on the computer that runs Onestead. When present, a nonempty list of nonempty strings. Each item is an absolute path (`C:/shop`, `\\server\share\shop`, or `/shop`). An empty list, a value that is not a list, a blank item, or a relative path marks the file as a problem. A missing field is valid. Local `company()` treats the working directory as this project when it equals one of these paths or is inside it. If several match, the deepest path wins. Comparison ignores a trailing slash; on Windows it also ignores case and treats `/` and `\` as the same. Remote callers do not match by directory and are not shown `paths`. |
| `visible_to` | when restricted | On create of a restricted project, the tool writes an empty list. |

A role may also have:

| Field | Meaning |
| --- | --- |
| `who` | Free text. It is the name shown for the person in that role. It is not looked up as a member key. |
| `key` | A true value marks a key role. Handover of a key role waits for approval. No writer sets `key`. Put it in the file by hand. |
| `since` | Date `YYYY-MM-DD`. Handover sets it to today. |

The create tool refuses `sensitivity: public` unless the caller is the local owner. Other callers choose `internal` or `restricted`.

Id shape for create: 2 to 41 characters, `^[a-z0-9][a-z0-9-]{1,40}$`.

### Project body

The body is Markdown after the front matter. The tools know these level-2 headings, in this order:

1. `目标`
2. `现状`
3. `下一步`
4. `分工`
5. `入口`
6. `边界与注意`

A heading matches when it is `##` plus the name, or the name plus a suffix that starts with `（`, `(`, `:`, `：`, or a space. The longest name wins. This matches:

```markdown
## 现状（截至 2026-10-01）
```

`update_project_summary(project_id, sections=None, signed_as=None, state=None, title=None, goal=None)` lets assigned writers change `现状`, `下一步`, `分工`, and `入口` on their granted projects. Full-access writers (`access_mode: full`, including writable remote identities) may also change `目标` and `边界与注意`, and the metadata fields `state`, `title`, and `goal`. Supplying any of these additions as an assigned writer rejects the whole request. Existing calls using only the original four sections retain their previous serialization and section order.

`state` accepts `进行中`, `维护中`, `构想中`, `暂停`, `已结束`, or `历史`. `title` is non-blank, at most 80 characters on one line; the first body title is updated too. `goal` is non-blank, at most 200 characters on one line. Sections can be omitted when changing metadata alone. When a project stops or is put aside, set its state to `暂停` or `已结束` and explain why in `现状`. Progress still goes through `update_project_status`.

A missing section is inserted in the order above. A heading the program does not know is left where it was.

Inside a section you send to `update_project_summary`, a line that starts with `## ` is refused. `###` and lower are allowed. When `现状` is updated, a `截至 YYYY-MM-DD` already in that heading is rewritten to today. Other words in the heading stay.

The body created with an empty template is:

```markdown
## 目标

## 现状

## 下一步

## 分工

## 入口
```

`边界与注意` is not in that template. Full-access writers can add it through `update_project_summary` when needed.

The summary is considered stale in the reader after 14 days. That does not change the file.

### Progress log

Path: `projects/<id>/status.md`. Plain Markdown. No front matter.

If the file is missing, the first status write creates it:

```markdown
# <title> · 进展

只往后追加，不改旧条目。
```

Each later write appends one entry. Old entries are not rewritten.

```markdown
## 2026-10-13 15:04 · clerk

Shipped the preview. Notes are in the shop repo. Next is the photo pass.
```

The clock is fixed at UTC+8. It does not follow daylight saving. The heading is `## YYYY-MM-DD HH:MM · <who>`. `<who>` is the member key, or `member-key · signed-as` when the call set `signed_as`.

The text is at most 4,000 characters. A line in the text that starts with `## ` is saved as `### `, so the log cannot be split into a fake entry.

When a status write leaves more than 40 entries in `status.md`, the same commit moves the older ones to `projects/<id>/status/<YYYY-MM>.md` by the month in their heading and keeps the latest 20 in `status.md`. An entry with no date goes with the entry before it. A role request that is still pending stays in `status.md`. Archive files follow the project's access rules. Normal reads do not load them; search does, and `get_project` lists which months exist. Every append stores the whole file in git again, so a short `status.md` keeps the repository small.

A status write still succeeds when it looks like process noise, but the reply adds a reminder in two cases: the previous entry in the same project has the same `<who>` and is less than 2 hours old, or the text is over 800 characters. The owner gets no reminder.

The same entry shape is used for a role handover. A key role (`key: true`) is not changed on the first call. That call only appends a request. A later approval updates `who` and `since` on the role and appends a handover entry. The pending list parses those entries. The sentences are fixed Chinese in this release. They insert the owner's display name: the title on the member whose `agent_key` is `owner_key`, or the key itself when that title is missing. This note does not quote them.

### Links

Path: `projects/<id>/links.md`. Optional. Plain Markdown. No front matter. No tool in this tree writes it.

### Decision

Optional `source`, `expires`, `happened`, `pending`, and `supersedes` use the shared [decision and lesson lifecycle](#decision-and-lesson-lifecycle) rules below.

Path: `decisions/<id>.md`.

The id from `record_decision` is `YYYY-MM-DD-<member-key>-HHMMSS`. A collision adds `-2`, `-3`, and so on.

| Field | Required | Values |
| --- | --- | --- |
| `title` | on write | Single line, at most 80 characters. |
| `date` | on write | `YYYY-MM-DD`. |
| `decided_by` | on write | Free text, at most 40 characters. Not a member key. |
| `project` | no | A project id. Assigned members must set one, and it must be a project they are granted. |
| `recorded_by` | on write | The author string, same shape as a git author name. |
| `access_note` | no | At most 200 characters. A reminder, not a grant. |
| `visible_to` | when restricted | See below. |
| `updated` | on write | The same date as `date` on create. |

The body is:

```markdown
# <title>

## 决定

<what was decided>

## 理由

<why>
```

Level-2 headings inside the decision or the reason are saved as level-3.

`sensitivity` is `public`, `internal`, or `restricted`. Default `internal`. An assigned member cannot write `public`, and cannot pass `visible_to`. For them, a restricted decision copies `visible_to` from the project when the project is restricted, and otherwise lists only that member's human key.

A full-access writer who marks a decision `restricted` may pass `visible_to`. Unknown member keys are refused. The reserved owner id is refused in that list. The local owner can read restricted files without being listed.

If the linked project is `restricted`, the decision must be `restricted` too, and its `visible_to` must be a subset of the project's list.

### Lesson

Path: `lessons/<id>.md`. The id follows decisions: `YYYY-MM-DD-<member-key>-HHMMSS`, using Taipei time. Collisions append `-2`, `-3`, and so on.

| Field | Write behavior |
| --- | --- |
| `title` | Single line, at most 80 characters. If omitted or blank, the first 40 characters of the conclusion's first line. |
| `date` | Writer sets today's Taipei date, `YYYY-MM-DD`. |
| `project` | Optional project id. Missing means company-wide. Assigned writers must name an assigned, visible project. |
| `source` | Optional `owner` (the owner's own words), `approved` (an agent's wording that the owner explicitly agreed to at the time), or `inferred` (an agent's inference, including a summary of several corrections); input also accepts `主人原话`, `主人认可`, or `伙伴推断` and stores the English value. Other values are refused. |
| `expires` | Optional valid date `YYYY-MM-DD`, for temporary arrangements. |
| `happened` | Optional valid date `YYYY-MM-DD`: when the lesson actually happened or was decided, for entries recorded later. Ordering and the date shown in briefs use it; without it, `date` is used. |
| `pending` | Optional boolean. `true` means awaiting confirmation and not effective. Missing or false means confirmed. |
| `supersedes` | Optional list of old lesson ids replaced by this entry. |
| `superseded_by` | System-written id of the replacement on an old entry. |
| `retired`, `retired_by`, `retire_note` | System-written retirement date, author string, and optional note (at most 200 characters). |
| `ref` | Optional project-relative file or folder path, or a link, at most 300 characters. Several pointers can be separated by `；`, `;`, or a line break; the digest reports the entry when any one of them is missing. Read projections redact local paths. |
| `origin_id` | Optional source key for safe replays, at most 120 characters. `import_lessons` skips a key already on a lesson; the JSON API `record_decision` skips a key already on a decision. |
| `recorded_by` | Writer's git author string, including signature when present. |
| `sensitivity`, `visible_to`, `access_note` | Same rules as decisions, including the restricted project's reader subset. |
| `updated`, `updated_by` | Writer sets today's date and author string. |

Only `lesson` is required by `record_lesson`. A conclusion, `how`, or `why` accepts at most 4,000 characters; optional blank sections are omitted. Caller-provided level-2 headings become level-3, as with decisions. Stored section headings remain fixed:

```markdown
# <title>

## 结论

<lesson>

## 怎么用

<how to apply it next time, if provided>

## 为什么

<why, if provided>
```

Missing `source`, a conclusion over 500 characters, and a project brief over its configured budget produce success hints, including for owner writes. They do not block writing. There is no approval step or content-quality gate.

### Lesson digest and owner review

`lesson_digest(batch=1, notify=False)` computes the calling viewer's visible lessons **and decisions** on demand. It installs no schedule, delivery channel, or model call. The human is `viewer.workspace_owner`: the caller themselves when human, or the agent's registered `owner`. The period starts at that human's `lessons_reviewed_at`, or seven days before the current Taipei time when absent. New records fall in `(since, now]`: their `date` plus the generated id's `HHMMSS` suffix determines creation time; manual ids without a valid time suffix use midnight on `date`. Missing dates cannot count as newly recorded. `updated` never makes an old record new again.

The digest has three blocks:

- **A, new records:** counts by project, company-wide first, with lessons and decisions counted separately. Inactive new records still count as newly recorded.
- **B, owner judgments:** all pending records, oldest first with days waiting; then new active records whose `source` is neither `owner` nor `approved` (missing source explicitly says "Source missing"); then groups of similar active lessons from different projects, suggesting one company-wide lesson. Each group counts as one judgment. Each batch contains at most 10 judgments, numbered from 1 within the batch, with total and remaining batch counts. Each record shows its id, kind, project, title, first 120 conclusion characters, and source. Retired and superseded records never reappear as pending or inferred judgments.
- **C, agent maintenance:** expired records (retire or revise expiry); missing local file references (fix the pointer); groups of similar active lessons within one project or company-wide (consider supersession); scopes whose rationale-bearing entries together exceed the scope's brief budget (the project's own `lessons_brief_chars`, else the global one) (clear less important `why` with `update_record`); and scopes whose brief exceeds that limit. Each entry or grouped issue counts as one maintenance task, so one record can contribute to multiple tasks. Rationale and overflow use the same budget rule as the brief (`records.brief_size` / `_brief_split`). The human sees only maintenance counts and outcomes; the presenting agent receives the work list.

Local file checks split `ref` into its pointers (`；`, `;`, or a line break) and check each one; a file or a folder counts as present. Absolute paths are checked directly. Relative references are checked against **every** directory in the associated project's `paths` and are broken only if none has the file or folder. A relative reference without project directories is unresolved and is skipped rather than interpreted against the process working directory. URL schemes are skipped. Remote callers never check local files. Retired and superseded records do not need pointer maintenance.

Similarity is deterministic. Concatenate title and conclusion, strip whitespace and Unicode punctuation, form the set of overlapping character bigrams, then compute Jaccard intersection / union. An empty union scores zero. The module constant `digest.SIMILARITY_THRESHOLD` is **0.5**, inclusive. Groups are connected components of qualifying pairs: cross-project edges between named projects for B, same-project edges (including company-wide) for C. No model judges conflicts or suitability; the presenting agent does so during the conversation.

`notify=true` writes `lessons_notified_at` on the associated **human** member file in one commit. It means an agent is already presenting the digest. `review_lessons(confirm=None, retire=None, done=False, signed_as=None)` accepts pending ids to confirm and objects `{"id":"...","note":"optional"}` to retire. Id resolution and modifications use the same preparation path as `update_record`; directory-qualified ids disambiguate lessons and decisions. Duplicate references to the same record in one request are rejected. All ids, visibility, and write permissions are checked before any file is written. One bad id rejects the entire request, including `done`. Successful changes, including timestamps, share one commit. `confirm=[]`, `retire=[]`, and `done=false` alone are an error.

`done=true` stamps `lessons_reviewed_at` and clears `lessons_notified_at` after all batches have been discussed. Humans may record their own personal timestamps; an agent needs write permission and may stamp only its registered human. Editing records additionally requires the ordinary record write permissions. An unchanged operation returns no commit.

`company()` reminds a registered human or their agent when review is at least seven days old (or absent) **and** B or C has visible content. A notification later than the last review, and less than seven days old, suppresses the reminder. Scope is recomputed using the calling viewer, including assigned and remote readers. The reminder directs the agent to present B in conversation one batch at a time using `lesson_digest(notify=true)`; C is handled by the agent. Company-wide promotions are new lessons without `project_id`, with `supersedes` naming the project lessons.

CLI equivalents: `onestead digest [--batch N] [--notify]`; `onestead review --confirm <id> [--confirm <id> ...] --retire '[{"id":"...","note":"..."}]' [--done]`. Supply at least one review action. The owner's web Lessons page uses this same review writer, with the owner as git author, exposes B in batches and only C counts, and groups current lessons with company-wide first. Its inactive switch includes retired, superseded, and expired lessons.

### Decision and lesson lifecycle

`record_decision` additionally accepts optional `source`, `expires`, `happened`, `pending`, and `supersedes`, with exactly the lesson meanings above. A decision keeps its existing `决定` and `理由` body sections. Without new arguments, its stored bytes are unchanged from the previous format; existing files need no migration. The system may later add `superseded_by`, `retired`, `retired_by`, `retire_note`, and `updated_by` when replacing or editing it.

Status is derived on read, never stored as a separate field. The first matching rule wins:

1. A present `retired` date: `retired` (withdrawn).
2. A present `superseded_by`: `superseded`, pointing to the replacement when visible.
3. `pending: true`: `pending` (not effective).
4. `expires` earlier than today's Taipei date: `expired`. An entry expiring today remains current.
5. Otherwise: `active` (current).

`list_lessons` and **`list_decisions` now default to current entries only**. Pending titles appear separately at the end; their content is not presented as effective. `include_inactive=true` also includes retired, superseded, and expired entries with their status. Entries are newest first within each group; lessons group company-wide entries before projects. Search, document opening, and history still allow access to inactive records.

`supersedes` replaces records of the same kind only. The writer must be able to see and write every old entry. Cross-project replacement is allowed, including promoting several project lessons to one company-wide lesson for a full-access writer. Each old entry receives `superseded_by`, `updated`, and `updated_by` in the same commit that creates the new entry. A missing, unwritable, already retired, or already superseded old entry rejects the entire write. For a superseded entry the error identifies the replacement if the caller can see it. Imports validate and prepare the whole batch before writing; one invalid item rejects the whole batch.

`update_record(record_id, changes, signed_as=None)` edits a lesson or decision. An id can be bare or prefixed with `lessons/` or `decisions/` (an optional `.md` suffix is accepted). If both kinds have the bare id, specify the directory. The same visibility, writer, project-grant, and restricted-reader rules apply to edits. Any authorized writer may edit another author's entry.

Allowed changes for both kinds: `title`, `source`, `expires` (null clears), `pending` (false confirms), and `retired` (true retires, false clears the three retirement fields). An optional `note` accompanies `retired=true`. Lessons also allow `lesson`, `how`, `why`, `ref`, and `project` (null makes it company-wide). Decisions also allow `decision` and `reason`. Unknown fields, including `id`, `date`, `recorded_by`, `superseded_by`, and `supersedes`, reject the entire change. An edit that makes no semantic change creates no commit.

`company()` includes the current project's lesson brief when started in its directory, and shows company-wide lessons to every member right after the company handbook, before the project table. `get_project()` includes current lessons after the project body and before progress. Each brief entry is one line: title (omitted when it is just the start of the conclusion), conclusion, how, why, source, date, and optional details reference. Entries that have a `why` are the important or counterintuitive ones, so they come first and are always shown in full, even past the budget. The configurable `lessons_brief_chars` (default 3,000, environment `ONESTEAD_LESSONS_BRIEF_CHARS`) then limits the other complete entries, newest first. The budget counts each entry's conclusion, how, and why only; title, source, date, and reference do not count, the same rule as `list_lessons`; the project brief and the company-wide brief each have their own budget. Once the next complete entry would exceed the budget, the remaining entries are listed by title, one per line, so titles should read as complete rules; titles get their own budget of the same size, and anything beyond it is reported as a count with a pointer to `list_lessons`. A project may set `lessons_brief_chars` (a non-negative integer) in its own front matter to override the global budget; a missing or invalid value falls back to the global one. Overflow asks the project lead (or, for company-wide lessons, whoever maintains them) to consolidate, replace, or retire entries. Pending entries contribute a count only.

### Target history

`history(target, limit=10)` accepts a visible record id or repository-relative Markdown path. A project expands to `project.md`, currently present `status.md` and `links.md`, and `status/*.md` archives. An id matching several kinds includes all visible matches, with each file labeled by path. Missing and invisible targets return the same error; personal to-dos remain private to their human and that human's agents.

The latest 1–50 relevant commits are shown newest first, with Taipei time (`YYYY-MM-DD HH:MM`), member and signature, tool parsed from the commit message, readable subject, visible files, and added/deleted line counts. Only the latest commit includes a diff excerpt, at most 1,500 characters. Remote and assigned readers receive the usual redacted text. The web activity view and agent history share git reading and subject translation.

### Person

Path: `people/<id>.md`. The reader indexes members by `agent_key`, not by `id`. The web app sets them to the same string.

| Field | Required | Values |
| --- | --- | --- |
| `agent_key` | yes | Lowercase letters, digits, and hyphens. It must match `^[a-z0-9][a-z0-9-]{0,40}$` and must not end in `-draft`. The web app's id rule is tighter: 2 to 41 characters. |
| `kind` | yes | `human` or `agent`. |
| `owner` | agent: yes. human: forbidden | The human member's `agent_key`. The reader rejects a human who has `owner`. An agent's owner must be a human who does not themselves have `owner`. Chains, loops, and a missing human mark the agent file as a problem. |
| `access_mode` | no | `full` or `assigned`. Missing means `assigned`. |
| `remote_enabled` | no | Boolean. Missing means false. The string `"false"` does not count as false. |
| `can_write` | no | Boolean. Missing means the member cannot write. The string `"true"` does not count as true. |
| `projects` | no | List of project ids. Used when access is `assigned`. |
| `assets` | no | List of asset ids. Used when access is `assigned`. |
| `skills` | no | List of skill ids. Used when access is `assigned`, on the shared-package path. |
| `role` | no | Free text, a label such as a job title. At most 120 characters on the web form. Not a project role. |
| `connect` | no | Free text, at most 300 characters. Not used to authenticate. |
| `signers` | no | A list of names. See [Commits](#commits). The reader does not check the shape of this field. |
| `lessons_reviewed_at` | human only, tool-managed | Quoted `"YYYY-MM-DD HH:MM"` string in Asia/Taipei (UTC+8); set by `review_lessons(done=true)`. |
| `lessons_notified_at` | human only, tool-managed | Quoted `"YYYY-MM-DD HH:MM"` string in Asia/Taipei (UTC+8); set by `lesson_digest(notify=true)` and cleared when review is done. |
| `archived` | no | Boolean. Missing or false means the member is active. `true` means disabled. The file stays. |
| `feishu_open_id` | no | A non-empty string. Two active members with the same value are both marked as problems. Feishu is optional: without configuration, binding fields are hidden and cannot be edited on the web; member saves and disabling preserve existing bindings. |
| `updated` | no | Date the web app sets on save. |

The reserved owner id cannot be used from MCP or the CLI. Set `owner_key` (or `ONESTEAD_OWNER`) to that member's `agent_key`. There is no compiled default. A missing value, or a value that is not a legal member key, is an error.

The two lesson timestamps belong to the human's `people/<key>.md`, never the agent's file. They are not editable fields in member management; web saves preserve both values and their quoting. Only the associated human or a writable agent owned by that human may change them through the digest/review tools.

A duplicate `agent_key` marks the colliding files as problems, so the key cannot be used. If one of the copies is archived, the archived file keeps the key and the active copies are the ones marked.

Disabling a member sets `archived: true`, sets `remote_enabled: false`, and removes `feishu_open_id` only when Feishu is configured. Restoring clears `archived` and leaves `remote_enabled` false. Old tokens stay revoked.

### Todo

Path: `todos/<id>.md`. A new id is `YYYY-MM-DD-HHMMSS-<n>` from the UTC+8 clock. `<n>` starts at 1.

| Field | Required | Values |
| --- | --- | --- |
| `owner` | yes for a valid file | A human member key. A missing `owner` is treated as the configured `owner_key`. Do not rely on that. |
| `title` | on write | Single line, at most 80 characters. |
| `status` | no | `open`, `done`, or `dropped`. Missing is treated as `open` in the list. Any other value fails the reader check. |
| `due` | no | `YYYY-MM-DD`. Required when `repeat` is set. |
| `time` | no | `HH:MM`, 24-hour. An unquoted time in YAML can load as an integer number of minutes. An integer from 0 through 1439 is shown as `HH:MM`. |
| `repeat` | no | `daily`, `weekdays`, `weekly`, `monthly`, `yearly`. Empty means the todo does not repeat. |
| `repeat_day` | no | Day of month. Set for `monthly` and `yearly`. A short month uses the last day, and the next month goes back to `repeat_day`. |
| `project` | no | A project id the writer can see. |
| `created` | on write | Date. |
| `created_by` | on write | Author string. |
| `updated`, `updated_by` | no | Set when the todo changes. |
| `done_at` | no | Date. Set when status becomes `done`. Cleared when it leaves `done`. |
| `repeat_from` | no | The id of the todo that spawned this one. |
| `ask` | requests only | `approve` (owner approval), `act` (owner action), or `fyi` (inform the owner). The title is one sentence another agent can relay directly to the owner. |
| `context` | with `ask` | Non-blank text, at most 1,000 characters: what this is and how it arose. |
| `why` | with `ask` | Non-blank text, at most 400 characters: why the owner is needed personally. |
| `if_skipped` | with `ask` | Non-blank text, at most 400 characters: what happens if skipped. |
| `action` | with `ask` | Non-blank text, at most 400 characters: exactly what the owner should do, including for `fyi` (e.g. "No action needed; just be aware"). |
| `notified_by` | server only | Author string of the first notifier, in the same format as `created_by`. |
| `notified_at` | server only | Quoted `YYYY-MM-DD HH:MM` string in Asia/Taipei (UTC+8). |

The body is a title line plus optional notes. Notes are at most 2,000 characters. Level-2 headings in notes are saved as level-3.

Closing a repeating todo (`done` or `dropped` from `open`) adds the next open todo in the same commit. Doing that twice does not add a second copy. Up to 50 todos can be added in one call.

A request is an ordinary personal todo with `ask`, registered by `created_by`. All four explanatory fields are required with `ask`; they are refused on a todo without `ask`. Requests cannot have `repeat`. Updates can switch `ask` among the three categories but cannot clear it or empty any explanation. An ordinary todo can become a request when the resulting record has all four explanations and no recurrence. Unknown input fields are rejected.

Notification state is derived, never stored: `done` or `dropped` means `closed`; otherwise `notified_at` means `notified`; otherwise a request is `unnotified`. Ordinary todos have no notification state. Reopening preserves the notification record and restores its prior notification state. Updates alone accept the boolean command `notified`: `true` stamps the calling author's name and Taipei time, preserving the first stamp when repeated (no change); `false` clears both stamps to correct a mistake. Callers cannot write the stamp fields directly. Acknowledging a request does not close it; the registering agent marks it done when resolved.

`ask`, `notified_by`, and `notified_at` follow `project` in the YAML front matter. The four explanations precede `created_by`; the body still holds only notes. `list_todos(scope='asks')` lists open requests, unnotified first, then notified, oldest registration first in each group. Other scopes keep their date grouping and append category and notification state to request lines. The web todo page groups open requests first under "For your attention" and lets the owner acknowledge them with "Got it" using the same write endpoint and permissions.

Todos are personal. See [Who can read what](#who-can-read-what). On read, if the viewer cannot see the linked project, the `project` field is omitted from what that viewer is shown. The file on disk still has it.

### App

Path: `apps/<id>.md`. Id shape matches a project id: 2 to 41 characters, lowercase letters, digits, and hyphens.

| Field | Required | Values |
| --- | --- | --- |
| `title` | on register | Single line, at most 40 characters. |
| `one_line` | on register | Single line, at most 120 characters. |
| `kind` | on register | `网页`, `本机程序`, `bot`, `浏览器插件`, `命令行工具`, or `后台服务`. |
| `listed` | no | Boolean. Register writes `true`. `false` hides the app from remote readers and from assigned readers. A local full-access reader can ask for unlisted apps. |
| `icon` | no | At most 8 characters. |
| `project` | no | A project id, or empty. |
| `maintainer` | no | At most 120 characters. |
| `open` | no | A map. Keys: `url`, `path`, `command`, `shortcut`. Each value is at most 500 characters. `url` must start with `http://` or `https://`. Remote readers are shown `url` only when it is a public `http` or `https` URL. Path, command, and shortcut stay on the machine. |
| `code_path` | no | At most 300 characters. Hidden from remote readers. |
| `probe_process` | no | At most 80 characters. |
| `status_note` | no | At most 200 characters. |
| `links` | no | A list of maps with `label` (at most 60) and `target` (at most 500). Optional `note`, at most 200. Remote readers see a link only when `target` is a public `http` or `https` URL. |
| `registered_by` | on register | Author string. |
| `updated` | on write | Date. |
| `updated_by` | no | Author string, set on update. |

Register writes `sensitivity: internal`. The reader does not check `kind`. The body is a title plus optional notes, at most 3,000 characters.

### Skill

Path: `skills/<id>.md`. This file is a note about a skill. The installed copies are scanned on the machine that runs the server. They are not stored in the data repository.

The id is the skill directory name: `^[A-Za-z0-9][A-Za-z0-9._-]{1,60}$`. Upper case, dots, and underscores are allowed. That is wider than a project id.

| Field | Required | Values |
| --- | --- | --- |
| `one_line` | on register | At most 160 characters. |
| `title` | no | At most 40 characters. |
| `origin` | no | `自建`, `第三方`, or `软件自带`. |
| `maintainer` | no | At most 120 characters. |
| `upstream` | no | At most 300 characters. Remote readers see it only when it is a public `http` or `https` URL. |
| `upstream_version` | no | At most 60 characters. |
| `home` | no | At most 300 characters. The directory treated as the canonical copy. |
| `project` | no | A project id. |
| `registered_by` | on register | Author string. |
| `listed` | no | Boolean; omitted means `true`. Set only through `update_skill`, not registration. |
| `updated`, `updated_by` | on write | Date and author string. |

Register writes `sensitivity: internal`. The web app can set a skill to `public` or back to `internal`. That flag is what opens the shared package to assigned readers who do not have the skill in their `skills` list. The raw file is still hidden from assigned readers. See below.

Notes in the body are at most 3,000 characters.

`listed: false` keeps the note and installed files, but removes the skill from default lists, totals, alerts, shared lists, and downloads. Local full-access readers can request `include_unlisted` (`skills --all` in the CLI), and the owner can include unlisted skills in the web gallery. Those entries and local `get_skill` output are marked Unlisted. Assigned and remote readers never receive unlisted packages, even when asking to include them. `update_skill` with `listed: true` relists the skill. Existing notes do not need this field added.

The built-in scan list covers common Claude, Codex, PI, Grok CLI, Cursor and WorkBuddy directories. WorkBuddy's bundled skills live in its install directory, which varies by system; add that path as `builtin` under the `workbuddy` key if you want them counted. Those paths sit under `ONESTEAD_SKILL_HOME`, or under the process home when that variable is unset.

Extra local agents or additional paths for a built-in agent come from `skill_harnesses`. Each item has non-empty `key` and `name`, a `user` path list, and optional `builtin` and `plugins` path lists. Each path must be a non-empty string; `~` expands to the process user's home. Matching keys append paths to the built-in item without replacing its paths or name; new keys add local agents. They use the same scan, content comparison, authoritative-copy selection, sharing, and installation hints as built-in agents. User Skill directories contain individual skill directories, built-in directories are counted separately, and plugin cache roots provide plugin counts. `ONESTEAD_SKILL_HARNESSES` overrides the file with a JSON list; the legacy `COMPANY_BASE_SKILL_HARNESSES` is also accepted, with the new name taking precedence, including explicit empty values. `[]` disables configured additions without removing built-in agents. Invalid JSON or a bad shape is an error.

Extra snapshots come from `skill_snapshots` in the config file. `ONESTEAD_SKILL_SNAPSHOTS` is the same list as JSON, and it wins over the file. Each item has non-empty `key`, `name`, and `root`; without a list, no extra snapshots are scanned. A bad shape is an error. A plain snapshot scans skill directories directly under `root`. An optional `layout = {type = "latest-subdir", pattern = "skills-*", subpaths = ["exports/user", "exports/vendor"]}` selects the last matching directory in name order and scans its relative subpaths. The defaults are `pattern = "*"` and `subpaths = ["."]`. Patterns may only match one directory level; subpaths must stay under the selected snapshot. No identity has a special scan branch. Snapshot roots are read-only, never local installation targets. Legacy environment conversion is described in [Compatibility](../README.md#已有安装仍然兼容).

### Home

Path: `home/<owner>.md` for the live page, `home/<owner>-draft.md` for a draft.

| Field | Required | Values |
| --- | --- | --- |
| `id` | yes | The human key for the live page. The human key plus `-draft` for a draft. |
| `owner` | yes | That same human key. A legacy file named `home/<owner_key>.md` or `home/<owner_key>-draft.md` may omit `owner` and `state`. Do not use that form. |
| `state` | yes | `active` on the live page, `draft` on the draft. The path and the state must match. |
| `modules` | yes for a layout the page will use | A non-empty list. See below. A broken list falls back to the default layout. |
| `proposed_by` | on a draft | Author string. |
| `note` | on a draft | Single line, at most 200 characters. |
| `updated` | on write | Date. |

`propose_home` writes the draft. It does not change the live page. The person adopts or discards it on the web. Adopt writes the live file and deletes the draft in one commit. Discard deletes the draft in one commit. If the draft changed after the preview, adopt and discard are refused.

Module `kind` is one of:

| `kind` | What it is | Options |
| --- | --- | --- |
| `today_todos` | Today's todos and overdue todos | none |
| `apps` | Apps from the catalog | `ids`: list of app ids. Empty means the first 8 apps whose kind is `网页` or `本机程序`. |
| `skills` | Broken skills only | none |
| `shared_skills` | Skills shared to this person | none |
| `attention` | Items waiting on this person | none |
| `projects` | Project health | `states`: list of state words. Empty means all states. |
| `activity_chart` | How often members wrote | `days`: integer 7 through 60, default 14. |
| `recent` | Recent commits | `limit`: integer 3 through 20, default 6. |

Unknown kinds and unknown options are refused. A kind may appear once.

A local session whose human is the reserved owner gets every kind. That is the owner, and the owner's agents, on this computer. When the home file is missing or its module list cannot be read, the order is `today_todos`, `apps`, `attention`, `projects`, `skills`, `activity_chart`.

A remote session cannot choose `attention` or `skills`. A remote agent of the reserved owner still starts from that list, with those two removed. Any other human starts from `today_todos`, `projects`, `apps`, `shared_skills`, `recent`, and also cannot choose `attention` or `skills`. A saved home file replaces the default, and modules the session cannot choose are skipped.

That split follows the configured `owner_key`.

## Who can read what

Access is calculated on each read. Two different switches are involved. `access_mode` decides which documents a member can read. `remote_enabled` decides whether a request from outside this computer is accepted at all. `can_write` is a third switch, for writes.

A viewer's human key is their own key when they are human, and their `owner` when they are an agent. Todos, home pages, and `visible_to` use that human key.

### How a member's grants are built

- Missing `access_mode` means `assigned`.
- A `full` member's `projects`, `assets`, and `skills` lists are not consulted. Full means the company files, with the personal exception below.
- An `assigned` member sees only the grants below.
- An agent whose human is `assigned`, and whose human is not the reserved owner, is forced to `assigned`. Each list on the agent is intersected with the human's list. A list the agent omits is copied from the human.
- An agent whose human is the reserved owner keeps the `access_mode` and the lists written on the agent file.

### Read rules, in order

1. An invalid viewer sees nothing. An archived member is invalid. A broken agent owner is invalid. A duplicate `agent_key` does not resolve to a member, so that caller is invalid too.
2. A todo or a home page is visible only when its personal owner is the viewer's human key. Full access does not open another person's todos or home. A public flag does not open them either. The human and that human's agents share them. A todo or home file that already has a problem resolves no owner, so nobody sees it.
3. An archived person file is visible only to the local owner, and only from the local app.
4. A known full-access viewer sees the document. This includes the local owner and any member with `access_mode: full`.
5. A problem file is hidden from everyone else.
6. A decision, lesson, or app that names a `project` is hidden when that project is missing or not visible to this viewer. A project lesson also requires the project in an assigned member's grants, even if public.
7. An anonymous viewer sees a document only when it is `public`.
8. A skill file is hidden from assigned viewers on this path. Sharing uses the package channel below.
9. A person file is visible to an assigned viewer when its `agent_key` is the viewer or the viewer's human. Sensitivity is not applied on this step.
10. A `public` document is visible.
11. Otherwise the viewer needs a grant:
    - `handbook`, and `lesson` without a `project`: any known member. Anonymous readers see only public company-wide lessons.
    - `project`, `project-status`, `project-links`: the project id is in `projects`.
    - `decision`, project-linked `lesson`, and `app`: the `project` id is in `projects`. A decision or app without a project has no grant on this step.
    - `asset`: the asset id is in `assets`.
    - Anything else: no grant.
12. A `restricted` document also needs the viewer's human key in `visible_to`. An empty list grants nobody on this step. The local owner already passed at step 4, so the owner is not listed.

The short member directory is a separate projection. It has `key`, `title`, `kind`, and `role` only. Archived members are left out. An assigned viewer also loses a `restricted` card unless it is their own key, their human's key, or their human key is in that file's `visible_to`.

### Skills that are shared

Assigned readers, remote readers, and the local owner can be given a shared package. The package is built from the copy on this machine. The raw `skills/<id>.md` file is not that package.

An assigned reader gets a skill that is `public`, plus skills listed in their `skills` grant. A remote full-access reader, and the local owner, get every scanned skill that is safe to pack. A local full-access agent reads skills through the on-machine scan, not through this package list. A skill that is broken, or that fails the id check, is not packed.

Remote text has local paths removed, and values that look like secrets are replaced. The file in git is not rewritten. Lesson and target-history projections also redact text for assigned readers, including local ones.

### Who can write

- The local owner can write.
- Another member needs `can_write: true` on their own file.
- An agent also needs `can_write: true` on the human they belong to.
- An assigned member can append status, update the four summary sections, record decisions and lessons, and edit those records only on projects in their `projects` grant. Being able to read a public project is not enough to write it.
- Creating a project needs full access, `can_write`, and a local session. A remote caller cannot create a project.
- The reserved owner id cannot be the MCP or CLI caller.

`remote_enabled` is checked on every remote request. Missing means false. An agent can connect remotely when their own flag is true and their human's flag is true. Agents of the reserved owner follow their own flag only. Turning the flag off is covered in [Deploy](deploy.md).

## What a write refuses

The checks below run on text the tools write. They do not run as a repository linter.

- A required string that is empty.
- Text longer than that field's limit. The usual limit is 4,000 characters. Longer bodies are noted on the type above. The error asks you to store a path, not the whole source.
- A control character other than line feed and tab. CR is normalized to LF first. A single-line field also refuses line feed and the Unicode line separators U+2028 and U+2029.
- A value that looks like a secret. The patterns are: an `sk-` token of 20 or more characters; GitHub token shapes `ghp_`, `gho_`, `ghu_`, `ghs_`, `ghr_`, and `github_pat_`; a Slack-style `xox` token; an AWS access-key id shape starting with `AKIA`; a PEM line `-----BEGIN ... PRIVATE KEY-----`; a JWT-shaped string that starts with `eyJ`; and a line that assigns `password`, `passwd`, `pwd`, `api_key`, `api-key`, `secret`, `token`, or `cookie` with `:` or `=` to a value of 8 or more non-space characters.

Write where the credential lives and what the item is called. Leave the value out.

`signed_as` is a single line of at most 30 characters. It must not contain the middle dot `·`.

## Commits

Each successful write is one git commit. The app locks `.git/company-base-write.lock` (the existing internal lock name is retained), writes the files, and commits those paths. The lock is stale after 120 seconds. A waiter gives up after 30 seconds.

The git author and committer are set for that commit only. The name is the member key, or `member-key · signed-as` when `signed_as` is set. The email is `<member-key>@agents.company-base.local`. The repository's `user.name` is not used.

The message body is:

```text
<subject>

由 <name> 通过Onestead写入（<tool>）。

Agent: <member-key>
Signed-as: <signed-as>
```

`Signed-as` is present only when the call set it. `<tool>` is the tool name, such as `update_project_status`. A save from the web app uses the tool name `网页界面` and the reserved owner id as the member key, with no `Signed-as` line. Those sentences are Chinese in this release.

If a member file has `signers`, a write as that member is refused unless `signed_as` is one of those names.

A save that does not change the file does not create a commit. If the commit fails, the app puts the files back.

There is no delete tool for company facts. Disabling a member sets `archived: true`. Adopting or discarding a home draft is the exception: that commit deletes `home/<owner>-draft.md`.

## Dataset identity

Bootstrap protocol 1 stores `.onestead/dataset.json` in the data repository:

```json
{"protocol":1,"dataset_id":"9f10092291974a2b90d40ea21dc753d3","owner_key":"owner-a92bba2f10057cfa"}
```

`dataset_id` is a randomly generated UUID4 encoded as 32 lowercase hexadecimal
characters (122 random bits). It identifies a company dataset independently of
its owner, executable version and filesystem location. It is committed with
the initial repository and is retained by ordinary writes, backups, clones and
restores. Two datasets owned by the same person have distinct identifiers.
`owner_key` lets a host recover the local web owner after relocating/restoring
the dataset without deriving it from an OS username or a personal path.

The file belongs to the initialization protocol, not an editable document type.
It is outside the document scanner, so `onestead check` does not flag it as an
unknown/problem document. Ordinary writes and web reverts cannot change it.
`inspect` validates its syntax and equality with its committed version. A legacy
dataset without this file is `needs_bootstrap`; bootstrap creates it and any
missing requested member in one scoped Git commit. Invalid or uncommitted
identity metadata requires repair and is never silently replaced. Commands,
states and machine error codes are in [Private local runtime](bundle.md).
