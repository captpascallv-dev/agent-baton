"""主人网页操作与同事的入口投影。"""

import json
import subprocess
from pathlib import Path

import pytest

from onestead import config, siteauth, sites, tokens
from onestead.base import CompanyBase
from onestead.store import BaseError, Store
from onestead.web import history
from .support import APP, OWNER, WEB_POST_HEADERS, change, git, login, signed


@pytest.fixture
def staged(ext, repo, tmp_path, monkeypatch):
    monkeypatch.setenv("COMPANY_BASE_SITE_DOMAIN", "example.com")
    build = tmp_path / "build"
    build.mkdir()
    (build / "index.html").write_text("第一版", encoding="utf-8")
    agent = ext("claude")
    agent.register_app(APP)
    agent.publish_site(APP["id"], str(build), "demo")
    return agent, CompanyBase(repo, OWNER, allow_owner=True), build


def doc(repo):
    return next(d for d in Store(repo).load() if d.kind == "app" and d.id == APP["id"])


def action(client, name, **fields):
    return client.post(f"/api/apps/{APP['id']}/site", headers=WEB_POST_HEADERS,
                       json={"action": name, **fields})


def test_approval_audience_versions_rename_revert(staged, client, repo):
    agent, owner, build = staged
    payload = client.get("/api/apps").json()
    row = next(a for a in payload["apps"] if a["id"] == APP["id"])
    assert row["site"]["status"] == "待发布"
    assert {p["key"] for p in row["site_people"]} == {"ext-li", "ext-zhang"}
    assert client.get("/api/me").json()["site_domain"] == "example.com"
    assert action(client, "publish", visibility="公开").status_code == 400
    assert action(client, "publish", visibility="指定的人", members=["ext-zhang-claude"]).status_code == 400
    before = int(git(repo, "rev-list", "--count", "HEAD"))
    result = action(client, "publish", visibility="指定的人", members=["ext-zhang"])
    assert result.status_code == 200
    assert git(repo, "log", "-1", "--format=%an") == OWNER
    assert int(git(repo, "rev-list", "--count", "HEAD")) == before + 1
    first = doc(repo).meta["site"]["current"]
    (build / "index.html").write_text("第二版", encoding="utf-8")
    agent.publish_site(APP["id"], str(build))
    assert doc(repo).meta["site"]["current"] != first
    assert action(client, "rollback", version=first).status_code == 200
    assert doc(repo).meta["site"]["current"] == first
    assert action(client, "rollback", version="../escape").status_code == 400
    assert action(client, "disable").status_code == 200
    assert doc(repo).meta["site"]["status"] == "已停用"
    assert action(client, "enable").status_code == 200
    assert action(client, "visibility", visibility="公开", confirm_public=True).status_code == 200
    rename = action(client, "rename", slug="new-demo")
    assert rename.status_code == 200 and "旧地址马上失效" in rename.json()["message"]
    assert (sites.content_dir(doc(repo).meta["site"], first) / "index.html").read_text(encoding="utf-8") == "第一版"
    history.revert(repo, rename.json()["commit"])
    site = doc(repo).meta["site"]
    assert site["slug"] == "demo"
    assert (sites.version_dir(site["slug"], site["current"]) / "index.html").is_file()


def test_rename_keeps_only_five_versions_without_deleting_other_app(staged, client, repo):
    agent, owner, build = staged
    owner.manage_site(APP["id"], {"action": "publish", "visibility": "所有远程同事"})
    owner.manage_site(APP["id"], {"action": "rename", "slug": "renamed"})
    agent.register_app(APP | {"id": "another-app"})
    agent.publish_site("another-app", str(build), "demo")
    other = next(d.meta["site"] for d in Store(repo).load() if d.id == "another-app")
    for i in range(6):
        (build / "index.html").write_text(f"新版本 {i}", encoding="utf-8")
        agent.publish_site(APP["id"], str(build))
    site = doc(repo).meta["site"]
    assert len(site["versions"]) == 5
    assert all((sites.content_dir(site, v["id"]) / "index.html").is_file() for v in site["versions"])
    assert (sites.content_dir(other, other["pending"]) / "index.html").is_file()
    stored = [p for p in (config.state_dir() / "sites").glob("*/*") if sites.VERSION.fullmatch(p.name)]
    assert len(stored) == 6  # 本应用五版，另一个应用一版；没有旧短名下的废弃副本。


