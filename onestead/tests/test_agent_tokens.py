"""本人 Agent 口令授权、公开记录投影、撤回与主人变更。"""

from .support import OWNER

import asyncio
import json
import threading
from concurrent.futures import ThreadPoolExecutor

import pytest
from starlette.testclient import TestClient

from onestead import tokens, usage
from onestead.base import CompanyBase
from onestead.server import TokenGate
from onestead.store import BaseError, member_keys
from onestead.web import history, views
from onestead.web.app import build_app

from .conftest import FIXED_NOW
from .support import (
    MANAGE_BASE,
    MANAGE_PORT,
    MANAGE_POST_HEADERS,
    MANAGE_TOKEN,
    WEB_POST_HEADERS as POST_HEADERS,
    change,
    make_external,
)


@pytest.fixture
def workspace(personal_workspace):
    """口令测试沿用个人工作台工厂。同事网页客户端是另一个同名夹具。"""
    return personal_workspace


@pytest.fixture
def owner(repo):
    return CompanyBase(repo, OWNER, now=lambda: FIXED_NOW, allow_owner=True)


def gate_ok(repo, token):
    return asyncio.run(TokenGate(str(repo)).verify_token(token)) is not None


def test_human_issues_and_revokes_own_agent_without_company_write(workspace, repo):  # noqa: F811
    change(repo, "people/a.md", can_write=False)
    base = workspace("a")
    result = base.token_issue("a-bot", "自己的工具")
    record = result["record"]
    assert tokens.lookup(result["token"]) == "a-bot" and gate_ok(repo, result["token"])
    assert record["issued_by"] == "a" and record["expires"] is None
    assert record["tail"] == result["token"][-4:] and "sha256" not in record
    assert base.agent_token_list() == [{**record, "live": True}]
    assert workspace(OWNER).token_list() == [{**record, "live": True}]
    revoked = base.token_revoke(record["id"])
    assert revoked["revoked"] and not gate_ok(repo, result["token"])
    assert tokens.lookup(result["token"]) is None
    assert base.token_revoke(record["id"]) == revoked


@pytest.mark.parametrize("target", ["a", "b", "b-bot", "reader", OWNER, "missing"])
def test_human_cannot_issue_for_self_other_human_or_other_agent(workspace, target):  # noqa: F811
    with pytest.raises(BaseError):
        workspace("a").token_issue(target, "不允许的目标")
    assert tokens.list_tokens() == []


@pytest.mark.parametrize("disabled", ["a", "a-bot"])
def test_issue_needs_remote_on_owner_and_agent(workspace, repo, disabled):  # noqa: F811
    change(repo, f"people/{disabled}.md", remote_enabled=False)
    with pytest.raises(BaseError):
        workspace("a").token_issue("a-bot", "已关闭接入")
    assert tokens.list_tokens() == []


@pytest.mark.parametrize("actor", ["a-bot", "b-bot", "codex"])
def test_agent_cannot_issue_revoke_or_read_token_management(workspace, actor):  # noqa: F811
    result = workspace("a").token_issue("a-bot", "有效口令")
    base = workspace(actor)
    for call in (lambda: base.token_issue("a-bot", "越权"),
                 lambda: base.token_revoke(result["record"]["id"]),
                 lambda: base.agent_token_list(), lambda: base.token_list()):
        with pytest.raises(BaseError):
            call()
    assert tokens.lookup(result["token"]) == "a-bot"


def test_revoke_foreign_and_missing_ids_indistinguishable(workspace):  # noqa: F811
    foreign = workspace("b").token_issue("b-bot", "乙的口令")
    messages = []
    for token_id in (foreign["record"]["id"], "不存在的ID"):
        with pytest.raises(BaseError) as exc:
            workspace("a").token_revoke(token_id)
        messages.append(str(exc.value))
    assert messages == ["找不到这个口令。"] * 2
    assert tokens.lookup(foreign["token"]) == "b-bot"


@pytest.mark.parametrize("kind", ["feishu", "web", "unknown", None])
def test_human_revoke_only_token_kind(workspace, kind):  # noqa: F811
    token, record = tokens.issue("a-bot", "测试会话", kind="feishu", days=30)
    with tokens._editing():
        items = tokens._load()
        items[0]["kind"] = kind
        tokens._save(items)
    with pytest.raises(BaseError, match="找不到这个口令"):
        workspace("a").token_revoke(record["id"])
    assert not tokens.list_tokens()[0]["revoked"]
    assert workspace("a").agent_token_list() == []
    assert token


def test_legacy_token_without_kind_is_revokeable(workspace):  # noqa: F811
    _, record = tokens.issue("a-bot", "旧格式")
    with tokens._editing():
        items = tokens._load()
        items[0].pop("kind")
        tokens._save(items)
    assert workspace("a").token_revoke(record["id"])["revoked"]


