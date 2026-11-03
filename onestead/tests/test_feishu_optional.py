"""飞书未配置时只留下口令入口；临时配置、成员与发布站点都不碰正式数据。"""

import html
import re

import pytest
from starlette.testclient import TestClient

from onestead import feishu, tokens
from onestead.base import CompanyBase
from onestead.server import remote_app
from onestead.store import Store
from .support import (APP, OWNER, PUBLIC_HOST, WORKSPACE_ORIGIN, WORKSPACE_PORT,
                      WEB_POST_HEADERS, change, git, login, signed, write_config)


@pytest.fixture(autouse=True)
def no_feishu(tmp_path, monkeypatch, feishu_cfg):
    # 显式换到空目录，即使共用夹具以后有默认配置，本文件也仍覆盖未配置分支。
    folder = tmp_path / "without-feishu"
    folder.mkdir()
    monkeypatch.setenv("COMPANY_BASE_FEISHU_DIR", str(folder))
    return folder


def assert_no_feishu(text):
    assert "feishu" not in text.lower() and "飞书" not in text


@pytest.mark.parametrize("app_id,secret,expected", [
    (None, None, False), ("app_id=cli_test", None, False),
    (None, "fake-test-secret", False), ("# 注释\napp_id=", "fake-test-secret", False),
    ("app_id=cli_test", "\ufeff  \n", False),
    ("# 注释\napp_id=cli_test", "\ufeff fake-test-secret \n", True),
])
def test_enabled_uses_existing_config_reader(no_feishu, app_id, secret, expected):
    if app_id is not None:
        (no_feishu / "feishu-login.txt").write_text(app_id, encoding="utf-8")
    if secret is not None:
        (no_feishu / "feishu-secret.txt").write_text(secret, encoding="utf-8")
    assert feishu.enabled() is expected
    assert feishu.ready() is expected


def test_login_and_capabilities_without_feishu(client, workspace):
    assert not feishu.enabled()
    page = workspace.get("/login")
    assert page.status_code == 200 and 'action="/auth/login"' in page.text
    assert_no_feishu(page.text)
    assert_no_feishu(client.get("/api/me").text)
    assert login(workspace)[0].status_code == 303
    me = workspace.get("/api/me")
    assert me.status_code == 200 and me.json()["key"] == "ext-zhang"
    assert_no_feishu(me.text)


@pytest.mark.parametrize("path", [
    "/portal/feishu", "/portal/feishu/", "/portal/feishu/login", "/portal/feishu/callback",
    "/auth/feishu", "/auth/feishu/login", "/auth/feishu/callback",
    "/api/feishu", "/api/feishu/bind", "/api/feishu/unbind", "/api/feishu/dismiss",
])
def test_disabled_routes_are_404_before_auth_or_side_effects(client, workspace, monkeypatch, path):
    def forbidden(*args, **kwargs):
        pytest.fail("未配置时不应执行飞书处理或消费登录挑战")

    for name in ("_exchange_code", "_user_info", "remember", "contains", "dismiss"):
        monkeypatch.setattr(feishu, name, forbidden)
    monkeypatch.setattr(tokens, "consume_challenge", forbidden)
    # 本机主人、未登录的同事和未登录的本机请求都看不到集成路由。
    for browser in (client, workspace):
        for method in ("GET", "HEAD", "POST"):
            response = browser.request(method, path, json={"open_id": "ou_old", "key": "ext-zhang"})
            assert response.status_code == 404
            assert_no_feishu(response.text)
            assert "location" not in response.headers
    client.cookies.clear()
    assert client.post(path, json={}).status_code == 404


