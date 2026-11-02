"""Requests share todo storage, permissions, and writes across all entry points."""

import asyncio
import datetime as dt
import io
import json
import re
import sys

import pytest

from onestead import todos
from onestead.cli import run
from onestead.server import build_server
from onestead.store import BaseError, NotFound, Store, TAIPEI
from .support import FIXED_NOW, OWNER, OWNER_NAME, WEB_POST_HEADERS, git, login, signed


REQUEST = {"title": "请重新登录供应商平台", "ask": "act",
           "context": "整理采购单时发现平台登录已过期。", "why": "登录需要本人的扫码确认。",
           "if_skipped": "采购单暂时无法提交。", "action": "打开平台扫码登录，再告诉采购伙伴。"}


def records(repo):
    return {d.title: d for d in Store(repo).load() if d.kind == "todo"}


def add(base, **changes):
    ids, _ = base._add_todos_commit([REQUEST | changes], signed_as="Morgan")
    return ids[0]


@pytest.fixture
def wire(repo, monkeypatch):
    def call(method, params=None):
        source = json.dumps({"method": method, "params": params or {}}, ensure_ascii=False).encode("utf-8")
        incoming = io.TextIOWrapper(io.BytesIO(source), encoding="utf-8")
        outgoing = io.TextIOWrapper(io.BytesIO(), encoding="utf-8")
        with monkeypatch.context() as patch:
            patch.setattr(sys, "stdin", incoming)
            patch.setattr(sys, "stdout", outgoing)
            code = run(["api", "--data", str(repo), "--as", "claude"])
            output = outgoing.buffer.getvalue()
        assert output.count(b"\n") == 1
        response = json.loads(output)
        assert code == (0 if response["ok"] else 1)
        return response
    return call


def test_registered_file_and_commit(as_agent, repo):
    tid = add(as_agent("claude"), ask="approve", project="alpha", notes="## 私下备注\n稍后再问")
    doc = records(repo)[REQUEST["title"]]
    assert doc.id == tid and doc.meta["created_by"] == "claude · Morgan"
    assert all(doc.meta[k] == REQUEST[k] for k in todos.ASK_FIELDS)
    assert doc.meta["ask"] == "approve"
    assert not {"notified_by", "notified_at", "notice"} & doc.meta.keys()
    assert "### 私下备注" in todos.notes_of(doc)
    assert REQUEST["context"] not in doc.body
    assert doc.raw.index("project:") < doc.raw.index("ask:") < doc.raw.index("context:") < doc.raw.index("created_by:")
    assert git(repo, "log", "-1", "--format=%an") == "claude · Morgan"
    assert git(repo, "log", "-1", "--format=%s") == "待办：登记「请重新登录供应商平台」（要主人批）"


@pytest.mark.parametrize("field", todos.ASK_FIELDS)
@pytest.mark.parametrize("value", [None, "", " \n "])
def test_required_explanations(as_agent, repo, field, value):
    item = dict(REQUEST)
    if value is None:
        item.pop(field)
    else:
        item[field] = value
    before = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError):
        as_agent("claude").add_todos([item])
    assert git(repo, "rev-parse", "HEAD") == before


@pytest.mark.parametrize("field", todos.ASK_FIELDS)
def test_explanations_only_with_ask(as_agent, field):
    with pytest.raises(BaseError):
        as_agent("claude").add_todos([{"title": "普通事", field: "解释"}])


@pytest.mark.parametrize("field", todos.ASK_FIELDS)
def test_explanation_limits_and_types(as_agent, repo, field):
    base = as_agent("claude")
    tid = add(base, **{field: "字" * todos.ASK_FIELDS[field]})
    for value in ("字" * (todos.ASK_FIELDS[field] + 1), [], 42, None):
        with pytest.raises(BaseError):
            base.update_todo(tid, {field: value})
    assert len(records(repo)[REQUEST["title"]].meta[field]) == todos.ASK_FIELDS[field]


