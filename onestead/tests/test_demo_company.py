"""示例公司：格式能读、冷启动事实能读到、外部合作方的范围、目录里没有像密钥的字符串。"""

import re
import shutil
from collections import Counter
from pathlib import Path

import pytest

from onestead.base import CompanyBase
from onestead.store import BaseError, Store
from onestead.textutil import SECRET_CONTENT, SECRET_PATTERNS

from .support import git

DEMO = Path(__file__).resolve().parents[1] / "examples" / "demo-company"
PROJECTS = {"website-relaunch", "wholesale", "packaging", "tea-lab", "holiday-tins"}
NORA_PROJECTS = {"packaging", "holiday-tins"}
HOSTED_REASON = "A custom cart would miss the 2 November launch, and a hosted checkout keeps card numbers off our servers."
WHOLESALE_REASON = "Three cafes asked for net-30 terms, and we cannot carry that before the holiday rush."
NEXT_STEP = "Photograph the winter tins after they arrive on 20 October 2026, then publish the catalog and open the shop on 2 November 2026."
SUPPLIER_ITEM = "Hearthleaf supplier portal"
OPEN_TODOS = (
    "Confirm the 2 November launch checklist",
    "Send Nora the matte black swatch photo",
)
DONE_TODO = "File the holiday tin sell-through note"
HOME_LINE = (
    "现在从上往下是：`today_todos`（今天的待办） → `projects`（项目健康） → "
    "`apps`（常用应用） → `recent`（最近动态）"
)


def listed_ids(text: str) -> list[str]:
    return re.findall(r"(?m)^\| `([^`]+)` \|", text)


def demo_repo(tmp_path: Path) -> Path:
    assert DEMO.is_dir()
    assert not (DEMO / ".git").exists()
    root = tmp_path / "hearthleaf"
    shutil.copytree(DEMO, root)
    git(root.parent, "init", "-q", "-b", "main", str(root))
    git(root, "config", "user.name", "test")
    git(root, "config", "user.email", "test@example.com")
    git(root, "config", "core.autocrlf", "false")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "Hearthleaf sample")
    return root


def test_demo_company_loads_without_format_problems(tmp_path):
    root = demo_repo(tmp_path)
    docs = Store(root).load()
    bad = [(d.rel, d.problem) for d in docs if d.problem]
    assert bad == []

    counts = Counter(d.kind for d in docs)
    assert counts["handbook"] == 3
    assert counts["person"] == 8
    assert counts["project"] == 5
    assert counts["project-status"] == 5
    assert counts["project-links"] == 1
    assert counts["decision"] == 6
    assert counts["asset"] == 3
    assert counts["app"] == 3
    assert counts["todo"] == 3
    assert counts["home"] == 1

    people = {d.meta["agent_key"]: d for d in docs if d.kind == "person"}
    titles = {d.title for d in people.values()}
    humans = {key for key, d in people.items() if d.meta["kind"] == "human"}
    assert humans == {"alex", "bri", "cam", "nora"}
    assert people["nora"].meta["access_mode"] == "assigned"
    assert people["nora"].meta["can_write"] is False
    assert set(people["nora"].meta["projects"]) == NORA_PROJECTS
    assert people["nora"].meta["assets"] == ["brand-kit"]
    assert "projects" not in people["nora-assistant"].meta
    for key in ("claude-code", "codex", "chatgpt", "nora-assistant"):
        assert people[key].meta["kind"] == "agent"
        assert people[key].meta["owner"] in humans

    projects = {d.id: d for d in docs if d.kind == "project"}
    assert set(projects) == PROJECTS
    assert projects["holiday-tins"].meta["state"] == "已结束"
    assert len({d.meta["state"] for d in projects.values()}) == 5
    for project in projects.values():
        for role in project.meta["roles"]:
            assert role["who"] in titles

    for doc in docs:
        if doc.kind in ("decision", "app", "todo") and doc.meta.get("project"):
            assert doc.meta["project"] in projects
        if doc.kind == "decision":
            assert doc.meta["decided_by"] in titles
        for name in doc.meta.get("visible_to") or []:
            assert name in people


