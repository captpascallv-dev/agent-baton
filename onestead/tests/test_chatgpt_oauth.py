"""ChatGPT OAuth 全流程与隔离边界；所有数据、凭据和提交均在临时目录。"""

from .support import OWNER, OWNER_NAME

import base64
import datetime as dt
import hashlib
import json
import re
from contextlib import contextmanager
from concurrent.futures import ThreadPoolExecutor
from urllib.parse import parse_qs, urlsplit

import pytest
from starlette.testclient import TestClient

from onestead import gitops, tokens
from onestead.base import CompanyBase
from onestead.web.oauth import CALLBACK_ALLOWLIST
from onestead.redact import safe_text
from onestead.server import remote_app
from onestead.store import BaseError, Store, make_viewer, now_taipei
from onestead.textutil import _dump_front
from onestead.web import guard, views

from .conftest import git

from .support import WORKSPACE_ORIGIN

ORIGIN = WORKSPACE_ORIGIN
RESOURCE = ORIGIN + "/chatgpt/mcp"
CALLBACK = "https://chatgpt.com/connector_platform_oauth_redirect"
VERIFIER = "test-pkce-verifier-" + "x" * 48
CHALLENGE = base64.urlsafe_b64encode(hashlib.sha256(VERIFIER.encode()).digest()).decode().rstrip("=")


def admin(repo):
    return CompanyBase(repo, OWNER, allow_owner=True)