def test_human_can_revoke_owner_issued_own_agent_token(workspace):  # noqa: F811
    result = workspace(OWNER).token_issue("a-bot", "管理员发的")
    assert result["record"]["issued_by"] == OWNER
    assert workspace("a").token_revoke(result["record"]["id"])["revoked"]
    assert tokens.lookup(result["token"]) is None


def test_owner_can_issue_for_any_remote_member_and_revoke_sessions(workspace):  # noqa: F811
    admin = workspace(OWNER)
    for key in ("a", "b", "a-bot", "b-bot"):
        result = admin.token_issue(key, "管理员发的")
        assert result["record"]["issued_by"] == OWNER
        assert admin.token_revoke(result["record"]["id"])["revoked"]
    _, session = tokens.issue("a", "飞书会话", kind="feishu", days=30)
    assert admin.token_revoke(session["id"])["revoked"]


def test_human_cannot_revoke_own_login_token(workspace):  # noqa: F811
    login = workspace(OWNER).token_issue("a", "本人的登录口令")
    with pytest.raises(BaseError, match="找不到这个口令"):
        workspace("a").token_revoke(login["record"]["id"])
    assert workspace("a").agent_token_list() == []
    assert tokens.lookup(login["token"]) == "a"


def test_issued_by_cannot_be_caller_supplied(workspace):  # noqa: F811
    with pytest.raises(TypeError):
        workspace("a").token_issue("a-bot", "伪造", issued_by=OWNER)
    with pytest.raises(TypeError):
        workspace("a").token_issue("b-bot", "伪造", owner="a")
    assert tokens.list_tokens() == []


def test_my_agents_tokens_and_usage_are_filtered_before_response(workspace, repo):  # noqa: F811
    admin = workspace(OWNER)
    results = [admin.token_issue(key, "测试") for key in ("a", "b", "a-bot", "b-bot")]
    tokens.issue("a-bot", "飞书会话", kind="feishu", days=30)
    usage.record("a-bot", "get_project", "mcp")
    usage.record("b-bot", "other-secret-tool", "mcp")
    usage.record("a", "human-login-tool", "cli")
    for key in ("a-bot", "b-bot"):
        workspace(key).update_project_status("alpha", "测试进展", signed_as=f"{key}署名")
    base = workspace("a")
    viewer, docs, visible = base._ctx()
    rows = views.agent_rows(visible, history.list_commits(repo, 100), member_keys(docs), usage.summary(),
                            viewer, tokens.list_tokens())
    assert len(rows) == 1 and rows[0]["key"] == "a-bot"
    row = rows[0]
    assert row["calls_7d"] == 1 and row["last_tool"] == "看项目" and row["names"] == ["a-bot署名"]
    assert row["tokens"] == [{field: results[2]["record"][field] for field in
                              ("id", "label", "created", "expires", "revoked", "tail", "issued_by")}]
    serialized = json.dumps(rows, ensure_ascii=False)
    assert "sha256" not in serialized and "b-bot" not in serialized and "human-login-tool" not in serialized
    assert "feishu" not in serialized and all(r["token"] not in serialized for r in results)


def test_local_owner_my_agents_and_member_token_issuer(workspace, repo, client):  # noqa: F811
    result = workspace("a").token_issue("a-bot", "同事发的")
    tokens.issue("codex", "本机测试口令", issued_by=OWNER)
    agents = client.get("/api/agents").json()["agents"]
    assert {a["key"] for a in agents} == {"claude", "codex"}
    assert next(a for a in agents if a["key"] == "codex")["tokens"][0]["issued_by"] == OWNER
    members = client.get("/api/members").json()["items"]
    colleague_agent = next(m for m in members if m["key"] == "a-bot")
    assert colleague_agent["tokens"][0]["issued_by"] == "a"
    assert client.post("/api/tokens/revoke", json={"id": result["record"]["id"]}, headers=POST_HEADERS).status_code == 200
    issued = client.post("/api/tokens/issue", json={"key": "b-bot", "label": "网页发的", "issued_by": "a"},
                         headers=POST_HEADERS).json()
    assert issued["record"]["issued_by"] == OWNER
    assert issued["token"] not in client.get("/api/agents").text
    assert issued["token"] not in client.get("/api/members").text


