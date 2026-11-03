"""跨平台分支只模拟平台和系统命令；备份只用临时测试仓库，不碰部署数据或回收站。"""

import json
import logging
import os
import signal
import socket
import subprocess
import sys
import sysconfig
import tempfile
from pathlib import Path
from types import SimpleNamespace

import pytest

from onestead import backup, config, online
from onestead.store import BaseError
from onestead.web import launcher, probe

from .support import APP, SID, WEB_POST_HEADERS, git, junction


@pytest.mark.parametrize("platform,local,xdg,relative", [
    ("win32", True, True, "local/Onestead"),
    ("win32", False, True, "home/Onestead"),
    ("darwin", True, True, "home/Library/Application Support/Onestead"),
    ("darwin", False, False, "home/Library/Application Support/Onestead"),
    ("linux", True, True, "xdg/onestead"),
    ("linux", True, False, "home/.local/state/onestead"),
])
def test_state_defaults(platform, local, xdg, relative, tmp_path, monkeypatch):
    monkeypatch.setattr(sys, "platform", platform)
    monkeypatch.setattr(Path, "home", lambda: tmp_path / "home")
    monkeypatch.delenv("COMPANY_BASE_STATE_DIR")
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "local") if local else "")
    monkeypatch.setenv("XDG_STATE_HOME", str(tmp_path / "xdg") if xdg else "")
    assert config.state_dir() == tmp_path / relative
    if not local:
        monkeypatch.delenv("LOCALAPPDATA")
    if not xdg:
        monkeypatch.delenv("XDG_STATE_HOME")
    assert config.state_dir() == tmp_path / relative


@pytest.mark.parametrize("platform", ["win32", "darwin", "linux"])
def test_state_override_priority(platform, isolated_config, tmp_path, monkeypatch):
    monkeypatch.setattr(sys, "platform", platform)
    isolated_config.write_text('state_dir = "from-file"\n', encoding="utf-8")
    monkeypatch.delenv("COMPANY_BASE_STATE_DIR")
    assert config.state_dir() == Path("from-file")
    monkeypatch.setenv("COMPANY_BASE_STATE_DIR", str(tmp_path / "explicit"))
    assert config.state_dir() == tmp_path / "explicit"
    monkeypatch.setenv("COMPANY_BASE_STATE_DIR", " ")
    monkeypatch.setattr(Path, "home", lambda: tmp_path / "home")
    monkeypatch.setenv("LOCALAPPDATA", "")
    monkeypatch.setenv("XDG_STATE_HOME", "")
    assert config.state_dir() != Path("from-file")


@pytest.mark.parametrize("platform", ["win32", "darwin", "linux"])
def test_cloudflared_lookup(platform, tmp_path, monkeypatch):
    monkeypatch.setattr(sys, "platform", platform)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    exe = tmp_path / "Microsoft/WinGet/Packages/Cloudflare.cloudflared_1/cloudflared.exe"
    exe.parent.mkdir(parents=True)
    exe.write_text("测试占位", encoding="utf-8")
    monkeypatch.setattr(online.shutil, "which", lambda name: "path-cloudflared")
    monkeypatch.setenv("COMPANY_BASE_CLOUDFLARED", "explicit-cloudflared")
    assert online._cloudflared() == "explicit-cloudflared"
    monkeypatch.delenv("COMPANY_BASE_CLOUDFLARED")
    assert online._cloudflared() == "path-cloudflared"
    monkeypatch.setattr(online.shutil, "which", lambda name: None)
    if platform != "win32":
        monkeypatch.setattr(Path, "glob", lambda *a: pytest.fail("POSIX 不应查 WinGet"))
    assert online._cloudflared() == (str(exe) if platform == "win32" else None)


def test_cloudflared_windows_missing(tmp_path, monkeypatch):
    monkeypatch.setattr(sys, "platform", "win32")
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    monkeypatch.setattr(online.shutil, "which", lambda name: None)
    assert online._cloudflared() is None