@pytest.fixture
def client(ext, repo):  # noqa: F811
    path = repo / "people/chatgpt.md"
    path.write_text(_dump_front({"id": "chatgpt", "type": "person", "title": "ChatGPT",
                                "agent_key": "chatgpt", "kind": "agent", "owner": OWNER,
                                "remote_enabled": True, "access_mode": "full", "can_write": True,
                                "sensitivity": "internal"}) + "\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "登记测试 ChatGPT")
    with TestClient(remote_app(str(repo), 48721), base_url=ORIGIN, follow_redirects=False) as c:
        yield c


def register(client, *, redirect=CALLBACK, method="none"):
    return client.post("/register", json={"redirect_uris": [redirect], "token_endpoint_auth_method": method,
                                         "grant_types": ["authorization_code", "refresh_token"],
                                         "response_types": ["code"], "client_name": f"冒充 {OWNER_NAME}"})


def registered(client, **kwargs):
    response = register(client, **kwargs)
    assert response.status_code == 201
    return response.json()


def authorize_params(info, **changes):
    return {"client_id": info["client_id"], "redirect_uri": info["redirect_uris"][0],
            "response_type": "code", "code_challenge": CHALLENGE, "code_challenge_method": "S256",
            "state": "原样 + & 中文", "resource": RESOURCE, **changes}


def form_page(client, info, **changes):
    response = client.get("/authorize", params=authorize_params(info, **changes))
    assert response.status_code == 302
    response = client.get(response.headers["location"])
    assert response.status_code == 200
    form = {name: re.search(r'name="' + name + r'" value="([^"]+)"', response.text)[1]
            for name in ("request", "challenge")}
    return response, form


def code(client, repo, info=None, key="chatgpt"):
    info = info or registered(client)
    source = admin(repo).token_issue(key, "OAuth 测试")
    _, form = form_page(client, info)
    response = client.post("/chatgpt/consent", data={**form, "token": source["token"]}, headers={"Origin": ORIGIN})
    assert response.status_code == 303
    query = parse_qs(urlsplit(response.headers["location"]).query)
    assert query["state"] == ["原样 + & 中文"] and query["iss"] == [ORIGIN]
    return info, source, query["code"][0]


def exchange(client, info, value, **changes):
    form = {"grant_type": "authorization_code", "client_id": info["client_id"], "code": value,
            "redirect_uri": info["redirect_uris"][0], "code_verifier": VERIFIER, "resource": RESOURCE, **changes}
    return client.post("/token", data=form)


def linked(client, repo, **kwargs):
    info, source, value = code(client, repo, **kwargs)
    response = exchange(client, info, value)
    assert response.status_code == 200
    return info, source, value, response.json()


def refresh(client, info, pair, **changes):
    return client.post("/token", data={"grant_type": "refresh_token", "client_id": info["client_id"],
                                       "refresh_token": pair["refresh_token"], "resource": RESOURCE, **changes})


def rpc(client, value, method="tools/list", *, path="/chatgpt/mcp", name=None, arguments=None):
    params = {"name": name, "arguments": arguments or {}} if name else {}
    return client.post(path, headers={"Authorization": "Bearer " + value,
                                     "Accept": "application/json, text/event-stream",
                                     "MCP-Protocol-Version": "2025-06-18"},
                       json={"jsonrpc": "2.0", "id": 1, "method": method, "params": params})


def call(client, value, name, arguments=None, **kwargs):
    response = rpc(client, value, "tools/call", name=name, arguments=arguments, **kwargs)
    assert response.status_code == 200
    return response.json()["result"]


def text(result):
    return "".join(c.get("text", "") for c in result["content"])


def test_complete_flow_identity_tools_write_and_refresh(client, repo):
    info, source, value, pair = linked(client, repo)
    tool_list = rpc(client, pair["access_token"]).json()["result"]["tools"]
    tools = {t["name"]: t for t in tool_list}
    assert "fetch" in tools and list(tools["search"]["inputSchema"]["properties"]) == ["query"]
    writes = {"create_project", "update_project_status", "update_project_summary", "record_decision",
              "handover_role", "add_todos", "update_todo", "register_app", "update_app",
              "register_skill", "update_skill", "propose_home"}
    for tool in tool_list:
        assert tool["annotations"]["readOnlyHint"] == (tool["name"] not in writes)
        assert tool["annotations"]["openWorldHint"] is False
        if tool["name"] in writes:
            assert tool["annotations"]["destructiveHint"] is False
    assert tools["search"]["outputSchema"] and tools["fetch"]["outputSchema"]
    assert "chatgpt" in text(call(client, pair["access_token"], "whoami"))
    assert "阿尔法" in text(call(client, pair["access_token"], "get_project", {"project_id": "alpha"}))
    search = call(client, pair["access_token"], "search", {"query": "阿尔法"})
    assert not search.get("isError")
    assert json.loads(text(search)) == search["structuredContent"]
    found = next(r for r in search["structuredContent"]["results"] if r["id"] == "projects/alpha/project.md")
    assert found["url"] == ORIGIN + "/#/docs/projects/alpha/project.md"
    fetched = call(client, pair["access_token"], "fetch", {"id": found["id"]})
    assert json.loads(text(fetched)) == fetched["structuredContent"]
    assert "还行" in fetched["structuredContent"]["text"] and "metadata" in fetched["structuredContent"]
    assert "C:\\" not in text(fetched) and "（本机路径）" not in found["url"]
    result = call(client, pair["access_token"], "update_project_status", {"project_id": "alpha", "text": "ChatGPT 记的进展"})
    assert not result.get("isError") and git(repo, "log", "-1", "--format=%an") == "chatgpt"
    refreshed = refresh(client, info, pair)
    assert refreshed.status_code == 200
    new = refreshed.json()
    assert new["access_token"] != pair["access_token"] and new["refresh_token"] != pair["refresh_token"]
    assert rpc(client, pair["access_token"]).status_code == 401
    assert rpc(client, new["access_token"]).status_code == 200
    # 重启应用仍能认已登记客户端和凭据。
    with TestClient(remote_app(str(repo), 48722), base_url=ORIGIN) as again:
        assert rpc(again, new["access_token"]).status_code == 200
    raw = tokens.store_path().read_text(encoding="utf-8")
    assert all(secret not in raw for secret in (source["token"], value, *[pair[k] for k in ("access_token", "refresh_token")]))
    rows = tokens.list_tokens("chatgpt")
    assert any(r["kind"] == "oauth" and r["live"] for r in rows)
    assert all(r["kind"] in ("token", "oauth") for r in rows)
    members = views.member_list(Store(repo).load(), rows)
    assert any(r["kind"] == "oauth" and r["live"] for m in members if m["key"] == "chatgpt" for r in m["sessions"])


def test_discovery_and_unauthorized_challenge(client):
    response = rpc(client, "cbx_invalid")
    assert response.status_code == 401
    metadata_url = ORIGIN + "/.well-known/oauth-protected-resource/chatgpt/mcp"
    assert f'resource_metadata="{metadata_url}"' in response.headers["www-authenticate"]
    resource = client.get(metadata_url).json()
    assert resource["resource"] == RESOURCE and resource["authorization_servers"] == [ORIGIN]
    auth = client.get("/.well-known/oauth-authorization-server").json()
    assert auth["issuer"] == ORIGIN and auth["code_challenge_methods_supported"] == ["S256"]
    assert auth["registration_endpoint"] == ORIGIN + "/register"
    assert "none" in auth["token_endpoint_auth_methods_supported"]
    assert auth["authorization_response_iss_parameter_supported"] is True


@pytest.mark.parametrize("method", ["client_secret_post", "client_secret_basic"])
def test_confidential_dcr_secret_is_hashed(client, repo, method):
    info = registered(client, method=method)
    info, _, value = code(client, repo, info)
    form = {"grant_type": "authorization_code", "client_id": info["client_id"], "code": value,
            "redirect_uri": CALLBACK, "code_verifier": VERIFIER, "resource": RESOURCE}
    if method == "client_secret_post":
        form["client_secret"] = info["client_secret"]
        response = client.post("/token", data=form)
    else:
        response = client.post("/token", data=form, auth=(info["client_id"], info["client_secret"]))
    assert response.status_code == 200
    assert info["client_secret"] not in tokens.store_path().read_text(encoding="utf-8")
    form["client_secret"] = "错的"
    assert client.post("/token", data=form).status_code == 401


@pytest.mark.parametrize("changes", [{"code_verifier": "wrong" + "x" * 43}, {"resource": ORIGIN + "/mcp"},
                                      {"resource": ""}, {"redirect_uri": "https://chatgpt.com/connector/oauth/other"},
                                      {"redirect_uri": CALLBACK + "?unexpected=1"}, {"client_id": "unknown"}])
def test_bad_token_exchange(client, repo, changes):
    info, _, value = code(client, repo)
    assert exchange(client, info, value, **changes).status_code in (400, 401)


def test_code_reuse_revokes_tokens_including_refreshed_chain(client, repo):
    info, _, value, pair = linked(client, repo)
    new = refresh(client, info, pair).json()
    assert exchange(client, info, value).status_code == 400
    assert rpc(client, new["access_token"]).status_code == 401
    assert refresh(client, info, new).status_code == 400


def test_old_refresh_reuse_revokes_chain(client, repo):
    info, _, _, pair = linked(client, repo)
    new = refresh(client, info, pair).json()
    assert refresh(client, info, pair).status_code == 400
    assert rpc(client, new["access_token"]).status_code == 401
    assert refresh(client, info, new).status_code == 400


def test_concurrent_code_exchange_consumes_once(client, repo):
    info, _, value = code(client, repo)
    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda _: exchange(client, info, value), range(2)))
    assert sorted(r.status_code for r in responses) == [200, 400]
    pair = next(r.json() for r in responses if r.status_code == 200)
    assert rpc(client, pair["access_token"]).status_code == 401
    assert refresh(client, info, pair).status_code == 400


