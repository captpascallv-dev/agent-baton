"""接入命令使用当前 Python 环境的 scripts 目录，不假定命令与解释器相邻。"""

import sys
import sysconfig

import pytest

from onestead.connect import connect_prompt, tool_path


@pytest.mark.parametrize("platform,interpreter,scripts", [
    ("win32", "venv/Scripts/python.exe", "venv/Scripts"),
    ("win32", "Python/python.exe", "Python/Scripts"),
    ("darwin", "venv/bin/python", "venv/bin"),
    ("darwin", "Python.framework/Versions/3.12/Python", "Python.framework/Versions/3.12/bin"),
    ("linux", "venv/bin/python", "venv/bin"),
    ("linux", "usr/bin/python3", "usr/local/bin"),
])
def test_tool_path_uses_scripts_for_each_install(platform, interpreter, scripts, tmp_path, monkeypatch):
    scripts_dir = tmp_path / scripts
    monkeypatch.setattr(sys, "platform", platform)
    monkeypatch.setattr(sys, "executable", str(tmp_path / interpreter))
    def get_path(name):
        assert name == "scripts"
        return str(scripts_dir)
    monkeypatch.setattr(sysconfig, "get_path", get_path)
    suffix = ".exe" if platform == "win32" else ""
    for name in ("onestead-mcp", "onestead"):
        expected = scripts_dir / (name + suffix)
        assert tool_path(name) == expected
        assert str(expected) in connect_prompt("Sample Owner")


def test_web_connect_prompt_uses_current_scripts(client, tmp_path, monkeypatch):
    scripts_dir = tmp_path / "Python" / "Scripts"
    monkeypatch.setattr(sys, "platform", "win32")
    monkeypatch.setattr(sys, "executable", str(tmp_path / "Python" / "python.exe"))
    monkeypatch.setattr(sysconfig, "get_path", lambda name: str(scripts_dir))
    prompt = client.get("/api/agents").json()["connect_prompt"]
    assert str(scripts_dir / "onestead-mcp.exe") in prompt
    assert str(scripts_dir / "onestead.exe") in prompt
    assert str(tmp_path / "Python" / "onestead-mcp.exe") not in prompt
