"""旧安装无须迁移任务、接入配置或状态；所有文件都在测试临时目录。"""

import importlib
import json
import os
import subprocess
import sys
import tomllib
from pathlib import Path

import pytest

from onestead import config, feishu, online, skillscan, skills, tokens, usage
from onestead.cli import resolve_agent
from onestead.connect import connect_prompt
from onestead.server import build_server
from onestead.store import BaseError

from .test_no_personal_values import test_no_personal_values as scan_personal_values


@pytest.mark.parametrize("old,target", [
    ("online", "onestead.online"), ("web", "onestead.web.launcher"),
    ("server", "onestead.server"), ("cli", "onestead.cli"),
])
def test_old_modules_import_and_execute_new_entry(old, target, tmp_path):
    legacy = importlib.import_module("company_base." + old)
    current = importlib.import_module(target)
    assert legacy.main is current.main
    # 真正执行 -m，但启动前替换新入口，不开网页、服务或隧道。
    (tmp_path / "sitecustomize.py").write_text(
        f"import importlib\nmodule = importlib.import_module({target!r})\n"
        f"module.main = lambda: print({target!r})\n", encoding="utf-8")
    source = Path(__file__).parents[1] / "src"
    env = dict(os.environ, PYTHONPATH=os.pathsep.join([str(tmp_path), str(source)]))
    result = subprocess.run([sys.executable, "-m", "company_base." + old], cwd=tmp_path,
                            env=env, capture_output=True, text=True, encoding="utf-8", timeout=30)
    assert result.returncode == 0, result.stderr
    assert result.stdout.strip() == target


def test_all_console_aliases_share_entrypoints():
    project = tomllib.loads((Path(__file__).parents[1] / "pyproject.toml").read_text(encoding="utf-8"))
    assert project["project"]["name"] == "onestead"
    for suffix, target in (("", "cli:main"), ("-mcp", "server:main"),
                           ("-remote", "server:main_remote"), ("-backup", "backup:main")):
        scripts = project["project"]["scripts"]
        assert scripts["onestead" + suffix] == scripts["company-base" + suffix] == "onestead." + target
    assert project["tool"]["hatch"]["build"]["targets"]["wheel"]["packages"] == ["src/onestead", "src/company_base"]


@pytest.mark.parametrize("suffix", ["CONFIG", "DATA", "PUBLIC_HOST", "OWNER", "STATE_DIR", "AGENT",
    "TOKENS", "USAGE_LOG", "FEISHU_DIR", "SKILL_HOME", "SKILL_SNAPSHOTS", "CLOUDFLARED", "BACKUP_DIR", "NAME"])
def test_environment_alias_priority_and_explicit_empty(suffix, monkeypatch):
    monkeypatch.delenv("ONESTEAD_" + suffix, raising=False)
    monkeypatch.setenv("COMPANY_BASE_" + suffix, "legacy")
    assert config.env(suffix) == "legacy"
    monkeypatch.setenv("ONESTEAD_" + suffix, "current")
    assert config.env(suffix) == "current"
    monkeypatch.setenv("ONESTEAD_" + suffix, "")
    assert config.env(suffix) == ""


def test_environment_consumers_use_central_resolver(tmp_path, monkeypatch):
    from types import SimpleNamespace
    for suffix in ("DATA", "PUBLIC_HOST", "OWNER", "STATE_DIR", "AGENT", "TOKENS", "USAGE_LOG",
                   "FEISHU_DIR", "SKILL_HOME", "CLOUDFLARED", "BACKUP_DIR"):
        monkeypatch.setenv("COMPANY_BASE_" + suffix, "old")
        monkeypatch.setenv("ONESTEAD_" + suffix, "new")
    assert config.data_dir() == config.public_host() == config.owner_key() == "new"
    assert config.state_dir() == config.backup_dir() == config.skill_home() == Path("new")
    assert tokens.store_path() == usage.log_path() == feishu.config_dir() == Path("new")
    assert resolve_agent(SimpleNamespace()) == "new"
    assert resolve_agent(SimpleNamespace(agent="explicit")) == "explicit"
    assert online._cloudflared() == "new"
    monkeypatch.setenv("ONESTEAD_USAGE_LOG", "")
    assert usage.log_path() is None
    monkeypatch.setenv("ONESTEAD_BACKUP_DIR", "")
    assert config.backup_dir() is None


def test_old_config_fallback_and_new_location_priority(tmp_path, monkeypatch):
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    monkeypatch.delenv("COMPANY_BASE_CONFIG")
    legacy = tmp_path / ".company-base" / "config.toml"
    current = tmp_path / ".onestead" / "config.toml"
    assert config.config_path() == current
    legacy.parent.mkdir()
    legacy.write_text('name = "旧配置"\n', encoding="utf-8")
    assert config.config_path() == legacy and config.name() == "旧配置"
    assert not current.exists()
    current.parent.mkdir()
    current.write_text('name = "新配置"\n', encoding="utf-8")
    assert config.config_path() == current and config.name() == "新配置"
    monkeypatch.setenv("COMPANY_BASE_CONFIG", str(legacy))
    assert config.name() == "旧配置"
    monkeypatch.setenv("ONESTEAD_CONFIG", str(current))
    assert config.name() == "新配置"
    monkeypatch.setenv("ONESTEAD_CONFIG", "")
    assert config.config_path() == current  # 新的空值不借用旧变量。