@pytest.mark.parametrize("platform", ["darwin", "linux"])
def test_posix_stale_tunnels(platform, monkeypatch, caplog):
    monkeypatch.setattr(sys, "platform", platform)
    text = f"""  PID COMMAND
101 /usr/local/bin/cloudflared tunnel run --token-file /tmp/cloudflare-tunnel-token.txt
102 cloudflared tunnel run --token-file /tmp/other-token.txt
103 python worker.py cloudflare-tunnel-token.txt
104 /opt/homebrew/bin/cloudflared tunnel run --token-file /tmp/cloudflare-tunnel-token.txt
105 cloudflared tunnel run --token-file /tmp/cloudflare-tunnel-token.txt
0 cloudflared cloudflare-tunnel-token.txt
{os.getpid()} cloudflared cloudflare-tunnel-token.txt
bad row
106 /bin/sh -c cloudflared cloudflare-tunnel-token.txt
"""
    def run(args, **kwargs):
        assert args == ["ps", "-eo", "pid,args"]
        assert kwargs["check"] and kwargs["timeout"] == 30
        assert "creationflags" not in kwargs
        return SimpleNamespace(stdout=text)

    calls = []
    def kill(pid, sig):
        calls.append((pid, sig))
        if pid == 104:
            raise ProcessLookupError()
        if pid == 105:
            raise PermissionError()

    monkeypatch.setattr(online.subprocess, "run", run)
    monkeypatch.setattr(os, "kill", kill)
    with caplog.at_level(logging.INFO):
        online._stop_stale_tunnels()
    assert calls == [(pid, signal.SIGTERM) for pid in (101, 104, 105)]
    assert "SIGTERM：101" in caplog.text and "无法终止残留隧道进程 105" in caplog.text


@pytest.mark.parametrize("error", [FileNotFoundError(), subprocess.CalledProcessError(1, "ps"),
                                        subprocess.TimeoutExpired("ps", 30)])
def test_posix_stale_lookup_unavailable(error, monkeypatch, caplog):
    monkeypatch.setattr(sys, "platform", "linux")
    def run(*args, **kwargs):
        raise error
    monkeypatch.setattr(online.subprocess, "run", run)
    monkeypatch.setattr(os, "kill", lambda *a: pytest.fail("查不到进程时不能发信号"))
    online._stop_stale_tunnels()
    assert "无法用 ps 查找残留隧道进程，跳过清理" in caplog.text


def test_windows_stale_tunnels_unchanged(monkeypatch, caplog):
    monkeypatch.setattr(sys, "platform", "win32")
    def run(args, **kwargs):
        assert args[:3] == ["powershell", "-NoProfile", "-Command"]
        assert "Win32_Process" in args[3] and "Name='cloudflared.exe'" in args[3]
        assert "cloudflare-tunnel-token.txt" in args[3] and "Stop-Process" in args[3]
        assert kwargs["creationflags"] == online.NO_WINDOW
        return SimpleNamespace(stdout="101 102")
    monkeypatch.setattr(online.subprocess, "run", run)
    monkeypatch.setattr(os, "kill", lambda *a: pytest.fail("Windows 不走 SIGTERM"))
    with caplog.at_level(logging.INFO):
        online._stop_stale_tunnels()
    assert "关掉了上次留下的隧道进程：101、102" in caplog.text


@pytest.mark.parametrize("platform", ["darwin", "linux"])
def test_posix_process_probe(platform, monkeypatch):
    def run(args, **kwargs):
        assert args == ["ps", "-eo", "comm="]
        assert kwargs["check"] and kwargs["timeout"] == 8
        assert "creationflags" not in kwargs
        return SimpleNamespace(stdout="/usr/bin/Python\n/Applications/My Tool\ncloudflared\n\n")
    monkeypatch.setattr(probe.subprocess, "run", run)
    monkeypatch.setattr(sys, "platform", platform)
    # os.name 是 pathlib 也会读的全局值；只在被测调用期间替换，退出后恢复。
    with monkeypatch.context() as patch:
        patch.setattr(os, "name", "posix")
        names = probe._processes()
        result = probe.probe([{"id": "yes", "process": "PYTHON"}, {"id": "no", "process": "missing"}])
    assert names == {"python", "my tool", "cloudflared"}
    assert result == {"yes": {"state": "running", "how": "进程"}, "no": {"state": "stopped", "how": "进程"}}


@pytest.mark.parametrize("error", [FileNotFoundError(), subprocess.CalledProcessError(1, "ps"),
                                        subprocess.TimeoutExpired("ps", 8)])
def test_posix_probe_unavailable_is_unknown(error, monkeypatch):
    def run(*args, **kwargs):
        raise error
    monkeypatch.setattr(probe.subprocess, "run", run)
    with monkeypatch.context() as patch:
        patch.setattr(os, "name", "posix")
        result = probe.probe([{"id": "app", "process": "python"}])
    assert result == {"app": {"state": "unknown", "how": "没法探：无法读取本机进程列表"}}


