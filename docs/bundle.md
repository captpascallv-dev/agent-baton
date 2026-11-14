# Private local runtime (protocol 1)

The native bundle runs without an installed Python or Git. The host application
chooses writable locations outside the installed artifact and reads only
`runtime.json` to find executables. All data below is an example, not a default
host-specific path. This protocol never configures other agent clients.

## Initialization and inspection

```sh
onestead bootstrap --data /private/company/data --config /private/company/config.toml --state /private/company/state --agent assistant --agent-name "Assistant" --owner-name "Owner" --lang en --json
onestead inspect --data /private/company/data --config /private/company/config.toml --agent assistant --json
```

`--agent` is required for bootstrap and optional for inspect. The optional
display names default to the agent key and `Owner` / `主人`. Keys and names are
not derived from an OS username. The generated owner key is separate from the
agent key; the full-access, writable agent belongs to the owner. The owner
continues to be the local web identity and cannot be claimed by MCP or the API.
`--lang en|zh` selects templates and diagnostics (default environment language,
then English). Output is one UTF-8 JSON object and one newline on stdout.

Bootstrap success:

```json
{"status":"created","dataset_id":"9f10092291974a2b90d40ea21dc753d3","owner_key":"owner-a92bba2f10057cfa","agent_key":"assistant","data_dir":"/private/company/data","config_path":"/private/company/config.toml","state_dir":"/private/company/state","protocol":1}
```

| Bootstrap status | Exit | Meaning |
| --- | --- | --- |
| `created` | 0 | Template, owner, agent, dataset identity and initial Git commit were built and checked in staging, then published by a same-filesystem directory rename. |
| `existing` | 0 | The existing dataset was retained. Missing identity metadata or members may have been added in one scoped commit; an existing configuration is preserved byte for byte. |
| `needs_repair` | 2 | A nonempty directory failed structural, member, identity or Git checks. Data and configuration are preserved. `reasons` names failed checks. |
| `error` | 1 | The operation could not run. The object is `{status:"error",code,message}`; see codes below. |

An OS file lock in `.<data-name>.onestead-bootstrap.lock` serializes calls at the
same canonical destination. Windows uses `msvcrt` byte locking and POSIX uses
`flock`; process death releases the lock. The lock file remains so waiting
processes never lock different inodes. A sibling transaction marker identifies
the owned staging directory. After interruption, the next call recycles only
that identified, non-linked directory and creates a complete new staging tree.
The final data directory is never treated as ready without actual validation.
The protocol targets a local filesystem with atomic rename and native locks,
not a network share.

The private TOML file records `data_dir`, `owner_key`, `state_dir`, `lang` and
`name`. A complete temporary file is published using a non-overwriting hard
link, so interruption cannot expose a partial configuration. If data publication
completed first, a retry keeps its identity and completes a missing private
configuration. Existing files are never overwritten. An existing configuration
selecting another dataset or owner gives `config_conflict`; choose a matching
private config path. Config and state must be outside the data repository.

Inspect response:

```json
{"initialized":true,"status":"ready","dataset_id":"9f10092291974a2b90d40ea21dc753d3","checks":{"template":true,"owner":true,"agent":true,"git":true},"data_dir":"/private/company/data","config_path":"/private/company/config.toml","protocol":1}
```

Inspect creates no files, directories, locks, commits or Git index updates.
`template` checks the required format/company/collaboration documents and
company directories, not literal template wording (owners can edit it).
`owner` requires a valid active human with full write access. When requested,
`agent` requires a valid active full-access writer belonging to that owner.
Without `--agent`, its check is true and no particular agent is required.
`git` verifies the exact repository root, HEAD and object connectivity.
Dataset metadata must match its committed version. `owner_key` and `reasons`
may be included as diagnostic fields.

| Inspect status | `initialized` | Meaning |
| --- | --- | --- |
| `absent` | false | No directory, or an empty directory. |
| `incomplete` | false | A required check failed; see `checks` and optional `reasons`. |
| `needs_bootstrap` | true | A usable legacy company has no dataset identity; bootstrap adds it in one commit. |
| `ready` | true | All requested checks pass and a committed identity exists. |

Inspect statuses use exit 0, including diagnostic findings; errors use exit 1.
Reason identifiers are `not_directory`, `template`, `owner`, `agent`, `git`,
`unreadable_template`, `invalid_dataset`, `uncommitted_dataset` and
`member_conflict`. Bootstrap can add a missing member; it preserves conflicting,
invalid, archived or differently authorized existing member records for repair.

