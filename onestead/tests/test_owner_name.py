"""显示名、缺标题回退、HTML 转义及历史申请的兼容。"""

import html

import pytest

from onestead.base import CompanyBase, remote_message
from onestead.handover import pending_handovers
from onestead.store import StorageError, Store, owner_name, owner_name_at
from .support import OWNER, OWNER_NAME, change


def test_owner_name_in_local_and_remote_pages(client, workspace):
    assert client.get("/api/me").json()["owner_name"] == OWNER_NAME
    assert f"先问 {OWNER_NAME} 用哪个名字" in client.get("/api/agents").json()["connect_prompt"]
    assert f"用 {OWNER_NAME} 发给你的口令" in workspace.get("/login").text


def test_current_title_is_used_after_edit(repo, client, as_agent):
    change(repo, f"people/{OWNER}.md", title="新的显示名")
    assert client.get("/api/me").json()["owner_name"] == "新的显示名"
    assert "关键角色要 新的显示名 批准" in as_agent("codex").get_project("alpha")


@pytest.mark.parametrize("title", [None, ""])
def test_missing_title_falls_back_to_owner_key(repo, client, title):
    change(repo, f"people/{OWNER}.md", title=title)
    assert owner_name(Store(repo).load()) == OWNER
    assert client.get("/api/me").json()["owner_name"] == OWNER


def test_missing_member_and_unreadable_repo_use_key(repo, tmp_path):
    assert owner_name([]) == OWNER
    assert owner_name_at(tmp_path / "missing") == OWNER
    assert OWNER in remote_message(StorageError("私有路径"), tmp_path / "missing")
    assert "私有路径" not in remote_message(StorageError("私有路径"), repo)


def test_login_escapes_owner_title(repo, workspace):
    title = '<b onclick="run()">显示名 & 测试</b>'
    change(repo, f"people/{OWNER}.md", title=title)
    page = workspace.get("/login")
    assert html.escape(title) in page.text and title not in page.text


def test_old_handover_survives_display_name_change(repo, as_agent):
    as_agent("codex").handover_role("alpha", "项目 Lead", "新负责人", "接任")
    change(repo, f"people/{OWNER}.md", title="新的主人名")
    docs = Store(repo).load()
    assert pending_handovers(docs, {d.id: d for d in docs if d.kind == "project"})[0]["to"] == "新负责人"
    CompanyBase(repo, OWNER, allow_owner=True).decide_handover("alpha", "项目 Lead", "新负责人", True)
    assert "新的主人名 批准" in (repo / "projects/alpha/status.md").read_text(encoding="utf-8")