def test_authorization_error_preserves_state_and_issuer(client):
    info = registered(client)
    denied = client.get("/authorize", params=authorize_params(info, scope="unknown"))
    assert denied.status_code == 302
    query = parse_qs(urlsplit(denied.headers["location"]).query)
    assert query["error"] == ["invalid_scope"] and query["state"] == ["原样 + & 中文"]
    assert query["iss"] == [ORIGIN]
    denied = client.get("/authorize", params=authorize_params(info, resource="wrong"))
    assert denied.status_code == 400 and denied.json()["iss"] == ORIGIN


def test_refresh_resource_and_absolute_expiry(client, repo, monkeypatch):
    info, _, _, pair = linked(client, repo)
    assert refresh(client, info, pair, resource=ORIGIN + "/mcp").status_code == 400
    assert refresh(client, info, pair, resource="").status_code == 400
    new = refresh(client, info, pair).json()
    monkeypatch.setattr(tokens, "now_taipei", lambda: now_taipei() + dt.timedelta(days=31))
    assert refresh(client, info, new).status_code == 400


@pytest.mark.parametrize("field", ["issuer", "audience", "resource"])
def test_access_issuer_and_audience_are_checked(client, repo, field):
    _, _, _, pair = linked(client, repo)
    with tokens._editing():
        items = tokens._load()
        for row in items:
            if row.get("kind") == "oauth":
                row[field] = "https://other.example/mcp"
        tokens._save(items)
    assert rpc(client, pair["access_token"]).status_code == 401