def test_owner_transfer_revokes_before_change_and_does_not_revive(workspace, repo, monkeypatch):  # noqa: F811
    old = workspace("a").token_issue("a-bot", "旧主人")
    admin = workspace(OWNER)
    original = tokens.revoke_key

    def checked_revoke(key, kind=None):
        doc = next(d for d in admin.store.load() if d.rel == "people/a-bot.md")
        assert doc.meta["owner"] == "a"
        return original(key, kind)

    monkeypatch.setattr(tokens, "revoke_key", checked_revoke)
    source = admin.doc_source("people/a-bot.md")
    admin.save_doc("people/a-bot.md", source["version"], {"owner": "b"}, None)
    monkeypatch.setattr(tokens, "revoke_key", original)
    assert tokens.lookup(old["token"]) is None and not gate_ok(repo, old["token"])
    with pytest.raises(BaseError, match="找不到"):
        workspace("a").token_revoke(old["record"]["id"])
    assert workspace("b").token_revoke(old["record"]["id"])["revoked"]
    fresh = workspace("b").token_issue("a-bot", "新主人")
    assert fresh["record"]["issued_by"] == "b" and gate_ok(repo, fresh["token"])
    source = admin.doc_source("people/a-bot.md")
    admin.save_doc("people/a-bot.md", source["version"], {"owner": "a"}, None)
    assert not gate_ok(repo, old["token"]) and not gate_ok(repo, fresh["token"])


def test_disable_owner_remote_revokes_owner_and_agents_permanently(workspace, repo):  # noqa: F811
    admin = workspace(OWNER)
    login = admin.token_issue("a", "登录口令")
    bot = workspace("a").token_issue("a-bot", "本人发的")
    session, _ = tokens.issue("a", "飞书会话", kind="feishu", days=30)
    unrelated = workspace("b").token_issue("b-bot", "乙的口令")
    source = admin.doc_source("people/a.md")
    admin.save_doc("people/a.md", source["version"], {"remote_enabled": False}, None)
    assert all(tokens.lookup(t) is None for t in (login["token"], bot["token"], session))
    source = admin.doc_source("people/a.md")
    admin.save_doc("people/a.md", source["version"], {"remote_enabled": True}, None)
    assert not gate_ok(repo, bot["token"]) and not gate_ok(repo, login["token"])
    assert gate_ok(repo, unrelated["token"])


def test_revoke_uses_current_agent_owner_from_id(workspace, repo):  # noqa: F811
    old = workspace("a").token_issue("a-bot", "先发")
    # 直接修改模拟外部变更；授权仍按当前 owner，不能按发证人或前端说明放行。
    change(repo, "people/a-bot.md", owner="b")
    with pytest.raises(BaseError, match="找不到"):
        workspace("a").token_revoke(old["record"]["id"])
    assert workspace("b").token_revoke(old["record"]["id"])["revoked"]


def test_issue_rechecks_owner_after_waiting_for_write_lock(workspace, repo, monkeypatch):  # noqa: F811
    from onestead import gitops
    started = threading.Event()
    original = gitops.write_lock

    def announced(root):
        started.set()
        return original(root)

    monkeypatch.setattr(gitops, "write_lock", announced)
    with ThreadPoolExecutor(max_workers=1) as pool:
        with original(repo):
            task = pool.submit(workspace("a").token_issue, "a-bot", "排队发证")
            assert started.wait(10)
            change(repo, "people/a-bot.md", owner="b")
        with pytest.raises(BaseError, match="自己名下"):
            task.result(timeout=20)
    assert tokens.list_tokens() == []


def test_tokens_only_for_external_and_only_by_owner(owner, as_agent):
    with pytest.raises(BaseError, match="只发给允许远程接入"):
        owner.token_issue("codex", "不该发")
    make_external(owner, "people/reader.md", projects=["alpha"])
    r = owner.token_issue("reader", "读者的电脑")
    assert tokens.lookup(r["token"]) == "reader"
    assert owner.token_list()[0]["label"] == "读者的电脑"
    owner.token_revoke(r["record"]["id"])
    assert tokens.lookup(r["token"]) is None
    with pytest.raises(BaseError):
        as_agent("codex").token_issue("reader", "伙伴替外部开门")


def test_web_token_round_trip(repo, owner):
    make_external(owner, "people/reader.md", projects=["alpha"])
    c = TestClient(build_app(repo, MANAGE_PORT, MANAGE_TOKEN), base_url=MANAGE_BASE, follow_redirects=False)
    assert c.get(f"/open?t={MANAGE_TOKEN}").status_code == 303
    assert c.post("/api/tokens/issue", json={"key": "reader", "label": "网页上发的"}).status_code == 403  # 没带来源核对
    r = c.post("/api/tokens/issue", json={"key": "reader", "label": "网页上发的"}, headers=MANAGE_POST_HEADERS)
    assert r.status_code == 200 and r.json()["token"].startswith("cbx_")
    members = c.get("/api/members").json()["items"]
    me = next(x for x in members if x["key"] == "reader")
    assert me["remote_enabled"] and me["projects"] == ["阿尔法"] and len(me["tokens"]) == 1
    assert "sha256" not in me["tokens"][0] and r.json()["token"] not in c.get("/api/members").text
    rid = me["tokens"][0]["id"]
    assert c.post("/api/tokens/revoke", json={"id": rid}, headers=MANAGE_POST_HEADERS).json()["revoked"]
