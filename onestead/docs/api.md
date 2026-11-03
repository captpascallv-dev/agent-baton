# Local JSON API

`onestead api` is the interface for local programs. Each process reads one request from standard input, performs one operation, writes one response to standard output, and exits. It does not need the web app or a running MCP server.

The implementation uses the same `CompanyBase` permissions, write lock, validation, and git commits as MCP and the human-readable CLI. Those interfaces keep their Markdown responses. This API does not install, move, or delete skill packages.

## Invocation

```sh
onestead api --as codex --data /srv/example-company < request.json
```

`python -m onestead.cli api` is equivalent. `--as` and `--data` (alias `--root`) may appear before or after `api`.

Identity comes from `--as`, then `ONESTEAD_AGENT`. Use a registered, active member key. The reserved owner key cannot be claimed through this interface. These are the local MCP identity rules; the API does not authenticate with a web owner token. Assigned members keep their assigned scope.

The data repository comes from `--data`, then `ONESTEAD_DATA`, then `data_dir` in the configuration file. There is no default data directory. `ONESTEAD_CONFIG` selects a configuration file; otherwise Onestead uses `~/.onestead/config.toml`, or an existing `~/.company-base/config.toml` when the new file is absent. The configuration also supplies `owner_key` (override: `ONESTEAD_OWNER`) and company language, `en` or `zh` (override: `ONESTEAD_LANG`). The legacy `COMPANY_BASE_*` environment names remain supported. When both names are set, `ONESTEAD_*` takes precedence, including explicit empty values.

Each invocation calls `usage.record(agent, "api:<method>", "api")` once, including failures. A request without a readable method uses `api:unknown`. As with other CLI calls, missing identities are not written to the usage log.

## Wire format

Input is the complete byte stream through EOF, decoded as UTF-8. An initial UTF-8 BOM is accepted. Send a JSON object:

```json
{"method":"list_projects","params":{}}
```

`method` is a required string. `params` is an object, optional when the method has no required parameters; omitting it means `{}`. `null` is not an empty parameter object. Unknown methods or keys, missing required keys, and incorrect types produce `invalid`. Unknown keys in `changes` are also rejected. JSON booleans must be `true` or `false`, not numbers or strings.

Output is exactly one JSON object followed by one newline, encoded directly as UTF-8 bytes with Unicode preserved (`ensure_ascii=False`). Console code pages do not affect it. Diagnostics go to standard error. Programs should parse standard output as JSON and use `error.code`, not message wording, to classify failures. Messages follow the company language.

| Outcome | Response | Exit code |
| --- | --- | --- |
| Success | `{"ok":true,"result":...}` | 0 |
| Failure | `{"ok":false,"error":{"code":"invalid","message":"..."}}` | 1 |

Identity failures, a missing data directory, and malformed JSON use this same response envelope. Dates in results are `YYYY-MM-DD` strings, or `null` when absent or unreadable. A `commit` is the short git commit id returned by the write operation, or `null` when that operation detected unchanged content and made no commit. It is never parsed from Markdown or read later from an unrelated write.

## Errors

There are exactly five codes:

| Code | Meaning | Automatic retry |
| --- | --- | --- |
| `not_found` | The project, skill, or todo does not exist, or the identity cannot see it. These cases intentionally share one message. | No |
| `forbidden` | Missing, unknown, invalid, or archived identity; a reserved owner identity; insufficient write permission; assigned or remote access to a local full-access read; a signature outside a shared identity's `signers`; or authentication rejected before writing. | No |
| `invalid` | Invalid JSON, envelope, parameters, content, signature format, suspected credentials in a write, duplicate id, or another expected validation failure. | No |
| `conflict` | A write-lock timeout or a `store.Conflict` concurrency failure. | Yes |
| `internal` | A storage/git failure, missing data repository, or unexpected exception. The message is generic; diagnostics may be on standard error. | No |

Only `conflict` is eligible for automatic retry. There is no request id, idempotency key, or exactly-once guarantee. If a process is interrupted or its response is lost, a caller must resolve that uncertainty before resending an append.

## Methods (11)

All lists are filtered by the identity's `viewer.visible` scope. Project and member lists do not grant access to further content. The owner name uses the visible owner record, with the configured owner key as its fallback. `list_skills` additionally requires a local, valid, full-access identity.

Every write requires a non-blank string `signed_as`, even for identities that normally allow unsigned MCP writes. Existing signature validation applies: at most 30 characters, one line, no control characters or suspected credentials, and no `·`. A shared identity's `signers` also applies. Commits record the calling identity as `Agent: <key>` and the normalized signature as `Signed-as: <signature>`; the git author is `<key> · <signature>`.

### `owner`

No parameters. Returns:

| Field | Type | Meaning |
| --- | --- | --- |
| `key` | string | Configured owner identity key. |
| `name` | string | Owner display title, or owner key when no visible valid record supplies a title. |

### `list_projects`

No parameters. Returns an array with these fields per visible project:

| Field | Type | Meaning |
| --- | --- | --- |
| `id`, `title` | string | Project id and display title. |
| `state`, `goal` | string or null | Stored state and one-line goal. |
| `roles` | array of objects | Each contains `role`, `who` (strings), `since` (date or null), `key` (boolean, default false). `key: true` marks a key role. |
| `paths` | array of strings | Absolute directories, or `[]` when absent or invalid. |
| `summary_as_of` | date or null | Date of the project summary. |
| `sensitivity` | string | `public`, `internal`, or `restricted`. |
| `problem` | string or null | The reader's metadata problem, if any. |

The state vocabulary in `manage.STATES` is `进行中`, `维护中`, `构想中`, `暂停`, `已结束`, `历史`. The reader preserves stored values rather than translating or enforcing this vocabulary. Creation accepts the smaller set below.

### `list_members`

No parameters. Returns an array of visible member records:

| Field | Type | Meaning |
| --- | --- | --- |
| `agent_key`, `title` | string | Member key and display title. |
| `kind` | string | `human` or `agent`. |
| `role` | string or null | Stored role. |
| `owner` | string or null | The owner key stored on an agent's member record. |
| `archived`, `can_write` | boolean | Stored flags; missing flags mean false. |

Visibility rules already hide archived members from this local agent interface. `can_write` is the member record's flag; inherited permission checks still apply on a write.

### `create_project`

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `id` | string | yes | `^[a-z0-9][a-z0-9-]{1,40}$`: 2–41 characters. Must not exist. |
| `title` | string | yes | Non-blank, one line, at most 80 characters. |
| `goal` | string | yes | Non-blank, at most 200 characters; stored on one line. |
| `state` | string | no | `进行中`, `维护中`, `构想中`, or `暂停` (`base.NEW_PROJECT_STATES`). Default `构想中`. |
| `paths` | array of strings | no | Absolute directories. Default `[]`; POSIX, drive-letter, and UNC paths are supported. |
| `signed_as` | string | yes | Write signature. |

Requires full access and write permission. Writes `projects/<id>/project.md` using the existing default body template and `sensitivity: internal`. Returns `{"id":"example","commit":"abc1234"}`. The id is trimmed before storage and in the result.

### `append_status`

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `project_id` | string | yes | Visible project id. |
| `text` | string | yes | Non-blank progress text, at most 4,000 characters (`textutil.MAX_TEXT`). |
| `signed_as` | string | yes | Write signature. |

Uses `update_project_status`, including assigned-project write checks. Appends a dated entry to the project's `status.md`; it never replaces earlier entries. Returns `{"commit":"abc1234"}`.

### `list_skills`

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `include_unlisted` | boolean | no | Default false. Include withdrawn notes in this local full-access list. |

Assigned and remote readers receive `forbidden`, including when `include_unlisted` is true. The list combines `skillscan.summarize` with visible `skills/<id>.md` notes. Returns an array:

| Field | Type | Meaning |
| --- | --- | --- |
| `id`, `title` | string | Directory id; title uses the note's title, then scanned skill name, then id. |
| `one_line` | string or null | Note's one-line description, falling back to the scanned description. |
| `origin`, `maintainer`, `home` | string or null | Values in the note. `home` is the canonical directory, not a copy of its files. |
| `updated` | date or null | Note's update date. |
| `installed_in` | array of strings | Harness keys from the scan's `installs`. |
| `listed` | boolean | Omitted metadata means true. |

Each call scans the installed skill directories of every configured harness; the scan cache lives only inside one process, so `onestead api` never reuses it. Expect about a second when the file system is warm and noticeably longer on a cold start with many skills (tens of seconds is possible). Callers should allow a generous timeout, for example 60 seconds, and cache the result themselves.

Unlisted skills remain readable through the local full-access `get_skill` MCP/CLI command, with an Unlisted label. They are excluded from default lists, totals, alerts, colleague shared lists, and downloads. This API does not expose `get_skill`.

### `register_skill`

Parameters are the **flat fields of one skill**, plus `signed_as`; there is no nested `skill` object or batch form.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `id` | string | yes | New skill directory id: `^[A-Za-z0-9][A-Za-z0-9._-]{1,60}$` (2–61 characters). |
| `one_line` | string | yes | Non-blank, at most 160 characters. |
| `title` | string or null | no | At most 40 characters. |
| `origin` | string | no | `自建`, `第三方`, or `软件自带`. |
| `maintainer` | string or null | no | At most 120 characters. |
| `upstream`, `home` | string or null | no | At most 300 characters each. |
| `upstream_version` | string or null | no | At most 60 characters. |
| `project` | string or null | no | Visible existing project id, or empty/null for no link. |
| `notes` | string or null | no | Body notes, at most 3,000 characters. |
| `signed_as` | string | yes | Write signature. |

Uses the existing skill registration path and writes an internal note. `listed` is not accepted at registration. A duplicate id is `invalid`. Returns `{"id":"example","commit":"abc1234"}`.

### `update_skill`

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `skill_id` | string | yes | Existing visible skill-note id. |
| `changes` | object | yes | Non-empty subset of the fields below. |
| `signed_as` | string | yes | Write signature. |