@pytest.mark.parametrize("platform,new_rel,old_rel", [
    ("win32", "Onestead", "CompanyBase"),
    ("darwin", "Library/Application Support/Onestead", "Library/Application Support/CompanyBase"),
    ("linux", ".local/state/onestead", ".local/state/company-base"),
])
def test_state_directory_fallback_without_moving_files(platform, new_rel, old_rel, tmp_path, monkeypatch):
    monkeypatch.setattr(sys, "platform", platform)
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    monkeypatch.delenv("XDG_STATE_HOME", raising=False)
    monkeypatch.delenv("COMPANY_BASE_STATE_DIR")
    current, legacy = tmp_path / new_rel, tmp_path / old_rel
    assert config.state_dir() == current
    legacy.mkdir(parents=True)
    marker = legacy / "state-marker.txt"
    marker.write_text("测试状态", encoding="utf-8")
    assert config.state_dir() == legacy
    assert marker.read_text(encoding="utf-8") == "测试状态" and not current.exists()
    current.mkdir()
    assert config.state_dir() == current
    assert marker.is_file() and not (current / marker.name).exists()
    monkeypatch.setenv("ONESTEAD_STATE_DIR", str(tmp_path / "explicit"))
    assert config.state_dir() == tmp_path / "explicit"


def test_name_default_and_configured_pages(client, as_agent, isolated_config, repo):
    assert config.name() == "Onestead"
    assert "<title>Onestead</title>" in client.get("/").text
    assert as_agent("codex").company().startswith("# Onestead · 入职说明")
    assert build_server(str(repo), "codex").instructions.startswith("Onestead（简称 OS）：")
    isolated_config.write_text('name = "协作站 <团队>"\n', encoding="utf-8")
    index = client.get("/").text
    assert "<title>协作站 &lt;团队&gt;</title>" in index
    assert 'id="brand-name">协作站 &lt;团队&gt;</span>' in index
    assert client.get("/api/me").json()["workspace_name"] == "协作站 <团队>"
    assert as_agent("codex").company().startswith("# 协作站 <团队>（Onestead） · 入职说明")
    prompt = connect_prompt("老板")
    assert "协作站 <团队>（Onestead）" in prompt and "简称 OS" in prompt
    server = build_server(str(repo), "codex")
    assert server.name == "company-base"  # 对外服务名不改。
    assert "协作站 <团队>" in server.instructions and "简称 OS" in server.instructions
    from onestead.web.auth import RemoteAuth
    page = RemoteAuth(repo, "base.example.test").login_page()
    assert "登录协作站 &lt;团队&gt;" in page.body.decode()


def test_partner_name_scan():
    scan_personal_values()


def test_generic_snapshot_layout_and_install_hints(tmp_path, monkeypatch):
    root = tmp_path / "snapshots"
    monkeypatch.setenv("ONESTEAD_SKILL_SNAPSHOTS", json.dumps([
        {"key": "assistant", "name": "远程环境", "root": str(root),
         "layout": {"type": "latest-subdir", "pattern": "skills-*", "subpaths": ["export/user", "export/vendor"]}},
        {"key": "flat", "name": "普通快照", "root": str(tmp_path / "flat")},
    ]))
    assert skillscan.snapshot_roots(config.skill_snapshots()[0]) == []
    (root / "skills-2026-10-12").mkdir(parents=True)
    (root / "skills-2026-10-13").mkdir()
    (root / "ignore-2099").mkdir()
    (root / "skills-2099-file").write_text("不是目录", encoding="utf-8")
    table = {row["key"]: row for row in skillscan.harness_table()}
    assert table["assistant"]["user"] == [root / "skills-2026-10-13" / "export" / p for p in ("user", "vendor")]
    assert table["flat"]["user"] == [tmp_path / "flat"]
    assert skills.install_dir("assistant") is None and skills.install_dir("flat") is None
    assert "维护者" in skills.install_hint("assistant")


@pytest.mark.parametrize("layout", ["latest-subdir", {"type": "unknown"},
    {"type": "latest-subdir", "pattern": "../*"},
    {"type": "latest-subdir", "subpaths": ["../outside"]},
    {"type": "latest-subdir", "subpaths": ["C:\\outside"]},
    {"type": "latest-subdir", "subpaths": []},
])
def test_bad_snapshot_layout_is_rejected(layout, monkeypatch):
    monkeypatch.setenv("ONESTEAD_SKILL_SNAPSHOTS", json.dumps([
        {"key": "assistant", "name": "远程环境", "root": "snapshots", "layout": layout}]))
    with pytest.raises(BaseError, match="layout"):
        config.skill_snapshots()
