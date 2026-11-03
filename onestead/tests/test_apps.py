"""应用广场、调用记录、我的 Agent：登记一次一个带作者的提交；网页只开登记过的路径；探活和调用记录只读。"""

from .support import OWNER

import json

import pytest

from onestead import usage
from onestead.store import BaseError, Store
from onestead.textutil import _dump_front
from onestead.web import probe

from .conftest import git
from .support import APP, WEB_POST_HEADERS as POST_HEADERS


def _apps(repo):
    return {d.id: d for d in Store(repo).load() if d.kind == "app"}


def test_register_list_get_and_update(as_agent, repo):
    msg = as_agent("claude").register_app(APP, signed_as="Onestead Lead")
    assert "已登记应用 `demo-web`" in msg
    assert git(repo, "log", "-1", "--format=%an|%s") == "claude · Onestead Lead|应用：登记了「演示网页」"
    d = _apps(repo)["demo-web"]
    assert d.meta["listed"] is True and d.meta["registered_by"] == "claude · Onestead Lead" and not d.problem
    assert d.raw.startswith("---\nid: demo-web\ntype: app\n")  # 一行一个键，方便手改

    text = as_agent("codex").list_apps()
    assert "`demo-web`" in text and "网址 http://127.0.0.1:9/" in text
    assert "代码在哪：C:\\x\\demo" in as_agent("codex").get_app("demo-web")

    base = as_agent("codex")
    base.update_app("demo-web", {"listed": False})
    assert git(repo, "log", "-1", "--format=%s") == "应用：下架了「演示网页」"
    assert "（没有登记的应用）" in base.list_apps() and "已下架" in base.list_apps(include_unlisted=True)
    base.update_app("demo-web", {"listed": True})
    assert git(repo, "log", "-1", "--format=%s") == "应用：重新上架了「演示网页」"
    base.update_app("demo-web", {"title": "新名字"})
    assert git(repo, "log", "-1", "--format=%s") == "应用：「演示网页」改名为「新名字」"
    base.update_app("demo-web", {"status_note": "改造期"})
    assert git(repo, "log", "-1", "--format=%s") == "应用：改了「新名字」"
    assert base.update_app("demo-web", {"status_note": "改造期"}).startswith("没有变化")


def test_register_apps_batch_is_one_commit(as_agent, repo):
    before = git(repo, "rev-list", "--count", "HEAD")
    as_agent("claude").register_apps([APP, {**APP, "id": "demo-bot", "title": "演示 bot", "kind": "bot", "open": {},
                                            "listed": False}], "首批", "Onestead Lead")
    assert int(git(repo, "rev-list", "--count", "HEAD")) == int(before) + 1
    assert git(repo, "log", "-1", "--format=%s") == "应用：登记了 2 个（首批）"
    apps = _apps(repo)
    assert apps["demo-bot"].meta["listed"] is False and "open" not in apps["demo-bot"].meta


@pytest.mark.parametrize("change", [
    {"id": "Bad_ID"}, {"kind": "游戏"}, {"title": ""}, {"project": "nope"}, {"open": {"url": "ftp://x"}},
    {"open": {"app": "x"}}, {"color": "red"}, {"links": [{"label": "x"}]}, {"listed": "yes"},
    {"notes": "token=abcdefghijklmnop1234"},
])
def test_bad_apps_are_refused(as_agent, repo, change):
    with pytest.raises(BaseError):
        as_agent("claude").register_app({**APP, **change})
    assert not (repo / "apps").exists()


def test_duplicate_and_reader(as_agent):
    as_agent("claude").register_app(APP)
    with pytest.raises(BaseError):
        as_agent("claude").register_app(APP)
    with pytest.raises(BaseError):
        as_agent("reader").register_app({**APP, "id": "other"})


def test_front_matter_is_one_key_per_line():
    text = _dump_front({"id": "x", "type": "todo", "title": "t", "links": [{"label": "a", "target": "b"}]})
    assert text == "---\nid: x\ntype: todo\ntitle: t\nlinks:\n- {label: a, target: b}\n---\n"


def test_web_apps_listed_and_activity(client, as_agent, repo):  # noqa: F811
    as_agent("claude").register_app(APP, signed_as="Onestead Lead")
    apps = client.get("/api/apps").json()["apps"]
    a = apps[0]
    assert a["id"] == "demo-web" and a["project"] == {"id": "alpha", "name": "阿尔法"} and a["listed"] is True
    assert a["registered_by"] == "Onestead Lead"
    assert client.post("/api/apps/demo-web/listed", json={"listed": False}, headers=POST_HEADERS).status_code == 200
    assert git(repo, "log", "-1", "--format=%an|%s") == f"{OWNER}|应用：下架了「演示网页」"
    rows = client.get("/api/activity").json()["rows"]
    assert rows[0]["type"] == "应用" and rows[0]["what"] == "下架了应用「演示网页」" and rows[0]["project"]["id"] == "alpha"
    assert client.post("/api/apps/demo-web/listed", json={"listed": True}).status_code == 403  # 不是从页面发出的