@pytest.mark.parametrize("changes", [
    {"ask": "later"}, {"ask": ""}, {"ask": None}, {"ask": []},
    {"repeat": "daily", "due": "2026-10-10"},
    {"notified": True}, {"notified_by": "forged"}, {"notified_at": "2026-10-10 00:00"},
])
def test_invalid_creation(as_agent, changes):
    with pytest.raises(BaseError):
        as_agent("claude").add_todos([REQUEST | changes])


def test_updates_and_conversion(as_agent, repo):
    base = as_agent("claude")
    tid = add(base)
    for changes in ({"ask": ""}, {"ask": None}, {"context": ""}, {"repeat": "weekly"}):
        with pytest.raises(BaseError):
            base.update_todo(tid, changes)
    for kind in todos.ASKS:
        base.update_todo(tid, {"ask": kind, "why": "只有本人有权限。"})
        assert records(repo)[REQUEST["title"]].meta["ask"] == kind
    plain, _ = base._add_todos_commit([{"title": "普通事"}])
    with pytest.raises(BaseError):
        base.update_todo(plain[0], {"ask": "fyi"})
    base.update_todo(plain[0], {k: v for k, v in REQUEST.items() if k != "title"})
    assert records(repo)["普通事"].meta["ask"] == "act"
    recurring, _ = base._add_todos_commit([{"title": "重复的事", "due": "2026-10-10", "repeat": "daily"}])
    with pytest.raises(BaseError):
        base.update_todo(recurring[0], {k: v for k, v in REQUEST.items() if k != "title"})
    base.update_todo(recurring[0], {**{k: v for k, v in REQUEST.items() if k != "title"}, "repeat": ""})
    assert "repeat" not in records(repo)["重复的事"].meta


def test_notification_stamps_noop_and_clear(as_agent, repo):
    base = as_agent("claude")
    tid = add(base)
    base._now = lambda: dt.datetime(2026, 10, 11, 2, 5, tzinfo=dt.timezone.utc)
    base.update_todo(tid, {"notified": True}, signed_as="Morgan")
    doc = records(repo)[REQUEST["title"]]
    assert doc.meta["notified_by"] == "claude · Morgan"
    assert doc.meta["notified_at"] == "2026-10-11 10:05"
    assert isinstance(doc.meta["notified_at"], str)
    assert re.fullmatch(r"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", doc.meta["notified_at"])
    assert git(repo, "log", "-1", "--format=%s") == "待办：已通知「请重新登录供应商平台」"
    assert doc.raw.index("ask:") < doc.raw.index("notified_by:") < doc.raw.index("notified_at:") < doc.raw.index("context:")
    before = git(repo, "rev-parse", "HEAD"), doc.raw
    assert as_agent("codex").update_todo(tid, {"notified": True}, signed_as="Riley") == "没有变化，没有提交。\n"
    assert (git(repo, "rev-parse", "HEAD"), records(repo)[REQUEST["title"]].raw) == before
    base.update_todo(tid, {"notified": False})
    assert not {"notified_by", "notified_at"} & records(repo)[REQUEST["title"]].meta.keys()
    as_agent("codex").update_todo(tid, {"notified": True})
    assert records(repo)[REQUEST["title"]].meta["notified_by"] == "codex"


@pytest.mark.parametrize("value", [1, 0, "true", "false", None, [], {}])
def test_notified_is_strict_boolean(as_agent, value):
    base = as_agent("claude")
    with pytest.raises(BaseError):
        base.update_todo(add(base), {"notified": value})


@pytest.mark.parametrize("value", [True, False])
def test_plain_todo_cannot_be_notified(as_agent, value):
    base = as_agent("claude")
    ids, _ = base._add_todos_commit([{"title": "普通事"}])
    with pytest.raises(BaseError):
        base.update_todo(ids[0], {"notified": value})