def test_windows_process_probe_unchanged(monkeypatch):
    def run(args, **kwargs):
        assert args == ["tasklist", "/FO", "CSV", "/NH"]
        assert kwargs["creationflags"] == getattr(subprocess, "CREATE_NO_WINDOW", 0)
        return SimpleNamespace(stdout=b'"Sample.App.exe","123","Console"\r\n')
    monkeypatch.setattr(probe.subprocess, "run", run)
    with monkeypatch.context() as patch:
        patch.setattr(os, "name", "nt")
        names = probe._processes()
    assert names == {"sample.app.exe", "sample.app"}


def test_backup_config(isolated_config, monkeypatch, tmp_path):
    assert config.backup_dir() is None
    isolated_config.write_text('backup_dir = "from-file"\n', encoding="utf-8")
    assert config.backup_dir() == Path("from-file")
    monkeypatch.setenv("COMPANY_BASE_BACKUP_DIR", str(tmp_path / "backups"))
    assert config.backup_dir() == tmp_path / "backups"
    monkeypatch.setenv("COMPANY_BASE_BACKUP_DIR", " ")
    assert config.backup_dir() is None
    monkeypatch.delenv("COMPANY_BASE_BACKUP_DIR")
    isolated_config.write_text("backup_dir = 123\n", encoding="utf-8")
    with pytest.raises(BaseError, match="backup_dir 要是文字"):
        config.backup_dir()


def test_unconfigured_backup_touches_no_repository(monkeypatch, caplog):
    monkeypatch.setattr(config, "data_dir", lambda: pytest.fail("未配置备份时不读数据目录"))
    monkeypatch.setattr(backup, "_git", lambda *a, **kw: pytest.fail("不能运行 git"))
    monkeypatch.setattr(backup.tempfile, "mkdtemp", lambda **kw: pytest.fail("不能创建临时备份"))
    assert backup.rotate() == {"skipped": "没配 backup_dir，不备份"}
    monkeypatch.setattr(backup, "rotate", lambda: pytest.fail("启动器不能调备份"))
    with caplog.at_level(logging.INFO):
        launcher._backup("不使用")
    assert "没配 backup_dir，不备份" in caplog.text


def test_launcher_configured_backup(configured_data, tmp_path, monkeypatch, caplog):
    monkeypatch.setenv("COMPANY_BASE_BACKUP_DIR", str(tmp_path / "backups"))
    calls = []
    monkeypatch.setattr(backup, "rotate", lambda: calls.append(True) or {"current": "test"})
    with caplog.at_level(logging.INFO):
        launcher._backup(str(tmp_path / "other"))
        assert not calls
        launcher._backup(str(configured_data))
    assert calls == [True]
    assert "不是正式数据仓库，不备份" in caplog.text and '"current": "test"' in caplog.text


@pytest.fixture
def backup_sandbox(configured_data, tmp_path, monkeypatch):
    code = tmp_path / "code"
    code.mkdir()
    git(code, "init", "-q", "-b", "main")
    git(code, "config", "user.name", "test")
    git(code, "config", "user.email", "test@example.com")
    (code / "source.txt").write_text("测试源码\n", encoding="utf-8")
    git(code, "add", ".")
    git(code, "commit", "-qm", "测试初始提交")
    root = tmp_path / "backups"
    monkeypatch.setenv("COMPANY_BASE_BACKUP_DIR", str(root))
    monkeypatch.setattr(backup, "CODE_REPO", code)
    mkdtemp = tempfile.mkdtemp
    monkeypatch.setattr(backup.tempfile, "mkdtemp", lambda **kw: mkdtemp(dir=tmp_path, **kw))
    recycled = []
    monkeypatch.setattr(backup, "_to_recycle_bin", recycled.append)
    return root, code, configured_data, recycled


