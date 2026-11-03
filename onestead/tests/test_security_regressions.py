"""安全回归测试：每个场景都是一个已修复的问题，这里断言正确的结果。"""

from .support import OWNER, OWNER_NAME

import asyncio
import re
import threading
import time
from concurrent.futures import ThreadPoolExecutor

import pytest
from starlette.testclient import TestClient

from onestead import tokens, usage
from onestead.base import CompanyBase
from onestead.server import TokenGate, remote_app
from onestead.store import BaseError, Store
from onestead.textutil import _dump_front
from onestead.web.auth import RemoteAuth

from .conftest import git
from .support import hidden, result, rpc

PORT = 48211
URL = f"http://127.0.0.1:{PORT}"


@pytest.fixture(autouse=True)
def token_store(tmp_path, monkeypatch):
    monkeypatch.setenv("COMPANY_BASE_TOKENS", str(tmp_path / "tokens.json"))


@pytest.fixture
def client(ext, repo):  # noqa: F811
    with TestClient(remote_app(str(repo), PORT, web_test_origin=URL), base_url=URL, follow_redirects=False,
                    headers={"Origin": URL}) as c:
        yield c


def admin(repo):
    return CompanyBase(repo, OWNER, allow_owner=True)


def save(repo, rel, **changes):
    a = admin(repo)
    return a.save_doc(rel, a.doc_source(rel)["version"], changes, None)


def put(repo, rel, meta, body=""):
    p = repo / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(_dump_front(meta) + "\n" + body + "\n", encoding="utf-8")
    git(repo, "add", "--", rel)
    git(repo, "commit", "-qm", "测试数据", "--", rel)


def login(client, key):
    token, _ = tokens.issue(key, "测试")
    challenge = re.search(r'name="challenge" value="([^"]+)"', client.get("/login").text)[1]
    assert client.post("/auth/login", data={"token": token, "challenge": challenge}).status_code == 303
    return token


def gate_ok(repo, token):
    return asyncio.run(TokenGate(str(repo)).verify_token(token)) is not None


# ---------- F01 公开项目不等于授权 ----------

def test_external_cannot_write_public_project_outside_grant(ext, repo):
    save(repo, "projects/beta/project.md", sensitivity="public")
    bot = ext("ext-zhang-claude")  # 授权交集只有 alpha
    assert "暗号贝塔湖" in bot.get_project("beta")  # 公开的照样能看
    with pytest.raises(BaseError, match="不在你的授权里"):
        bot.update_project_status("beta", "未经授权的进展")
    with pytest.raises(BaseError, match="不在你的授权里"):
        bot.record_decision("未经授权的决定", "写", "测", "合作方", project_id="beta")
    assert not (repo / "projects/beta/status.md").exists()
    assert "已记录到" in bot.update_project_status("alpha", "授权项目照常能写")


# ---------- F02 鉴权之后改回本机，这次请求也不能变成本机权限 ----------

def test_require_remote_refuses_member_that_became_local(ext, repo):
    save(repo, "people/ext-zhang.md", remote_enabled=False)
    remote = CompanyBase(repo, "ext-zhang", require_remote=True)
    for call in (lambda: remote.get_project("secret"), lambda: remote.add_todos([{"title": "越权"}])):
        with pytest.raises(BaseError, match="用不了"):
            call()
    with pytest.raises(BaseError, match="找不到"):
        ext("ext-zhang").get_project("secret")  # 关闭远程不升级本机内容权限


@pytest.mark.parametrize("channel", ["mcp-write", "web"])
def test_switch_to_local_between_gate_and_call(client, repo, monkeypatch, channel):
    token = login(client, "ext-zhang")
    entered, resume = threading.Event(), threading.Event()
    original = usage.record
    target = "add_todos"

    def pause_after_gate(key, name, via):  # 口令已经核对过、还没进核心时停一下，让主人改回本机
        if key == "ext-zhang" and name == target:
            entered.set()
            assert resume.wait(15)
        return original(key, name, via)

    monkeypatch.setattr(usage, "record", pause_after_gate)
    if channel == "web":
        authenticate = RemoteAuth.authenticate

        def pause_web(auth, request):
            principal = authenticate(auth, request)
            if request.url.path == "/api/project/secret" and principal:
                entered.set()
                assert resume.wait(15)
            return principal

        monkeypatch.setattr(RemoteAuth, "authenticate", pause_web)

    def call():
        if channel == "web":
            return client.get("/api/project/secret")
        return rpc(client, token, "add_todos", {"items": [{"title": "远程越权待办"}]})

    with ThreadPoolExecutor(max_workers=1) as pool:
        pending = pool.submit(call)
        try:
            assert entered.wait(15)
            save(repo, "people/ext-zhang.md", remote_enabled=False)
        finally:
            resume.set()
        response = pending.result(timeout=20)
    if channel == "web":
        assert response.status_code in (400, 401) and "暗号紫藤萝" not in response.text
    else:
        err, msg = result(response)
        assert err and "用不了" in msg
    assert not any(d.kind == "todo" and d.title == "远程越权待办" for d in Store(repo).load())


