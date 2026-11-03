"""子域名隔离、一次性码、会话收回与静态路径；全部是临时数据的 ASGI 请求。"""

import asyncio
import datetime as dt
import html
import json
import re
import statistics
import time
from concurrent.futures import ThreadPoolExecutor
from urllib.parse import parse_qs, urlencode, urlsplit

import pytest
from starlette.testclient import TestClient

from onestead import config, siteauth, sites, tokens
from onestead.base import CompanyBase
from onestead.server import build_server, remote_app
from onestead.store import Store, now_taipei
from onestead.web.sites import COOKIE, Snapshot
from .support import (APP, OWNER, PUBLIC_HOST, WORKSPACE_ORIGIN, WORKSPACE_COOKIE, WORKSPACE_PORT,
                      change, git, junction, login, patch_user, plant, result, rpc, signed, write_config)

SITE = "https://demo.example.com"
OTHER = "https://other.example.com"


@pytest.fixture
def published(ext, repo, tmp_path, monkeypatch):
    monkeypatch.setenv("COMPANY_BASE_SITE_DOMAIN", "example.com")
    folder = tmp_path / "static-build"
    folder.mkdir()
    (folder / "index.html").write_text("<!doctype html><h1>这是发布的网站</h1>", encoding="utf-8")
    (folder / "style.css").write_text("body {color:red}", encoding="utf-8")
    (folder / "nested").mkdir()
    (folder / "nested" / "index.html").write_text("目录页", encoding="utf-8")
    agent = ext("claude")
    agent.register_app(APP)
    agent.publish_site(APP["id"], str(folder), "demo")
    owner = CompanyBase(repo, OWNER, allow_owner=True)
    owner.manage_site(APP["id"], {"action": "publish", "visibility": "指定的人", "members": ["ext-zhang"]})
    return owner, agent, folder


@pytest.fixture
def browser(published, repo):
    with TestClient(remote_app(str(repo), WORKSPACE_PORT, public_host=PUBLIC_HOST),
                    base_url=WORKSPACE_ORIGIN, follow_redirects=False) as client:
        yield client


def current(repo):
    return sites.site_of(next(d for d in Store(repo).load() if d.id == APP["id"] and d.kind == "app"))


def enter_code(browser, slug="demo", next="/"):
    response = browser.get("/sites/enter", params={"slug": slug, "next": next})
    assert response.status_code == 302
    assert response.headers["location"].startswith(sites.url(slug) + "/_base/callback?")
    return response.headers["location"]


def sign_in_site(browser):
    assert login(browser)[0].status_code == 303
    callback = enter_code(browser)
    assert browser.get(callback).status_code == 302
    assert browser.get(SITE).status_code == 200
    return browser.cookies.get(COOKIE, domain="demo.example.com")


def publish_other(published):
    owner, agent, folder = published
    agent.register_app(APP | {"id": "other-web"})
    agent.publish_site("other-web", str(folder), "other")
    owner.manage_site("other-web", {"action": "publish", "visibility": "指定的人", "members": ["ext-zhang"]})


def test_login_flow_and_cookie_flags(browser):
    response = browser.get(SITE + "/nested/?q=ok")
    assert response.status_code == 302
    params = parse_qs(urlsplit(response.headers["location"]).query)
    assert params == {"slug": ["demo"], "next": ["/nested/?q=ok"]}
    response = browser.get(response.headers["location"])
    assert response.status_code == 302 and response.headers["location"].startswith("/login?")
    login_page = browser.get(response.headers["location"])
    challenge = re.search(r'name="challenge" value="([^"]+)"', login_page.text)[1]
    target = html.unescape(re.search(r'name="return_to" value="([^"]+)"', login_page.text)[1])
    value, _ = tokens.issue("ext-zhang", "临时同事")
    response = browser.post("/auth/login", data={"challenge": challenge, "token": value, "return_to": target},
                            headers={"Origin": WORKSPACE_ORIGIN})
    assert response.status_code == 303 and response.headers["location"] == target
    callback = browser.get(response.headers["location"])
    response = browser.get(callback.headers["location"])
    assert response.status_code == 302 and response.headers["location"] == "/nested/?q=ok"
    cookie = response.headers["set-cookie"]
    assert COOKIE in cookie and "Secure" in cookie and "HttpOnly" in cookie and "SameSite=lax" in cookie
    assert "Path=/" in cookie and "Max-Age=604800" in cookie and "domain=" not in cookie.lower()
    assert browser.get(SITE + response.headers["location"]).text == "目录页"
    assert "code=" not in response.headers["location"]