def test_phone_code_only_hash_and_undo(staged, client, repo):
    assert action(client, "phone").status_code == 400
    action(client, "publish", visibility="所有远程同事")
    response = action(client, "phone")
    assert response.status_code == 200
    result = response.json()
    value = result["url"].split("code=")[1]
    assert result["url"].startswith("https://demo.example.com/_base/callback?")
    assert "10 分钟" in result["message"] and "7 天" in result["message"]
    raw = siteauth.path().read_text(encoding="utf-8")
    assert value not in raw and tokens._digest(value) in raw
    assert tokens.lookup_web(value) is None
    assert value not in doc(repo).raw
    assert doc(repo).meta["site"]["previews"]
    history.revert(repo, result["commit"])
    assert "previews" not in doc(repo).meta["site"]


def test_remote_links_and_no_owner_actions(staged, workspace, repo):
    _, owner, _ = staged
    owner.manage_site(APP["id"], {"action": "publish", "visibility": "指定的人", "members": ["ext-zhang"]})
    assert login(workspace)[0].status_code == 303
    row = next(a for a in workspace.get("/api/apps").json()["apps"] if a["id"] == APP["id"])
    assert row["site_url"] == "https://demo.example.com"
    assert "site" not in row and "site_people" not in row
    assert workspace.post(f"/api/apps/{APP['id']}/site", headers=signed(workspace),
                          json={"action": "disable"}).status_code == 403
    owner.manage_site(APP["id"], {"action": "visibility", "visibility": "指定的人", "members": []})
    row = next(a for a in workspace.get("/api/apps").json()["apps"] if a["id"] == APP["id"])
    assert "site_url" not in row
    owner.manage_site(APP["id"], {"action": "visibility", "visibility": "所有远程同事"})
    assert login(workspace, "ext-li")[0].status_code == 303
    row = next(a for a in workspace.get("/api/apps").json()["apps"] if a["id"] == APP["id"])
    assert row["site_url"]
    owner.manage_site(APP["id"], {"action": "disable"})
    row = next(a for a in workspace.get("/api/apps").json()["apps"] if a["id"] == APP["id"])
    assert "site_url" not in row


def test_disabled_ui_and_owner_boundary(staged, client, monkeypatch):
    agent, _, _ = staged
    with pytest.raises(BaseError, match="agent 不能代做"):
        agent.manage_site(APP["id"], {"action": "disable"})
    monkeypatch.delenv("COMPANY_BASE_SITE_DOMAIN")
    assert client.get("/api/me").json()["site_domain"] is None
    assert all("site" not in a for a in client.get("/api/apps").json()["apps"])
    assert "没开应用发布" in action(client, "disable").json()["error"]
    assert client.post(f"/api/apps/{APP['id']}/site", json={"action": "disable"}).status_code == 403


def test_copy_prompt_is_pure_frontend():
    source = Path(__file__).parents[1] / "src/onestead/web/static/apps.js"
    script = r'''
const assert = require('node:assert/strict');
const fs = require('node:fs'), vm = require('node:vm');
const c = vm.createContext({ onWorkspaceReset() {}, MODULES: {}, S: { me: { site_domain: 'example.com' } },
  appName: () => '协作站', capability: () => true, ICON: { copy: '' }, icon: () => '', h: (tag, attrs, ...kids) => ({tag, attrs, kids}),
  promptModal(...args) { c.prompt = args; }, api() { throw Error('复制按钮不能发请求'); } });
c.navigator = { languages: ['zh-CN'] };
c.document = { querySelectorAll: () => [], getElementById: () => null };
for (const name of ['i18n-zh.js', 'i18n-en.js', 'i18n.js', 'values.js']) {
  vm.runInContext(fs.readFileSync(require('node:path').join(require('node:path').dirname(process.argv[1]), name), 'utf8'), c);
}
vm.runInContext(fs.readFileSync(process.argv[1], 'utf8'), c);
c.promptModal = (...args) => { c.prompt = args; };
const b = vm.runInContext('publishButton({id:"demo-web",kind:"网页"})', c);
b.attrs.onclick();
assert.ok(c.prompt[2].includes('发布到协作站'));
assert.ok(c.prompt[2].includes('app_id 填 demo-web'));
assert.ok(c.prompt[2].includes('slug 填 demo-web'));
assert.ok(c.prompt[2].includes('demo-web.example.com'));
const text = vm.runInContext('publishPrompt({id:"demo-web",site:{slug:"old-name"}})', c);
assert.ok(text.includes('slug 不用填'));
assert.ok(text.includes('old-name.example.com'));
vm.runInContext('S.me.site_domain = null', c);
assert.equal(vm.runInContext('publishButton()', c), null);
'''
    result = subprocess.run(["node", "-e", script, str(source)], capture_output=True, text=True, encoding="utf-8")
    assert result.returncode == 0, result.stderr