def test_projection_hides_existing_bindings_pending_and_sessions(ext, repo, client, no_feishu, monkeypatch):
    change(repo, "people/ext-zhang.md", feishu_open_id="ou_saved")
    feishu.remember({"open_id": "ou_pending", "name": "等待登记的人"})
    tokens.issue("ext-zhang", "飞书登录", kind="feishu")
    tokens.issue_web("ext-zhang", "feishu", binding_id="ou_saved")
    password, token = tokens.issue("ext-zhang", "口令")
    tokens.issue_web("ext-zhang", "token", source_token_id=token["id"])
    original_pending = feishu.list_pending

    def forbidden():
        pytest.fail("未配置时不应读取旧待登记列表")

    monkeypatch.setattr(feishu, "list_pending", forbidden)
    for path in ("/api/members", "/api/home", "/api/source?rel=people/ext-zhang.md"):
        response = client.get(path)
        assert response.status_code == 200
        assert_no_feishu(response.text)
        assert "ou_saved" not in response.text and "ou_pending" not in response.text
    member = next(x for x in client.get("/api/members").json()["items"] if x["key"] == "ext-zhang")
    assert len(member["tokens"]) == len(member["sessions"]) == 1
    assert tokens.lookup_record(password)

    # 同一服务重新配上即可恢复原有投影；隐藏不删除历史绑定、待登记或会话。
    monkeypatch.setattr(feishu, "list_pending", original_pending)
    write_config(no_feishu)
    assert client.get("/api/me").json()["capabilities"]["feishu"] is True
    data = client.get("/api/members").json()
    assert data["feishu_pending"][0]["open_id"] == "ou_pending"
    member = next(x for x in data["items"] if x["key"] == "ext-zhang")
    assert member["feishu_bound"] is True and len(member["sessions"]) == 3
    assert client.get("/api/home").json()["feishu_pending"] == data["feishu_pending"]


def test_web_save_preserves_uneditable_binding(ext, repo, client, no_feishu):
    change(repo, "people/ext-zhang.md", feishu_open_id="ou_saved")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "原有成员绑定")
    source = client.get("/api/source", params={"rel": "people/ext-zhang.md"}).json()
    assert all(f["key"] != "feishu_open_id" for f in source["fields"])
    response = client.post("/api/source/save", headers=WEB_POST_HEADERS, json={
        "rel": source["rel"], "version": source["version"],
        "changes": {"role": "产品同事", "feishu_open_id": "ou_not_allowed"}, "body": "成员说明。",
    })
    assert response.status_code == 200 and response.json()["commit"]
    member = next(d for d in Store(repo).load() if d.rel == source["rel"])
    assert not member.problem and member.meta["role"] == "产品同事"
    assert member.meta["feishu_open_id"] == "ou_saved" and "成员说明。" in member.body
    write_config(no_feishu)
    item = next(x for x in client.get("/api/members").json()["items"] if x["key"] == "ext-zhang")
    assert item["feishu_bound"] is True


def test_disable_remote_warning_has_no_feishu(ext, repo, client):
    change(repo, "people/ext-zhang.md", feishu_open_id="ou_saved")
    password, _ = tokens.issue("ext-zhang", "同事口令")
    source = client.get("/api/source", params={"rel": "people/ext-zhang.md"}).json()
    response = client.post("/api/source/save", headers=WEB_POST_HEADERS, json={
        "rel": source["rel"], "version": source["version"], "changes": {"remote_enabled": False},
    })
    assert response.status_code == 200 and response.json()["warnings"]
    assert_no_feishu(response.text)
    assert tokens.lookup_record(password) is None
    member = next(d for d in Store(repo).load() if d.rel == source["rel"])
    assert member.meta["feishu_open_id"] == "ou_saved"


