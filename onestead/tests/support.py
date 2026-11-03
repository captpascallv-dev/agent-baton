"""测试之间共用的夹具、常量和小函数。各文件只从这里或 conftest 取，不再互相 import。

端口按原来的测试分开，不合成一个。conftest 引入下面带 @pytest.fixture 的名字。
同名夹具以测试文件里的为准：ChatGPT 和安全回归有自己的 client；
个人工作台有自己的 workspace；真 MCP 服务有自己的 remote。
"""

import datetime as dt
import io
import os
import re
import subprocess
from pathlib import Path
from urllib.parse import parse_qs, urlparse

import pytest
from starlette.testclient import TestClient

from onestead import feishu, tokens
from onestead.base import CompanyBase, _dump_front
from onestead.server import remote_app
from onestead.store import TAIPEI, Store, split_front
from onestead.textutil import _dump_front as dump_meta
from onestead.web.app import build_app, build_workspace_app
from onestead.web.auth import RemoteAuth

OWNER = os.environ.get("COMPANY_BASE_TEST_OWNER", "alex")
OWNER_NAME = "老板" if OWNER == "boss" else ("Alex" if OWNER == "alex" else OWNER)

@pytest.fixture(autouse=True)
def isolated_config(tmp_path, monkeypatch):
    """配置只读临时文件；清空继承的部署参数，绝不碰真实配置或状态。"""
    path = tmp_path / "config.toml"
    for name in list(os.environ):
        if name.startswith("ONESTEAD_"):
            monkeypatch.delenv(name)
    monkeypatch.setenv("COMPANY_BASE_CONFIG", str(path))
    for name in ("DATA", "PUBLIC_HOST", "OWNER", "STATE_DIR", "SKILL_SNAPSHOTS", "SKILL_HARNESSES", "SITE_DOMAIN", "BACKUP_DIR", "CLOUDFLARED", "LANG"):
        monkeypatch.delenv("COMPANY_BASE_" + name, raising=False)
    # 旧的界面断言照旧在中文公司设置下验证；默认英文单独测试。
    monkeypatch.setenv("COMPANY_BASE_LANG", "zh")
    monkeypatch.setenv("COMPANY_BASE_OWNER", OWNER)
    monkeypatch.setenv("COMPANY_BASE_PUBLIC_HOST", PUBLIC_HOST)
    monkeypatch.setenv("COMPANY_BASE_STATE_DIR", str(tmp_path / "state"))
    return path


@pytest.fixture
def configured_data(repo, monkeypatch):
    """需要默认数据目录的入口只指向本次测试仓库。"""
    monkeypatch.setenv("COMPANY_BASE_DATA", str(repo))
    return repo


# 本机快捷方式网页。
WEB_PORT, WEB_TOKEN = 48123, "test-token-0123456789"
WEB_BASE = f"http://127.0.0.1:{WEB_PORT}"
WEB_POST_HEADERS = {"Origin": WEB_BASE, "X-Requested-By": "company-base"}

# 管理页用的另一个本机网页，和上面不是同一个端口。
MANAGE_PORT, MANAGE_TOKEN = 48124, "test-token-0123456789"
MANAGE_BASE = f"http://127.0.0.1:{MANAGE_PORT}"
MANAGE_POST_HEADERS = {"Origin": MANAGE_BASE, "X-Requested-By": "company-base"}

# 同事工作台。
WORKSPACE_PORT = 48321
PUBLIC_HOST = "workspace.example.test"
WORKSPACE_ORIGIN = f"https://{PUBLIC_HOST}"
WORKSPACE_COOKIE = "__Host-cb_web"
WORKSPACE_SOURCE = {"Origin": WORKSPACE_ORIGIN, "X-Requested-By": "company-base"}

# 技能共享的网页客户端。
SKILL_PORT = 48241
SKILL_BASE = f"http://127.0.0.1:{SKILL_PORT}"
SID = "shared-tool"

FEISHU_SECRET = "fake-secret-for-test"
INFO = {"name": "王五", "open_id": "ou_wang", "union_id": "on_wang", "tenant_key": "tk_test"}

APP = {"id": "demo-web", "title": "演示网页", "one_line": "一个测试用的网页应用", "kind": "网页", "project": "alpha",
       "maintainer": "claude", "icon": "演", "open": {"url": "http://127.0.0.1:9/"}, "code_path": "C:\\x\\demo"}

FIXED_NOW = dt.datetime(2026, 10, 10, 23, 30, tzinfo=TAIPEI)