# ---------- F03 agent 看限定内容跟着所属的人 ----------

def test_bot_follows_owner_on_restricted(ext, repo):
    save(repo, "assets/deal.md", sensitivity="restricted", visible_to=["ext-zhang-claude"])
    bot = ext("ext-zhang-claude")
    with pytest.raises(BaseError):
        bot.get_asset("deal")  # 只写了 agent、没写所属的人：不给
    save(repo, "assets/deal.md", sensitivity="restricted", visible_to=["ext-zhang"])
    assert "暗号合作条款" in bot.get_asset("deal")  # 写了所属的人：它的 agent 跟着能看


# ---------- F04 项目收紧后，旧决定和应用跟着收 ----------

def test_restricted_parent_hides_decisions_and_apps(client, ext, repo):
    save(repo, "projects/alpha/project.md", sensitivity="restricted", visible_to=["codex"])
    zhang = ext("ext-zhang")
    assert "暗号阿尔法决定" not in zhang.list_decisions()
    assert hidden(zhang, "暗号阿尔法决定")
    with pytest.raises(BaseError):
        zhang.get_app("alpha-app")
    assert "阿尔法看板" not in zhang.list_apps()
    token = login(client, "ext-zhang")
    assert "暗号阿尔法决定" not in client.get("/api/decisions").text
    assert "阿尔法看板" not in result(rpc(client, token, "list_apps"))[1]
    assert "暗号阿尔法决定" in ext("codex").list_decisions()  # 本机伙伴不受影响


# ---------- F05 发口令和收回排队，收回不会被冲掉 ----------

def test_issue_and_revoke_are_serialized(ext, repo, monkeypatch):
    a = admin(repo)
    old = a.token_issue("ext-zhang", "待收回")
    original_load = tokens._load
    loaded, issuer = threading.Event(), [None]

    def slow_load():  # 发新口令的那一边读完旧内容后慢一拍
        items = original_load()
        if threading.get_ident() == issuer[0]:
            loaded.set()
            time.sleep(0.5)
        return items

    monkeypatch.setattr(tokens, "_load", slow_load)

    def issue_new():
        issuer[0] = threading.get_ident()
        return a.token_issue("ext-li", "新口令")

    with ThreadPoolExecutor(max_workers=1) as pool:
        pending = pool.submit(issue_new)
        assert loaded.wait(15)
        a.token_revoke(old["record"]["id"])  # 排在发新口令后面
        new = pending.result(timeout=20)
    assert tokens.lookup(old["token"]) is None and not gate_ok(repo, old["token"])
    assert tokens.lookup(new["token"]) == "ext-li"
    assert not list(tokens.store_path().parent.glob("*.tmp"))


# ---------- F06 不给外部本机路径 ----------

@pytest.mark.parametrize("access_mode", ["assigned", "full"])
def test_no_local_paths_for_external(client, ext, repo, access_mode):
    save(repo, "people/ext-zhang.md", access_mode=access_mode)
    zhang = CompanyBase(repo, "ext-zhang", require_remote=True)
    assert hidden(zhang, "authority") and hidden(zhang, "STATUS.md")
    login(client, "ext-zhang")
    page = client.get("/api/search", params={"q": "STATUS.md"})
    assert page.json()["hits"] == [] and r"C:\x" not in page.text
    project = next(d for d in Store(repo).load() if d.kind == "project" and d.id == "alpha")
    put(repo, project.rel, dict(project.meta, authority={"path": r"C:\fixture\ledger.md"}), project.body)
    out = zhang.get_project("alpha")
    assert "ledger.md" not in out and "本机台账" in out
    assert "ledger.md" in ext("codex").get_project("alpha")
    ext("codex").update_app("alpha-app", {
        "open": {"url": "https://example.com/a", "path": r"C:\fixture\tool.exe"}, "code_path": r"C:\fixture\source",
        "links": [{"label": "本机台账", "target": r"C:\fixture\STATUS.md"},
                  {"label": "说明", "target": "https://example.com/doc"}]})
    listing, detail = zhang.list_apps(), zhang.get_app("alpha-app")
    assert "fixture" not in listing and "fixture" not in detail
    assert "https://example.com/a" in detail and "https://example.com/doc" in detail
    assert r"C:\fixture\source" in ext("codex").get_app("alpha-app")