def test_same_site_post_still_rejected(browser):
    sign_in_site(browser)
    before = browser.get("/api/todos").json()
    headers = signed(browser) | {"Origin": SITE, "sec-fetch-site": "same-site"}
    response = browser.post("/api/todos/add", headers=headers, json={"title": "不应写入"})
    assert response.status_code == 403
    assert browser.get("/api/todos").json() == before
    # 即使伪造 same-origin 或缺少 CSRF，也不能靠 sibling Origin 获得写权限。
    for extra in ({"sec-fetch-site": "same-origin"}, {"X-CSRF-Token": ""}):
        assert browser.post("/api/todos/add", headers=headers | extra, json={"title": "不应写入"}).status_code == 403


@pytest.mark.parametrize("path", ["/", "/mcp", "/chatgpt/mcp", "/skills/demo.zip", "/auth/login", "/.well-known/oauth-authorization-server", "/api/me", "/static/app.js"])
def test_site_host_cannot_reach_base(browser, published, path):
    published[0].manage_site(APP["id"], {"action": "visibility", "visibility": "公开", "confirm_public": True})
    response = browser.get(SITE + path)
    assert response.status_code == (200 if path == "/" else 404)
    assert "X-Workspace-Context" not in response.headers
    if path == "/":
        assert "这是发布的网站" in response.text and "Onestead" not in response.text
    else:
        assert response.text == "找不到"


@pytest.mark.parametrize("host", ["Demo.example.com", "demo.example.com:443", "demo.example.com.", "a.demo.example.com", "unknown.example.com"])
def test_host_is_exact(browser, host):
    response = browser.get("/", headers={"Host": host})
    assert response.status_code == 404 and response.text == "找不到"


def test_duplicate_hosts_and_base_not_static(browser):
    for headers in ([('Host', 'demo.example.com'), ('Host', PUBLIC_HOST)],
                    [('Host', 'demo.example.com'), ('Host', 'demo.example.com')]):
        response = browser.get("/", headers=headers)
        assert response.status_code == 404 and response.text == "找不到"
    sign_in_site(browser)
    assert browser.get("/style.css").status_code == 404
    assert browser.get("/nested/").status_code == 404
    assert "这是发布的网站" not in browser.get("/").text


@pytest.mark.parametrize("condition", ["replay", "expired", "other-slug", "revoked-base"])
def test_one_time_code_rejections(browser, published, condition):
    login(browser)
    callback = enter_code(browser)
    rows = tokens._load(siteauth.path())
    code = parse_qs(urlsplit(callback).query)["code"][0]
    assert code not in siteauth.path().read_text(encoding="utf-8")
    assert 50 < (dt.datetime.fromisoformat(rows[-1]["expires"]) - now_taipei()).total_seconds() <= 60
    if condition == "replay":
        assert browser.get(callback).status_code == 302
    elif condition == "expired":
        records = tokens._load(siteauth.path())
        records[-1]["expires"] = (now_taipei() - dt.timedelta(seconds=1)).isoformat()
        tokens._save(records, siteauth.path())
    elif condition == "other-slug":
        publish_other(published)
        callback = callback.replace(SITE, OTHER)
    else:
        record = tokens.lookup_web(browser.cookies.get(WORKSPACE_COOKIE))
        tokens.revoke(record["id"])
    response = browser.get(callback)
    assert response.status_code == 403
    assert COOKIE not in response.headers.get("set-cookie", "")