def test_backup_verification_and_rotation_in_sandbox(backup_sandbox, monkeypatch):
    root, code, data, recycled = backup_sandbox
    monkeypatch.chdir(root.parent)
    monkeypatch.setenv("COMPANY_BASE_BACKUP_DIR", root.name)  # 相对路径不能被 git -C 带到数据仓库里
    first = backup.rotate()
    assert first["current"].startswith("01-current-complete-")
    assert "skipped" in backup.rotate() and len(recycled) == 1
    slot = root / "Company-Base" / first["current"]
    manifest = json.loads((slot / "MANIFEST.json").read_text(encoding="utf-8"))
    assert manifest["verification"]["bundle_verify"] == "pass"
    assert "product_status" not in manifest and "note" not in manifest
    for label in ("data", "code"):
        record = manifest["repos"][label]
        assert record["tracked_files"] > 0
        for entry in record["files"]:
            assert entry["sha256"] == backup._sha256(slot / entry["name"])
    index_path = root / "INDEX.json"
    index = json.loads(index_path.read_text(encoding="utf-8"))
    assert index["projects"][0]["status"] == "CURRENT_VERIFIED_NO_PREVIOUS_YET"
    index["projects"].append({"name": "别的项目", "directory": "other"})
    index_path.write_text(json.dumps(index), encoding="utf-8")
    for n in (2, 3):
        (data / "new.txt").write_text(str(n), encoding="utf-8")
        git(data, "add", ".")
        git(data, "commit", "-qm", f"测试提交 {n}")
        result = backup.rotate()
    assert len(list((root / "Company-Base").glob("01-current-*"))) == 1
    assert len(list((root / "Company-Base").glob("02-previous-*"))) == 1
    assert Path(result["retired"]).parent == root / "_待删除"
    assert (Path(result["retired"]) / "MANIFEST.json").exists()
    index = json.loads(index_path.read_text(encoding="utf-8"))
    assert index["projects"][0]["status"] == "TWO_COMPLETE_VERSIONS_VERIFIED"
    assert index["projects"][1] == {"name": "别的项目", "directory": "other"}
    assert len(recycled) == 3 and all(path.parent == root.parent for path in recycled)


def test_backup_without_code_repo_backs_up_data_only(backup_sandbox, monkeypatch):
    root, code, data, recycled = backup_sandbox
    monkeypatch.setattr(backup, "CODE_REPO", None)
    result = backup.rotate()
    assert result["code"] is None and "pip install" in result["code_not_backed_up"]
    slot = root / "Company-Base" / result["current"]
    manifest = json.loads((slot / "MANIFEST.json").read_text(encoding="utf-8"))
    assert manifest["repos"]["code"] is None
    assert "source_repo" in manifest["code_not_backed_up"]["hint"]
    assert "这次没有备份代码仓库" in (slot / "RESTORE.md").read_text(encoding="utf-8")
    assert [p.name for p in slot.glob("company-base-*")] and all(p.name.startswith("company-base-data-") for p in slot.glob("company-base-*"))
    assert list(slot.glob("company-base-data-*-source.zip"))


def test_backup_uses_configured_source_repo(backup_sandbox, monkeypatch):
    root, code, data, recycled = backup_sandbox
    (code / "pyproject.toml").write_text("[project]\nname = \"demo\"\n", encoding="utf-8")
    git(code, "add", ".")
    git(code, "commit", "-qm", "加 pyproject")
    monkeypatch.setattr(backup, "CODE_REPO", None)
    monkeypatch.setenv("ONESTEAD_SOURCE_REPO", str(code))
    result = backup.rotate()
    slot = root / "Company-Base" / result["current"]
    manifest = json.loads((slot / "MANIFEST.json").read_text(encoding="utf-8"))
    assert manifest["repos"]["code"]["repo"] == str(code.resolve())
    assert "code_not_backed_up" not in manifest
    monkeypatch.setenv("ONESTEAD_SOURCE_REPO", str(root))
    with pytest.raises(SystemExit, match="source_repo"):
        backup.rotate()


def test_failed_verification_keeps_current_slot(backup_sandbox, monkeypatch):
    root, code, data, recycled = backup_sandbox
    first = backup.rotate()
    before = (root / "INDEX.json").read_bytes()
    (data / "new.txt").write_text("新测试提交", encoding="utf-8")
    git(data, "add", ".")
    git(data, "commit", "-qm", "新测试提交")
    def fail(*args):
        raise SystemExit("测试校验失败")
    monkeypatch.setattr(backup, "_pack", fail)
    with pytest.raises(SystemExit, match="测试校验失败"):
        backup.rotate()
    assert (root / "Company-Base" / first["current"]).exists()
    assert (root / "INDEX.json").read_bytes() == before
    assert any(p.name.startswith("_staging-") for p in recycled)
    assert len([p for p in recycled if p.name.startswith("company-base-backup-verify-")]) == 2