EXTRA = {
    "people/ext-zhang.md": (
        "---\nid: ext-zhang\ntype: person\ntitle: 张三（合作方）\nkind: human\nagent_key: ext-zhang\n"
        "remote_enabled: true\naccess_mode: assigned\nprojects: [alpha, secret]\nassets: [deal]\ncan_write: true\nsensitivity: internal\n---\n"),
    "people/ext-zhang-claude.md": (
        "---\nid: ext-zhang-claude\ntype: person\ntitle: 张三的 Claude\nkind: agent\nagent_key: ext-zhang-claude\n"
        "remote_enabled: true\naccess_mode: assigned\nowner: ext-zhang\nprojects: [alpha, beta]\ncan_write: true\nsensitivity: internal\n---\n"),
    "people/ext-orphan.md": (
        "---\nid: ext-orphan\ntype: person\ntitle: 没主人的 agent\nkind: agent\nagent_key: ext-orphan\n"
        "remote_enabled: true\naccess_mode: assigned\nowner: ext-nobody\nprojects: [alpha]\ncan_write: true\nsensitivity: internal\n---\n"),
    "people/ext-li.md": (
        "---\nid: ext-li\ntype: person\ntitle: 李四（只读）\nkind: human\nagent_key: ext-li\n"
        "remote_enabled: true\naccess_mode: assigned\nprojects: [alpha]\ncan_write: false\nsensitivity: internal\n---\n"),
    "people/ext-li-bot.md": (
        "---\nid: ext-li-bot\ntype: person\ntitle: 李四的 bot\nkind: agent\nagent_key: ext-li-bot\n"
        "remote_enabled: true\naccess_mode: assigned\nowner: ext-li\ncan_write: true\nsensitivity: internal\n---\n"),
    "people/ext-typo.md": (
        "---\nid: ext-typo\ntype: person\ntitle: 写错了的远程成员\nagent_key: ext-typo\n"
        "kind: human\nremote_enabled: true\naccess_mode: typo\nprojects: [alpha]\ncan_write: true\nsensitivity: internal\n---\n"),
    "projects/beta/project.md": (
        "---\nid: beta\ntype: project\ntitle: 贝塔\nstate: 进行中\ngoal: 没授权给外部\n"
        "sensitivity: internal\nupdated: 2026-10-01\n---\n\n# 贝塔\n\n暗号贝塔湖。\n"),
    "projects/alpha/status.md": "# 阿尔法 · 进展\n\n## 2026-10-01 10:00 · codex\n\n暗号阿尔法进展。\n",
    "decisions/2026-10-01-alpha.md": (
        f"---\nid: 2026-10-01-alpha\ntype: decision\ntitle: 阿尔法的决定\ndate: 2026-10-01\ndecided_by: {OWNER_NAME}\n"
        "project: alpha\nsensitivity: internal\n---\n\n# 阿尔法的决定\n\n## 决定\n\n暗号阿尔法决定。\n"),
    "decisions/2026-10-01-general.md": (
        f"---\nid: 2026-10-01-general\ntype: decision\ntitle: 公司内部决定\ndate: 2026-10-01\ndecided_by: {OWNER_NAME}\n"
        "sensitivity: internal\n---\n\n# 公司内部决定\n\n## 决定\n\n暗号内部决定。\n"),
    "decisions/2026-10-01-secret.md": (
        f"---\nid: 2026-10-01-secret\ntype: decision\ntitle: 家里的决定\ndate: 2026-10-01\ndecided_by: {OWNER_NAME}\n"
        "project: secret\nsensitivity: restricted\nvisible_to: [codex]\n---\n\n# 家里的决定\n\n暗号家里决定。\n"),
    "assets/deal.md": (
        "---\nid: deal\ntype: asset\ntitle: 合作条款\nsensitivity: restricted\nvisible_to: [ext-zhang]\n---\n\n暗号合作条款。\n"),
    "assets/handbook-like.md": (
        "---\nid: handbook-like\ntype: asset\ntitle: 内部模板\nsensitivity: internal\n---\n\n暗号内部模板。\n"),
    "apps/alpha-app.md": (
        "---\nid: alpha-app\ntype: app\ntitle: 阿尔法看板\none_line: 阿尔法的看板\nkind: 网页\nproject: alpha\n"
        "open:\n  url: http://127.0.0.1:1/\nsensitivity: internal\n---\n"),
    "apps/beta-app.md": (
        "---\nid: beta-app\ntype: app\ntitle: 贝塔看板\none_line: 贝塔的看板\nkind: 网页\nproject: beta\n"
        "open:\n  url: http://127.0.0.1:2/\nsensitivity: internal\n---\n"),
    "todos/2026-10-01-100000-1.md": (
        "---\nid: 2026-10-01-100000-1\ntype: todo\ntitle: 暗号公开待办\nstatus: open\nsensitivity: public\n---\n"),
}