def test_code_consumption_is_atomic(browser, repo):
    login(browser)
    callback = enter_code(browser)
    code = parse_qs(urlsplit(callback).query)["code"][0]
    docs, site = Store(repo).load(), current(repo)
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda _: siteauth.exchange(code, site, docs), range(2)))
    assert sum(value is not None for value in results) == 1


@pytest.mark.parametrize("change_kind", ["remove-member", "remote-off", "archived", "base-revoke", "source-revoke", "disable", "member-delete", "not-human"])
def test_revocation_on_next_request(browser, published, repo, change_kind):
    value = sign_in_site(browser)
    # 已经打热缓存以后撤权限，不能沿用上一次允许的结果。
    assert browser.get(SITE + "/style.css").status_code == 200
    if change_kind == "remove-member":
        published[0].manage_site(APP["id"], {"action": "visibility", "visibility": "指定的人", "members": []})
    elif change_kind == "remote-off":
        change(repo, "people/ext-zhang.md", remote_enabled=False)
    elif change_kind == "archived":
        change(repo, "people/ext-zhang.md", archived=True)
    elif change_kind == "base-revoke":
        tokens.revoke(tokens.lookup_web(browser.cookies.get(WORKSPACE_COOKIE))["id"])
    elif change_kind == "source-revoke":
        tokens.revoke(tokens.lookup_web(browser.cookies.get(WORKSPACE_COOKIE))["source_token_id"])
    elif change_kind == "disable":
        published[0].manage_site(APP["id"], {"action": "disable"})
    elif change_kind == "member-delete":
        (repo / "people/ext-zhang.md").unlink()
    else:
        change(repo, "people/ext-zhang.md", kind="agent", owner=OWNER)
    response = browser.get(SITE)
    assert response.status_code == (404 if change_kind == "disable" else 302)
    assert siteauth.find(tokens._load(siteauth.path()), value, "session") is None


@pytest.mark.parametrize("target", ["//evil.example.test", "https://evil.example.test", "/\\evil.example.test", "/%5cevil.example.test", "/%2fevil.example.test", "/%252fevil.example.test", "/\n/evil.example.test", "javascript:alert(1)"])
def test_next_never_external(browser, target):
    login(browser)
    callback = enter_code(browser, next=target)
    assert parse_qs(urlsplit(callback).query)["next"] == ["/"]
    # 回调参数由客户端改写，同样不能跳外站。
    params = parse_qs(urlsplit(callback).query)
    response = browser.get(SITE + "/_base/callback?" + urlencode({"code": params["code"][0], "next": target}))
    assert response.status_code == 302 and response.headers["location"] == "/"


@pytest.mark.parametrize("path", ["/%2e%2e/index.html", "/nested/%2e%2e/index.html", "/.env", "/.git/config", "/%5cindex.html", "/%00index.html", "/index.html:secret", "/index.html.", "/index.html%20"])
def test_static_paths_refused(browser, published, path):
    published[0].manage_site(APP["id"], {"action": "visibility", "visibility": "公开", "confirm_public": True})
    assert browser.get(SITE + path).status_code == 404


def test_static_headers_directories_no_spa_and_junction(browser, published, repo, tmp_path):
    published[0].manage_site(APP["id"], {"action": "visibility", "visibility": "公开", "confirm_public": True})
    response = browser.get(SITE)
    assert response.status_code == 200 and response.headers["content-type"].startswith("text/html")
    assert response.headers["cache-control"] == "private, no-cache"
    assert response.headers["x-content-type-options"] == "nosniff"
    assert response.headers["referrer-policy"] == "same-origin"
    assert response.headers["content-security-policy"] == "frame-ancestors 'self'"
    css = browser.get(SITE + "/style.css")
    assert css.headers["content-type"].startswith("text/css") and "private" in css.headers["cache-control"]
    assert browser.get(SITE + "/nested/").text == "目录页"
    assert browser.get(SITE + "/nested").headers["location"] == "/nested/"
    assert browser.get(SITE + "/no-spa-route").status_code == 404
    assert browser.post(SITE + "/", json={}).status_code == 405
    assert browser.head(SITE).status_code == 200 and not browser.head(SITE).content
    outside = tmp_path / "outside"
    outside.mkdir()
    (outside / "index.html").write_text("不能外泄", encoding="utf-8")
    site = current(repo)
    junction(sites.version_dir(site["slug"], site["current"]) / "escape", outside)
    assert browser.get(SITE + "/escape/").status_code == 404