@pytest.mark.parametrize("uri", ["https://evil.example/callback", "http://chatgpt.com/connector_platform_oauth_redirect",
                                "https://chatgpt.com.evil.example/connector_platform_oauth_redirect",
                                CALLBACK + "?code=do-not-log", CALLBACK + "#fragment", "https://chatgpt.com/other",
                                "https://user@chatgpt.com/connector_platform_oauth_redirect"])
def test_callback_allowlist_and_safe_logs(client, caplog, uri):
    assert register(client, redirect=uri).status_code == 400
    info = registered(client)
    assert client.get("/authorize", params=authorize_params(info, redirect_uri=uri)).status_code == 400
    assert "拒绝 OAuth 回调 host=" in caplog.text and "time=" in caplog.text
    assert "do-not-log" not in caplog.text and "fragment" not in caplog.text
    assert len(CALLBACK_ALLOWLIST) == 2


def test_allowed_but_unregistered_callback_is_denied(client):
    info = registered(client, redirect="https://chatgpt.com/connector/oauth/callback_123")
    assert client.get("/authorize", params=authorize_params(info, redirect_uri=CALLBACK)).status_code == 400
    assert client.get("/authorize", params=authorize_params(info)).status_code == 302


@pytest.mark.parametrize("changes", [{"resource": ORIGIN + "/mcp"}, {"resource": ""},
                                      {"code_challenge_method": "plain"}, {"code_challenge_method": ""},
                                      {"code_challenge": "short"}])
def test_bad_authorize_request(client, changes):
    info = registered(client)
    assert client.get("/authorize", params=authorize_params(info, **changes)).status_code == 400


@pytest.mark.parametrize("kind", ["human", "web", OWNER, "revoked", "archived", "local", "expired", "feishu", "oauth"])
def test_consent_only_accepts_live_remote_agent_source(client, repo, kind):
    info = registered(client)
    if kind == "human":
        value, _ = tokens.issue("ext-zhang", "人的口令")
    elif kind == "web":
        _, source = tokens.issue("chatgpt", "源口令")
        value, _ = tokens.issue_web("chatgpt", "token", source_token_id=source["id"])
    elif kind == "oauth":
        _, _, _, pair = linked(client, repo)
        value = pair["access_token"]
    elif kind in (OWNER, "local", "feishu", "expired"):
        value, _ = tokens.issue(OWNER if kind == OWNER else "codex" if kind == "local" else "chatgpt",
                                "不能用", kind="feishu" if kind == "feishu" else "token",
                                days=-1 if kind == "expired" else None)
    else:
        value, source = tokens.issue("chatgpt", "不能用")
        if kind == "revoked":
            tokens.revoke(source["id"])
        else:
            admin(repo).archive_member("chatgpt")
    _, form = form_page(client, info)
    response = client.post("/chatgpt/consent", data={**form, "token": value}, headers={"Origin": ORIGIN})
    assert response.status_code == 401 and "这个口令用不了" in response.text and value not in response.text
    assert "name=\"token\" type=\"password\"" in response.text


