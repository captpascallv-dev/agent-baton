"""onestead init：临时目录里建仓库和配置，不碰真实主目录。"""

import os
import sysconfig
import tomllib
from pathlib import Path

from onestead.base import CompanyBase
from onestead.cli import run
from onestead.connect import connect_prompt, tool_path
from onestead.store import Store

from .support import git


def _init(root: Path, *extra: str) -> int:
    return run(["init", str(root), "--owner", "ada", "--name", "Ada Lovelace", *extra])


def _section(text: str, heading: str) -> str:
    body = text.split(f"## {heading}", 1)[1]
    if "\n## " in body:
        body = body.split("\n## ", 1)[0]
    return body


def test_new_directory_loads_and_owner_can_read(tmp_path, monkeypatch, capsys):
    monkeypatch.delenv("COMPANY_BASE_OWNER", raising=False)
    monkeypatch.delenv("COMPANY_BASE_DATA", raising=False)
    root = tmp_path / "company"
    assert _init(root) == 0
    out = capsys.readouterr().out

    docs = Store(root).load()
    assert docs and all(d.problem is None for d in docs)
    assert {d.rel for d in docs} == {
        "FORMAT.md",
        "handbook/company.md",
        "handbook/collaboration.md",
        "people/ada.md",
        "projects/getting-started/project.md",
        "projects/getting-started/status.md",
    }
    person = next(d for d in docs if d.kind == "person")
    assert person.meta["agent_key"] == "ada"
    assert person.meta["kind"] == "human"
    assert person.meta["access_mode"] == "full"
    assert person.meta["can_write"] is True
    assert person.meta["title"] == "Ada Lovelace"
    assert "owner" not in person.meta
    project = next(d for d in docs if d.kind == "project")
    assert project.id == "getting-started"
    assert project.meta["state"] == "进行中"
    for heading in ("## 目标", "## 现状", "## 下一步", "## 分工", "## 入口"):
        assert heading in project.body

    listed = set(git(root, "ls-files").splitlines())
    for rel in ("decisions/.gitkeep", "assets/.gitkeep", "apps/.gitkeep",
                "todos/.gitkeep", "home/.gitkeep", "skills/.gitkeep"):
        assert rel in listed
    assert git(root, "rev-list", "--count", "HEAD") == "1"
    assert git(root, "log", "-1", "--format=%an|%ae") == "Ada Lovelace|ada@agents.company-base.local"

    base = CompanyBase(root, "ada", allow_owner=True)
    assert base._ctx()[0].is_owner
    assert "可以写入" in base.whoami()
    assert "getting-started" in base.company()
    assert "Onestead keeps" in base.company()
    assert out.rstrip().splitlines()[-1].startswith("下一步：")
    assert "python -m onestead.web" in out and "company()" in out


def test_nonempty_directory_is_refused(tmp_path, capsys):
    root = tmp_path / "company"
    root.mkdir()
    (root / "keep.txt").write_text("stay", encoding="utf-8")
    assert _init(root) == 2
    err = capsys.readouterr().err
    assert "拒绝初始化" in err
    assert (root / "keep.txt").read_text(encoding="utf-8") == "stay"
    assert not (root / ".git").exists()
    assert not (root / "handbook").exists()


def test_existing_config_is_kept_until_force(tmp_path, isolated_config, capsys):
    isolated_config.write_text('data_dir = "keep-me"\nowner_key = "keep"\n', encoding="utf-8")
    first = tmp_path / "first"
    assert _init(first) == 0
    assert "已有配置，要改请手动编辑或加 --force-config" in capsys.readouterr().out
    assert isolated_config.read_text(encoding="utf-8") == 'data_dir = "keep-me"\nowner_key = "keep"\n'
    assert not list(isolated_config.parent.glob("config.toml.bak-*"))

    second = tmp_path / "second"
    assert _init(second, "--force-config") == 0
    saved = list(isolated_config.parent.glob("config.toml.bak-*"))
    assert len(saved) == 1
    assert saved[0].read_text(encoding="utf-8") == 'data_dir = "keep-me"\nowner_key = "keep"\n'
    written = tomllib.loads(isolated_config.read_text(encoding="utf-8"))
    assert written["owner_key"] == "ada"
    assert written["data_dir"] == str(second.resolve())
    assert "已把原来的配置改名为" in capsys.readouterr().out


def test_both_languages_load(tmp_path, capsys):
    for lang, needle, title in (
        ("en", "Onestead keeps", "Getting started"),
        ("zh", "公司事实", "开始使用"),
    ):
        root = tmp_path / lang
        assert run(["init", str(root), "--owner", "lin", "--name", "林", "--lang", lang]) == 0
        docs = Store(root).load()
        assert docs and all(d.problem is None for d in docs)
        assert needle in (root / "handbook" / "company.md").read_text(encoding="utf-8")
        project = (root / "projects" / "getting-started" / "project.md").read_text(encoding="utf-8")
        assert f"title: {title}" in project
        assert "state: 进行中" in project
        for heading in ("## 目标", "## 现状", "## 下一步", "## 分工", "## 入口"):
            assert heading in project
        base = CompanyBase(root, "lin", allow_owner=True)
        assert "getting-started" in base.company()
        assert git(root, "log", "-1", "--format=%an") == "林"
    capsys.readouterr()


def test_connect_instructions_use_this_interpreter(tmp_path, capsys):
    root = tmp_path / "company"
    assert _init(root) == 0
    out = capsys.readouterr().out
    mcp = str(tool_path("onestead-mcp"))
    parent = str(Path(sysconfig.get_path("scripts")))
    assert parent in mcp
    assert connect_prompt("Ada Lovelace").rstrip() in out
    for heading in ("Claude Code", "Codex", "Cursor", "ChatGPT"):
        section = _section(out, heading)
        assert mcp in section
        assert parent in section
    chatgpt = _section(out, "ChatGPT")
    assert "https://" not in chatgpt
    assert "deploy.md" in chatgpt
    assert "onestead-mcp.exe" in mcp if os.name == "nt" else not mcp.endswith(".exe")