def test_full_agent_reads_cold_start_facts(tmp_path):
    """题目 1–4、7–12 里全读身份用读工具就能核对的事实。"""
    root = demo_repo(tmp_path)
    claude = CompanyBase(root, "claude-code")

    company = claude.company()
    assert "Hearthleaf" in company
    assert "update_project_status" in company
    assert set(listed_ids(claude.list_projects())) == PROJECTS

    handbook = claude.get_handbook("collaboration")
    assert "company()" in handbook
    assert "update_project_status" in handbook
    assert "packaging" in handbook and "holiday-tins" in handbook
    assert "supplier-terms" in handbook
    format_doc = claude.get_handbook("format")
    assert "docs/data-format.md" in format_doc
    assert "已完成" not in format_doc

    page = claude.get_project("website-relaunch")
    assert "进行中" in page
    assert "| Project lead | Alex |" in page
    assert "| Site builder | Codex |" in page
    assert "last year's photos" in page
    assert NEXT_STEP in page
    assert "C:/hearthleaf/launch-checklist.md" in page

    decisions = claude.list_decisions()
    assert "共 6 条" in decisions
    assert HOSTED_REASON in decisions
    assert WHOLESALE_REASON in decisions
    assert "matte black matches the brand kit" in decisions
    assert "will not be reprinted" in decisions
    assert "label no longer matched the leaf" in decisions
    assert "two weeks for photos" in decisions
    wholesale = claude.get_project("wholesale")
    assert "暂停" in wholesale
    assert "net-30" in wholesale

    asset = claude.get_asset("supplier-terms")
    assert SUPPLIER_ITEM in asset
    assert "password manager" in asset
    assert "限定" in asset
    assert "Nora cannot read this file" in asset
    for pattern in SECRET_PATTERNS:
        assert pattern.search(asset) is None

    todos = claude.list_todos()
    for title in OPEN_TODOS:
        assert title in todos
    assert DONE_TODO not in todos
    assert DONE_TODO in claude.list_todos("all")

    apps = claude.list_apps()
    assert {"shop-preview", "blend-board", "tin-tracker"} <= set(listed_ids(apps))
    tracker = claude.get_app("tin-tracker")
    assert "https://tins.hearthleaf.example.test" in tracker
    assert "packaging" in tracker

    home = claude.home_layout()
    layout, _, _catalog = home.partition("可用的首页模块")
    assert HOME_LINE in layout
    assert "shop-preview" in layout and "tin-tracker" in layout
    assert '"limit": 8' in layout
    assert "shared_skills" not in layout


def test_assigned_partner_scope_and_personal_isolation(tmp_path):
    """题目 5、6、10、11、12：外部合作方的项目和限定资产，以及别人的待办和首页。"""
    root = demo_repo(tmp_path)
    nora = CompanyBase(root, "nora")
    assistant = CompanyBase(root, "nora-assistant")
    chatgpt = CompanyBase(root, "chatgpt")

    assert set(listed_ids(nora.list_projects())) == NORA_PROJECTS
    assert set(listed_ids(assistant.list_projects())) == NORA_PROJECTS
    with pytest.raises(BaseError, match="找不到，或者你没有权限查看"):
        nora.get_project("website-relaunch")
    with pytest.raises(BaseError, match="找不到，或者你没有权限查看"):
        nora.get_asset("supplier-terms")
    with pytest.raises(BaseError, match="找不到，或者你没有权限查看"):
        assistant.get_asset("supplier-terms")
    assert "Hearthleaf Brand Kit" in nora.get_asset("brand-kit")
    assert set(listed_ids(nora.list_assets())) == {"brand-kit"}
    assert set(listed_ids(nora.list_apps())) == {"tin-tracker"}

    visible_decisions = nora.list_decisions()
    assert "共 2 条" in visible_decisions
    assert "matte black matches the brand kit" in visible_decisions
    assert "will not be reprinted" in visible_decisions
    assert "card numbers" not in visible_decisions
    assert "net-30" not in visible_decisions

    assert SUPPLIER_ITEM in chatgpt.get_asset("supplier-terms")
    assert set(listed_ids(chatgpt.list_projects())) == PROJECTS
    for title in OPEN_TODOS:
        assert title not in chatgpt.list_todos("all")
        assert title not in nora.list_todos("all")
    assert "shop-preview" not in chatgpt.home_layout()
    assert "shared_skills" in chatgpt.home_layout()
    assert "shop-preview" not in nora.home_layout()


def test_demo_tree_has_no_secret_shaped_text():
    files = [path for path in DEMO.rglob("*") if path.is_file()]
    assert files
    assert {path.suffix for path in files} == {".md", ".toml"}
    config_example = (DEMO / "config.example.toml").read_text(encoding="utf-8")
    assert 'owner_key = "alex"' in config_example
    assert "public_host" not in config_example
    assert not any(".git" in path.parts for path in files)
    for path in files:
        text = path.read_text(encoding="utf-8")
        for pattern in SECRET_PATTERNS:
            assert pattern.search(text) is None, path.name
        assert SECRET_CONTENT.search(text) is None, path.name