def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True,
                          encoding="utf-8", check=True).stdout.strip()


def hidden(agent, term):
    return agent.search(term).startswith("没有找到")


def make_external(owner_base, rel, **changes):
    s = owner_base.doc_source(rel)
    values = {f["key"]: f["value"] for f in s["fields"]}
    return owner_base.save_doc(rel, s["version"], values | {"remote_enabled": True, "access_mode": "assigned"} | changes, None)


@pytest.fixture
def ext(repo, as_agent):
    for rel, content in EXTRA.items():
        path = repo / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        with io.open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "远程成员")
    return as_agent


@pytest.fixture
def client(repo):
    c = TestClient(build_app(repo, WEB_PORT, WEB_TOKEN), base_url=WEB_BASE, follow_redirects=False)
    r = c.get(f"/open?t={WEB_TOKEN}")
    assert r.status_code == 303
    return c


@pytest.fixture
def workspace(ext, repo):
    app = remote_app(str(repo), WORKSPACE_PORT, public_host=PUBLIC_HOST)
    with TestClient(app, base_url=WORKSPACE_ORIGIN, follow_redirects=False) as c:
        yield c


def challenge(client):
    response = client.get("/login")
    assert response.status_code == 200
    return re.search(r'name="challenge" value="([^"]+)"', response.text)[1]


def login(client, key="ext-zhang", password=None):
    password, record = tokens.issue(key, "测试登录") if password is None else (password, None)
    value = challenge(client)
    response = client.post("/auth/login", data={"token": password, "challenge": value},
                           headers={"Origin": WORKSPACE_ORIGIN})
    return response, password, record


def signed(client):
    return WORKSPACE_SOURCE | {"X-CSRF-Token": client.get("/api/me").json()["csrf"]}


def put(repo, rel, meta, body=""):
    path = repo / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(_dump_front(meta) + body)


def change(repo, rel, **fields):
    doc = next(d for d in Store(repo).load() if d.rel == rel)
    put(repo, rel, doc.meta | fields, doc.body)


@pytest.fixture
def personal_workspace(repo):
    for key, kind, owner in (("a", "human", None), ("b", "human", None),
                             ("a-bot", "agent", "a"), ("b-bot", "agent", "b")):
        meta = dict(id=key, type="person", title=key, agent_key=key, kind=kind, can_write=True,
                    access_mode="assigned", remote_enabled=True, sensitivity="internal", projects=["alpha"])
        if owner:
            meta["owner"] = owner
        put(repo, f"people/{key}.md", meta)
    change(repo, "projects/alpha/project.md", sensitivity="public")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "测试同事工作台")

    def make(key):
        return CompanyBase(repo, key, now=lambda: FIXED_NOW, allow_owner=key == OWNER)

    return make


def skill(path, name, text="正文", when=None, extra=None):
    path.mkdir(parents=True, exist_ok=True)
    (path / "SKILL.md").write_text(f"---\nname: {name}\ndescription: {name} 的说明\n---\n\n{text}\n", encoding="utf-8")
    for fn, body in (extra or {}).items():
        (path / fn).write_text(body, encoding="utf-8")
    if when:
        for p in path.rglob("*"):
            os.utime(p, (when, when))
    return path


def junction(link, target):
    link.parent.mkdir(parents=True, exist_ok=True)
    if os.name == "nt":
        import _winapi

        _winapi.CreateJunction(str(target), str(link))
    else:
        link.symlink_to(target, target_is_directory=True)


def mark(repo, rel, **changes):
    path = repo / rel
    meta, body, problem = split_front(path.read_text(encoding="utf-8"))
    assert not problem
    meta.update(changes)
    path.write_text(dump_meta(meta) + body, encoding="utf-8")


def grant(repo, key="ext-zhang", ids=None):
    mark(repo, f"people/{key}.md", skills=[SID] if ids is None else ids)


