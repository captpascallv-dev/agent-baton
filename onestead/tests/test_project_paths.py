"""项目 paths：校验、创建、网页保存，以及按当前目录认出项目。"""

import asyncio
import json
import os

import pytest

from onestead.base import CompanyBase
from onestead.cli import run
from onestead.i18n import text
from onestead.redact import documents
from onestead.server import INSTRUCTIONS, build_server
from onestead.store import BaseError, Store, check_meta, make_viewer
from onestead.web.present import project as present_project

from .support import FIXED_NOW, OWNER, change, login, put


def _meta(**extra):
    return {"id": "alpha", "type": "project", "title": "阿尔法", "sensitivity": "internal",
            "roles": [], **extra}


def test_paths_validation_marks_bad_files(repo):
    assert check_meta(_meta(), "project") is None
    assert check_meta(_meta(paths=["C:/Work/Shop", "C:/Work/Shop/notes"]), "project") is None
    assert check_meta(_meta(paths=["\\\\server\\share\\shop"]), "project") is None
    assert check_meta(_meta(paths=["/shop"]), "project") is None
    for bad in ([], "C:/Work/Shop", [""], ["notes"], ["C:/Work/Shop", 1], ["C:/ok", " "]):
        assert check_meta(_meta(paths=bad), "project"), bad

    put(repo, "projects/alpha/project.md", _meta(paths="C:/Work/Shop"), "\n# 阿尔法\n")
    doc = next(d for d in Store(repo).load() if d.rel == "projects/alpha/project.md")
    assert doc.problem and "paths" in doc.problem
    assert not CompanyBase(repo, "claude", now=lambda: FIXED_NOW).company(cwd="C:/Work/Shop").startswith("你现在在")


def test_create_project_and_cli_path(repo, as_agent, capsys):
    base = as_agent("claude")
    base.create_project("shop", "店", "卖茶", paths=["C:/Work/Shop", "C:/Work/Shop/web"])
    made = next(d for d in Store(repo).load() if d.id == "shop")
    assert made.problem is None and made.meta["paths"] == ["C:/Work/Shop", "C:/Work/Shop/web"]
    plain = base.create_project("plain", "普通", "没有目录")
    assert "已新建" in plain
    assert "paths" not in next(d for d in Store(repo).load() if d.id == "plain").meta
    with pytest.raises(BaseError, match="paths"):
        base.create_project("bad-paths", "坏", "坏", paths=["notes"])
    assert not (repo / "projects" / "bad-paths").exists()
    with pytest.raises(BaseError, match="paths"):
        base.create_project("empty-paths", "坏", "坏", paths=[])
    assert not (repo / "projects" / "empty-paths").exists()

    assert run(["project-create", "cli-shop", "--title", "命令行店", "--goal", "从命令行",
                "--path", "C:/Work/Cli", "--path", "C:/Work/Cli/web",
                "--data", str(repo), "--as", "claude"]) == 0
    cli_doc = next(d for d in Store(repo).load() if d.id == "cli-shop")
    assert cli_doc.meta["paths"] == ["C:/Work/Cli", "C:/Work/Cli/web"]
    assert run(["company", "--cwd", "C:/Work/Cli/web", "--data", str(repo), "--as", "claude"]) == 0
    assert "你现在在「命令行店」（`cli-shop`）的目录里。" in capsys.readouterr().out
    assert run(["company", "--cwd", "D:/unrelated", "--data", str(repo), "--as", "claude"]) == 0
    missed = capsys.readouterr().out
    assert "你现在在" not in missed


