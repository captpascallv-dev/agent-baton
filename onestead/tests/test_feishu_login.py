"""飞书登录进入共用工作台：只给有效远程 human 发网页会话，机器接口不认。"""

from .support import OWNER, OWNER_NAME

import datetime as dt
import json
import logging
import os
import re
from pathlib import Path

import pytest
from starlette.testclient import TestClient

from onestead import feishu, tokens
from onestead.base import CompanyBase
from onestead.server import remote_app
from onestead.store import BaseError, Store, check_meta, now_taipei
from onestead.web.app import build_app

from .conftest import git
from .support import (
    FEISHU_SECRET as SECRET,
    INFO,
    MANAGE_BASE as WEB_BASE,
    MANAGE_PORT as WEB_PORT,
    MANAGE_POST_HEADERS as POST_HEADERS,
    MANAGE_TOKEN as TOKEN,
    auth_query,
    patch_user,
    plant,
    write_config,
)

PORT = 48231
BASE = f"http://127.0.0.1:{PORT}"


@pytest.fixture(autouse=True)
def token_store(tmp_path, monkeypatch):
    monkeypatch.setenv("COMPANY_BASE_TOKENS", str(tmp_path / "tokens.json"))


def portal_client(repo, public_host=None):
    app = remote_app(str(repo), PORT, public_host=public_host,
                     web_test_origin=BASE if public_host is None else None)
    base = f"https://{public_host}" if public_host else BASE
    return TestClient(app, base_url=base, follow_redirects=False, headers={"Origin": base})


def begin(c):
    got = c.get("/portal/feishu/login")
    assert got.status_code == 303, got.text
    return got, auth_query(got)["state"][0]


def callback(c, state, code="auth-code"):
    return c.get("/portal/feishu/callback", params={"code": code, "state": state})


def set_cookie_names(response):
    return response.headers.get_list("set-cookie")


def cookie_flags(header: str) -> list[str]:
    return [part.strip().lower() for part in header.split(";")[1:]]


def has_web_cookie(response) -> bool:
    return any(item.lower().startswith(("cb_web_dev_", "__host-cb_web=")) for item in set_cookie_names(response))


def test_unconfigured_hides_button(ext, repo, feishu_cfg):  # noqa: F811
    with portal_client(repo) as c:
        page = c.get("/portal")
        assert page.status_code == 303 and page.headers["location"] == "/"
        missed = c.get("/portal/feishu/login")
        assert missed.status_code == 404 and "飞书" not in missed.text
        assert "飞书" not in c.get("/login").text
    (feishu_cfg / "feishu-login.txt").write_text("# 只有注释\napp_id=\n", encoding="utf-8")
    (feishu_cfg / "feishu-secret.txt").write_text("   \n", encoding="utf-8")
    with portal_client(repo) as c:
        assert c.get("/portal").headers["location"] == "/"
        assert c.get("/portal/feishu/login").status_code == 404
        assert "飞书" not in c.get("/login").text


def test_login_redirects_to_feishu(ext, repo, feishu_cfg):  # noqa: F811
    write_config(feishu_cfg)
    with portal_client(repo) as c:
        assert 'href="/portal/feishu/login"' in c.get("/login").text
        got, state = begin(c)
        query = auth_query(got)
        assert got.headers["location"].startswith("https://accounts.feishu.cn/open-apis/authen/v1/authorize?")
        assert query["client_id"] == ["cli_test"] and query["response_type"] == ["code"]
        assert "scope" not in query and query["state"] == [state]
        assert query["redirect_uri"] == [f"http://127.0.0.1:{PORT}/portal/feishu/callback"]
        flags = cookie_flags(got.headers.get_list("set-cookie")[0])
        assert got.headers["set-cookie"].lower().startswith("cb_feishu_state=")
        assert "httponly" in flags and "samesite=lax" in flags and "path=/portal/feishu" in flags
        assert "secure" not in flags
    with portal_client(repo, public_host="base.example.test") as c:
        got, _state = begin(c)
        query = auth_query(got)
        assert query["redirect_uri"] == ["https://base.example.test/portal/feishu/callback"]
        flags = cookie_flags(got.headers.get_list("set-cookie")[0])
        assert "httponly" in flags and "samesite=lax" in flags and "secure" in flags