@pytest.mark.parametrize("changes", [{"Origin": "https://evil.example"}, {"Origin": ""},
                                      {"Origin": ORIGIN, "Sec-Fetch-Site": "cross-site"}])
def test_consent_csrf_origin(client, repo, changes):
    info = registered(client)
    value, _ = tokens.issue("chatgpt", "测试")
    _, form = form_page(client, info)
    assert client.post("/chatgpt/consent", data={**form, "token": value}, headers=changes).status_code == 403


def test_consent_challenge_binding_and_replay(client, repo):
    info = registered(client)
    value, _ = tokens.issue("chatgpt", "测试")
    _, first = form_page(client, info)
    _, second = form_page(client, info)
    assert client.post("/chatgpt/consent", data={**first, "challenge": second["challenge"], "token": value},
                       headers={"Origin": ORIGIN}).status_code == 403
    _, form = form_page(client, info)
    payload = {**form, "token": value}
    assert client.post("/chatgpt/consent", data=payload, headers={"Origin": ORIGIN}).status_code == 303
    assert client.post("/chatgpt/consent", data=payload, headers={"Origin": ORIGIN}).status_code == 400


def test_expired_code_and_tokens(client, repo, monkeypatch):
    info, _, value = code(client, repo)
    original = now_taipei()
    monkeypatch.setattr(tokens, "now_taipei", lambda: original + dt.timedelta(minutes=6))
    assert exchange(client, info, value).status_code == 400
    monkeypatch.setattr(tokens, "now_taipei", now_taipei)
    info, _, _, pair = linked(client, repo)
    monkeypatch.setattr(tokens, "now_taipei", lambda: now_taipei() + dt.timedelta(hours=2))
    assert rpc(client, pair["access_token"]).status_code == 401
    assert refresh(client, info, pair).status_code == 200


@pytest.mark.parametrize("action", ["revoke", "archive", "remote_off", "owner_archive", "delete", "bad_owner"])
def test_live_revalidation_for_access_and_refresh(client, repo, action):
    info, source, _, pair = linked(client, repo)
    if action == "revoke":
        admin(repo).token_revoke(source["record"]["id"])
    elif action == "archive":
        admin(repo).archive_member("chatgpt")
    elif action == "remote_off":
        p = repo / "people/chatgpt.md"
        p.write_text(p.read_text(encoding="utf-8").replace("remote_enabled: true", "remote_enabled: false"), encoding="utf-8")
    elif action == "owner_archive":
        p = repo / f"people/{OWNER}.md"
        p.write_text(p.read_text(encoding="utf-8").replace("kind: human", "kind: human\narchived: true"), encoding="utf-8")
    elif action == "delete":
        # 模拟成员不再存在，不实际删除文件。
        p = repo / "people/chatgpt.md"
        p.write_text("不是有效成员", encoding="utf-8")
    else:
        p = repo / "people/chatgpt.md"
        p.write_text(p.read_text(encoding="utf-8").replace(f"owner: {OWNER}", "owner: missing"), encoding="utf-8")
    assert rpc(client, pair["access_token"]).status_code == 401
    assert refresh(client, info, pair).status_code == 400


def test_oauth_revoke_endpoint_revokes_family(client, repo):
    info, _, _, pair = linked(client, repo)
    assert client.post("/revoke", data={"client_id": info["client_id"], "token": pair["access_token"]}).status_code == 200
    assert rpc(client, pair["access_token"]).status_code == 401
    assert refresh(client, info, pair).status_code == 400


