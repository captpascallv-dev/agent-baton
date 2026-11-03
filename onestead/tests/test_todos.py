"""待办：agent 和主人都能加、改；每次一个带作者的提交；按日期分组；不认识的字段和项目拒绝。"""

from .support import OWNER

import pytest

from onestead.store import BaseError, Store

from .conftest import FIXED_NOW, git
from .support import WEB_POST_HEADERS as POST_HEADERS


def _todos(repo):
    return {d.title: d for d in Store(repo).load() if d.kind == "todo"}


def test_add_todos_in_one_commit_and_list_groups(as_agent, repo):
    msg = as_agent("claude").add_todos([
        {"title": "逾期的事", "due": "2026-10-01"},
        {"title": "今天开会", "due": "2026-10-10", "time": "14:30", "project": "alpha"},
        {"title": "周末买菜", "due": "2026-10-11"},
        {"title": "有空再说"},
    ], note="从聊天里整理", signed_as="claude")
    assert "已加 4 条" in msg
    assert git(repo, "log", "-1", "--format=%an") == "claude · claude"
    assert git(repo, "log", "-1", "--format=%s") == "待办：整理了 4 条（从聊天里整理）"
    todos = _todos(repo)
    assert todos["今天开会"].meta["time"] == "14:30" and todos["今天开会"].meta["project"] == "alpha"
    assert todos["有空再说"].meta["created_by"] == "claude · claude"

    text = as_agent("codex").list_todos()
    # FIXED_NOW 是 2026-10-10，10-11 在接下来 7 天里
    assert FIXED_NOW.date().isoformat() == "2026-10-10"
    for head in ("## 逾期（1）", "## 今天（1）", "## 接下来 7 天（1）", "## 没定日期（1）"):
        assert head in text
    assert "## 今天" in as_agent("codex").list_todos("today") and "接下来" not in as_agent("codex").list_todos("today")


def test_update_todo_done_and_reopen(as_agent, repo):
    as_agent("claude").add_todos([{"title": "回电话", "due": "2026-10-12"}])
    tid = _todos(repo)["回电话"].id
    as_agent("codex").update_todo(tid, {"status": "done"}, signed_as="alex")
    d = _todos(repo)["回电话"]
    assert d.meta["status"] == "done" and str(d.meta["done_at"]) == "2026-10-10"
    assert git(repo, "log", "-1", "--format=%s") == "待办：做完了「回电话」"
    as_agent("codex").update_todo(tid, {"status": "open", "due": "", "notes": "下周再打"})
    d = _todos(repo)["回电话"]
    assert d.meta["status"] == "open" and "done_at" not in d.meta and "due" not in d.meta
    assert "下周再打" in d.body
    assert git(repo, "log", "-1", "--format=%s") == "待办：重新打开了「回电话」"
    assert as_agent("codex").update_todo(tid, {"notes": "下周再打"}).startswith("没有变化")


@pytest.mark.parametrize("item", [
    {"title": ""}, {"title": "x", "due": "下周"}, {"title": "x", "time": "25:00"},
    {"title": "x", "project": "nope"}, {"title": "x", "color": "red"}, {"title": "x", "status": "maybe"},
    {"title": "密码 password=abcdefgh1234"},
])
def test_bad_todos_are_refused(as_agent, repo, item):
    with pytest.raises(BaseError):
        as_agent("claude").add_todos([item])
    assert not (repo / "todos").exists()


def test_unknown_members_cannot_add_todos(as_agent):
    with pytest.raises(BaseError):
        as_agent("nobody").add_todos([{"title": "x"}])


def test_web_todo_flow_as_owner(client, as_agent, repo):  # noqa: F811
    r = client.post("/api/todos/add", json={"title": "网页上加的", "due": "2026-10-10", "project": "alpha"},
                    headers=POST_HEADERS)
    assert r.status_code == 200
    assert git(repo, "log", "-1", "--format=%an") == OWNER
    data = client.get("/api/todos").json()
    item = next(t for t in data["items"] if t["title"] == "网页上加的")
    assert item["project"] == {"id": "alpha", "name": "阿尔法"} and item["created_by"] == "你"
    assert {p["id"] for p in data["projects"]} >= {"alpha", "secret"}
    r = client.post(f"/api/todos/{item['id']}", json={"status": "done"}, headers=POST_HEADERS)
    assert r.status_code == 200
    rows = client.get("/api/activity").json()["rows"]
    assert rows[0]["type"] == "待办" and rows[0]["what"] == "做完了待办「网页上加的」"
    assert rows[0]["project"]["id"] == "alpha"
    assert client.post("/api/todos/add", json={"title": ""}, headers=POST_HEADERS).status_code == 400
    assert client.post("/api/todos/add", json={"title": "x"}).status_code == 403  # 不是从页面发出的