def test_bad_state_does_not_exchange(ext, repo, feishu_cfg, monkeypatch):  # noqa: F811
    write_config(feishu_cfg)
    calls = patch_user(monkeypatch)
    with portal_client(repo) as c:
        _got, state = begin(c)
        wrong = callback(c, "not-the-state")
        assert wrong.status_code == 200 and feishu.USER_FAIL in wrong.text
        assert not has_web_cookie(wrong)
        assert calls == []
        cleared = " ".join(set_cookie_names(wrong)).lower()
        assert "cb_feishu_state=" in cleared and "max-age=0" in cleared
        c.cookies.clear()
        missing = c.get("/portal/feishu/callback", params={"code": "auth-code", "state": state})
        assert feishu.USER_FAIL in missing.text and calls == []
        again, _state = begin(c)
        assert again.status_code == 303
        no_code = c.get("/portal/feishu/callback", params={"state": auth_query(again)["state"][0]})
        assert feishu.USER_FAIL in no_code.text and calls == []


def test_unknown_user_is_pending_once(ext, repo, feishu_cfg, monkeypatch):  # noqa: F811
    write_config(feishu_cfg)
    calls = patch_user(monkeypatch)
    with portal_client(repo) as c:
        _got, state = begin(c)
        first = callback(c, state)
        assert "还没在底座登记" in first.text and "王五" in first.text
        assert not has_web_cookie(first)
        _got, state = begin(c)
        second = callback(c, state)
        assert "还没在底座登记" in second.text and not has_web_cookie(second)
    rows = feishu.list_pending()
    assert len(rows) == 1 and rows[0]["open_id"] == "ou_wang" and rows[0]["count"] == 2
    assert rows[0]["name"] == "王五" and rows[0]["union_id"] == "on_wang"
    assert rows[0]["first_seen"] and rows[0]["last_seen"]
    assert len(calls) == 4  # 两次都换了令牌、取了用户信息


def test_pending_keeps_latest_50(feishu_cfg, monkeypatch):
    stamps = iter(dt.datetime(2026, 10, 11, 8, 0, tzinfo=dt.timezone(dt.timedelta(hours=8)))
                  + dt.timedelta(seconds=i) for i in range(60))
    monkeypatch.setattr(feishu, "now_taipei", lambda: next(stamps))
    for i in range(51):
        feishu.remember({"open_id": f"ou_{i}", "name": str(i), "union_id": "", "tenant_key": ""})
    rows = feishu.list_pending()
    assert len(rows) == 50
    assert rows[0]["open_id"] == "ou_50"  # 最新的在前
    assert all(item["open_id"] != "ou_0" for item in rows)


