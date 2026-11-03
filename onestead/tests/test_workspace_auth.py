"""共用工作台的认证边界：身份、会话、来源与远程默认关闭的真实路由。"""

from .support import OWNER, OWNER_NAME

import datetime as dt
import hashlib
import hmac
import json
import os
import re
from contextlib import asynccontextmanager
from pathlib import Path

import pytest
from starlette.testclient import TestClient

from onestead import feishu, server, tokens
from onestead.base import CompanyBase
from onestead.store import BaseError, Store, now_taipei
from onestead.web.app import ROUTE_POLICY, build_app
from onestead.web.auth import RemoteAuth

from .conftest import git
from .support import (
    INFO,
    PUBLIC_HOST,
    WORKSPACE_COOKIE as COOKIE,
    WORKSPACE_ORIGIN as ORIGIN,
    WORKSPACE_PORT as PORT,
    WORKSPACE_SOURCE as SOURCE,
    auth_query,
    challenge,
    login,
    patch_user,
    plant,
    signed,
    write_config,
)

MCP_BODY = {"jsonrpc": "2.0", "id": 1, "method": "tools/call",
            "params": {"name": "whoami", "arguments": {}}}


def security(response):
    assert response.headers["cache-control"] == "no-store"
    assert response.headers["x-content-type-options"] == "nosniff"
    assert "frame-ancestors 'none'" in response.headers["content-security-policy"]
    assert "unsafe-inline" not in response.headers["content-security-policy"]
    assert "access-control-allow-origin" not in response.headers


def test_local_shortcut_keeps_cookie_host_and_business(repo):
    origin, password = f"http://127.0.0.1:{PORT}", "temporary-local-shortcut-token"
    app = build_app(repo, PORT, password)
    with TestClient(app, base_url=origin, follow_redirects=False) as client:
        assert client.get("/").status_code == 403
        assert client.get("/open?t=wrong").status_code == 403
        response = client.get("/open", params={"t": password})
        assert response.status_code == 303 and response.headers["location"] == "/"
        assert client.cookies.get(f"cb_session_{PORT}") == password
        assert client.get("/api/members").status_code == 200
        me = client.get("/api/me").json()
        assert me["is_owner"] and not me["remote"] and me["key"] == OWNER
        assert "approvals" in me["navigation"]
        for host in (f"localhost:{PORT}", f"127.0.0.1:{PORT + 1}", f"127.0.0.1.:{PORT}"):
            response = client.get("/api/members", headers={"Host": host})
            assert response.status_code == 403
            security(response)
        before = app.state.last_beat
        assert client.post("/api/heartbeat", json={}, headers={"Origin": origin,
                           "X-Requested-By": "company-base"}).status_code == 200
        assert app.state.last_beat >= before


def test_human_login_me_and_absolute_session(workspace):
    assert workspace.get("/").headers["location"] == "/login"
    page = workspace.get("/login")
    assert "阿尔法" not in page.text and "ext-zhang" not in page.text
    assert page.headers["referrer-policy"] == "same-origin"
    response, password, source = login(workspace)
    assert response.status_code == 303 and response.headers["location"] == "/"
    header = next(h for h in response.headers.get_list("set-cookie") if h.startswith(COOKIE + "="))
    flags = [f.strip().lower() for f in header.split(";")[1:]]
    assert {"path=/", "secure", "httponly", "samesite=strict", "max-age=2592000"} <= set(flags)
    assert not any(f.startswith("domain=") for f in flags)
    value = workspace.cookies.get(COOKIE)
    record = tokens.lookup_record(value)
    assert record["kind"] == "web" and record["auth_method"] == "token"
    assert record["source_token_id"] == source["id"] and record["binding_id"] is None
    assert dt.timedelta(days=29) < dt.datetime.fromisoformat(record["expires"]) - now_taipei() <= dt.timedelta(days=30)
    raw = Path(os.environ["COMPANY_BASE_TOKENS"]).read_text(encoding="utf-8")
    assert value not in raw and password not in raw
    me = workspace.get("/api/me", params={"key": OWNER, "owner": OWNER, "is_owner": True},
                       headers={"X-Actor-Key": OWNER}).json()
    assert me["key"] == "ext-zhang" and me["kind"] == "human" and not me["is_owner"] and me["remote"]
    assert me["navigation"] == ["home", "activity", "todos", "projects", "apps", "agents", "skills", "docs", "decisions", "members"]
    assert me["csrf"] == hmac.new(value.encode(), b"company-base:web-csrf:v1", hashlib.sha256).hexdigest()
    assert not {"pending", "members", "projects", "assets", "feishu_pending", "tokens"} & me.keys()
    shell = workspace.get("/")
    assert shell.status_code == 200 and shell.headers["referrer-policy"] == "same-origin"
    assert OWNER_NAME not in shell.text
    security(shell)
    assert workspace.get("/open?t=whatever").status_code == 404
    assert workspace.get("/static/app.js").status_code == 200
    assert workspace.get("/static/index.html").status_code == 404
    expires = record["expires"]
    headers = signed(workspace)
    assert workspace.post("/api/heartbeat", json={"key": OWNER}, headers=headers).status_code == 200
    assert tokens.lookup_record(value)["expires"] == expires
    assert not hasattr(workspace.app.state, "last_beat")