@pytest.mark.parametrize("status", ["done", "dropped"])
@pytest.mark.parametrize("notified", [False, True])
def test_notice_close_and_reopen(as_agent, repo, status, notified):
    base = as_agent("claude")
    tid = add(base)
    assert todos.notice(records(repo)[REQUEST["title"]].meta) == "unnotified"
    if notified:
        base.update_todo(tid, {"notified": True})
    base.update_todo(tid, {"status": status})
    assert todos.notice(records(repo)[REQUEST["title"]].meta) == "closed"
    assert tid not in base.list_todos("asks")
    assert ("做完了" if status == "done" else "不做了") in base.list_todos("all")
    base.update_todo(tid, {"status": "open"})
    assert todos.notice(records(repo)[REQUEST["title"]].meta) == ("notified" if notified else "unnotified")
    assert tid in base.list_todos("asks")


def test_asks_multiline_order_empty_and_project_filter(as_agent):
    base = as_agent("claude")
    assert base.list_todos("asks") == f"没有登记给{OWNER_NAME}的事\n"
    first = add(base, title="最早已通知", project="alpha")
    base.update_todo(first, {"notified": True}, signed_as="Morgan")
    base._now = lambda: FIXED_NOW + dt.timedelta(minutes=1)
    second = add(base, title="未通知在前", ask="approve", context="第一行\n第二行", project="alpha")
    base._now = lambda: FIXED_NOW + dt.timedelta(days=1)
    third = add(base, title="较晚未通知", ask="fyi")
    text = base.list_todos("asks")
    assert text.index("## 未通知（2）") < text.index(second) < text.index(third) < text.index("## 已通知（1）") < text.index(first)
    assert f"- `{second}` 未通知在前 · 要{OWNER_NAME}批 · 登记：claude · Morgan · 2026-10-10" in text
    assert "  - 怎么来的：第一行\n    第二行" in text
    assert f"  - 为什么要{OWNER_NAME}：{REQUEST['why']}" in text
    assert f"  - 不做会怎样：{REQUEST['if_skipped']}" in text
    assert f"  - 要{OWNER_NAME}做：{REQUEST['action']}" in text
    assert "  - 已通知：claude · Morgan，2026-10-10 23:30" in text
    assert text.count("  - 已通知：") == 1
    assert f'告诉{OWNER_NAME}后用 `update_todo(id, {{"notified": true}})` 标已通知。' in text
    filtered = base.list_todos("asks", project_id="alpha")
    assert first in filtered and second in filtered and third not in filtered


def test_plain_output_stays_identical_in_mixed_list(as_agent, repo):
    base = as_agent("claude")
    ids, _ = base._add_todos_commit([{"title": "普通事", "due": "2026-10-10", "time": "14:30", "project": "alpha"}])
    ordinary_line = f"- `{ids[0]}` · 普通事 · 2026-10-10 14:30 · 项目 `alpha` · claude 加的"
    before = base.list_todos()
    assert ordinary_line in before
    tid = add(base, title="请求批准", ask="approve", due="2026-10-10")
    mixed = base.list_todos()
    assert ordinary_line in mixed
    assert f"- `{tid}` · 请求批准 · 2026-10-10 · claude · Morgan 加的 · 要{OWNER_NAME}批 · 未通知" in mixed
    assert all(line in mixed for line in before.splitlines() if line.startswith("- "))
    assert todos.notice(records(repo)["普通事"].meta) is None


def test_company_counts_only_visible_open_unnotified(ext, repo):
    base = ext("claude")
    assert "list_todos(scope='asks')" not in base.company()
    first = add(base)
    second = add(base, title="请确认采购预算")
    third = add(base, title="无需处理的事")
    base.update_todo(third, {"status": "dropped"})
    add(ext("ext-zhang-claude"), title="别人的登记")
    assert f"有 2 条登记给{OWNER_NAME}的事还没通知他：list_todos(scope='asks')。" in base.company()
    assert "别人的登记" not in base.list_todos("asks")
    with pytest.raises(NotFound):
        ext("ext-zhang-claude").update_todo(first, {"notified": True})
    base.update_todo(first, {"notified": True})
    assert "有 1 条登记给" in base.company()
    base.update_todo(second, {"status": "done"})
    assert "list_todos(scope='asks')" not in base.company()