def test_bind_then_callback_opens_workspace(ext, repo, feishu_cfg, monkeypatch):  # noqa: F811
    write_config(feishu_cfg)
    patch_user(monkeypatch)
    owner = CompanyBase(repo, OWNER, allow_owner=True)
    before = int(git(repo, "rev-list", "--count", "HEAD"))
    with portal_client(repo) as c:
        _got, state = begin(c)
        assert "还没在底座登记" in callback(c, state).text
        bound = owner.feishu_bind("ou_wang", "ext-zhang")
        text = (repo / "people/ext-zhang.md").read_text(encoding="utf-8")
        assert "feishu_open_id:" in text and "ou_wang" in text
        assert int(git(repo, "rev-list", "--count", "HEAD")) == before + 1
        assert git(repo, "log", "-1", "--format=%an") == OWNER
        assert bound["commit"]
        assert feishu.list_pending() == []
        _got, state = begin(c)
        done = callback(c, state)
        assert done.status_code == 200 and "登录成功" in done.text
        assert '<meta http-equiv="refresh" content="0;url=/">' in done.text
        assert 'href="/">进入底座</a>' in done.text
        portal_cookie = next(item for item in set_cookie_names(done) if item.lower().startswith(("cb_web_dev_", "__host-cb_web=")))
        lowered = portal_cookie.lower()
        assert "httponly" in lowered and "samesite=strict" in lowered and "path=/" in lowered
        assert c.get("/api/me").json()["key"] == "ext-zhang"
        session = c.cookies.get(f"cb_web_dev_{PORT}")
        record = tokens.lookup_record(session)
        assert record["key"] == "ext-zhang" and record["kind"] == "web"
        expires = dt.datetime.fromisoformat(record["expires"])
        delta = expires - now_taipei()
        assert dt.timedelta(days=29) < delta <= dt.timedelta(days=30, minutes=1)
        listed = next(item for item in tokens.list_tokens("ext-zhang") if item["kind"] == "web" and not item["revoked"])
        assert listed["expires"] == record["expires"]


def test_feishu_session_rejected_by_mcp(ext, repo, feishu_cfg, monkeypatch):  # noqa: F811
    write_config(feishu_cfg)
    patch_user(monkeypatch)
    with portal_client(repo) as c:
        _got, state = begin(c)
        callback(c, state)
        CompanyBase(repo, OWNER, allow_owner=True).feishu_bind("ou_wang", "ext-zhang")
        _got, state = begin(c)
        callback(c, state)
        session = c.cookies.get(f"cb_web_dev_{PORT}")
        headers = {"Accept": "application/json, text/event-stream", "Authorization": f"Bearer {session}"}
        body = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "whoami", "arguments": {}}}
        assert c.post("/mcp", headers=headers, json=body).status_code == 401
        password, _rec = tokens.issue("ext-zhang", "口令")
        headers["Authorization"] = f"Bearer {password}"
        assert c.post("/mcp", headers=headers, json=body).status_code == 200


def test_back_to_local_partner_blocks_feishu(ext, repo, feishu_cfg, monkeypatch):  # noqa: F811
    write_config(feishu_cfg)
    patch_user(monkeypatch)
    owner = CompanyBase(repo, OWNER, allow_owner=True)
    with portal_client(repo) as c:
        _got, state = begin(c)
        callback(c, state)
        owner.feishu_bind("ou_wang", "ext-zhang")
        _got, state = begin(c)
        callback(c, state)
        session = c.cookies.get(f"cb_web_dev_{PORT}")
        source = owner.doc_source("people/ext-zhang.md")
        owner.save_doc("people/ext-zhang.md", source["version"], {"remote_enabled": False}, None)
        assert tokens.lookup(session) is None
        assert c.get("/").headers["location"] == "/login"
        _got, state = begin(c)
        refused = callback(c, state)
        assert "这个飞书账号现在不能登录底座" in refused.text
        assert not has_web_cookie(refused)
        assert "feishu_open_id" in (repo / "people/ext-zhang.md").read_text(encoding="utf-8")


def test_unbind_revokes_feishu_session_only(ext, repo, feishu_cfg, monkeypatch):  # noqa: F811
    write_config(feishu_cfg)
    patch_user(monkeypatch)
    owner = CompanyBase(repo, OWNER, allow_owner=True)
    with portal_client(repo) as c:
        _got, state = begin(c)
        callback(c, state)
        owner.feishu_bind("ou_wang", "ext-zhang")
        password, _rec = tokens.issue("ext-zhang", "仍可用的口令")
        _got, state = begin(c)
        callback(c, state)
        session = c.cookies.get(f"cb_web_dev_{PORT}")
        owner.feishu_unbind("ext-zhang")
        assert "feishu_open_id" not in (repo / "people/ext-zhang.md").read_text(encoding="utf-8")
        assert tokens.lookup(session) is None
        assert tokens.lookup(password) == "ext-zhang"
        assert c.get("/").headers["location"] == "/login"