def test_web_open_only_registered_paths(client, as_agent, tmp_path, monkeypatch):  # noqa: F811
    exe = tmp_path / "tool.exe"
    exe.write_text("x")
    page = tmp_path / "课堂.html"
    page.write_text("x")
    as_agent("claude").register_app({**APP, "open": {"path": str(exe)}, "code_path": str(tmp_path),
                                     "links": [{"label": "课堂", "target": str(page)},
                                               {"label": "网站", "target": "https://example.com"}]})
    opened = []
    monkeypatch.setattr("os.startfile", lambda p: opened.append(p), raising=False)

    def post(body):
        return client.post("/api/apps/demo-web/open", json=body, headers=POST_HEADERS)

    assert post({"what": "path"}).status_code == 200
    assert post({"what": "folder"}).status_code == 200
    assert post({"what": "code"}).status_code == 200
    assert post({"what": "link", "index": 0}).status_code == 200
    assert opened == [str(exe), str(tmp_path), str(tmp_path), str(page)]
    assert post({"what": "link", "index": 1}).status_code == 400  # 网址用浏览器开，不走这里
    assert post({"what": "link", "index": 9}).status_code == 400
    assert post({"what": "shortcut"}).status_code == 400  # 没登记快捷方式
    assert post({"what": "C:\\Windows\\System32\\cmd.exe"}).status_code == 400
    exe.unlink()
    assert "找不到" in post({"what": "path"}).json()["error"]
    assert len(opened) == 4
    assert client.post("/api/apps/nope/open", json={"what": "path"}, headers=POST_HEADERS).status_code == 404


def test_web_probe(client, as_agent, monkeypatch):  # noqa: F811
    as_agent("claude").register_apps([
        {**APP, "id": "company-base", "title": "Onestead", "open": {"shortcut": "C:\\x.lnk"}},
        APP,
        {**APP, "id": "sample-native", "title": "示例应用", "kind": "本机程序", "open": {"path": "C:\\x.exe"}, "probe_process": "Sample.App"},
        {**APP, "id": "bot", "title": "机器人", "kind": "bot", "open": {}},
    ], "测试", None)
    monkeypatch.setattr(probe, "_url", lambda url: "offline")
    monkeypatch.setattr(probe, "_processes", lambda: {"sample.app.exe", "sample.app"})
    data = client.get("/api/apps/probe").json()
    assert data["company-base"] == {"state": "online", "how": "本页面"}
    assert data["demo-web"]["state"] == "offline"
    assert data["sample-native"] == {"state": "running", "how": "进程"}
    assert data["bot"]["state"] == "unknown"


def test_usage_record_and_summary(usage_log):
    usage.record("claude", "get_project", "mcp")
    usage.record("claude", "todos", "cli")
    usage.record(None, "x", "mcp")  # 没身份不记
    usage_log.open("a", encoding="utf-8").write("坏行\n")
    rows = [json.loads(x) for x in usage_log.read_text(encoding="utf-8").splitlines() if x.startswith("{")]
    assert [r["tool"] for r in rows] == ["get_project", "todos"]
    s = usage.summary()
    assert s["claude"]["recent"] == 2 and s["claude"]["last_tool"] == "todos" and s["claude"]["last_via"] == "cli"


def test_usage_can_be_turned_off(monkeypatch, tmp_path):
    monkeypatch.setenv("COMPANY_BASE_USAGE_LOG", "")
    usage.record("claude", "x", "mcp")
    assert usage.summary() == {}


def test_web_agents_page(client, as_agent, usage_log):  # noqa: F811
    as_agent("codex").update_project_status("alpha", "今天的进展", signed_as="alex")
    usage.record("codex", "get_project", "mcp")
    rows = {r["key"]: r for r in client.get("/api/agents").json()["agents"]}
    assert rows["codex"]["writes_7d"] >= 1 and rows["codex"]["names"] == ["alex"]
    assert rows["codex"]["calls_7d"] == 1 and rows["codex"]["last_tool"] == "看项目"
    assert rows["claude"]["last_use"] is None
    assert list(rows)[0] == "codex"  # 最近用过、写过的排前面


def test_home_apps_module(client, as_agent):  # noqa: F811
    as_agent("claude").register_apps([APP, {**APP, "id": "cli", "title": "命令", "kind": "命令行工具",
                                            "open": {"command": "x"}}], "测试", None)
    mods = {m["kind"]: m["data"] for m in client.get("/api/home").json()["modules"]}
    assert [a["id"] for a in mods["apps"]["items"]] == ["demo-web"]  # 默认只放网页和本机程序