@pytest.mark.parametrize("lang", ["zh", "en"])
def test_mcp_rules_and_parameter_docs(repo, monkeypatch, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    server = build_server(str(repo), "claude")
    tools = {t.name: t for t in asyncio.run(server.list_tools())}
    assert "ask" in server.instructions and OWNER_NAME in server.instructions
    assert ("标已通知" if lang == "zh" else "mark them notified") in server.instructions
    add_text = tools["add_todos"].description
    for word in ("ask", "approve", "act", "fyi", "context", "why", "if_skipped", "action", "title", OWNER_NAME):
        assert word in add_text
    assert (f"急事（花钱、对外发送、删真实数据，或马上就要卡住进度的）照旧直接找{OWNER_NAME}" if lang == "zh" else f"continue to contact {OWNER_NAME} directly") in add_text
    assert "scope='asks'" in tools["list_todos"].description
    assert '"notified": true' in tools["list_todos"].description
    assert '"notified": true' in tools["update_todo"].description
    assert ("由登记的伙伴标 done" if lang == "zh" else "agent that registered the request marks it done") in tools["update_todo"].description
    for name, field, word in (("list_todos", "scope", "asks"), ("add_todos", "items", "ask"), ("update_todo", "changes", "notified")):
        assert word in tools[name].input_schema["properties"][field]["description"]


def test_api_methods_and_commit_receipts(wire, repo):
    added = wire("add_todo", REQUEST | {"signed_as": "Morgan"})
    assert added["ok"]
    tid = added["result"]["id"]
    assert added["result"]["commit"] == git(repo, "rev-parse", "--short", "HEAD")
    listed = wire("list_todos", {"scope": "asks"})["result"]
    assert listed == [{"id": tid, "title": REQUEST["title"], "status": "open", "due": None, "time": None,
                       "repeat": None, "project": None, "notes": "", "created": str(dt.datetime.now(TAIPEI).date()),
                       "created_by": "claude · Morgan", "done_at": None,
                       **{k: REQUEST[k] for k in ("ask", *todos.ASK_FIELDS)}, "notice": "unnotified",
                       "notified_by": None, "notified_at": None}]
    updated = wire("update_todo", {"todo_id": tid, "changes": {"notified": True}, "signed_as": "Riley"})
    assert updated == {"ok": True, "result": {"commit": git(repo, "rev-parse", "--short", "HEAD")}}
    row = wire("list_todos", {"scope": "asks"})["result"][0]
    assert row["notice"] == "notified" and row["notified_by"] == "claude · Riley"
    assert re.fullmatch(r"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", row["notified_at"])
    assert wire("update_todo", {"todo_id": tid, "changes": {"notified": True}, "signed_as": "Morgan"}) == {"ok": True, "result": {"commit": None}}
    missing = wire("update_todo", {"todo_id": "missing", "changes": {"status": "done"}, "signed_as": "Morgan"})
    assert missing["error"]["code"] == "not_found"


@pytest.mark.parametrize("method,params", [
    ("list_todos", {"scope": "bad"}), ("list_todos", {"project_id": 1}),
    ("add_todo", REQUEST | {"signed_as": ""}), ("add_todo", REQUEST | {"signed_as": "Morgan", "unknown": 1}),
    ("add_todo", REQUEST | {"signed_as": "Morgan", "context": False}),
    ("add_todo", REQUEST | {"signed_as": "Morgan", "notified": True}),
    ("update_todo", {"todo_id": "missing", "changes": {"notified": 1}, "signed_as": "Morgan"}),
    ("update_todo", {"todo_id": "missing", "changes": {"bad": "value"}, "signed_as": "Morgan"}),
])
def test_api_invalid_fields(wire, method, params):
    assert wire(method, params)["error"]["code"] == "invalid"


def test_api_sorting_plain_fields_and_empty_changes(wire, as_agent):
    base = as_agent("claude")
    first = add(base, title="已通知", due="2026-10-01")
    second = add(base, title="未通知", project="alpha")
    base.update_todo(first, {"notified": True})
    plain, _ = base._add_todos_commit([{"title": "普通事", "due": "2026-10-10"}])
    assert [d["id"] for d in wire("list_todos", {"scope": "asks"})["result"]] == [second, first]
    assert [d["id"] for d in wire("list_todos", {"scope": "asks", "project_id": "alpha"})["result"]] == [second]
    row = next(d for d in wire("list_todos")["result"] if d["id"] == plain[0])
    assert all(row[k] is None for k in ("ask", *todos.ASK_FIELDS, "notice", "notified_by", "notified_at"))
    assert wire("update_todo", {"todo_id": first, "changes": {}, "signed_as": "Morgan"})["error"]["code"] == "invalid"
    assert [d["id"] for d in wire("list_todos")["result"]] == [first, plain[0], second]


def test_cli_add_update_and_asks(repo, capsys):
    common = ["--data", str(repo), "--as", "claude", "--signed-as", "Morgan"]
    assert run(["todo-add", *common, "--title", REQUEST["title"], "--ask", "act",
                "--context", REQUEST["context"], "--why", REQUEST["why"],
                "--if-skipped", REQUEST["if_skipped"], "--action", REQUEST["action"]]) == 0
    tid = records(repo)[REQUEST["title"]].id
    capsys.readouterr()
    assert run(["todo-update", tid, *common, "--notified"]) == 0
    assert records(repo)[REQUEST["title"]].meta["notified_by"] == "claude · Morgan"
    assert run(["todos", *common, "--scope", "asks"]) == 0
    assert "## 已通知（1）" in capsys.readouterr().out
    assert run(["todo-update", tid, *common, "--not-notified", "--ask", "fyi", "--action", "知道就行，不用操作"]) == 0
    assert todos.notice(records(repo)[REQUEST["title"]].meta) == "unnotified"
    with pytest.raises(SystemExit):
        run(["todo-update", tid, *common, "--notified", "--not-notified"])


def test_web_projection_and_owner_acknowledgement(client, as_agent, repo):
    tid = add(as_agent("claude"))
    row = next(d for d in client.get("/api/todos").json()["items"] if d["id"] == tid)
    assert all(row[k] == REQUEST[k] for k in ("ask", *todos.ASK_FIELDS))
    assert row["notice"] == "unnotified" and row["notified_by"] is None and row["notified_at"] is None
    assert client.post(f"/api/todos/{tid}", json={"notified": True}, headers=WEB_POST_HEADERS).status_code == 200
    doc = records(repo)[REQUEST["title"]]
    assert doc.meta["notified_by"] == OWNER and git(repo, "log", "-1", "--format=%an") == OWNER
    row = next(d for d in client.get("/api/todos").json()["items"] if d["id"] == tid)
    assert row["notice"] == "notified" and row["notified_by"] == "你"
    assert row["status"] == "open"
    assert client.post(f"/api/todos/{tid}", json={"notified_by": "forged"}, headers=WEB_POST_HEADERS).status_code == 400
    assert client.post(f"/api/todos/{tid}", json={"notified": False}).status_code == 403


def test_remote_projection_keeps_personal_scope_and_masks(workspace, ext, repo):
    base = ext("ext-zhang-claude")
    tid = add(base, context="本机 C:/PrivateMark/document 需要处理。")
    add(ext("claude"), title="另一人的登记")
    assert login(workspace)[0].status_code == 303
    data = workspace.get("/api/todos")
    assert "PrivateMark" not in data.text and "另一人的登记" not in data.text
    row = next(d for d in data.json()["items"] if d["id"] == tid)
    assert row["ask"] == "act" and row["notice"] == "unnotified"
    assert workspace.post(f"/api/todos/{tid}", json={"notified": True}, headers=signed(workspace)).status_code == 200
    assert records(repo)[REQUEST["title"]].meta["notified_by"] == "ext-zhang"