def test_expired_session_is_rejected(ext, repo, feishu_cfg):  # noqa: F811
    token, _rec = tokens.issue("ext-zhang", "飞书登录", kind="feishu", days=30)
    path = Path(os.environ["COMPANY_BASE_TOKENS"])
    items = json.loads(path.read_text(encoding="utf-8"))
    items[0]["expires"] = "2000-01-01T00:00+08:00"
    path.write_text(json.dumps(items), encoding="utf-8")
    assert tokens.lookup(token) is None and tokens.lookup_record(token) is None
    password, _rec = tokens.issue("ext-zhang", "口令")
    assert tokens.lookup(password) == "ext-zhang"
    with portal_client(repo) as c:
        c.cookies.set("cb_portal", token, path="/portal")
        assert c.get("/portal").headers["location"] == "/"
        assert c.get("/api/me").status_code == 401


def test_duplicate_open_id_blocks_both(ext, repo, feishu_cfg, monkeypatch):  # noqa: F811
    assert "非空" in check_meta(
        {"type": "person", "id": "a", "agent_key": "a", "kind": "human", "sensitivity": "internal", "feishu_open_id": "  "}, "person")
    assert check_meta(
        {"type": "person", "id": "a", "agent_key": "a", "kind": "human", "sensitivity": "internal", "feishu_open_id": "ou_ok"}, "person") is None
    plant(repo, "people/ext-zhang.md", "ou_same")
    plant(repo, "people/ext-li.md", "ou_same")
    people = [d for d in Store(repo).load() if d.meta.get("feishu_open_id") == "ou_same"]
    assert len(people) == 2 and all(d.problem and "重了" in d.problem for d in people)
    write_config(feishu_cfg)
    patch_user(monkeypatch, {"name": "重复", "open_id": "ou_same", "union_id": "on", "tenant_key": "tk"})
    with portal_client(repo) as c:
        _got, state = begin(c)
        refused = callback(c, state)
        assert "这个飞书账号现在不能登录底座" in refused.text
        assert not has_web_cookie(refused)
    assert feishu.list_pending() == []


class _Resp:
    def __init__(self, payload: bytes):
        self.payload = payload

    def read(self):
        return self.payload

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def test_secret_stays_out_of_responses_and_logs(ext, repo, feishu_cfg, monkeypatch, caplog):  # noqa: F811
    write_config(feishu_cfg)
    caplog.set_level(logging.DEBUG)
    mode = {"fail": True}
    flags = {"secret_in_body": False}

    def fake_urlopen(req, timeout=10):
        if req.full_url == feishu.TOKEN_URL:
            body = req.data.decode("utf-8")
            flags["secret_in_body"] = SECRET in body
            if mode["fail"]:
                payload = {"code": 99991, "error": "invalid_grant", "error_description": f"detail {SECRET}"}
            else:
                payload = {"code": 0, "access_token": "atk-not-the-secret"}
            return _Resp(json.dumps(payload).encode("utf-8"))
        if req.full_url == feishu.USER_INFO_URL:
            payload = {"code": 0, "data": {"name": "王五", "open_id": "ou_log", "union_id": "on", "tenant_key": "tk"}}
            return _Resp(json.dumps(payload).encode("utf-8"))
        return _Resp(b"{}")

    monkeypatch.setattr(feishu.urllib.request, "urlopen", fake_urlopen)

    def clean(response):
        blob = response.text + "\n" + str(response.headers)
        assert SECRET not in blob
        assert SECRET not in caplog.text
        for rec in caplog.records:
            assert SECRET not in rec.getMessage()
            assert SECRET not in str(rec.args)

    with portal_client(repo) as c:
        page = c.get("/portal")
        clean(page)
        jumped, _state = begin(c)
        clean(jumped)
        failed = callback(c, auth_query(jumped)["state"][0])
        assert feishu.USER_FAIL in failed.text and "99991" not in failed.text
        clean(failed)
        assert "99991" in caplog.text
        mode["fail"] = False
        _got, state = begin(c)
        pending = callback(c, state)
        assert "还没在底座登记" in pending.text
        clean(pending)
    assert flags["secret_in_body"]
    usage = Path(os.environ["COMPANY_BASE_USAGE_LOG"])
    if usage.exists():
        assert SECRET not in usage.read_text(encoding="utf-8")
    for path in feishu_cfg.iterdir():
        if path.is_file() and path.name != "feishu-secret.txt":
            assert SECRET not in path.read_text(encoding="utf-8")
    for path in repo.rglob("*"):
        if path.is_file():
            assert SECRET not in path.read_text(encoding="utf-8", errors="ignore")