def test_not_authorized_and_no_base_credentials_on_site(browser, published):
    login(browser, "ext-li")
    response = browser.get("/sites/enter?slug=demo")
    assert response.status_code == 403 and "你没有打开这个应用的权限，请找主人开" in response.text
    password, _ = tokens.issue("ext-zhang", "测试")
    response = browser.get(SITE, headers={"Authorization": "Bearer " + password,
                                          "Cookie": WORKSPACE_COOKIE + "=" + browser.cookies.get(WORKSPACE_COOKIE)})
    assert response.status_code == 302


def test_owner_phone_only_this_site_and_undo(browser, published, repo):
    owner = published[0]
    publish_other(published)
    link = owner.manage_site(APP["id"], {"action": "phone"})
    response = browser.get(link["url"])
    assert response.status_code == 302
    value = browser.cookies.get(COOKIE, domain="demo.example.com")
    assert browser.get(SITE).status_code == 200
    assert browser.get("/api/me", headers={"Cookie": COOKIE + "=" + value}).status_code == 401
    assert browser.get("/", headers={"Cookie": COOKIE + "=" + value}).status_code == 303
    assert browser.get(OTHER, headers={"Cookie": COOKIE + "=" + value}).status_code == 302
    assert tokens.lookup_record(value) is None
    assert browser.get(link["url"]).status_code == 403
    # 撤回签发提交后，未使用链接也立即失效。
    link = owner.manage_site(APP["id"], {"action": "phone"})
    from onestead.web.history import revert
    revert(repo, link["commit"])
    assert browser.get(link["url"]).status_code == 403


def test_phone_expiry_and_site_session_expiry(browser, published):
    owner = published[0]
    link = owner.manage_site(APP["id"], {"action": "phone"})
    records = tokens._load(siteauth.path())
    expires = dt.datetime.fromisoformat(records[-1]["expires"])
    assert 590 < (expires - now_taipei()).total_seconds() <= 600
    records[-1]["expires"] = (now_taipei() - dt.timedelta(seconds=1)).isoformat()
    tokens._save(records, siteauth.path())
    assert browser.get(link["url"]).status_code == 403
    link = owner.manage_site(APP["id"], {"action": "phone"})
    browser.get(link["url"])
    records = tokens._load(siteauth.path())
    assert records[-1]["kind"] == "session"
    assert 604790 < (dt.datetime.fromisoformat(records[-1]["expires"]) - now_taipei()).total_seconds() <= 604800
    records[-1]["expires"] = (now_taipei() - dt.timedelta(seconds=1)).isoformat()
    tokens._save(records, siteauth.path())
    assert browser.get(SITE).status_code == 302


def test_new_phone_link_preserves_existing_preview(browser, published):
    owner = published[0]
    first = owner.manage_site(APP["id"], {"action": "phone"})
    browser.get(first["url"])
    value = browser.cookies.get(COOKIE, domain="demo.example.com")
    second = owner.manage_site(APP["id"], {"action": "phone"})
    assert browser.get(SITE).status_code == 200
    assert browser.cookies.get(COOKIE, domain="demo.example.com") == value
    assert browser.get(second["url"]).status_code == 302


def test_configured_base_host_is_not_widened(published, repo, monkeypatch):
    # 显式 public_host 覆盖配置；只改变回到底座的目的地，不把子域名加入其允许名单。
    custom = "base.other.test"
    with TestClient(remote_app(str(repo), WORKSPACE_PORT, public_host=custom), follow_redirects=False) as client:
        response = client.get(SITE)
        assert response.headers["location"].startswith("https://" + custom + "/sites/enter?")


