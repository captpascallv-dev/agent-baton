"""PreToolUse guard shared by agents: refuse test runs that lack an OS memory cap.

An uncapped pytest batch once committed ~72 GB and took a whole 32 GB machine down.
A test run must go through a Windows Job memory-cap entry: capped_run.py next to this
file, or the project's bounded_pytest.py.

usage (hook command): python -X utf8 guard_test_runs.py --client claude|codex|grok|cursor
Reads one native hook event on stdin; prints a deny decision in that client's format,
prints nothing to allow. Always exits 0 (a denial is a protocol answer, not a crash).
"""
import argparse
import json
import os
import re
import sys
import shlex

CAP_ENTRIES = ("capped_run.py", "bounded_pytest.py")
CAPPED_RUN = os.path.join(os.path.dirname(os.path.abspath(__file__)), "capped_run.py").replace("\\", "/")
# Only invocations count: a runner at command position (start of a line or after ; & | ( or
# a timeout N prefix), a python -m runner, or a package-runner launch. Prose that merely
# mentions the word "pytest" is not a test run.
CMD_POS = r"(?:^|[;&|(]|\n|\btimeout\s+\d+)\s*"
TEST_RUNNER = re.compile(
    r"-m\s+(?:pytest|unittest)\b"
    rf"|{CMD_POS}(?:\S*[/\\])?(?:pytest|py\.test|vitest|jest)(?:\.exe|\.cmd)?(?=\s|$)"
    r"|\b(?:npx|pnpm|yarn|bunx)(?:\.cmd)?\s+(?:vitest|jest)\b"
    r"|\bnode(?:\.exe)?\s+--test\b"
    r"|\bnpm(?:\.cmd)?\s+(?:run\s+)?test\b",
    re.IGNORECASE | re.MULTILINE,
)
REASON = (
    "TEST_MEMORY_CAP_REQUIRED: 跑测试必须套 Windows Job 内存上限 / test runs need a memory cap. 改成\n"
    f"  python {CAPPED_RUN} --mem-gb 2 --timeout 900 --log <日志文件> -- <原命令>\n"
    "（或项目的 bounded_pytest.py）。建议同时最多 3 个上限进程、上限合计不超过机器内存的四分之一。")


if sys.platform == "darwin":
    REASON = REASON.replace("Windows Job 内存上限", "macOS 采样式进程树内存上限")


def command_of(event) -> str:
    if not isinstance(event, dict):
        return ""
    for box in (event.get("tool_input"), event.get("toolInput"), event):
        if isinstance(box, dict):
            cmd = box.get("command") or box.get("cmd")
            if isinstance(cmd, list):
                cmd = " ".join(str(part) for part in cmd)
            if isinstance(cmd, str):
                return cmd
    return ""


def denied(client: str) -> dict:
    if client == "grok":
        return {"decision": "deny", "reason": REASON}
    if client == "cursor":
        return {"permission": "deny", "agent_message": REASON,
                "user_message": "已阻止未设内存上限的测试命令。"}
    return {"hookSpecificOutput": {"hookEventName": "PreToolUse",
                                   "permissionDecision": "deny",
                                   "permissionDecisionReason": REASON}}


def needs_cap(command: str) -> bool:
    if sys.platform == "darwin":
        # Match POSIX executable paths and ordinary env/assignment prefixes.
        # This remains a visible-command hook, not a shell sandbox.
        try:
            tokens = shlex.split(command)
        except ValueError:
            return True
        if tokens and os.path.basename(tokens[0]) == "env":
            tokens = tokens[1:]
        while tokens and re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", tokens[0]):
            tokens = tokens[1:]
        normalized = " ".join(os.path.basename(t) if t.startswith("/") else t for t in tokens)
        # Preserve shell line separators lost by shlex.split/join.
        return bool(TEST_RUNNER.search(command) or TEST_RUNNER.search(normalized)) and not any(e in command for e in CAP_ENTRIES)
    return bool(TEST_RUNNER.search(command)) and not any(e in command for e in CAP_ENTRIES)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--client", choices=("claude", "codex", "grok", "cursor"), default="claude")
    args = ap.parse_args()
    try:
        event = json.load(sys.stdin.buffer)
    except (ValueError, TypeError):
        return 0
    if needs_cap(command_of(event)):
        print(json.dumps(denied(args.client), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
