"""Offline ACP smoke. The fake agent is a local subprocess. No model, vendor CLI, or network."""
import ast
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLIENT = ROOT / "acp_client.py"
FAKE = Path(__file__).resolve().parent / "fake_agent.py"
ALLOWED_MODULES = {"argparse", "json", "os", "re", "subprocess", "sys", "threading", "time", "queue"}


def fail(message):
    raise SystemExit(message)


def check_sources():
    for path in (CLIENT, FAKE):
        text = path.read_text(encoding="utf-8")
        if re.search(r"[A-Za-z]:\\", text):
            fail(f"{path.name} contains a drive path")
        tree = ast.parse(text)
        modules = set()
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                for alias in node.names:
                    modules.add(alias.name.split(".")[0])
            elif isinstance(node, ast.ImportFrom) and node.module:
                modules.add(node.module.split(".")[0])
        extra = modules - ALLOWED_MODULES
        if extra:
            fail(f"{path.name} imports outside the stdlib allowlist: {sorted(extra)}")
    print("PASS source allowlist")


def run_client(log_dir, extra, mode, expect):
    log_dir.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    env["ACP_FAKE_MODE"] = mode
    if mode == "resume":
        env["ACP_FAKE_RESUME_ID"] = "fake-session"
    command = [
        sys.executable, str(CLIENT),
        "--agent", sys.executable,
        "--agent-arg", str(FAKE),
        "--cwd", str(log_dir),
        "--log-dir", str(log_dir),
        "--rpc-timeout", "20",
        "--timeout-min", "1",
        *extra,
    ]
    completed = subprocess.run(command, env=env, capture_output=True, timeout=40)
    stdout = completed.stdout.decode("utf-8", errors="replace")
    stderr = completed.stderr.decode("utf-8", errors="replace")
    if completed.returncode != expect:
        detail = ""
        result_path = log_dir / "result.json"
        if result_path.exists():
            detail = result_path.read_text(encoding="utf-8")
        agent_err = log_dir / "stderr.txt"
        if agent_err.exists():
            detail += "\n" + agent_err.read_text(encoding="utf-8", errors="replace")
        fail(f"expected exit {expect}, got {completed.returncode}\n{stdout}\n{stderr}\n{detail}")
    return stdout, stderr


def read_jsonl(path):
    if not path.exists():
        return []
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            rows.append(json.loads(line))
    return rows


def main():
    check_sources()
    missing = subprocess.run([sys.executable, str(CLIENT)], capture_output=True, text=True, timeout=20)
    if missing.returncode == 0:
        fail("client without --agent exited 0")
    print("PASS missing agent is rejected")
    with tempfile.TemporaryDirectory(prefix="acp-smoke-") as temp:
        base = Path(temp)
        prompt = base / "prompt.md"
        prompt.write_text("smoke-prompt-marker\n", encoding="utf-8")

        deny_dir = base / "deny"
        stdout, _stderr = run_client(deny_dir, [
            "--prompt-file", str(prompt), "--model", "demo-model", "--effort", "demo-effort",
            "--permission-mode", "deny",
        ], "full", 0)
        result = json.loads((deny_dir / "result.json").read_text(encoding="utf-8"))
        if result.get("error") or result.get("stop_reason") != "end_turn":
            fail(f"deny run did not finish cleanly: {result}")
        if result.get("config_current", {}).get("model") != "demo-model":
            fail("model was not confirmed")
        if result.get("config_current", {}).get("reasoning_effort") != "demo-effort":
            fail("effort was not confirmed")
        transcript = (deny_dir / "transcript.md").read_text(encoding="utf-8")
        if "smoke-prompt-marker" not in transcript or "permission-settled" not in transcript:
            fail("transcript missed the prompt stream")
        perms = read_jsonl(deny_dir / "permissions.jsonl")
        if len(perms) != 1 or perms[0].get("option_kind") != "reject_once" or perms[0].get("outcome") != "selected":
            fail(f"default deny did not record reject_once: {perms}")
        if perms[0].get("title") != "echo smoke":
            fail("permission log missed the tool title")
        events = (deny_dir / "events.jsonl").read_text(encoding="utf-8")
        for method in ("initialize", "session/new", "session/prompt", "session/request_permission"):
            if method not in events:
                fail(f"events missed {method}")
        if "session/resume" in events:
            fail("new session used resume")
        if "demo-model" not in stdout and "end_turn" not in (deny_dir / "result.json").read_text(encoding="utf-8"):
            fail("result file missing")
        print("PASS initialize, session, prompt, stream, deny")

        approve_dir = base / "approve"
        run_client(approve_dir, [
            "--prompt-file", str(prompt), "--permission-mode", "approve",
        ], "full", 0)
        approved = read_jsonl(approve_dir / "permissions.jsonl")
        if len(approved) != 1 or approved[0].get("option_kind") != "allow_once":
            fail(f"approve mode did not select allow_once: {approved}")
        print("PASS approve records allow_once")

        resume_dir = base / "resume"
        run_client(resume_dir, [
            "--prompt-file", str(prompt), "--resume-session", "fake-session", "--permission-mode", "deny",
        ], "resume", 0)
        resumed = json.loads((resume_dir / "result.json").read_text(encoding="utf-8"))
        if resumed.get("resumed") != "fake-session" or resumed.get("session_id") != "fake-session":
            fail(f"resume was not confirmed: {resumed}")
        resume_events = (resume_dir / "events.jsonl").read_text(encoding="utf-8")
        if "session/resume" not in resume_events or "session/new" in resume_events:
            fail("resume path called session/new")
        print("PASS resume")

        bare_dir = base / "no-config"
        _stdout, stderr = run_client(bare_dir, ["--prompt-file", str(prompt), "--model", "demo-model"], "no-config", 1)
        blob = (bare_dir / "result.json").read_text(encoding="utf-8") + stderr
        if "不支持配置项" not in blob:
            fail("unsupported model config was set or the error was unclear")
        if "session/prompt" in (bare_dir / "events.jsonl").read_text(encoding="utf-8"):
            fail("unsupported config still sent a prompt")
        print("PASS unsupported config is not set")

        bad_dir = base / "bad-readback"
        _stdout, stderr = run_client(bad_dir, ["--prompt-file", str(prompt), "--model", "demo-model"], "bad-readback", 1)
        blob = (bad_dir / "result.json").read_text(encoding="utf-8") + stderr
        if "回读不一致" not in blob:
            fail("readback mismatch was accepted")
        print("PASS config readback must match")

        probe_dir = base / "probe"
        stdout, _stderr = run_client(probe_dir, ["--probe"], "full", 0)
        if "fake-acp-agent" not in stdout:
            fail("probe did not report the fake agent")
        if "session/prompt" in (probe_dir / "events.jsonl").read_text(encoding="utf-8"):
            fail("probe sent a prompt")
        print("PASS probe")

    print("SMOKE PASS")


if __name__ == "__main__":
    main()