@pytest.mark.parametrize("key,mutation", [(OWNER, None), ("ext-zhang-claude", None),
                                         ("ext-zhang", "disabled"), ("ext-zhang", "invalid"),
                                         ("ext-zhang", "duplicate"), ("missing", None)])
def test_unqualified_identities_cannot_login(workspace, repo, key, mutation):
    path = repo / "people/ext-zhang.md"
    if mutation == "disabled":
        path.write_text(path.read_text(encoding="utf-8").replace("remote_enabled: true", "remote_enabled: false"), encoding="utf-8")
    elif mutation == "invalid":
        path.write_text(path.read_text(encoding="utf-8").replace("kind: human", "kind: invalid"), encoding="utf-8")
    elif mutation == "duplicate":
        (repo / "people/duplicate.md").write_text(path.read_text(encoding="utf-8").replace("id: ext-zhang", "id: duplicate"), encoding="utf-8")
    response, _, _ = login(workspace, key)
    assert response.status_code == 401 and "登录失败" in response.text
    assert workspace.cookies.get(COOKIE) is None
    security(response)


@pytest.mark.parametrize("host", ["evil.example.test", PUBLIC_HOST + ".", PUBLIC_HOST + ":443", PUBLIC_HOST + ":80",
                                 f"127.0.0.1:{PORT}"])
@pytest.mark.parametrize("path", ["/", "/login", "/static/app.js", "/api/me", "/does-not-exist"])
def test_web_host_is_exact_even_for_static_and_unknown(workspace, host, path):
    response = workspace.get(path, headers={"Host": host, "X-Forwarded-Host": PUBLIC_HOST,
                                           "X-Forwarded-Proto": "https"})
    if host == f"127.0.0.1:{PORT}":
        assert response.status_code == 403
        security(response)
    else:
        # 非底座 Host 在最外层分流，连 guard 都不进入，统一纯文本 404。
        assert response.status_code == 404 and response.text == "找不到"
        assert "X-Workspace-Context" not in response.headers


def test_duplicate_host_and_proxy_cannot_change_origin(workspace):
    response = workspace.get("/login", headers=[("Host", PUBLIC_HOST), ("Host", PUBLIC_HOST)])
    assert response.status_code == 404 and response.text == "找不到"
    value = challenge(workspace)
    response = workspace.post("/auth/login", data={"challenge": value, "token": "unused"},
                              headers={"Origin": "http://" + PUBLIC_HOST, "X-Forwarded-Proto": "https"})
    assert response.status_code == 403


@pytest.mark.parametrize("origin,site", [(None, None), ("null", None), (f"http://{PUBLIC_HOST}", None),
                                       (ORIGIN + ":443", None), (ORIGIN + ":8765", None),
                                       (ORIGIN, "cross-site"), (ORIGIN, "same-site")])
def test_login_origin_failures(workspace, origin, site):
    password, _ = tokens.issue("ext-zhang", "测试")
    value = challenge(workspace)
    headers = {}
    if origin is not None:
        headers["Origin"] = origin
    if site is not None:
        headers["Sec-Fetch-Site"] = site
    response = workspace.post("/auth/login", data={"token": password, "challenge": value}, headers=headers)
    assert response.status_code == 403 and workspace.cookies.get(COOKIE) is None
    security(response)


