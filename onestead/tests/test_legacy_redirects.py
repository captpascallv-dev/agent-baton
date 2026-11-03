"""旧地址 /portal 退场：固定跳转、目标重新鉴权、来源与旧 cookie 隔离。"""

import re
from urllib.parse import quote, parse_qs, urlsplit

import pytest
from starlette.testclient import TestClient

from onestead import tokens
from onestead.server import remote_app

from .conftest import git

PORT = 48201
BASE = f"http://127.0.0.1:{PORT}"


@pytest.fixture(autouse=True)
def token_store(tmp_path, monkeypatch):
    monkeypatch.setenv("COMPANY_BASE_TOKENS", str(tmp_path / "tokens.json"))


@pytest.fixture
def portal(ext, repo):  # noqa: F811
    with TestClient(remote_app(str(repo), PORT, web_test_origin=BASE), base_url=BASE,
                    follow_redirects=False, headers={"Origin": BASE}) as c:
        yield c


def login(c, key):
    password, record = tokens.issue(key, "测试")
    page = c.get("/login")
    challenge = re.search(r'name="challenge" value="([^"]+)"', page.text)[1]
    r = c.post("/auth/login", data={"token": password, "challenge": challenge})
    assert r.status_code == 303 and r.headers["location"] == "/"
    assert password not in r.headers["location"]
    return record


@pytest.mark.parametrize("old,target", [
    ("/portal", "/"), ("/portal/p/alpha", "/#/projects/alpha"),
    ("/portal/a/deal", "/#/docs/deal"), ("/portal/decisions", "/#/decisions"),
    ("/portal/search", "/#/search?q="), ("/portal/skills/shared-tool", "/#/skills/shared-tool"),
    ("/portal/skills/shared-tool.zip", "/api/skills/shared-tool/download"),
    ("/portal/static/app.css", "/static/app.css"), ("/portal/static/app.js", "/static/app.js"),
])
def test_fixed_redirects(portal, old, target):
    for params in ({}, {"next": "https://evil.example", "redirect": "//evil.example"}):
        r = portal.get(old, params=params)
        assert r.status_code == 303 and r.headers["location"] == target
        assert r.content == b"" and r.headers["cache-control"] == "no-store"


@pytest.mark.parametrize("q", ["项目 & 进展/?#=", "https://evil.example/#/", "中" * 120])
def test_search_encoding_and_limit(portal, q):
    target = portal.get("/portal/search", params={"q": " " + q + " ", "next": "https://evil.example"}).headers["location"]
    assert target == "/#/search?q=" + quote(q[:100], safe="")
    assert parse_qs(urlsplit(target).fragment.partition("?")[2])["q"] == [q[:100]]


@pytest.mark.parametrize("path", ["/portal/p/项目甲", "/portal/a/资料甲", "/portal/skills/工具甲"])
def test_id_is_an_encoded_segment(portal, path):
    r = portal.get(path)
    assert r.status_code == 303 and "%E" in r.headers["location"]
    assert urlsplit(r.headers["location"]).netloc == ""


@pytest.mark.parametrize("path", ["/portal/login", "/portal/logout"])
@pytest.mark.parametrize("method", ["get", "post"])
def test_legacy_auth_is_only_a_redirect(portal, path, method):
    password, _ = tokens.issue("ext-zhang", "旧表单")
    before = tokens.list_tokens()
    kwargs = {"data": {"token": password}} if method == "post" else {}
    r = getattr(portal, method)(path, **kwargs)
    assert r.status_code == 303 and r.headers["location"] == "/login"
    assert password not in r.headers["location"] and before == tokens.list_tokens()
    assert portal.get("/api/me").status_code == 401


@pytest.mark.parametrize("kind", ["token", "feishu", "web"])
def test_old_cookie_never_becomes_a_web_session(portal, kind):
    source, record = tokens.issue("ext-zhang", "旧口令")
    value = tokens.issue_web("ext-zhang", "token", source_token_id=record["id"])[0] if kind == "web" else (
        tokens.issue("ext-zhang", "旧飞书", kind="feishu", days=30)[0] if kind == "feishu" else source)
    portal.cookies.set("cb_portal", value, path="/portal")
    r = portal.get("/portal/p/alpha")
    assert r.status_code == 303 and "暗号" not in r.text
    assert any("cb_portal=" in x and "Max-Age=0" in x and "Path=/portal" in x for x in r.headers.get_list("set-cookie"))
    assert portal.get("/").headers["location"] == "/login"
    assert portal.get("/api/me").status_code == 401