def test_home_today_module(client, as_agent):  # noqa: F811
    as_agent("claude").add_todos([{"title": "很久以前", "due": "2020-01-01"}, {"title": "下下个月", "due": "2099-01-01"}])
    mods = {m["kind"]: m["data"] for m in client.get("/api/home").json()["modules"]}
    titles = [t["title"] for t in mods["today_todos"]["items"]]
    assert titles == ["很久以前"]


def test_repeating_todo_adds_next_one_and_can_stop(as_agent, repo):
    from onestead.todos import next_due
    import datetime as dt
    claude = as_agent("claude")
    with pytest.raises(BaseError, match="要写日期"):
        claude.add_todos([{"title": "每周对账", "repeat": "weekly"}])
    with pytest.raises(BaseError, match="repeat 只能是"):
        claude.add_todos([{"title": "每周对账", "due": "2026-10-06", "repeat": "biweekly"}])
    claude.add_todos([{"title": "每周对账", "due": "2026-10-06", "time": "09:00", "repeat": "weekly", "notes": "看流水"}])
    first = _todos(repo)["每周对账"]
    msg = claude.update_todo(first.id, {"status": "done"})
    # 10-06 逾期到 10-10 才勾掉：下一次落在今天之后的 10-13，不补出过去的
    assert "下一次 2026-10-13" in msg
    assert git(repo, "log", "-1", "--format=%s") == "待办：做完了「每周对账」（下一次 10-13）"
    docs = [d for d in Store(repo).load() if d.kind == "todo" and d.title == "每周对账"]
    nxt = next(d for d in docs if d.meta.get("status") == "open")
    assert str(nxt.meta["due"]) == "2026-10-13" and nxt.meta["repeat"] == "weekly"
    assert nxt.meta["time"] == "09:00" and nxt.meta["repeat_from"] == first.id and "看流水" in nxt.body
    # 重新打开再勾掉，不会再加一次
    claude.update_todo(first.id, {"status": "open"})
    claude.update_todo(first.id, {"status": "done"})
    assert len([d for d in Store(repo).load() if d.kind == "todo" and d.title == "每周对账"]) == 2
    # 停止重复：这一条之后勾掉不再加
    claude.update_todo(nxt.id, {"repeat": ""})
    assert git(repo, "log", "-1", "--format=%s") == "待办：停了重复「每周对账」"
    claude.update_todo(nxt.id, {"status": "done"})
    assert len([d for d in Store(repo).load() if d.kind == "todo" and d.title == "每周对账"]) == 2
    # 推日期
    today = dt.date(2026, 10, 10)
    assert next_due(dt.date(2026, 10, 9), "weekdays", today) == dt.date(2026, 10, 12)  # 周五往后跳过周末
    assert next_due(dt.date(2026, 8, 31), "monthly", dt.date(2026, 8, 31)) == dt.date(2026, 9, 30)
    assert next_due(dt.date(2026, 10, 10), "daily", today) == dt.date(2026, 10, 11)
    # 每月 30 号：2 月落在 28 号，3 月回到 30 号；每年 3 月 19 号
    assert next_due(dt.date(2027, 1, 30), "monthly", dt.date(2027, 1, 30), 30) == dt.date(2027, 2, 28)
    assert next_due(dt.date(2027, 2, 28), "monthly", dt.date(2027, 2, 28), 30) == dt.date(2027, 3, 30)
    assert next_due(dt.date(2026, 3, 19), "yearly", today, 19) == dt.date(2027, 3, 19)


def test_monthly_repeat_keeps_its_day(as_agent, repo):
    claude = as_agent("claude")
    claude.add_todos([{"title": "评估订阅", "due": "2026-09-30", "repeat": "monthly"}])
    first = _todos(repo)["评估订阅"]
    assert first.meta["repeat_day"] == 30
    claude.update_todo(first.id, {"status": "done"})
    nxt = next(d for d in Store(repo).load() if d.kind == "todo" and d.meta.get("repeat_from") == first.id)
    assert str(nxt.meta["due"]) == "2026-10-30" and nxt.meta["repeat_day"] == 30


def test_web_repeat_round_trip(client, repo):  # noqa: F811
    r = client.post("/api/todos/add", json={"title": "交房租", "due": "2026-10-31", "repeat": "monthly"}, headers=POST_HEADERS)
    assert r.status_code == 200
    item = next(t for t in client.get("/api/todos").json()["items"] if t["title"] == "交房租")
    assert item["repeat"] == "monthly"
    r = client.post(f"/api/todos/{item['id']}", json={"status": "dropped"}, headers=POST_HEADERS)
    assert "下一次 2026-11-30" in r.json()["message"]