@pytest.fixture
def shared(ext, skill_home, repo):
    path = skill(skill_home / ".agents" / "skills" / SID, "共享工具", "完整操作步骤\n<script>危险标签</script>")
    (path / "scripts").mkdir()
    (path / "scripts" / "run.py").write_text("print('测试')\n", encoding="utf-8")
    (path / "SKILL.md").write_text((path / "SKILL.md").read_text(encoding="utf-8").replace(
        "---\n\n", "version: 1.2\n---\n\n", 1), encoding="utf-8")
    junction(skill_home / ".claude" / "skills" / SID, path)
    ext("claude").register_skill({"id": SID, "title": "共享工具", "one_line": "处理示例任务",
        "maintainer": "claude", "home": str(path), "upstream": "https://example.test/tool",
        "notes": "私有备注不外传"})
    return path


@pytest.fixture
def remote(shared, repo):
    with TestClient(remote_app(str(repo), SKILL_PORT, web_test_origin=SKILL_BASE), base_url=SKILL_BASE,
                    follow_redirects=False, headers={"Origin": SKILL_BASE}) as c:
        yield c


def bearer(key="ext-zhang", **kwargs):
    token, record = tokens.issue(key, "测试共享", **kwargs)
    return token, record, {"Authorization": f"Bearer {token}"}


def rpc(client, token, name, args=None):
    h = {"Accept": "application/json, text/event-stream", "MCP-Protocol-Version": "2025-06-18",
         "Authorization": f"Bearer {token}"}
    return client.post("/mcp", headers=h, json={"jsonrpc": "2.0", "id": 9, "method": "tools/call",
                                                 "params": {"name": name, "arguments": args or {}}})


def result(response):
    assert response.status_code == 200
    r = response.json()["result"]
    return bool(r.get("isError")), "".join(x.get("text", "") for x in r.get("content", []))


@pytest.fixture
def business(workspace, shared, ext, repo):
    mark(repo, "people/ext-zhang.md", projects=["alpha"])
    grant(repo)
    a = CompanyBase(repo, "ext-zhang", require_remote=True)
    b = CompanyBase(repo, "ext-li", require_remote=True)
    a.add_todos([{"title": "甲本人待办", "project": "alpha"}])
    b.add_todos([{"title": "乙私密待办", "project": "alpha"}])
    a.propose_home([{"kind": "today_todos"}, {"kind": "shared_skills"}, {"kind": "activity_chart"},
                    {"kind": "recent"}], "甲的草稿")
    assert login(workspace)[0].status_code == 303
    todos = [d for d in Store(repo).load() if d.kind == "todo"]
    return {"client": workspace, "a": a, "b": b, "tid": next(d.id for d in todos if d.title == "甲本人待办"),
            "other_tid": next(d.id for d in todos if d.title == "乙私密待办"), "shared": shared}


def _remote(repo):
    origin = "http://127.0.0.1:48331"
    auth = RemoteAuth(repo, "workspace.example.test", test_origin=origin)
    c = TestClient(build_workspace_app(repo, auth), base_url=origin, follow_redirects=False)
    found = re.search(r'name="challenge" value="([^"]+)"', c.get("/login").text)[1]
    password, _ = tokens.issue("ext-zhang", "同事登录")
    assert c.post("/auth/login", data={"token": password, "challenge": found},
                  headers={"Origin": origin}).status_code == 303
    headers = {"Origin": origin, "X-Requested-By": "company-base",
               "X-CSRF-Token": c.get("/api/me").json()["csrf"]}
    return c, headers


def write_config(folder: Path) -> None:
    (folder / "feishu-login.txt").write_text("# 测试应用，下面才是 id\napp_id=cli_test\n", encoding="utf-8")
    (folder / "feishu-secret.txt").write_text(f"  {FEISHU_SECRET}\n", encoding="utf-8")


def patch_user(monkeypatch, info=None, calls=None):
    info = dict(INFO if info is None else info)
    calls = calls if calls is not None else []

    def exchange(app, code, redirect_uri):
        calls.append(("exchange", app.get("app_id"), code, redirect_uri))
        return "atk-test"

    def user_info(access_token):
        calls.append(("info", access_token))
        return dict(info)

    monkeypatch.setattr(feishu, "_exchange_code", exchange)
    monkeypatch.setattr(feishu, "_user_info", user_info)
    return calls


def auth_query(response):
    return parse_qs(urlparse(response.headers["location"]).query)


def plant(repo, rel, open_id):
    path = repo / rel
    text = path.read_text(encoding="utf-8")
    path.write_text(text.replace("\n---\n", f"\nfeishu_open_id: {open_id}\n---\n", 1), encoding="utf-8")