@pytest.mark.parametrize("path", ["/", "/login", "/static/app.js", "/api/me", "/not-found", "/mcp", "/skills/test.zip"])
def test_any_entry_clears_old_cookie(portal, path):
    portal.cookies.set("cb_portal_old", "legacy-placeholder", path="/")
    r = portal.get(path)
    assert any("cb_portal_old=" in x and "Max-Age=0" in x for x in r.headers.get_list("set-cookie"))


def test_redirect_targets_reauthenticate_and_hide_ungranted(portal):
    assert portal.get("/portal").headers["location"] == "/"
    assert portal.get("/").headers["location"] == "/login"
    assert portal.get("/portal/skills/test.zip").headers["location"] == "/api/skills/test/download"
    assert portal.get("/api/skills/test/download").headers["location"] == "/login"
    login(portal, "ext-zhang")
    assert "暗号阿尔法进展" in portal.get("/api/project/alpha").text
    for pid in ("beta", "secret", "missing"):
        assert portal.get(f"/portal/p/{pid}").status_code == 303
        assert portal.get(f"/api/project/{pid}").status_code == 404
    assert portal.get("/api/search", params={"q": "贝塔湖"}).json()["hits"] == []


def test_agent_and_local_partner_cannot_log_in_as_humans(portal):
    for key in ("ext-zhang-claude", "codex"):
        password, _ = tokens.issue(key, "不能登录")
        challenge = re.search(r'name="challenge" value="([^"]+)"', portal.get("/login").text)[1]
        assert portal.post("/auth/login", data={"token": password, "challenge": challenge}).status_code == 401


def test_source_revocation_invalidates_target_session(portal):
    record = login(portal, "ext-zhang")
    assert portal.get("/api/project/alpha").status_code == 200
    tokens.revoke(record["id"])
    assert portal.get("/api/project/alpha").status_code == 401
    assert portal.get("/").headers["location"] == "/login"


def test_business_content_stays_filtered(portal, repo):
    path = repo / "projects/alpha/status.md"
    path.write_text(path.read_text(encoding="utf-8") + "\n## 测试\n\n<script>alert(1)</script> 坏内容\n", encoding="utf-8")
    git(repo, "commit", "-qam", "坏内容")
    login(portal, "ext-zhang")
    r = portal.get("/api/project/alpha")
    assert "<script" not in r.text and "坏内容" in r.text
    assert "unsafe-inline" not in r.headers["content-security-policy"]


def test_real_logout_requires_origin_and_csrf(portal):
    record = login(portal, "ext-zhang")
    csrf = portal.get("/api/me").json()["csrf_token"]
    for headers in ({}, {"X-CSRF-Token": csrf, "Origin": "https://evil.example"},
                    {"X-CSRF-Token": csrf, "Sec-Fetch-Site": "cross-site"}):
        assert portal.post("/auth/logout", headers=headers).status_code == 403
        assert portal.get("/api/me").json()["key"] == "ext-zhang"
    assert portal.post("/auth/logout", headers={"X-CSRF-Token": csrf}).headers["location"] == "/login"
    assert tokens.lookup_record(portal.cookies.get(f"cb_web_dev_{PORT}") or "") is None
    assert not next(t for t in tokens.list_tokens() if t["id"] == record["id"])["revoked"]


def test_host_and_static_alias(portal):
    # 未知 Host 不再进入旧门户 guard，最外层统一 404。
    assert portal.get("/portal", headers={"Host": "evil.example"}).status_code == 404
    assert portal.post("/portal/login", headers={"Host": "evil.example"}).status_code == 404
    assert portal.get("/portal/static/app.css", follow_redirects=True).content == portal.get("/static/app.css").content
    assert portal.get("/portal/static/not-found.css", follow_redirects=True).status_code == 404


def test_login_style_and_csp(portal, feishu_cfg):
    from .support import write_config
    write_config(feishu_cfg)
    r = portal.get("/login")
    assert 'class="auth-page"' in r.text and 'class="card auth-card"' in r.text
    assert 'class="input"' in r.text and 'class="btn primary"' in r.text
    assert 'class="btn auth-feishu"' in r.text and "<script" not in r.text
    assert r.headers["referrer-policy"] == "same-origin"
    assert "unsafe-inline" not in r.headers["content-security-policy"]