def test_login_challenge_mismatch_replay_and_expiry(workspace, monkeypatch):
    password, _ = tokens.issue("ext-zhang", "测试")
    value = challenge(workspace)
    assert workspace.post("/auth/login", data={"token": password, "challenge": "wrong"}, headers=SOURCE).status_code == 403
    workspace.cookies.clear()
    value = challenge(workspace)
    cookie = workspace.cookies.get("__Host-cb_login")
    assert workspace.post("/auth/login", data={"token": password, "challenge": value}, headers=SOURCE).status_code == 303
    workspace.cookies.set("__Host-cb_login", cookie, domain=PUBLIC_HOST, path="/")
    assert workspace.post("/auth/login", data={"token": password, "challenge": value}, headers=SOURCE).status_code == 403
    value = challenge(workspace)
    later = now_taipei() + dt.timedelta(minutes=11)
    monkeypatch.setattr(tokens, "now_taipei", lambda: later)
    assert workspace.post("/auth/login", data={"token": password, "challenge": value}, headers=SOURCE).status_code == 403


@pytest.mark.parametrize("unknown", ["mystery", "", None, ["token"]])
def test_unknown_kind_is_never_token(workspace, unknown):
    value, _ = tokens.issue("ext-zhang", "未知种类")
    path = Path(os.environ["COMPANY_BASE_TOKENS"])
    items = json.loads(path.read_text(encoding="utf-8"))
    items[0]["kind"] = unknown
    path.write_text(json.dumps(items), encoding="utf-8")
    assert tokens.lookup_record(value) is None
    response, _, _ = login(workspace, password=value)
    assert response.status_code == 401
    h = {"Authorization": f"Bearer {value}", "Accept": "application/json, text/event-stream"}
    assert workspace.post("/mcp", json=MCP_BODY, headers=h).status_code == 401
    assert workspace.get("/skills/anything.zip", headers=h).status_code == 401


def test_missing_kind_legacy_token_and_credential_separation(workspace):
    password, source = tokens.issue("ext-zhang", "旧口令")
    path = Path(os.environ["COMPANY_BASE_TOKENS"])
    items = json.loads(path.read_text(encoding="utf-8"))
    del items[0]["kind"]
    path.write_text(json.dumps(items), encoding="utf-8")
    assert tokens.lookup_record(password)["kind"] == "token"
    workspace.cookies.set(COOKIE, password, domain=PUBLIC_HOST, path="/")
    assert workspace.get("/api/me").status_code == 401
    workspace.cookies.clear()
    assert workspace.get("/api/me", headers={"Authorization": f"Bearer {password}"}).status_code == 401
    assert login(workspace, password=password)[0].status_code == 303
    value = workspace.cookies.get(COOKIE)
    h = {"Authorization": f"Bearer {value}", "Accept": "application/json, text/event-stream"}
    assert workspace.post("/mcp", json=MCP_BODY, headers=h).status_code == 401
    assert workspace.get("/skills/anything.zip", headers=h).status_code == 401
    workspace.cookies.set("cb_portal", value, domain=PUBLIC_HOST, path="/portal")
    response = workspace.get("/portal")
    assert response.status_code == 303 and response.headers["location"] == "/"
    assert workspace.get("/api/me").json()["key"] == "ext-zhang"
    h["Authorization"] = f"Bearer {password}"
    assert workspace.post("/mcp", json=MCP_BODY, headers=h).status_code == 200


@pytest.mark.parametrize("change", ["revoke_source", "expire_source", "revoke_session", "expire_session", "disable", "invalid"])
def test_session_is_rechecked_on_next_api(workspace, repo, change):
    response, _, source = login(workspace)
    assert response.status_code == 303
    value = workspace.cookies.get(COOKIE)
    if change == "revoke_source":
        tokens.revoke(source["id"])
    elif change == "revoke_session":
        tokens.revoke(tokens.lookup_record(value)["id"])
    elif change.startswith("expire_"):
        path = Path(os.environ["COMPANY_BASE_TOKENS"])
        items = json.loads(path.read_text(encoding="utf-8"))
        target = source["id"] if change == "expire_source" else tokens.lookup_record(value)["id"]
        next(i for i in items if i["id"] == target)["expires"] = "2000-01-01T00:00:00+08:00"
        path.write_text(json.dumps(items), encoding="utf-8")
    elif change == "disable":
        owner = CompanyBase(repo, OWNER, allow_owner=True)
        source = owner.doc_source("people/ext-zhang.md")
        owner.save_doc("people/ext-zhang.md", source["version"], {"remote_enabled": False}, None)
        assert tokens.lookup_record(value) is None
    else:
        path = repo / "people/ext-zhang.md"
        path.write_text(path.read_text(encoding="utf-8").replace("kind: human", "kind: invalid"), encoding="utf-8")
    response = workspace.get("/api/me")
    assert response.status_code == 401
    security(response)