| Stable error code | Meaning |
| --- | --- |
| `invalid_arguments` | A required parameter is missing, unknown or malformed, or a display name is invalid. |
| `invalid_agent` | Invalid agent key, or agent key equals the owner key. |
| `invalid_paths` | Config/state overlap the data tree or each other. |
| `invalid_config` | Config is malformed UTF-8/TOML. |
| `config_conflict` | An existing configuration identifies another data tree or owner. |
| `staging_conflict` | Residual staging cannot be identified safely or recycled. |
| `verification_failed` | The staged or published repository failed verification; retained for diagnosis. |
| `busy` | Initialization or write-lock timeout. Retry after the other process completes. |
| `git_failed` | Git executable, inspection or commit failed. |
| `io_error` | Filesystem access, permission or publication failed. |

## Consistent paths and Git

`onestead` (including `api`), `onestead-mcp`, and `onestead-web` accept
`--data`, `--config`, `--state`. The priority for each is argument, then
`ONESTEAD_DATA` / `ONESTEAD_CONFIG` / `ONESTEAD_STATE_DIR`, then the existing
configuration/default behavior. Legacy `COMPANY_BASE_*` aliases still work.
CLI path flags normally work before or after a subcommand. Two existing project
commands use `--state` after the subcommand for project status:
`project-create` and `project-summary`; keep that usage or use the unambiguous
`--project-state`. For those commands, put the private directory `--state`
before the subcommand. The JSON API has no such ambiguity.

MCP identity comes from `--agent` (alias `--as`) then `ONESTEAD_AGENT`.
The web entry also accepts `--port 0` for a temporary loopback port and
`--no-browser` for an embedding host. These options do not change default web
launch behavior. `onestead backup` runs the existing configured backup
rotation; `ONESTEAD_BACKUP_DIR` or `backup_dir` selects its destination.

All Git invocations (including failed-write rollback, web revert, clone and
backup) use one resolver: absolute `ONESTEAD_GIT`, then the frozen executable's
adjacent `runtime.json` Git entry, then PATH only for non-frozen installations.
An invalid explicit override or missing bundled Git fails clearly. Frozen
programs never fall back to a system Git. The child environment strips inherited
`GIT_*` redirects/config/exec/template/object/index variables, disables system
and global Git config and attributes, hooks, signing and filesystem monitors,
and disables optional index writes. Explicit commit identities are passed
separately. User configuration and parent PATH are untouched. Windows uses an
absolute executable, a trusted working directory, a System32-only child PATH,
and clears the PyInstaller DLL directory while creating the Git subprocess.

## Artifact contract and native builds

```text
onestead/
  runtime.json
  onestead[.exe]  onestead-mcp[.exe]  onestead-web[.exe]
  _internal/
  git/
  LICENSES/  NOTICE  SOURCES.md  SOURCES/build-recipes/
```

```json
{"schema":1,"name":"onestead","version":"0.1.0","platform":"windows","arch":"x64","protocol":1,"entries":{"cli":"onestead.exe","mcp":"onestead-mcp.exe","web":"onestead-web.exe","git":"git/cmd/git.exe"}}
```

`schema` versions the runtime manifest; `protocol` versions bootstrap/inspect
independently. Both start at integer 1. `name` identifies the product, `version`
comes from project metadata, `platform` is `windows|macos|linux`, and `arch` is
`x64|arm64`. Each entry is relative to the artifact root, with no shell command,
absolute path or argument interpolation. Hosts resolve paths under that root,
pass an argument array, and keep writable company/config/state elsewhere. ZIP
extractors on POSIX must preserve executable permissions (the smoke script
restores ZIP `external_attr` permissions).

Build on the target OS and CPU with CPython 3.12.13:

```sh
python -m pip install -r packaging/requirements-build.lock
python packaging/build.py --work ../bundle-work --output ../bundle-output
python packaging/smoke.py --archive ../bundle-output/onestead-0.1.0-linux-x64.zip --work ../bundle-smoke --record ../bundle-output/smoke.json
```

All PyInstaller caches and build files stay under `--work`. Windows may pass
`--git-archive /cache/MinGit-2.56.1-64-bit.zip`; the same official checksum is
verified for local copies and downloads. `packaging/git-sources.json` pins Git,
zlib, downloads and checksum sources. macOS/Linux compile unchanged Git 2.56.1
and static zlib 1.3.1 on the native runner, omitting network and scripting
integrations; build-time compiler/make are not runtime dependencies. Source
archives, recipes, licenses, package versions and linkage records accompany
the result. See `SOURCES.md` for redistribution and source retrieval details.

`.github/workflows/bundle.yml` is manually dispatched only, for Windows x64,
macOS arm64/x64 and Ubuntu x64. Each native job builds, runs the relocated
runtime smoke script and uploads artifacts plus records. Committing this
workflow does not execute it. macOS/Linux results require those jobs to run;
Windows results do not establish those platforms' runtime compatibility.
Signing, notarization, host integration and shared-company migration are
outside this artifact protocol.
