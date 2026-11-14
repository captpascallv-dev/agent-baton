"""初始化协议的持久性、跨进程并发、中断恢复和不覆盖边界。"""
import json
import os
from pathlib import Path
import subprocess
import sys
import time

import pytest

from onestead import bootstrap as boot, config, gitops, scaffold
from onestead.cli import run
from onestead.store import StorageError, Store


def command(base, *extra):
    return [sys.executable, "-m", "onestead.cli", "bootstrap", "--data", str(base / "data"),
            "--config", str(base / "config.toml"), "--state", str(base / "state"),
            "--agent", "assistant", "--lang", "en", "--json", *extra]


def call(base, *extra):
    process = subprocess.run(command(base, *extra), capture_output=True, text=True, encoding="utf-8")
    assert process.returncode == 0, process.stdout + process.stderr
    assert len(process.stdout.splitlines()) == 1
    return json.loads(process.stdout)


def snapshot(root):
    return {p.relative_to(root).as_posix(): (p.stat().st_mtime_ns, p.read_bytes() if p.is_file() else None)
            for p in root.rglob("*")}


def test_idempotent_identity_and_private_config(tmp_path):
    first = call(tmp_path, "--owner-name", "主人 Example", "--agent-name", "Helper")
    assert first["status"] == "created" and first["owner_key"] != "assistant"
    assert len(first["dataset_id"]) == 32
    before = snapshot(tmp_path / "data")
    configuration = (tmp_path / "config.toml").read_bytes()
    second = call(tmp_path)
    assert second == {**first, "status": "existing"}
    assert snapshot(tmp_path / "data") == before
    assert (tmp_path / "config.toml").read_bytes() == configuration
    root = tmp_path / "data"
    assert gitops._git(root, ["rev-list", "--count", "HEAD"]) == "1"
    assert boot.IDENTITY in gitops._git(root, ["ls-files"]).splitlines()
    with pytest.raises(StorageError):
        gitops.write_and_commit(root, {boot.IDENTITY: "{}"}, "change", "test", "assistant", None)


def test_parallel_first_calls_have_one_dataset(tmp_path):
    procs = [subprocess.Popen(command(tmp_path), stdout=subprocess.PIPE, stderr=subprocess.PIPE) for _ in range(2)]
    rows = []
    for p in procs:
        out, err = p.communicate(timeout=50)
        assert p.returncode == 0, (out, err)
        rows.append(json.loads(out))
    assert {r["status"] for r in rows} == {"created", "existing"}
    assert rows[0]["dataset_id"] == rows[1]["dataset_id"]
    assert gitops._git(tmp_path / "data", ["rev-list", "--count", "HEAD"]) == "1"