def test_cross_entry_rejection_and_legacy_contract(client, repo):
    _, source, _, pair = linked(client, repo)
    assert rpc(client, pair["access_token"], path="/mcp").status_code == 401
    assert rpc(client, source["token"]).status_code == 401
    assert rpc(client, pair["refresh_token"]).status_code == 401
    legacy = rpc(client, source["token"], path="/mcp").json()["result"]["tools"]
    oauth = rpc(client, pair["access_token"]).json()["result"]["tools"]
    assert {t["name"] for t in oauth} == {t["name"] for t in legacy} | {"fetch"}
    assert set(next(t for t in legacy if t["name"] == "search")["inputSchema"]["properties"]) == {"query", "limit"}
    result = call(client, source["token"], "search", {"query": "阿尔法", "limit": 3}, path="/mcp")
    assert text(result) == safe_text(CompanyBase(repo, "chatgpt", require_remote=True).search("阿尔法", 3))
    assert all(t["annotations"]["openWorldHint"] is False for t in legacy)
    for key in (OWNER, "codex", "claude"):
        with pytest.raises(BaseError):
            admin(repo).token_issue(key, "不能发")
        value, _ = tokens.issue(key, "不该有的")
        assert rpc(client, value, path="/mcp").status_code == 401
    assert client.get("/login").status_code == 200
    page = client.get("/login")
    challenge = re.search(r'name="challenge" value="([^"]+)"', page.text)[1]
    value, _ = tokens.issue(OWNER, "不能登录")
    assert client.post("/auth/login", data={"challenge": challenge, "token": value},
                       headers={"Origin": ORIGIN}).status_code == 401
    assert not make_viewer(Store(repo).load(), "codex").can_remote


def test_assigned_search_fetch_visibility_and_projection(client, repo):
    _, _, _, pair = linked(client, repo, key="ext-zhang-claude")
    # 夹具已有仅授给 codex 的限定项目；远程 agent 跟 owner 的授权走。
    found = call(client, pair["access_token"], "search", {"query": "暗号紫藤萝"})
    assert found["structuredContent"] == {"results": []}
    for document_id in ("projects/secret/project.md", "assets/sample.md", "../outside.md", "C:/x/secret.md"):
        denied = call(client, pair["access_token"], "fetch", {"id": document_id})
        assert denied.get("isError") and "找不到这个文档" in text(denied)
        assert document_id not in text(denied)
    card = call(client, pair["access_token"], "fetch", {"id": "people/codex.md"})["structuredContent"]
    assert card["text"] == "Codex\n\ncodex\nagent" and card["url"] == ORIGIN + "/#/members"
    p = repo / "projects/alpha/project.md"
    p.write_text(p.read_text(encoding="utf-8") + "\n路径测试 C:\\private\\alpha.md\n", encoding="utf-8")
    found = call(client, pair["access_token"], "search", {"query": "路径测试"})
    assert len(found["structuredContent"]["results"]) == 1
    fetched = call(client, pair["access_token"], "fetch", {"id": "projects/alpha/project.md"})
    assert "C:\\" not in text(fetched) and "（本机路径）" in fetched["structuredContent"]["text"]
    assert not call(client, pair["access_token"], "search", {"query": "C:\\private"})["structuredContent"]["results"]
    # full 按原权限规则能读限定文档，不另加一层 OAuth ACL。
    _, _, _, full = linked(client, repo)
    assert not call(client, full["access_token"], "fetch", {"id": "projects/secret/project.md"}).get("isError")


def test_write_rechecks_oauth_under_data_lock(client, repo, monkeypatch):
    _, source, _, pair = linked(client, repo)
    before = git(repo, "rev-parse", "HEAD")
    original = gitops.write_lock

    @contextmanager
    def revoke_before_lock(root):
        tokens.revoke(source["record"]["id"])
        with original(root):
            yield

    monkeypatch.setattr(gitops, "write_lock", revoke_before_lock)
    result = call(client, pair["access_token"], "update_project_status", {"project_id": "alpha", "text": "不应写进去"})
    assert result.get("isError") and git(repo, "rev-parse", "HEAD") == before
    assert rpc(client, pair["access_token"]).status_code == 401