def test_web_api_requires_owner_session(ext, repo, feishu_cfg):  # noqa: F811
    write_config(feishu_cfg)
    feishu.remember({"open_id": "ou_web", "name": "网页上来的", "union_id": "on_web", "tenant_key": "tk"})
    feishu.remember({"open_id": "ou_skip", "name": "忽略", "union_id": "", "tenant_key": ""})
    body = {"open_id": "ou_web", "key": "ext-zhang"}
    with pytest.raises(BaseError, match=OWNER_NAME):
        ext("codex").feishu_bind("ou_web", "ext-zhang")
    c = TestClient(build_app(repo, WEB_PORT, TOKEN), base_url=WEB_BASE, follow_redirects=False)
    assert c.post("/api/feishu/bind", json=body).status_code == 401
    assert c.post("/api/feishu/unbind", json={"key": "ext-zhang"}).status_code == 401
    assert c.post("/api/feishu/dismiss", json={"open_id": "ou_skip"}).status_code == 401
    assert c.get(f"/open?t={TOKEN}").status_code == 303
    assert c.post("/api/feishu/bind", json=body).status_code == 403
    assert c.post("/api/feishu/bind", json=body, headers=POST_HEADERS).status_code == 200
    assert "ou_web" in (repo / "people/ext-zhang.md").read_text(encoding="utf-8")
    members = c.get("/api/members").json()
    assert members["feishu_pending"][0]["open_id"] == "ou_skip"
    me = next(item for item in members["items"] if item["key"] == "ext-zhang")
    assert me["feishu_bound"] is True
    assert c.post("/api/feishu/dismiss", json={"open_id": "ou_skip"}, headers=POST_HEADERS).status_code == 200
    assert feishu.list_pending() == []
    assert c.post("/api/feishu/unbind", json={"key": "ext-zhang"}, headers=POST_HEADERS).status_code == 200
    assert "feishu_open_id" not in (repo / "people/ext-zhang.md").read_text(encoding="utf-8")


def test_logout_revokes_feishu_session_but_not_token(ext, repo, feishu_cfg, monkeypatch):  # noqa: F811
    """退出时飞书会话在服务器上一起收回；主人发的口令退出不收回。"""
    write_config(feishu_cfg)
    patch_user(monkeypatch)
    plant(repo, "people/ext-zhang.md", "ou_wang")
    git(repo, "commit", "-qam", "绑飞书")
    with portal_client(repo) as c:
        _got, state = begin(c)
        assert "登录成功" in callback(c, state).text
        session = c.cookies.get(f"cb_web_dev_{PORT}")
        csrf = c.get("/api/me").json()["csrf"]
        assert c.post("/auth/logout", headers={"X-CSRF-Token": csrf}).status_code == 303
        assert tokens.lookup_record(session) is None
        token, _ = tokens.issue("ext-zhang", "测试")
        c.cookies.clear()
        challenge = re.search(r'name="challenge" value="([^"]+)"', c.get("/login").text)[1]
        assert c.post("/auth/login", data={"token": token, "challenge": challenge}).status_code == 303
        csrf = c.get("/api/me").json()["csrf_token"]
        assert c.post("/auth/logout", headers={"X-CSRF-Token": csrf}).status_code == 303
        assert tokens.lookup(token) == "ext-zhang"