def test_owner_saves_local_dirs_and_rejects_stale_version(repo):
    owner = CompanyBase(repo, OWNER, now=lambda: FIXED_NOW, allow_owner=True)
    source = owner.doc_source("projects/alpha/project.md")
    field = next(f for f in source["fields"] if f["key"] == "paths")
    assert field["type"] == "lines" and field["label"] == "本机目录" and field["value"] == ""
    owner.save_doc(source["rel"], source["version"], {"paths": "C:/Work/Alpha\n\nC:/Work/Alpha/notes"}, None)
    saved = next(d for d in Store(repo).load() if d.rel == source["rel"])
    assert saved.problem is None
    assert saved.meta["paths"] == ["C:/Work/Alpha", "C:/Work/Alpha/notes"]
    with pytest.raises(BaseError, match="被别人改过"):
        owner.save_doc(source["rel"], source["version"], {"paths": "C:/Work/Other"}, None)
    assert "C:/Work/Other" not in (repo / source["rel"]).read_text(encoding="utf-8")
    again = owner.doc_source(source["rel"])
    with pytest.raises(BaseError, match="paths"):
        owner.save_doc(again["rel"], again["version"], {"paths": "notes"}, None)
    assert owner.doc_source(source["rel"])["version"] == again["version"]
    owner.save_doc(again["rel"], again["version"], {"paths": ""}, None)
    cleared = next(d for d in Store(repo).load() if d.rel == source["rel"])
    assert "paths" not in cleared.meta


def test_company_matches_deepest_visible_directory(repo, as_agent, ext, monkeypatch, tmp_path):
    base = as_agent("claude")
    base.update_project_summary("alpha", {"下一步": "A" * 850, "入口": "看板在这边。"})
    base.update_project_status("alpha", "第一条进展")
    base.update_project_status("alpha", "第二条进展")
    base.update_project_status("alpha", "第三条进展")
    change(repo, "projects/alpha/project.md", paths=["C:/Work/Shop"])
    change(repo, "projects/beta/project.md", paths=["C:\\Work\\Shop\\Site"])
    change(repo, "projects/secret/project.md", paths=["D:/Secret/Only"])

    hit = base.company(cwd="C:/Work/Shop")
    head, _, rest = hit.partition("\n\n")
    assert head.startswith(text("here.located", title="阿尔法", id="alpha"))
    assert "进行中" in head and "测试项目" in head and "老 Lead" in head
    assert "A" * 800 in head and "A" * 801 not in hit
    assert "2026-10-10 第二条进展" in head and "2026-10-10 第三条进展" in head
    assert "第一条进展" not in hit
    assert "get_project('alpha')" in head
    if os.name == "nt":
        assert base.company(cwd="c:\\work\\shop").startswith(text("here.located", title="阿尔法", id="alpha"))
        deep = base.company(cwd="c:/work/shop/site/page")
    else:
        deep = base.company(cwd="C:/Work/Shop/Site/page")
    assert deep.startswith(text("here.located", title="贝塔", id="beta"))

    limited = CompanyBase(repo, "ext-li", now=lambda: FIXED_NOW)
    seen = limited.company(cwd="C:/work/shop/site/page" if os.name == "nt" else "C:/Work/Shop/Site/page")
    assert seen.startswith(text("here.located", title="阿尔法", id="alpha"))
    assert "贝塔" not in seen
    hidden = limited.company(cwd="D:/Secret/Only/notes")
    assert not hidden.startswith("你现在在")
    assert base.company(cwd="D:/Secret/Only").startswith(text("here.located", title="家里的事", id="secret"))

    miss = base.company(cwd="D:/unrelated/place")
    assert miss == base.company(cwd="D:/also/unrelated")
    assert not miss.startswith("你现在在")
    assert hit.endswith(miss) and rest == miss

    page = base.get_project("alpha")
    assert page.index("## 入口") < page.index("本机目录") < page.index("## 最近进展")
    assert "看板在这边。" in page and "`C:/Work/Shop`" in page

    work = tmp_path / "AlphaWork"
    (work / "src").mkdir(parents=True)
    change(repo, "projects/alpha/project.md", paths=[str(work)])
    monkeypatch.chdir(work / "src")
    assert base.company().startswith(text("here.located", title="阿尔法", id="alpha"))
    monkeypatch.chdir(tmp_path)
    assert not base.company().startswith("你现在在")
    monkeypatch.chdir(work / "src")
    remote = CompanyBase(repo, "ext-zhang", require_remote=True)
    assert remote.company() == remote.company(cwd=str(work / "src"))
    assert not remote.company().startswith("你现在在")
    assert str(work) not in remote.company()
    assert str(work) not in remote.get_project("alpha")
    assert "本机目录" not in remote.get_project("alpha")