def test_logout_and_relogin_revoke_only_web(workspace):
    response, password, source = login(workspace)
    first = workspace.cookies.get(COOKIE)
    headers = signed(workspace)
    assert login(workspace, password=password)[0].status_code == 303
    assert tokens.lookup_record(first) is None
    second = workspace.cookies.get(COOKIE)
    assert workspace.post("/api/heartbeat", json={}, headers=headers).status_code == 403
    assert workspace.post("/auth/logout", headers={"Origin": ORIGIN}).status_code == 403
    response = workspace.post("/auth/logout", headers=signed(workspace))
    assert response.status_code == 303 and response.headers["location"] == "/login"
    assert workspace.cookies.get(COOKIE) is None and tokens.lookup_record(second) is None
    assert tokens.lookup_record(password)["id"] == source["id"]


def test_all_closed_routes_refuse_before_side_effects(workspace, repo, monkeypatch, feishu_cfg):
    write_config(feishu_cfg)
    assert login(workspace)[0].status_code == 303
    before = git(repo, "status", "--porcelain"), git(repo, "rev-parse", "HEAD")
    called = []
    monkeypatch.setattr(os, "startfile", lambda *args: called.append(args), raising=False)
    for (method, template), (_, allowed) in ROUTE_POLICY.items():
        if allowed:
            continue
        path = re.sub(r"\{[^}]+\}", "anything", template)
        response = workspace.request(method, path, json={"key": OWNER, "owner": OWNER, "is_owner": True},
                                     headers=signed(workspace))
        assert response.status_code == 403 and response.json()["error"] == "这个功能远程还没开放", path
        security(response)
    assert workspace.get("/api/not-in-policy").status_code == 403
    assert workspace.head("/api/home").status_code == 200
    assert not called
    assert before == (git(repo, "status", "--porcelain"), git(repo, "rev-parse", "HEAD"))


@pytest.mark.parametrize("method,path", [("POST", "/api/source/save"),
                                         ("POST", "/api/apps/demo/open")])
def test_required_business_endpoints_are_closed(workspace, method, path):
    assert login(workspace)[0].status_code == 303
    response = workspace.request(method, path, json={}, headers=signed(workspace))
    assert response.status_code == 403 and response.json() == {"error": "这个功能远程还没开放"}


def test_every_registered_api_has_an_explicit_policy(workspace):
    routes = {(method, route.path) for route in workspace.app.routes if route.path.startswith("/api/")
              for method in route.methods if method != "HEAD"}
    assert routes == set(ROUTE_POLICY)


def test_revocation_before_response_prevents_api_output(workspace, monkeypatch):
    assert login(workspace)[0].status_code == 303
    auth = workspace.app.state.auth
    authenticate = auth.authenticate
    calls = []
    value = workspace.cookies.get(COOKIE)
    source_id = tokens.lookup_record(value)["source_token_id"]
    def check(request):
        calls.append(1)
        if len(calls) == 2:
            tokens.revoke(source_id)
        return authenticate(request)
    monkeypatch.setattr(auth, "authenticate", check)
    response = workspace.get("/api/me")
    assert response.status_code == 401 and "ext-zhang" not in response.text
    assert len(calls) == 2


@pytest.mark.parametrize("missing", ["Origin", "X-CSRF-Token", "X-Requested-By"])
def test_heartbeat_requires_full_write_headers(workspace, missing):
    assert login(workspace)[0].status_code == 303
    headers = signed(workspace)
    del headers[missing]
    response = workspace.post("/api/heartbeat", json={}, headers=headers)
    assert response.status_code == 403
    security(response)


def test_limits_json_errors_and_unknown_exception(workspace, monkeypatch):
    challenge(workspace)
    response = workspace.post("/auth/login", content=b"x" * (8192 + 1), headers=SOURCE)
    assert response.status_code == 413
    assert login(workspace)[0].status_code == 303
    headers = signed(workspace)
    assert workspace.post("/api/heartbeat", content=b"x" * (256 * 1024 + 1), headers=headers).status_code == 413
    for body in (b"not-json", b"[]", b"null"):
        assert workspace.post("/api/heartbeat", content=body, headers=headers).status_code == 400
    def fail(value):
        raise RuntimeError("私有异常和路径 C:/secret")
    monkeypatch.setattr(tokens, "lookup_web", fail)
    response = workspace.get("/api/me")
    assert response.status_code == 500 and "secret" not in response.text and "私有异常" not in response.text
    security(response)