def test_archive_and_restore_keep_dormant_binding_but_revoke_access(ext, repo, client, no_feishu):
    change(repo, "people/ext-zhang.md", feishu_open_id="ou_saved")
    password, source = tokens.issue("ext-zhang", "同事口令")
    web, _ = tokens.issue_web("ext-zhang", "token", source_token_id=source["id"])
    response = client.post("/api/members/ext-zhang/archive", headers=WEB_POST_HEADERS, json={})
    assert response.status_code == 200
    assert tokens.lookup_record(password) is None and tokens.lookup_record(web) is None
    member = next(d for d in Store(repo).load() if d.rel == "people/ext-zhang.md")
    assert member.meta["archived"] is True and member.meta["remote_enabled"] is False
    assert member.meta["feishu_open_id"] == "ou_saved"
    assert client.post("/api/members/ext-zhang/restore", headers=WEB_POST_HEADERS, json={}).status_code == 200
    member = next(d for d in Store(repo).load() if d.rel == "people/ext-zhang.md")
    assert not member.meta.get("archived") and member.meta["remote_enabled"] is False
    assert member.meta["feishu_open_id"] == "ou_saved"
    write_config(no_feishu)
    row = next(x for x in client.get("/api/members").json()["items"] if x["key"] == "ext-zhang")
    assert row["feishu_bound"] is True and row["remote_enabled"] is False
    assert tokens.lookup_record(password) is None


@pytest.fixture
def token_site(ext, repo, tmp_path, monkeypatch):
    monkeypatch.setenv("COMPANY_BASE_SITE_DOMAIN", "example.com")
    folder = tmp_path / "published-site"
    folder.mkdir()
    (folder / "index.html").write_text("<!doctype html><h1>仅口令访问的网站</h1>", encoding="utf-8")
    agent = ext("claude")
    agent.register_app(APP)
    agent.publish_site(APP["id"], str(folder), "optional")
    CompanyBase(repo, OWNER, allow_owner=True).manage_site(
        APP["id"], {"action": "publish", "visibility": "指定的人", "members": ["ext-zhang"]})
    with TestClient(remote_app(str(repo), WORKSPACE_PORT, public_host=PUBLIC_HOST),
                    base_url=WORKSPACE_ORIGIN, follow_redirects=False) as browser:
        yield browser


def test_token_issue_login_site_logout_and_revoke(client, token_site):
    browser = token_site
    issued = client.post("/api/tokens/issue", headers=WEB_POST_HEADERS,
                         json={"key": "ext-zhang", "label": "同事网页登录"})
    assert issued.status_code == 200
    ticket = issued.json()
    site = "https://optional.example.com"
    response = browser.get(site + "/?from=optional")
    assert response.status_code == 302 and "/sites/enter?" in response.headers["location"]
    enter = browser.get(response.headers["location"])
    assert enter.status_code == 302 and enter.headers["location"].startswith("/login?")
    page = browser.get(enter.headers["location"])
    assert_no_feishu(page.text)
    challenge = re.search(r'name="challenge" value="([^"]+)"', page.text)[1]
    target = html.unescape(re.search(r'name="return_to" value="([^"]+)"', page.text)[1])
    assert target.startswith("/sites/enter?")
    response = browser.post("/auth/login", headers={"Origin": WORKSPACE_ORIGIN}, data={
        "challenge": challenge, "token": ticket["token"], "return_to": target,
    })
    assert response.status_code == 303 and response.headers["location"] == target
    me = browser.get("/api/me")
    assert me.status_code == 200 and me.json()["key"] == "ext-zhang"
    assert_no_feishu(me.text)
    callback = browser.get(target)
    assert callback.status_code == 302 and "/_base/callback?" in callback.headers["location"]
    response = browser.get(callback.headers["location"])
    assert response.status_code == 302 and response.headers["location"] == "/?from=optional"
    assert "仅口令访问的网站" in browser.get(site + response.headers["location"]).text
    assert browser.post("/auth/logout", headers=signed(browser)).status_code == 303
    assert browser.get("/api/me").status_code == 401
    assert tokens.lookup_record(ticket["token"])
    assert login(browser, password=ticket["token"])[0].status_code == 303
    assert browser.get("/api/me").status_code == 200
    callback = browser.get(target)
    assert browser.get(callback.headers["location"]).status_code == 302
    assert browser.get(site).status_code == 200
    revoked = client.post("/api/tokens/revoke", headers=WEB_POST_HEADERS, json={"id": ticket["record"]["id"]})
    assert revoked.status_code == 200
    assert browser.get("/api/me").status_code == 401
    assert browser.get(site).status_code == 302
    assert login(browser, password=ticket["token"])[0].status_code == 401