def test_remote_projection_omits_local_dirs(repo, ext, workspace):
    change(repo, "projects/alpha/project.md", paths=["C:/Work/Shop"])
    login(workspace)
    assert workspace.get("/api/project/alpha").status_code == 200
    assert "C:/Work/Shop" not in workspace.get("/api/project/alpha").text
    assert "C:/Work/Shop" not in workspace.get("/api/doc", params={"rel": "projects/alpha/project.md"}).text

    docs = Store(repo).load()
    viewer = make_viewer(docs, "ext-zhang", remote=True)
    alpha = next(d for d in docs if d.kind == "project" and d.id == "alpha")
    row = present_project(alpha, viewer, docs, [], FIXED_NOW.date())
    assert "paths" not in row and "C:/Work/Shop" not in json.dumps(row, ensure_ascii=False, default=str)
    shown = next(d for d in documents(viewer, docs) if d.kind == "project" and d.id == "alpha")
    assert "paths" not in shown.meta and "C:/Work/Shop" not in shown.raw

    change(repo, "projects/alpha/project.md", sensitivity="public", paths=["C:/Work/Shop"])
    assert "C:/Work/Shop" not in workspace.get("/api/project/alpha").text
    docs = Store(repo).load()
    public = make_viewer(docs, None, remote=True)
    shown = next(d for d in documents(public, docs) if d.kind == "project" and d.id == "alpha")
    assert "paths" not in shown.meta and "C:/Work/Shop" not in shown.raw
    remote = CompanyBase(repo, "ext-zhang", require_remote=True)
    assert "C:/Work/Shop" not in remote.get_project("alpha")
    assert "本机目录" not in remote.get_project("alpha")
    assert remote.company(cwd="C:/Work/Shop") == remote.company(cwd="D:/unrelated")


def test_copy_in_both_languages(repo, as_agent, monkeypatch):
    base = as_agent("claude")
    base.create_project("shop", "Shop", "Sell tea", paths=["C:/Work/Shop"],
                        body="## 下一步\n\nBrew.\n\n## 入口\n\nCounter.\n")
    monkeypatch.setenv("ONESTEAD_LANG", "en")
    page = base.get_project("shop")
    assert "Local directories:" in page and "`C:/Work/Shop`" in page
    assert page.index("## 入口") < page.index("Local directories:")
    hit = base.company(cwd="C:/Work/Shop")
    assert hit.startswith('You are in the directory of "Shop" (`shop`).')
    assert "Status:" in hit and "Goal: Sell tea" in hit and "Next steps: Brew." in hit
    assert "get_project('shop')" in hit
    monkeypatch.setenv("ONESTEAD_LANG", "zh")
    assert base.company(cwd="C:/Work/Shop").startswith("你现在在「Shop」（`shop`）的目录里。")
    assert "本机目录：" in base.get_project("shop")
    assert "目录" in text("here.located", lang="zh", title="Shop", id="shop")
    assert "directory" in text("here.located", lang="en", title="Shop", id="shop")
    assert "项目目录" in text(INSTRUCTIONS, lang="zh", title="N", owner_name="O")
    assert "project directory" in text(INSTRUCTIONS, lang="en", title="N", owner_name="O")


def test_mcp_exposes_cwd_and_paths(repo):
    tools = asyncio.run(build_server(str(repo), "claude").list_tools())
    company = next(tool for tool in tools if tool.name == "company")
    created = next(tool for tool in tools if tool.name == "create_project")
    assert company.input_schema["properties"]["cwd"]["description"]
    assert created.input_schema["properties"]["paths"]["description"]