def test_explicit_dev_configuration_and_configured_public_host(ext, repo, monkeypatch):  # noqa: F811
    local = f"http://127.0.0.1:{PORT}"
    with TestClient(server.remote_app(str(repo), PORT, public_host=PUBLIC_HOST, web_test_origin=local),
                    base_url=local, follow_redirects=False) as client:
        password, _ = tokens.issue("ext-zhang", "开发测试")
        response = client.post("/auth/login", data={"token": password, "challenge": challenge(client)},
                               headers={"Origin": local})
        header = response.headers.get_list("set-cookie")[0]
        assert response.status_code == 303 and header.startswith(f"cb_web_dev_{PORT}=")
        assert "secure" not in [p.strip().lower() for p in header.split(";")[1:]]
        assert client.get("/api/me").json()["remote"]
    with pytest.raises(BaseError):
        RemoteAuth(repo, PUBLIC_HOST, test_origin="http://other.example.test:80")
    monkeypatch.setenv("COMPANY_BASE_PUBLIC_HOST", "configured.example.test")
    with TestClient(server.remote_app(str(repo), PORT), base_url="https://configured.example.test") as client:
        assert client.get("/login").status_code == 200
        assert client.get("/login", headers={"Host": PUBLIC_HOST}).status_code == 404


@pytest.mark.parametrize("state_mode", ["wrong", "expired", "reused"])
def test_feishu_state_is_consumed_before_exchange(workspace, feishu_cfg, monkeypatch, state_mode):
    write_config(feishu_cfg)
    calls = patch_user(monkeypatch)
    response = workspace.get("/portal/feishu/login")
    state = auth_query(response)["state"][0]
    if state_mode == "wrong":
        state = "wrong"
    elif state_mode == "expired":
        later = now_taipei() + dt.timedelta(minutes=11)
        monkeypatch.setattr(tokens, "now_taipei", lambda: later)
    else:
        assert tokens.consume_challenge(state, "feishu")
    response = workspace.get("/portal/feishu/callback", params={"state": state, "code": "测试授权码"})
    assert calls == [] and feishu.USER_FAIL in response.text and workspace.cookies.get(COOKIE) is None
    assert any("cb_feishu_state=" in h and "Max-Age=0" in h for h in response.headers.get_list("set-cookie"))
    security(response)


@pytest.mark.parametrize("failure", ["challenge", "config"])
def test_feishu_callback_clears_state_on_internal_failure(workspace, feishu_cfg, monkeypatch, failure):
    write_config(feishu_cfg)
    calls = patch_user(monkeypatch)
    state = auth_query(workspace.get("/portal/feishu/login"))["state"][0]
    def fail(*args):
        raise RuntimeError("不应出现在响应里的内部详情")
    monkeypatch.setattr(tokens if failure == "challenge" else feishu,
                        "consume_challenge" if failure == "challenge" else "load_app", fail)
    response = workspace.get("/portal/feishu/callback", params={"state": state, "code": "测试授权码"})
    assert feishu.USER_FAIL in response.text and "内部详情" not in response.text and calls == []
    assert workspace.cookies.get("cb_feishu_state") is None
    assert any("Max-Age=0" in h for h in response.headers.get_list("set-cookie"))


def test_feishu_pending_then_web_binding_revalidation(workspace, repo, feishu_cfg, monkeypatch):
    write_config(feishu_cfg)
    calls = patch_user(monkeypatch)
    state = auth_query(workspace.get("/portal/feishu/login"))["state"][0]
    response = workspace.get("/portal/feishu/callback", params={"state": state, "code": "测试授权码"})
    assert "还没在底座登记" in response.text and workspace.cookies.get(COOKIE) is None
    owner = CompanyBase(repo, OWNER, allow_owner=True)
    owner.feishu_bind(INFO["open_id"], "ext-zhang")
    password, _ = tokens.issue("ext-zhang", "长期口令")
    state = auth_query(workspace.get("/portal/feishu/login"))["state"][0]
    response = workspace.get("/portal/feishu/callback", params={"state": state, "code": "测试授权码"})
    assert '<meta http-equiv="refresh" content="0;url=/">' in response.text and 'href="/">进入底座</a>' in response.text
    assert "<script" not in response.text and response.headers["referrer-policy"] == "same-origin"
    value = workspace.cookies.get(COOKIE)
    record = tokens.lookup_record(value)
    assert record["kind"] == "web" and record["auth_method"] == "feishu" and record["source_token_id"] is None
    assert record["binding_id"] == INFO["open_id"] and workspace.get("/api/me").json()["key"] == "ext-zhang"
    assert all(r["kind"] != "feishu" for r in tokens.list_tokens("ext-zhang"))
    previous_calls = list(calls)
    workspace.cookies.set("cb_feishu_state", state, domain=PUBLIC_HOST, path="/portal/feishu")
    workspace.get("/portal/feishu/callback", params={"state": state, "code": "测试授权码"})
    assert calls == previous_calls
    owner.feishu_unbind("ext-zhang")
    assert tokens.lookup_record(value) is None and workspace.get("/api/me").status_code == 401
    assert tokens.lookup(password) == "ext-zhang"