def test_killed_before_publication_recovers(tmp_path):
    marker = tmp_path / "paused"
    program = """
import sys,time
from pathlib import Path
from onestead import scaffold
from onestead.cli import run
original = scaffold._commit
def pause(*args):
    original(*args)
    Path(sys.argv[1]).touch()
    time.sleep(120)
scaffold._commit = pause
raise SystemExit(run(sys.argv[2:]))
"""
    proc = subprocess.Popen([sys.executable, "-c", program, str(marker), *command(tmp_path)[3:]],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        deadline = time.monotonic() + 30
        while not marker.exists() and proc.poll() is None and time.monotonic() < deadline:
            time.sleep(0.05)
        assert marker.exists()
        assert not (tmp_path / "data").exists()
    finally:
        proc.kill()
        proc.communicate(timeout=10)
    assert call(tmp_path)["status"] == "created"
    assert not (tmp_path / ".data.onestead-stage").exists()


def test_nonempty_and_damaged_are_preserved(tmp_path):
    root = tmp_path / "data"
    root.mkdir()
    (root / "unrelated.txt").write_bytes(b"keep\x00\xff")
    before = snapshot(root)
    proc = subprocess.run(command(tmp_path), capture_output=True)
    row = json.loads(proc.stdout)
    assert proc.returncode == 2 and row["status"] == "needs_repair"
    assert snapshot(root) == before
    assert not (tmp_path / "config.toml").exists()


def test_inspect_is_readonly_absent_and_ready(tmp_path):
    root, path = tmp_path / "data", tmp_path / "config.toml"
    before = snapshot(tmp_path)
    assert boot.inspect(root, path, "assistant")["status"] == "absent"
    assert snapshot(tmp_path) == before
    created = call(tmp_path)
    before = snapshot(tmp_path)
    checked = boot.inspect(root, path, "assistant")
    assert checked["status"] == "ready" and all(checked["checks"].values())
    assert checked["dataset_id"] == created["dataset_id"]
    assert snapshot(tmp_path) == before
    assert boot.inspect(root, path, "missing")["status"] == "incomplete"
    (root / "handbook/company.md").write_text("broken", encoding="utf-8")
    before = snapshot(root)
    proc = subprocess.run(command(tmp_path), capture_output=True)
    assert json.loads(proc.stdout)["status"] == "needs_repair"
    assert snapshot(root) == before


def test_legacy_migration_and_new_agent_preserve_user_content(tmp_path, monkeypatch, capsys):
    root = tmp_path / "data"
    path = tmp_path / "config.toml"
    monkeypatch.setenv("ONESTEAD_CONFIG", str(path))
    assert run(["init", str(root), "--owner", "example-owner", "--name", "Example Owner"]) == 0
    capsys.readouterr()
    before = {p.relative_to(root).as_posix(): p.read_bytes() for p in root.rglob("*.md")}
    old_config = path.read_bytes()
    assert boot.inspect(root, path)["status"] == "needs_bootstrap"
    created = call(tmp_path)
    assert created["status"] == "existing" and created["owner_key"] == "example-owner"
    assert path.read_bytes() == old_config
    assert before == {rel: (root / rel).read_bytes() for rel in before}
    assert gitops._git(root, ["rev-list", "--count", "HEAD"]) == "2"
    additional = call(tmp_path, "--agent", "other-agent")
    assert additional["dataset_id"] == created["dataset_id"]
    assert call(tmp_path, "--agent", "other-agent") == additional
    assert gitops._git(root, ["rev-list", "--count", "HEAD"]) == "3"


def test_restored_dataset_keeps_identity(tmp_path):
    first = call(tmp_path)
    root = tmp_path / "data"
    restored = tmp_path / "restored"
    gitops._git(root, ["clone", "--quiet", str(root), str(restored)])
    checked = boot.inspect(restored, tmp_path / "new.toml", "assistant")
    assert checked["status"] == "ready" and checked["dataset_id"] == first["dataset_id"]


def test_bad_json_config_errors_are_localized(tmp_path):
    (tmp_path / "config.toml").write_text("[broken", encoding="utf-8")
    for lang, needle in (("en", "configuration"), ("zh", "配置")):
        proc = subprocess.run(command(tmp_path, "--lang", lang), capture_output=True)
        row = json.loads(proc.stdout)
        assert proc.returncode == 1 and row["code"] == "invalid_config"
        assert needle in row["message"]
    assert not (tmp_path / "data").exists()


def test_mutated_identity_needs_repair(tmp_path):
    call(tmp_path)
    path = tmp_path / "data" / boot.IDENTITY
    path.write_text("{}", encoding="utf-8")
    before = snapshot(tmp_path / "data")
    proc = subprocess.run(command(tmp_path), capture_output=True)
    assert json.loads(proc.stdout)["status"] == "needs_repair"
    assert snapshot(tmp_path / "data") == before


def test_parameter_errors_are_one_json_line(tmp_path):
    process = subprocess.run([*command(tmp_path), "--config"], capture_output=True)
    assert process.returncode == 1
    assert len(process.stdout.splitlines()) == 1
    assert json.loads(process.stdout)["code"] == "invalid_arguments"


def test_missing_config_after_publication_recovers_identity(tmp_path):
    first = call(tmp_path)
    (tmp_path / "config.toml").unlink()
    second = call(tmp_path)
    assert second == {**first, "status": "existing"}
    assert (tmp_path / "config.toml").is_file()


def test_invalid_staging_marker_is_preserved(tmp_path):
    marker = tmp_path / ".data.onestead-transaction.json"
    marker.write_text("unrelated", encoding="utf-8")
    (tmp_path / ".data.onestead-stage").mkdir()
    proc = subprocess.run(command(tmp_path), capture_output=True)
    assert proc.returncode == 1 and json.loads(proc.stdout)["code"] == "staging_conflict"
    assert marker.read_text(encoding="utf-8") == "unrelated"
    assert not (tmp_path / "data").exists()


def test_bootstrap_does_not_load_connect(tmp_path):
    program = "import sys; sys.modules['onestead.connect']=None; from onestead.cli import run; raise SystemExit(run(sys.argv[1:]))"
    proc = subprocess.run([sys.executable, "-c", program, *command(tmp_path)[3:]], capture_output=True)
    assert proc.returncode == 0, proc.stderr + proc.stdout
    assert json.loads(proc.stdout)["status"] == "created"