def test_one_source_token_authorizes_only_once(client, repo):
    info, source, _ = code(client, repo)
    _, form = form_page(client, info)
    response = client.post("/chatgpt/consent", data={**form, "token": source["token"]}, headers={"Origin": ORIGIN})
    assert response.status_code == 401 and "已经授权过一次" in response.text
    assert source["token"] not in response.text
    # 换一个新口令就能重新连
    assert code(client, repo, info)[2]


def test_expired_unexchanged_code_does_not_use_up_source(client, repo, monkeypatch):
    info, source, _ = code(client, repo)
    monkeypatch.setattr(tokens, "now_taipei", lambda: now_taipei() + dt.timedelta(seconds=tokens.OAUTH_CODE_SECONDS + 5))
    _, form = form_page(client, info)
    response = client.post("/chatgpt/consent", data={**form, "token": source["token"]}, headers={"Origin": ORIGIN})
    assert response.status_code == 303


def test_consent_page_lets_the_form_redirect_back_to_chatgpt(client):
    # 浏览器对表单提交后的跳转也按 form-action 检查；只放行 'self' 会把跳回 ChatGPT 的 303 拦下。
    response, _ = form_page(client, registered(client))
    csp = response.headers["content-security-policy"]
    assert "form-action 'self' https://chatgpt.com" in csp and "frame-ancestors 'none'" in csp
    # 其它页面仍只许提交回自己
    assert guard.CSP.endswith("form-action 'self'")


def test_token_refusal_logs_cannot_forge_lines(client, caplog):
    client.post("/token", data={"grant_type": "x\n2026 INFO 伪造", "resource": "wrong", "a\nb": "1"})
    lines = [r.getMessage() for r in caplog.records if "拒绝 OAuth 换令牌" in r.getMessage()]
    assert lines and all("\n" not in line and "伪造" not in line for line in lines)


def test_token_requests_without_resource_use_the_bound_resource(client, repo):
    info, _, value = code(client, repo)
    form = {"grant_type": "authorization_code", "client_id": info["client_id"], "code": value,
            "redirect_uri": info["redirect_uris"][0], "code_verifier": VERIFIER}
    pair = client.post("/token", data=form).json()
    assert rpc(client, pair["access_token"]).status_code == 200
    new = client.post("/token", data={"grant_type": "refresh_token", "client_id": info["client_id"],
                                      "refresh_token": pair["refresh_token"]}).json()
    assert rpc(client, new["access_token"]).status_code == 200


def test_shared_identity_must_sign_with_a_listed_name(client, repo):
    # alex 和网页 GPT 共用 chatgpt 这一个授权；谁写就得署谁的名，记录里才分得清。
    path = repo / "people/chatgpt.md"
    path.write_text(path.read_text(encoding="utf-8").replace("can_write: true", "can_write: true\nsigners:\n- alex\n- GPT"),
                    encoding="utf-8")
    git(repo, "commit", "-qam", "chatgpt 由alex和GPT共用")
    _, _, _, pair = linked(client, repo)
    assert "alex、GPT共用" in text(call(client, pair["access_token"], "whoami"))
    before = git(repo, "rev-parse", "HEAD")
    for signed in (None, "别人"):
        arguments = {"project_id": "alpha", "text": "没署对名"} | ({"signed_as": signed} if signed else {})
        result = call(client, pair["access_token"], "update_project_status", arguments)
        assert result.get("isError") and "signed_as 填你是谁" in text(result)
    assert git(repo, "rev-parse", "HEAD") == before
    result = call(client, pair["access_token"], "update_project_status",
                  {"project_id": "alpha", "text": "alex 记的进展", "signed_as": "alex"})
    assert not result.get("isError")
    assert git(repo, "log", "-1", "--format=%an") == "chatgpt · alex"