@pytest.mark.parametrize("platform", ["win32", "darwin", "linux"])
@pytest.mark.parametrize("failure", [False, True])
def test_recycle_never_permanently_deletes(platform, failure, tmp_path, monkeypatch, caplog):
    monkeypatch.setattr(sys, "platform", platform)
    folder = tmp_path / "校验'目录"
    folder.mkdir()
    calls = []
    def trash(path):
        calls.append(path)
        if failure:
            raise OSError("无法回收")
    def run(args, **kwargs):
        calls.append(args)
        assert args[:3] == ["powershell", "-NoProfile", "-Command"]
        assert "SendToRecycleBin" in args[3] and "校验''目录" in args[3]
        return SimpleNamespace(returncode=1 if failure else 0)
    monkeypatch.setitem(sys.modules, "send2trash", SimpleNamespace(send2trash=trash))
    monkeypatch.setattr(backup.subprocess, "run", run)
    backup._to_recycle_bin(folder)
    assert len(calls) == 1 and folder.exists()
    if platform != "win32":
        assert calls == [str(folder.resolve())]
    assert ("保留在" in caplog.text) == failure
    if failure:
        assert str(folder.resolve()) in caplog.text


def test_missing_send2trash_leaves_temporary_directory(tmp_path, monkeypatch, caplog):
    monkeypatch.setattr(sys, "platform", "linux")
    monkeypatch.setitem(sys.modules, "send2trash", None)
    backup._to_recycle_bin(tmp_path)
    assert tmp_path.exists() and str(tmp_path.resolve()) in caplog.text


@pytest.mark.parametrize("platform", ["win32", "darwin", "linux"])
def test_browser_and_socket_platform_behavior(platform, monkeypatch):
    monkeypatch.setattr(sys, "platform", platform)
    urls, options, addresses = [], [], []
    monkeypatch.setattr(launcher.webbrowser, "open", lambda url: urls.append(url) or True)
    launcher._open({"port": 8764, "token": "test-only"})
    assert urls == ["http://127.0.0.1:8764/open?t=test-only"]
    sock = SimpleNamespace(setsockopt=lambda *a: options.append(a), bind=addresses.append)
    monkeypatch.setattr(socket, "socket", lambda *a: sock)
    if platform == "win32":
        monkeypatch.setattr(socket, "SO_EXCLUSIVEADDRUSE", -5, raising=False)
    else:
        monkeypatch.delattr(socket, "SO_EXCLUSIVEADDRUSE", raising=False)
    assert launcher._bind() is sock
    assert addresses == [("127.0.0.1", launcher.UI_PORT)]
    assert options == ([(socket.SOL_SOCKET, -5, 1)] if platform == "win32" else [])


def test_headless_browser_explains_limitation(monkeypatch, caplog):
    monkeypatch.setattr(sys, "platform", "linux")
    monkeypatch.setattr(launcher.webbrowser, "open", lambda url: False)
    launcher._open({"port": 8764, "token": "test-only"})
    assert "没能自动打开浏览器" in caplog.text and "CLI、MCP 或远程服务" in caplog.text


def test_posix_directory_link_uses_symlink(monkeypatch):
    calls = []
    link = SimpleNamespace(parent=SimpleNamespace(mkdir=lambda **kw: None),
                           symlink_to=lambda target, **kw: calls.append((target, kw)))
    with monkeypatch.context() as patch:
        patch.setattr(os, "name", "posix")
        junction(link, "test-target")
    assert calls == [("test-target", {"target_is_directory": True})]


def test_posix_web_local_path_actions_disabled(client, as_agent, shared, monkeypatch):
    as_agent("claude").register_app({**APP, "open": {"path": str(shared)}})
    monkeypatch.delattr(os, "startfile", raising=False)
    response = client.post("/api/apps/demo-web/open", json={"what": "path"}, headers=WEB_POST_HEADERS)
    assert response.status_code == 501 and "请手动打开登记的路径" in response.json()["error"]
    row = next(row for row in client.get("/api/skills").json()["skills"] if row["id"] == SID)
    path = row["installs"]["claude"]["path"]
    response = client.post("/api/skills/open", json={"id": SID, "path": path}, headers=WEB_POST_HEADERS)
    assert response.status_code == 501 and "手动打开登记的路径" in response.json()["error"]


@pytest.mark.parametrize("platform", ["win32", "darwin", "linux"])
def test_agent_commands_platform_suffix(platform, client, monkeypatch):
    monkeypatch.setattr(sys, "platform", platform)
    prompt = client.get("/api/agents").json()["connect_prompt"]
    suffix = ".exe" if platform == "win32" else ""
    assert str(Path(sysconfig.get_path("scripts")) / ("onestead-mcp" + suffix)) + "，" in prompt
    assert str(Path(sysconfig.get_path("scripts")) / ("onestead" + suffix)) + " <命令>" in prompt
    if platform != "win32":
        assert ".exe" not in prompt
