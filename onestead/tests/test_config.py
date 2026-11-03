"""配置优先级、部署入口与本机状态隔离。"""

import os
import sys
import sysconfig
from pathlib import Path
from types import SimpleNamespace

import pytest

from onestead import config, feishu, skillscan, tokens, usage
from onestead.cli import resolve_data, run
from onestead.store import BaseError


def test_config_file_and_environment(isolated_config, monkeypatch):
    isolated_config.write_text('data_dir = "data-file"\npublic_host = "file.example.test"\nowner_key = "chief"\nstate_dir = "state-file"\nskill_snapshots = [{key="extra", name="额外", root="skills-file"}]\n', encoding="utf-8")
    for env in ("PUBLIC_HOST", "OWNER", "STATE_DIR"):
        monkeypatch.delenv("COMPANY_BASE_" + env, raising=False)
    assert config.data_dir() == "data-file"
    assert config.public_host() == "file.example.test"
    assert config.owner_key() == "chief"
    assert config.state_dir() == Path("state-file")
    assert config.skill_snapshots() == [{"key": "extra", "name": "额外", "root": "skills-file"}]
    for env, value in (("DATA", "data-env"), ("PUBLIC_HOST", "env.example.test"), ("OWNER", "boss"), ("STATE_DIR", "state-env")):
        monkeypatch.setenv("COMPANY_BASE_" + env, value)
    monkeypatch.setenv("COMPANY_BASE_SKILL_SNAPSHOTS", '[{"key":"env","name":"环境","root":"skills-env"}]')
    assert config.data_dir() == "data-env"
    assert config.public_host() == "env.example.test"
    assert config.owner_key() == "boss"
    assert config.state_dir() == Path("state-env")
    assert config.skill_snapshots()[0]["key"] == "env"
    assert resolve_data(SimpleNamespace(data="explicit")) == "explicit"


def test_config_file_edits_are_read_again(isolated_config, monkeypatch):
    monkeypatch.delenv("COMPANY_BASE_OWNER", raising=False)
    isolated_config.write_text('owner_key = "chief"\n', encoding="utf-8")
    assert config.owner_key() == "chief"
    isolated_config.write_text('owner_key = "boss"\n', encoding="utf-8")
    assert config.owner_key() == "boss"
    # 同样大小的内容只靠修改时间区分
    before = isolated_config.stat()
    isolated_config.write_text('owner_key = "keep"\n', encoding="utf-8")
    os.utime(isolated_config, ns=(before.st_atime_ns, before.st_mtime_ns + 1_000_000_000))
    assert config.owner_key() == "keep"
    isolated_config.write_text('owner_key = \n', encoding="utf-8")
    with pytest.raises(BaseError, match="TOML"):
        config.owner_key()


def test_missing_data_does_not_fall_back(capsys):
    with pytest.raises(BaseError, match="data_dir.*COMPANY_BASE_CONFIG"):
        resolve_data(SimpleNamespace())
    assert run(["company", "--as", "codex"]) == 2
    assert "COMPANY_BASE_DATA" in capsys.readouterr().err


def test_data_default_and_explicit_root(configured_data, capsys):
    assert resolve_data(SimpleNamespace()) == str(configured_data)
    assert run(["company", "--as", "codex"]) == 0
    assert run(["company", "--root", str(configured_data), "--as", "codex"]) == 0
    assert "测试公司" in capsys.readouterr().out


def test_state_dir_and_old_overrides(tmp_path, monkeypatch):
    folder = tmp_path / "state"
    for env in ("TOKENS", "USAGE_LOG", "FEISHU_DIR"):
        monkeypatch.delenv("COMPANY_BASE_" + env)
    assert tokens.store_path() == folder / "tokens.json"
    assert usage.log_path() == folder / "usage.jsonl"
    assert feishu.config_dir() == folder
    monkeypatch.setenv("COMPANY_BASE_TOKENS", str(tmp_path / "override.json"))
    monkeypatch.setenv("COMPANY_BASE_USAGE_LOG", "")
    monkeypatch.setenv("COMPANY_BASE_FEISHU_DIR", str(tmp_path / "feishu"))
    assert tokens.store_path() == tmp_path / "override.json"
    assert usage.log_path() is None
    assert feishu.config_dir() == tmp_path / "feishu"
    monkeypatch.delenv("COMPANY_BASE_STATE_DIR")
    monkeypatch.setattr(sys, "platform", "win32")  # 这一段守住新安装的 Windows 默认目录
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    assert config.state_dir() == tmp_path / "Onestead"
    monkeypatch.delenv("LOCALAPPDATA")
    monkeypatch.setattr(Path, "home", lambda: tmp_path / "home")
    assert config.state_dir() == tmp_path / "home" / "Onestead"


def test_missing_public_host(client, configured_data, monkeypatch):
    from onestead import online
    from onestead.server import remote_app

    monkeypatch.delenv("COMPANY_BASE_PUBLIC_HOST")
    assert client.get("/api/members").json()["remote"] == {"web": "还没配对外域名", "mcp": "还没配对外域名"}
    with pytest.raises(BaseError, match="public_host"):
        remote_app(str(configured_data), 8765)
    with pytest.raises(BaseError, match="public_host"):
        online.main()


def test_snapshot_configuration(isolated_config, monkeypatch, tmp_path):
    assert not any(row.get("snapshot") for row in skillscan.harness_table())
    root = tmp_path / "snapshots"
    isolated_config.write_text(f'skill_snapshots = [{{key="extra", name="额外", root="{root.as_posix()}"}}]', encoding="utf-8")
    assert next(row for row in skillscan.harness_table() if row["key"] == "extra") == {"key": "extra", "name": "额外", "snapshot": True, "user": [root]}
    monkeypatch.setenv("ONESTEAD_SKILL_SNAPSHOTS", "[]")
    assert not any(row.get("snapshot") for row in skillscan.harness_table())


def test_default_config_location(tmp_path, monkeypatch):
    monkeypatch.delenv("COMPANY_BASE_CONFIG")
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    assert config.config_path() == tmp_path / ".onestead" / "config.toml"
    assert config.load() == {}


def test_bad_config_fails_clearly(isolated_config):
    isolated_config.write_text("invalid = [", encoding="utf-8")
    with pytest.raises(BaseError, match="TOML"):
        config.load()


def test_launcher_and_online_use_configured_state(tmp_path):
    from onestead import online
    from onestead.web import launcher

    folder = tmp_path / "state"
    folder.mkdir()
    (folder / "ui-state.json").write_text('{"pid":123}', encoding="utf-8")
    (folder / "tunnel-protocol.txt").write_text("http2", encoding="utf-8")
    assert launcher._read_state() == {"pid": 123}
    assert online._last_protocol() == "http2"


def test_agent_connect_command_uses_current_python(client):
    import sys

    prompt = client.get("/api/agents").json()["connect_prompt"]
    suffix = ".exe" if sys.platform == "win32" else ""
    assert str(Path(sysconfig.get_path("scripts")) / ("onestead-mcp" + suffix)) in prompt