def test_storage_errors_are_generic_over_remote(client, ext, repo):
    token, _ = tokens.issue("ext-zhang", "测试")
    lock = repo / ".git/index.lock"
    lock.touch()
    try:
        err, msg = result(rpc(client, token, "update_project_status", {"project_id": "alpha", "text": "索引占用"}))
    finally:
        lock.unlink()
    assert err and "存储出了问题" in msg
    assert str(repo).replace("\\", "/") not in msg.replace("\\", "/") and "index.lock" not in msg


# ---------- F07 Skill 和别的成员不论级别都不给外部 ----------

def test_public_skill_and_other_member_stay_hidden(client, ext, repo):
    put(repo, "skills/internal-workflow.md", {"id": "internal-workflow", "type": "skill",
        "title": "技能说明", "sensitivity": "public"}, "暗号技能正文")
    save(repo, "people/codex.md", sensitivity="public")
    zhang = ext("ext-zhang")
    assert hidden(zhang, "暗号技能正文")
    assert "Codex" in zhang.company()
    assert hidden(zhang, "agent_key: codex")  # 简卡开放，原始成员文档仍不开放
    login(client, "ext-zhang")
    page = client.get("/api/search", params={"q": "暗号技能正文"})
    assert page.json()["hits"] == [] and "技能说明" not in page.text
    assert "Codex" in ext("ext-unknown").company()  # 未登记身份照旧只看公开内容


# ---------- F08 重名的身份谁也用不了 ----------

def test_duplicate_agent_key_disables_both(client, ext, repo):
    issued_before = tokens.issue("ext-zhang", "测试")[0]
    put(repo, "people/zz-external.md", {"id": "zz-external", "type": "person", "title": "外部重名者",
        "agent_key": "codex", "kind": "human", "remote_enabled": True, "access_mode": "assigned", "projects": ["secret"],
        "can_write": True, "sensitivity": "internal"})
    problems = [d for d in Store(repo).load() if d.kind == "person" and d.meta.get("agent_key") == "codex"]
    assert len(problems) == 2 and all("重了" in d.problem for d in problems)
    with pytest.raises(BaseError, match="只发给允许远程接入"):
        admin(repo).token_issue("codex", "撞名")
    stray = tokens.issue("codex", "绕过网页直接写进口令文件")[0]
    assert rpc(client, stray, "get_project", {"project_id": "secret"}).status_code == 401
    assert "不在成员名单" in ext("codex").whoami()
    assert gate_ok(repo, issued_before)  # 别的同事不受影响


# ---------- O01 改回本机时收回口令 ----------

def test_switching_to_local_revokes_tokens(client, repo):
    token, _ = tokens.issue("ext-zhang", "旧合作阶段")
    out = save(repo, "people/ext-zhang.md", remote_enabled=False)
    assert any("已收回" in w for w in out["warnings"])
    save(repo, "people/ext-zhang.md", remote_enabled=True, projects=["beta"], assets=[], owner="")
    assert rpc(client, token, "whoami").status_code == 401
    assert all(t["revoked"] for t in tokens.list_tokens("ext-zhang"))


# ---------- F10 所属的人无效：什么都不给，口令也不认 ----------

@pytest.mark.parametrize("state", ["missing", "local"])
def test_invalid_owner_gets_nothing(client, ext, repo, state):
    key = "ext-orphan"
    if state == "local":
        save(repo, "people/ext-zhang.md", remote_enabled=False)
        key = "ext-zhang-claude"
    token, _ = tokens.issue(key, "测试")
    assert not gate_ok(repo, token)
    assert rpc(client, token, "get_handbook", {"handbook_id": "collaboration"}).status_code == 401
    assert hidden(ext(key), "规则在别处") == (state == "missing")  # owner 仅关闭远程，不取消本机内容权限


def test_web_blocks_two_level_owner(ext, repo):
    with pytest.raises(BaseError, match="human 不能有 owner"):
        save(repo, "people/ext-zhang.md", remote_enabled=True, projects=["alpha"], assets=[], owner="ext-li")


