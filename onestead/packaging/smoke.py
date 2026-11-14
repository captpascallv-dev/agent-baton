"""用系统 Python 调用解压后的工件；所有公司、配置和口令均为隔离合成数据。"""
from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.request
import zipfile

TAIPEI = timezone(timedelta(hours=8))
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def snapshot(root):
    return {p.relative_to(root).as_posix(): (p.stat().st_mtime_ns, p.read_bytes() if p.is_file() else None)
            for p in root.rglob("*")}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--record", type=Path, required=True)
    args = parser.parse_args()
    work = args.work.resolve() / ("中文 空格 " + datetime.now(TAIPEI).strftime("%Y%m%d-%H%M%S"))
    work.mkdir(parents=True)
    records = dict(started_at=datetime.now(TAIPEI).isoformat(), status="running", work=str(work), checks=[])
    args.record.parent.mkdir(parents=True, exist_ok=True)

    def passed(name, **details):
        records["checks"].append(dict(name=name, status="pass", **details))
        print("PASS " + name, flush=True)

    env = {k: v for k, v in os.environ.items() if not k.startswith(("ONESTEAD_", "COMPANY_BASE_", "PYTHON", "GIT_"))}
    for name in ("home", "temp", "empty-path"):
        (work / name).mkdir()
    env.update(HOME=str(work / "home"), USERPROFILE=str(work / "home"),
               TEMP=str(work / "temp"), TMP=str(work / "temp"),
               LOCALAPPDATA=str(work / "home/local"), APPDATA=str(work / "home/roaming"),
               XDG_CONFIG_HOME=str(work / "home/config"), XDG_STATE_HOME=str(work / "home/state"),
               ONESTEAD_LANG="en", ONESTEAD_USAGE_LOG="", PYTHONUTF8="1")
    env["PATH"] = str(Path(os.environ["SystemRoot"]) / "System32") if os.name == "nt" else str(work / "empty-path")
    assert all(shutil.which(name, path=env["PATH"]) is None for name in ("python", "python3", "git"))
    env.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_SYSTEM=os.devnull, GIT_CONFIG_GLOBAL=os.devnull)

    def run(command, *, data=None, expected=0, extra_env=None):
        child = subprocess.run(list(map(str, command)), input=data, env={**env, **(extra_env or {})},
                               capture_output=True, cwd=work, creationflags=NO_WINDOW, timeout=90)
        if child.returncode != expected:
            # 网页不经本函数启动；这里的输出不含网页会话内容。
            raise RuntimeError(f"exit={child.returncode}, expected={expected}; " + child.stdout.decode("utf-8", "replace") +
                               child.stderr.decode("utf-8", "replace"))
        return child.stdout

    try:
        with zipfile.ZipFile(args.archive) as stream:
            stream.extractall(work)
            if os.name != "nt":
                for info in stream.infolist():
                    mode = info.external_attr >> 16
                    if mode:
                        (work / info.filename).chmod(mode)
        artifact = work / "onestead"
        runtime = json.loads((artifact / "runtime.json").read_text(encoding="utf-8"))
        assert runtime["protocol"] == runtime["schema"] == 1
        entries = {k: artifact / rel for k, rel in runtime["entries"].items()}
        assert all(p.is_file() and p.resolve().is_relative_to(artifact) for p in entries.values())
        passed("relocated_and_path_isolated", path=env["PATH"], runtime=runtime)
        for name in ("cli", "mcp", "web"):
            assert b"--help" in run([entries[name], "--help"])
            passed("help_" + name)

        def options(base, state=True):
            result = ["--data", str(base / "data"), "--config", str(base / "config.toml")]
            return result + ["--state", str(base / "state")] if state else result

        def boot_command(base):
            return [entries["cli"], "bootstrap", *options(base), "--agent", "assistant", "--lang", "en", "--json"]

        def boot(base, expected=0):
            output = run(boot_command(base), expected=expected)
            assert len(output.splitlines()) == 1
            return json.loads(output)

        base = work / "normal"
        # inspect 在目标尚不存在时也不得建父目录、配置或状态。
        before = snapshot(work)
        absent = json.loads(run([entries["cli"], "inspect", *options(base, False), "--agent", "assistant", "--json"]))
        assert absent["status"] == "absent" and not absent["initialized"]
        assert before == snapshot(work)
        passed("inspect_absent_readonly")
        first = boot(base)
        assert first["status"] == "created"
        dataset = first["dataset_id"]
        before = snapshot(base)
        second = boot(base)
        assert second == {**first, "status": "existing"}
        assert before == snapshot(base)
        passed("bootstrap_created_then_existing", dataset_id=dataset)
        before = snapshot(base)
        checked = json.loads(run([entries["cli"], "inspect", *options(base, False), "--agent", "assistant", "--json"]))
        assert checked["status"] == "ready" and all(checked["checks"].values())
        assert checked["dataset_id"] == dataset and snapshot(base) == before
        passed("inspect_ready_readonly")
        run([entries["cli"], "check", *options(base)])
        passed("dataset_identity_accepted_by_check")

        parallel = work / "parallel"
        procs = [subprocess.Popen(list(map(str, boot_command(parallel))), env=env, cwd=work,
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=NO_WINDOW) for _ in range(2)]
        rows = []
        for proc in procs:
            out, err = proc.communicate(timeout=90)
            assert proc.returncode == 0, (out, err)
            rows.append(json.loads(out))
        assert {r["status"] for r in rows} == {"created", "existing"}
        assert rows[0]["dataset_id"] == rows[1]["dataset_id"]
        assert run([entries["git"], "-C", parallel / "data", "rev-list", "--count", "HEAD"]).strip() == b"1"
        passed("bootstrap_two_processes", statuses=[r["status"] for r in rows], dataset_id=rows[0]["dataset_id"])

        interrupted = work / "interrupted"
        interrupted.mkdir()
        stage = interrupted / ".data.onestead-stage"
        proc = subprocess.Popen(list(map(str, boot_command(interrupted))), env=env, cwd=work,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=NO_WINDOW)
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline and proc.poll() is None:
                if (stage / ".git/HEAD").exists() and not (interrupted / "data").exists():
                    proc.kill()
                    break
                time.sleep(0.001)
            else:
                raise RuntimeError("could not interrupt bootstrap before publication")
        finally:
            if proc.poll() is None:
                proc.kill()
            proc.communicate(timeout=15)
        assert not (interrupted / "data").exists()
        time.sleep(0.3)  # 被杀进程最后一个短命 Git 子进程退出后再回收暂存目录。
        recovered = boot(interrupted)
        assert recovered["status"] == "created" and not stage.exists()
        passed("bootstrap_killed_and_recovered", exit_code=proc.returncode)

        occupied = work / "occupied"
        (occupied / "data").mkdir(parents=True)
        (occupied / "data/unrelated.bin").write_bytes(b"unrelated\x00\xff")
        before = snapshot(occupied / "data")
        repair = boot(occupied, expected=2)
        assert repair["status"] == "needs_repair" and before == snapshot(occupied / "data")
        assert not (occupied / "config.toml").exists() and not (occupied / "state").exists()
        passed("needs_repair_preserves_nonempty", reasons=repair["reasons"])

        def api(method, params):
            reply = json.loads(run([entries["cli"], "api", *options(base), "--as", "assistant"],
                                   data=json.dumps(dict(method=method, params=params)).encode("utf-8")))
            assert reply["ok"], reply
            return reply["result"]

        lesson = api("record_lesson", dict(lesson="Check the incoming delivery before counting stock.", signed_as="Example"))
        listed = api("list_lessons", {})
        assert len(listed["lessons"]) == 1
        passed("api_record_and_list_lessons")
        imported = api("import_lessons", dict(items=[dict(lesson="Keep a single delivery log.", origin_id="smoke:lesson:1")], signed_as="Example"))
        repeated = api("import_lessons", dict(items=[dict(lesson="Keep a single delivery log.", origin_id="smoke:lesson:1")], signed_as="Example"))
        assert len(imported["created"]) == 1 and not repeated["created"] and len(repeated["skipped"]) == 1
        passed("api_import_lessons_replay")
        decision_params = dict(title="Use one log", decision="Use one log for deliveries.", reason="Avoid duplicates.",
                               decided_by="Owner", signed_as="Example", origin_id="smoke:decision:1")
        decision = api("record_decision", decision_params)
        repeated_decision = api("record_decision", decision_params)
        assert not decision["skipped"] and repeated_decision["skipped"] and repeated_decision["commit"] is None
        passed("api_record_decision_replay")
        history = api("history", dict(target=decision["path"]))
        assert history
        passed("api_history", entries=len(history))

        proc = subprocess.Popen([str(entries["mcp"]), *options(base), "--agent", "assistant"], env=env, cwd=work,
                                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=NO_WINDOW)
        incoming = queue.Queue()
        def read_lines():
            for line in proc.stdout:
                incoming.put(line)
        threading.Thread(target=read_lines, daemon=True).start()
        def request(payload):
            proc.stdin.write(json.dumps(payload).encode("utf-8") + b"\n")
            proc.stdin.flush()
        def response(request_id):
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                row = json.loads(incoming.get(timeout=max(0.1, deadline - time.monotonic())))
                if row.get("id") == request_id:
                    assert "error" not in row, row
                    return row["result"]
            raise RuntimeError("MCP response timeout")
        try:
            request(dict(jsonrpc="2.0", id=1, method="initialize", params=dict(protocolVersion="2025-03-26", capabilities={}, clientInfo=dict(name="bundle-smoke", version="1"))))
            initialized = response(1)
            request(dict(jsonrpc="2.0", method="notifications/initialized"))
            request(dict(jsonrpc="2.0", id=2, method="tools/call", params=dict(name="company", arguments={})))
            company = response(2)
            assert not company.get("isError") and any(c.get("text") for c in company["content"])
            passed("mcp_initialize_and_company", protocol=initialized["protocolVersion"])
        finally:
            proc.stdin.close()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=10)

        # 口令只在隔离 state 文件和内存中；stdout/stderr 不落网页日志，避免记录口令。
        proc = subprocess.Popen([str(entries["web"]), *options(base), "--port", "0", "--no-browser"],
                                env=env, cwd=work, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=NO_WINDOW)
        try:
            deadline = time.monotonic() + 30
            response_code = None
            while time.monotonic() < deadline and proc.poll() is None:
                try:
                    state = json.loads((base / "state/ui-state.json").read_text(encoding="utf-8"))
                    request_web = urllib.request.Request(f"http://127.0.0.1:{state['port']}/",
                         headers={"Cookie": f"cb_session_{state['port']}={state['token']}"})
                    with urllib.request.urlopen(request_web, timeout=1) as page:
                        html = page.read().decode("utf-8")
                        response_code = page.status
                    assert response_code == 200 and "<html" in html and "Onestead" in html
                    break
                except (OSError, ValueError):
                    time.sleep(0.1)
            assert response_code == 200, "web did not become ready"
            with urllib.request.urlopen(f"http://127.0.0.1:{state['port']}/static/app.js", timeout=5) as resource:
                assert resource.status == 200 and len(resource.read()) > 100
            passed("web_http_and_static", http_status=response_code, port=state["port"], token_logged=False)
        finally:
            proc.terminate()
            proc.wait(timeout=15)
            state = None

        # 现有 backup.rotate 经 CLI 进入；其内部 bundle verify/clone/ZIP 检查实际执行。
        backup_root = work / "backups"
        backup = json.loads(run([entries["cli"], "backup", *options(base)], extra_env={"ONESTEAD_BACKUP_DIR": str(backup_root)}))
        current = backup_root / "Company-Base" / backup["current"]
        bundle = next(current.glob("*-history.bundle"))
        manifest = json.loads((current / "MANIFEST.json").read_text(encoding="utf-8"))
        branch = manifest["repos"]["data"]["branch"]
        restored = work / "restored"
        run([entries["git"], "clone", "--quiet", "-b", branch, bundle, restored])
        original_history = run([entries["git"], "-C", base / "data", "rev-list", "HEAD"])
        restored_history = run([entries["git"], "-C", restored, "rev-list", "HEAD"])
        assert restored_history == original_history
        restored_check = json.loads(run([entries["cli"], "inspect", "--data", restored, "--config", work / "restored.toml", "--agent", "assistant", "--json"]))
        assert restored_check["dataset_id"] == dataset and restored_check["status"] == "ready"
        passed("git_backup_and_restore", commits=len(original_history.splitlines()), dataset_id=dataset)
        assert not any((work / "home" / name).exists() for name in (".claude", ".codex", ".cursor"))
        passed("no_other_harness_configuration")
        records["status"] = "pass"
    except Exception as error:
        records.update(status="fail", error_type=type(error).__name__, error=str(error))
        raise
    finally:
        records["finished_at"] = datetime.now(TAIPEI).isoformat()
        args.record.write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
