# MAC-PORT-A: macOS adaptation and validation

Date: 2026-11-12. Base: `09f0738`. Implementation commit: `6db3c12`. Platform: Intel x86_64, macOS 15.7.9, 8 GB RAM.
Scope: repository code and isolated test data only. No system settings, existing Codex configuration/plugins, other repositories, or credentials were changed. No real model dispatch has been performed: that single model-consuming step is pending the owner's explicit approval.

## Toolchain

- Node 26.7.0 / npm 11.19; existing interpreter Python 3.11.15.
- Task-local Python 3.12.16, installed using PyPI uv 0.13.7 and its official Python distribution; isolated venv.
- Task-local Microsoft PowerShell 7.7.0 macOS x64 archive. Official SHA256 verified: `61906bf8f407ef5cc263874c904c551d69fbb61d42dbd8f848a461ec47bf68b5`.
- Onestead declared `[docs]` and pytest installed only in the venv. pip `--only-binary=:all:` avoids expensive dependency compilation. No new runtime dependency was added to the repository.

## Verified commands

Commands below use relative repository paths and omit machine-private locations. Tests were serial and wrapped by `python3 guards/capped_run.py --mem-gb 1 --timeout <seconds> --log <test-log> -- ...` after that runner's own tests passed.

| Command | Exit | Evidence |
| --- | --- | --- |
| `npm --prefix codex/plugin test` | 0 | 36/36; 169.7 seconds; sampled peak 0.36 GB |
| `node codex/tools/check-public.mjs` | 0 | 13 Node syntax checks, 3 Python AST checks; public path and config checks true |
| `python3 acp/tests/smoke.py` | 0 | `SMOKE PASS`; mock initialize/session/prompt/deny/approve/resume/probe |
| `pwsh -NoProfile -File claude/tests/Smoke.ps1 -KeepTemp` | 0 | 12/12; mock stdin/pointer dispatch, UTF-8, waiters, resume, rejection before writes; 90.4 seconds |
| `pwsh -NoProfile -File mailbox/tests/Smoke.ps1` | 0 | 8/8; UTF-8 send/read/archive/wait; task-local mock mail only |
| `npm --prefix studio run test:live` | 0 | 29/29; fake process/local HTTP only, not a real live-provider validation |
| `python3 -m unittest discover -s guards/tests -v` | 0 | 11/11 (8 runner + 3 command-hook); normal exit7, timeout124, memory137, runner error125, tree sampling, fail-closed sampling, process-group cleanup |
| `python3 -m unittest discover -s guards/file-guard/tests -q` | 0 | Mac11 passed; Windows12 skipped; native trash of owned fixture verified |

## Onestead coverage across bounded runs

`HOME`, `USERPROFILE`, `TMPDIR`, `TEMP`, and `TMP` all pointed to an empty task-owned temporary tree. `pytest --collect-only -q` collected 1312 nodes in the same order.

- Initial `python -m pytest onestead/tests -q`: outer runner exited 124 at 900 seconds, sampled peak 0.19 GB. Progress recorded 1009 completed nodes: 1008 passes and 1 failure. It was **not** a successful complete-suite run.
- The failure was node 505, `test_company_matches_deepest_visible_directory`. After the platform-correct fixture change, `pytest onestead/tests/test_project_paths.py -q` passed 7/7, exit 0.
- Resume explicitly selected collected nodes 1009–1312 (304 nodes, one overlaps the completed prefix). All 304 passed, exit 0,283.73 seconds, sampled peak0.12 GB.
- The union covers all 1312 collected nodes, with the known failing node fixed and reverified. This is bounded split-run coverage, not a claim that a single unbroken full-suite run finished green. No test was silently dropped.

## Changes and root causes

- Ordinary Codex plugin fake-host fixture used a 250 ms launch deadline. On the older Intel host, failures moved between tests; outputs were pending/missing acknowledgements rather than deterministic path failures. Darwin's ordinary fixture now has 2000 ms startup headroom. All explicit deadline tests and production defaults remain unchanged. The original 120-second outer cap was also too short; the complete failed baseline took 178 seconds, then the corrected full suite passed.
- `capped_run.py` now uses a separate session/process group on Mac, samples system `ps` RSS every 100 ms, and cleans up the original group on exit. Windows Job code is unchanged apart from conditional imports/platform routing.
- The Mac command hook recognizes common POSIX executable paths and environment prefixes. It remains a visible-command hook, not a shell sandbox.
- File guard now supports canonical absolute POSIX paths and native macOS14+ `/usr/bin/trash -s`. Dynamic/relative paths, root/ancestor targets, symbolic-link retargeting, and Finder aliases fail closed. Native trash failure never calls permanent deletion. Windows implementation remains in its existing platform branch.

- Claude smoke initially rejected the same directory spelled through macOS `/var` and `/private/var`. Retained mock logs proved that the runner had executed normally; no production dispatch change was needed. The Mac test now compares directory device/inode, preserving the Windows string comparison.
- Onestead project-path fixture used Windows backslashes on POSIX. That separator is a literal character there; the test now supplies forward slashes on POSIX while keeping the Windows fixture. Production path semantics remain unchanged; the whole affected module passed 7/7.

## Limits / unresolved work

- Mac RSS sampling is not a Windows-style hard memory limit. Shared pages can be counted twice; sampling can miss short peaks. Deliberately daemonized descendants that escape the process group are not securely contained.
- Finder aliases are detected and refused rather than automatically resolved/re-targeted. Real Finder alias, cross-volume and very-large-target tests were not performed. The fixture confirmed removal from the source into native trash, but content restoration from trash was not tested. The accepted-path native trash fixture needed no Finder automation or permission dialog.
- Windows regression testing must be performed on Windows before merge.
- Real Codex dispatch remains unexecuted pending approval. Offline mocks do not prove real account dispatch, delivery, or App wake.
- `watch/`, `hud/`, and studio real mode remain out of scope.