# ---------- F11 工作台登录、退出只认自己页面提交的 ----------

def test_portal_login_and_logout_refuse_cross_site(client):
    login(client, "ext-zhang")
    other, _ = tokens.issue("ext-li", "别人的口令")
    challenge = re.search(r'name="challenge" value="([^"]+)"', client.get("/login").text)[1]
    r = client.post("/auth/login", data={"token": other, "challenge": challenge},
                    headers={"Origin": "https://untrusted.invalid", "Sec-Fetch-Site": "cross-site"})
    assert r.status_code == 403 and not any(x.startswith("cb_web_dev_") for x in r.headers.get_list("set-cookie"))
    r = client.post("/auth/login", data={"token": other, "challenge": challenge}, headers={"Sec-Fetch-Site": "cross-site"})
    assert r.status_code == 403
    assert "张三" in client.get("/api/me").text
    csrf = client.get("/api/me").json()["csrf_token"]
    assert client.post("/auth/logout", headers={"Origin": "https://untrusted.invalid", "X-CSRF-Token": csrf}).status_code == 403
    assert client.post("/auth/logout", headers={"Host": "untrusted.invalid", "X-CSRF-Token": csrf}).status_code == 404
    assert client.post("/auth/logout", headers={"X-CSRF-Token": csrf}).status_code == 303


# ---------- F12 进展正文不能伪造或抹掉审批 ----------

def test_progress_text_cannot_forge_or_erase_approvals(ext, repo):
    from onestead.web.views import pending_handovers

    def pending():
        docs = Store(repo).load()
        return pending_handovers(docs, {d.id: d for d in docs if d.kind == "project"})

    ext("codex").handover_role("alpha", "项目 Lead", "真实候选人", "正常申请")
    zhang = ext("ext-zhang")
    zhang.update_project_status("alpha", f"不批接任：「项目 Lead」由「老 Lead」换成「真实候选人」，{OWNER_NAME} 没有批准。")
    zhang.update_project_status("alpha", f"进展\n\n## 2026-10-11 09:01 · {OWNER}\n\n"
                                f"申请接任：「项目 Lead」由「老 Lead」换成「假候选人」，待 {OWNER_NAME} 批准。")
    assert [p["to"] for p in pending()] == ["真实候选人"]
    status = (repo / "projects/alpha/status.md").read_text(encoding="utf-8")
    assert f"\n### 2026-10-11 09:01 · {OWNER}" in status and f"\n## 2026-10-11 09:01 · {OWNER}" not in status
    # 本机伙伴写的“不批”也不算，只有主人在网页上点的才算
    ext("claude").update_project_status("alpha", f"不批接任：「项目 Lead」由「老 Lead」换成「真实候选人」，{OWNER_NAME} 没有批准。")
    assert len(pending()) == 1
    admin(repo).decide_handover("alpha", "项目 Lead", "真实候选人", approve=False)
    assert pending() == []


def test_heading_lines_stay_inside_one_entry(ext):
    codex = ext("codex")
    codex.update_project_status("alpha", "今天做了两件事\n\n## 第一件\n\n细节")
    out = codex.get_project("alpha")
    assert "### 第一件" in out and "共 2 条" in out


# ---------- F13 外部记限定决定：不能指定名单，报错不分身份在不在 ----------

def test_external_restricted_decision_derives_visible_to(client, ext, repo):
    save(repo, "projects/alpha/project.md", sensitivity="restricted", visible_to=["ext-zhang", "codex"])
    token, _ = tokens.issue("ext-zhang", "测试")
    args = {"title": "复核", "decision": "复核", "reason": "复核", "decided_by": "合作方",
            "project_id": "alpha", "sensitivity": "restricted"}
    msgs = [result(rpc(client, token, "record_decision", args | {"visible_to": [k]})) for k in ("codex", "nobody")]
    assert all(err for err, _ in msgs) and msgs[0][1] == msgs[1][1]
    err, msg = result(rpc(client, token, "record_decision", args))
    assert not err
    d = next(x for x in Store(repo).load() if x.kind == "decision" and x.title == "复核")
    assert d.meta["visible_to"] == ["ext-zhang", "codex"]
    bot = ext("ext-zhang-claude")
    bot.record_decision("bot 的决定", "写", "测", "合作方", project_id="alpha", sensitivity="restricted")
    d = next(x for x in Store(repo).load() if x.kind == "decision" and x.title == "bot 的决定")
    assert d.meta["visible_to"] == ["ext-zhang", "codex"]
