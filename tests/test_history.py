"""查改动按当前可见目标过滤，复用网页的真实 git 读取。"""
import datetime as dt
import json

import pytest

from onestead.base import CompanyBase
from onestead.store import BaseError, Store
from .support import FIXED_NOW, OWNER_NAME, git, put


@pytest.mark.parametrize("target", ["alpha", "projects/alpha/project.md", "company", "sample"])
def test_target_project_relative_handbook_asset(repo, as_agent, target):
    base = as_agent("claude")
    base.update_project_status("alpha", "结果已交付", signed_as="Writer")
    rows = base.history_data(target)
    assert rows and rows[0]["files"]
    if target == "alpha":
        assert rows[0]["tool"] == "update_project_status" and rows[0]["author"] == "claude · Writer"
        assert "结果已交付" in rows[0]["excerpt"]
        assert any(f["path"] == "projects/alpha/project.md" for r in rows for f in r["files"])


def test_decision_lesson_todo_and_tool_parsing(repo, as_agent):
    base = as_agent("claude")
    base.record_decision("选择", "甲", "好", OWNER_NAME, signed_as="Writer")
    base.record_lesson("经验", signed_as="Writer")
    base.add_todos([{"title": "待办"}], signed_as="Writer")
    for kind, tool in (("decision", "record_decision"), ("lesson", "record_lesson"), ("todo", "add_todos")):
        d = next(d for d in Store(repo).load() if d.kind == kind)
        row = base.history_data(d.rel)[0]
        assert row["author"] == "claude · Writer" and row["tool"] == tool
        assert row["files"][0]["path"] == d.rel and row["files"][0]["added"] > 0
        assert row["files"][0]["deleted"] == 0 and len(row["excerpt"]) <= 1500


def test_hidden_missing_and_other_todos_share_message(repo, ext):
    base = ext("ext-li")
    messages = []
    for target in ("beta", "projects/beta/project.md", "missing", "../outside", "todos/2026-10-01-100000-1.md"):
        with pytest.raises(BaseError) as error:
            base.history_data(target)
        messages.append(str(error.value))
    assert len(set(messages)) == 1


def test_own_todo_and_owned_agent_can_read(repo, personal_workspace):
    a = personal_workspace("a")
    a.add_todos([{"title": "私人待办"}])
    todo = next(d for d in Store(repo).load() if d.kind == "todo" and d.meta.get("owner") == "a")
    assert a.history_data(todo.id)
    assert personal_workspace("a-bot").history_data(todo.id)
    with pytest.raises(BaseError):
        personal_workspace("b").history_data(todo.id)


def test_remote_excerpt_masked_limit_and_taipei_time(repo, ext):
    base = ext("claude")
    for i in range(4):
        base.update_project_status("alpha", f"第{i}条 C:/PrivateMark/file.md", signed_as="Writer")
    remote = CompanyBase(repo, "ext-zhang", require_remote=True)
    rows = remote.history_data("alpha", limit=2)
    assert len(rows) == 2 and "excerpt" not in rows[1]
    assert "PrivateMark" not in json.dumps(rows) and "本机路径" in rows[0]["excerpt"]
    expected = dt.datetime.fromisoformat(git(repo, "log", "-1", "--format=%aI")).astimezone(FIXED_NOW.tzinfo).strftime("%Y-%m-%d %H:%M")
    assert rows[0]["when"] == expected


def test_same_id_all_kinds_and_project_archives(repo, as_agent):
    put(repo, "lessons/alpha.md", {"id": "alpha", "type": "lesson", "title": "同名经验", "sensitivity": "internal"}, "\n# 同名经验\n\n## 结论\n\n好\n")
    archive = repo / "projects/alpha/status/2026-09.md"
    archive.parent.mkdir()
    archive.write_text("# 旧进展\n\n结果归档\n", encoding="utf-8")
    (repo / "projects/alpha/status.md").write_text("# 进展\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "记录同名和归档")
    rows = as_agent("claude").history_data("alpha", 1)
    paths = {f["path"] for f in rows[0]["files"]}
    assert "lessons/alpha.md" in paths and "projects/alpha/status/2026-09.md" in paths


def test_path_filter_finds_older_target_and_latest_excerpt_cap(repo, as_agent):
    base = as_agent("claude")
    base.record_lesson("长" * 2000)
    id = next(d.id for d in Store(repo).load() if d.kind == "lesson")
    for i in range(3):
        base.update_project_status("alpha", f"无关{i}")
    rows = base.history_data(id, limit=1)
    assert len(rows) == 1 and rows[0]["tool"] == "record_lesson" and len(rows[0]["excerpt"]) == 1500


def test_unlisted_app_hidden_and_archive_without_current_status(repo, ext):
    from .support import change
    change(repo, "apps/alpha-app.md", listed=False)
    archive = repo / "projects/alpha/status/2026-09.md"
    archive.parent.mkdir()
    archive.write_text("# 归档\n\n已交付。\n", encoding="utf-8")
    # 用本来没有 status.md 的项目验证归档，不删已有夹具。
    archive_beta = repo / "projects/beta/status/2026-09.md"
    archive_beta.parent.mkdir()
    archive_beta.write_text("# 归档\n\n已交付。\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "归档和下架")
    assert any(f["path"] == "projects/beta/status/2026-09.md" for row in ext("claude").history_data("beta") for f in row["files"])
    with pytest.raises(BaseError):
        CompanyBase(repo, "ext-zhang", require_remote=True).history_data("alpha-app")