def test_logout(browser):
    sign_in_site(browser)
    response = browser.get(SITE + "/_base/logout")
    assert response.status_code == 200 and response.text == "已退出"
    assert "Max-Age=0" in response.headers["set-cookie"]
    assert browser.get(SITE).status_code == 302
    assert browser.get("/api/me").status_code == 200


def test_disable_feature_and_remote_mcp(browser, published, repo, monkeypatch):
    password, _ = tokens.issue("ext-zhang-claude", "MCP 测试")
    failed, text = result(rpc(browser, password, "publish_site", {"app_id": APP["id"], "build_dir": "C:/no"}))
    assert failed
    names = {t.name for t in asyncio.run(build_server(str(repo), None, remote=True, oauth_provider=object()).list_tools())}
    assert "publish_site" not in names
    monkeypatch.delenv("COMPANY_BASE_SITE_DOMAIN")
    assert browser.get(SITE).status_code == 404 and browser.get(SITE).text == "找不到"
    assert browser.get("/sites/enter?slug=demo").status_code == 404


def test_doc_controls_rename_disable_and_revert(browser, published, repo):
    owner = published[0]
    sign_in_site(browser)
    rename = owner.manage_site(APP["id"], {"action": "rename", "slug": "new-demo"})
    assert browser.get(SITE).status_code == 404
    assert browser.get("https://new-demo.example.com").status_code == 302
    from onestead.web.history import revert
    revert(repo, rename["commit"])
    assert browser.get("https://new-demo.example.com").status_code == 404
    assert browser.get(enter_code(browser)).status_code == 302
    assert browser.get(SITE).status_code == 200
    disabled = owner.manage_site(APP["id"], {"action": "disable"})
    assert browser.get(SITE).status_code == 404
    revert(repo, disabled["commit"])
    assert browser.get(enter_code(browser)).status_code == 302
    assert browser.get(SITE).status_code == 200


def test_feishu_return_and_binding_recheck(browser, published, repo, feishu_cfg, monkeypatch):
    write_config(feishu_cfg)
    patch_user(monkeypatch)
    plant(repo, "people/ext-zhang.md", "ou_wang")
    target = "/sites/enter?slug=demo&next=%2Fnested%2F"
    start = browser.get("/portal/feishu/login", params={"return_to": target})
    state = parse_qs(urlsplit(start.headers["location"]).query)["state"][0]
    response = browser.get("/portal/feishu/callback", params={"code": "test-code", "state": state})
    assert response.status_code == 200 and html.escape(target, quote=True) in response.text
    callback = enter_code(browser)
    assert browser.get(callback).status_code == 302
    assert browser.get(SITE).status_code == 200
    change(repo, "people/ext-zhang.md", feishu_open_id="ou_changed")
    assert browser.get(SITE).status_code == 302


def test_cache_refresh_and_timing(browser, repo):
    sign_in_site(browser)
    snapshot = Snapshot(repo)
    cold = time.perf_counter()
    first = snapshot.load()
    cold = (time.perf_counter() - cold) * 1000
    cached, requests = [], []
    for _ in range(30):
        start = time.perf_counter()
        assert snapshot.load() is first
        cached.append((time.perf_counter() - start) * 1000)
        start = time.perf_counter()
        assert browser.get(SITE).status_code == 200
        requests.append((time.perf_counter() - start) * 1000)
    change(repo, "people/ext-zhang.md", remote_enabled=False)
    assert snapshot.load() is not first
    print(f"\n站点缓存测量（{len(first)} 份文档，30 次）：冷读 {cold:.2f}ms；"
          f"缓存 median {statistics.median(cached):.2f}ms / p95 {sorted(cached)[28]:.2f}ms；"
          f"鉴权静态请求 median {statistics.median(requests):.2f}ms / p95 {sorted(requests)[28]:.2f}ms")