def test_binding_change_invalidates_web_even_without_manage(workspace, repo, feishu_cfg, monkeypatch):
    write_config(feishu_cfg)
    patch_user(monkeypatch)
    plant(repo, "people/ext-zhang.md", INFO["open_id"])
    state = auth_query(workspace.get("/portal/feishu/login"))["state"][0]
    workspace.get("/portal/feishu/callback", params={"state": state, "code": "测试授权码"})
    path = repo / "people/ext-zhang.md"
    path.write_text(path.read_text(encoding="utf-8").replace(INFO["open_id"], "ou_new"), encoding="utf-8")
    assert workspace.get("/api/me").status_code == 401


@pytest.mark.parametrize("key", [OWNER, "ext-zhang-claude"])
def test_feishu_owner_and_agent_never_receive_web_session(workspace, repo, feishu_cfg, monkeypatch, key):
    write_config(feishu_cfg)
    patch_user(monkeypatch)
    plant(repo, f"people/{key}.md", INFO["open_id"])
    state = auth_query(workspace.get("/portal/feishu/login"))["state"][0]
    response = workspace.get("/portal/feishu/callback", params={"state": state, "code": "测试授权码"})
    assert "不能登录底座" in response.text and workspace.cookies.get(COOKIE) is None
    assert not any(i["kind"] == "web" for i in tokens.list_tokens())


def test_real_mcp_mount_preserves_lifespan(ext, repo, monkeypatch):  # noqa: F811
    original = server.build_server
    events = []
    def build(*args, **kwargs):
        entry = "chatgpt" if kwargs.get("oauth_provider") else "legacy"
        mcp = original(*args, **kwargs)
        original_app = mcp.streamable_http_app
        def streamable(*args, **kwargs):
            app = original_app(*args, **kwargs)
            original_context = app.router.lifespan_context
            @asynccontextmanager
            async def lifespan(child):
                events.append(f"enter:{entry}")
                async with original_context(child):
                    yield
                events.append(f"exit:{entry}")
            app.router.lifespan_context = lifespan
            return app
        mcp.streamable_http_app = streamable
        return mcp
    monkeypatch.setattr(server, "build_server", build)
    app = server.remote_app(str(repo), PORT, public_host=PUBLIC_HOST)
    assert [r.path for r in app.routes][-1] == ""
    with TestClient(app, base_url=ORIGIN) as client:
        assert events == ["enter:legacy", "enter:chatgpt"]
        password, _ = tokens.issue("ext-zhang", "机器")
        headers = {"Authorization": f"Bearer {password}", "Accept": "application/json, text/event-stream"}
        response = client.post("/mcp", json=MCP_BODY, headers=headers)
        assert response.status_code == 200 and "ext-zhang" in response.text
        assert client.get("/skills/not-registered.zip", headers=headers).status_code == 404
        assert client.get("/login").status_code == 200
    assert events == ["enter:legacy", "enter:chatgpt", "exit:chatgpt", "exit:legacy"]


def test_revoked_password_still_blocks_write_after_doc_cache(workspace, repo, monkeypatch):
    """请求开头可以缓存文档。写之前仍要重新核对口令：中途收回源口令，这次写入被拒，进展不会留下。"""
    response, _password, record = login(workspace)
    assert response.status_code == 303
    csrf = workspace.get("/api/me").json()["csrf_token"]
    real = Store.load
    seen = {"n": 0}

    def wrapped(self):
        docs = real(self)
        seen["n"] += 1
        if seen["n"] == 1:
            tokens.revoke(record["id"])
        return docs

    monkeypatch.setattr(Store, "load", wrapped)
    posted = workspace.post("/api/project/alpha/status", json={"text": "中途收回后不该留下"},
                            headers=SOURCE | {"X-CSRF-Token": csrf})
    assert posted.status_code == 401
    assert "会话已失效" in posted.json()["error"]
    assert "中途收回后不该留下" not in (repo / "projects/alpha/status.md").read_text(encoding="utf-8")