`changes` accepts `title`, `one_line`, `origin`, `maintainer`, `upstream`, `upstream_version`, `home`, `project`, and `notes`, with the registration limits. Optional text can be cleared with an empty string or null; `origin` must remain one of its stored values. It also accepts **`listed` as a boolean**: false withdraws the skill, true relists it. The note and installed files are preserved. An absent `listed` field means true, and existing notes are not rewritten to add it.

Returns `{"commit":"abc1234"}` or `{"commit":null}` if the resulting file is identical. Write metadata includes the current date and author, so repeating a value on a later day or under a different author can still produce a commit.

### `list_todos`

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `scope` | string | no | `open` (default), `today`, `week`, `all`, or `asks`. |
| `project_id` | string or null | no | Filter by linked project id. |

Returns an array of visible personal todos. Date scopes use the same group order as MCP: overdue, today, next 7 days, later, no date, then completed and cancelled for `all`. `today` includes overdue; `week` includes overdue and the next seven days. Within each group the order is due date, time, creation date, id. `asks` includes only open requests: unnotified first, then notified, oldest registration first within each group.

| Result fields | Type | Meaning |
| --- | --- | --- |
| `id`, `title`, `status` | string | Todo id, title, and `open` / `done` / `dropped`. |
| `due`, `created`, `done_at` | date or null | Dates as `YYYY-MM-DD`. |
| `time`, `repeat`, `project` | string or null | Time, recurrence, linked project id. |
| `notes` | string | Body notes, without the title. |
| `created_by` | string or null | Registering author string. |
| `ask` | string or null | `approve`, `act`, `fyi`; null for ordinary todos. |
| `context`, `why`, `if_skipped`, `action` | string or null | The four explanations; null when absent. |
| `notice` | string or null | `unnotified`, `notified`, `closed`; null for ordinary todos. |
| `notified_by`, `notified_at` | string or null | First notifier's author string and `YYYY-MM-DD HH:MM` in Asia/Taipei. |

### `add_todo`

Flat fields of one todo, plus required `signed_as`; returns `{"id":"...","commit":"abc1234"}`.

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `title` | string | yes | Non-blank, one line, at most 80 characters; for requests, a sentence another agent can relay. |
| `due`, `time` | string or null | no | `YYYY-MM-DD`, `HH:MM`; empty clears the value. |
| `repeat` | string or null | no | `daily`, `weekdays`, `weekly`, `monthly`, `yearly`; requires `due`. |
| `project` | string or null | no | Visible project id; empty means no link. |
| `notes` | string or null | no | Up to 2,000 characters; default empty. |
| `ask` | string | no | `approve`, `act`, or `fyi`; requests cannot repeat. |
| `context` | string | with `ask` | Non-blank, up to 1,000 characters; what this is and how it arose. |
| `why`, `if_skipped`, `action` | string | with `ask` | Non-blank, up to 400 characters each; why the owner is needed, consequences of skipping, and the exact action (even for `fyi`). |
| `signed_as` | string | yes | Write signature. |

Writes under the identity's human owner, using the same personal permission rules as `add_todos`. Explanatory fields without `ask` are invalid. Notification stamps cannot be supplied by callers.

### `update_todo`

| Parameter | Type | Required | Meaning |
| --- | --- | --- | --- |
| `todo_id` | string | yes | Visible personal todo id. Missing or invisible yields `not_found`. |
| `changes` | object | yes | Nonempty subset of `title`, `due`, `time`, `project`, `notes`, `status`, `repeat`, `ask`, `context`, `why`, `if_skipped`, `action`, `notified`. |
| `signed_as` | string | yes | Write signature. |

Returns `{"commit":"abc1234"}`, or `{"commit":null}` for no change. Field types and limits match `add_todo`; `status` is `open`, `done`, or `dropped`. A request's `ask` may switch category but cannot be cleared; all four explanations must remain non-blank. An ordinary todo may become a request if the resulting record is complete and has no recurrence.

`notified` accepts only a JSON boolean, only for requests: `true` records the calling author and Taipei time; repeated `true` preserves the first stamp and makes no change. `false` clears both stamps. Closing takes precedence in the derived `notice`; reopening keeps the previous notification state. Acknowledgement leaves the request open. Wrong fields, types, or content yield `invalid`.

## Examples

Success (`request.json` is UTF-8):

```json
{"method":"create_project","params":{"id":"example","title":"Example project","goal":"Ship the first version","signed_as":"Alex"}}
```

```sh
onestead api --as codex --data /srv/example-company < request.json
```

```json
{"ok":true,"result":{"id":"example","commit":"abc1234"}}
```

Failure (`bad-request.json` omits the required signature):

```json
{"method":"append_status","params":{"project_id":"example","text":"First version shipped."}}
```

```sh
onestead api --as codex --data /srv/example-company < bad-request.json
```

```json
{"ok":false,"error":{"code":"invalid","message":"The request has missing required fields or unknown fields."}}
```

The first call exits 0, the second exits 1. Error message text may differ with company language; the codes and result field names stay the same. For the underlying document format and access rules, see [Data format and permissions](data-format.md).
