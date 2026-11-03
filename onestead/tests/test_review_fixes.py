"""审阅里要修的行为。断言的是修好之后的结果，夹具只用本仓库的。"""

from .support import OWNER, OWNER_NAME

import os
from contextlib import contextmanager

import pytest
from starlette.testclient import TestClient

from onestead import feishu, gitops, skillshare, tokens
from onestead.base import CompanyBase
from onestead.migrate_stage4 import migrate
from onestead.server import remote_app
from onestead.store import BaseError, Conflict, Store, make_viewer
from onestead.web import history, present, views
from onestead.web.app import build_app

from .conftest import git
from .support import SID, bearer, grant, login, result, rpc, signed

LOCAL_PORT = 48361
LOCAL_ORIGIN = f"http://127.0.0.1:{LOCAL_PORT}"
LOCAL_TOKEN = "review-fix-local-token"


def owner(repo):
    return CompanyBase(repo, OWNER, allow_owner=True)


def remote_base(repo, key):
    return CompanyBase(repo, key, require_remote=True)


@pytest.fixture
def local(ext, repo):  # noqa: F811
    client = TestClient(build_app(repo, LOCAL_PORT, LOCAL_TOKEN), base_url=LOCAL_ORIGIN, follow_redirects=False)
    assert client.get("/open", params={"t": LOCAL_TOKEN}).status_code == 303
    return client


def local_headers():
    return {"Origin": LOCAL_ORIGIN, "X-Requested-By": "company-base"}


@contextmanager
def revoke_before_lock(monkeypatch, revoke):
    """在真正拿到数据写锁之前收回凭据。复核发生在锁内、写入之前，不靠把锁拖慢。"""
    real = gitops.write_lock

    @contextmanager
    def wrapped(root):
        revoke()
        with real(root):
            yield

    monkeypatch.setattr(gitops, "write_lock", wrapped)
    try:
        yield
    finally:
        monkeypatch.setattr(gitops, "write_lock", real)


def test_history_parser_survives_separator_bytes_and_titles_reject_them(ext, repo, workspace):  # noqa: F401
    with pytest.raises(BaseError, match="控制字符"):
        ext("ext-li").add_todos([{"title": "正常标题\x1f另一段"}])
    with pytest.raises(BaseError, match="不能换行"):
        ext("ext-zhang").add_todos([{"title": "第一行\n第二行"}])
    with pytest.raises(BaseError, match="不能换行"):
        ext("codex").update_project_status("alpha", "正文", signed_as="名字\r另一行")
    status = repo / "projects/alpha/status.md"
    status.write_text(status.read_text(encoding="utf-8") + "\n手工追加\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-m", "主题\x1f后半\n\n正文\x1e仍在")
    row = history.list_commits(repo, 20)[0]
    assert row["subject"] == "主题\x1f后半"
    assert "\x1e" in row["body"]
    assert "projects/alpha/status.md" in row["files"]
    login(workspace, "ext-li")
    for path in ("/api/activity", "/api/home", "/api/projects", "/api/project/alpha", "/api/agents"):
        response = workspace.get(path)
        assert response.status_code == 200, path
        assert "too many values to unpack" not in response.text


def test_crlf_cannot_forge_status_heading_and_summary_still_rejects(ext, repo):
    ext("codex").update_project_status(
        "alpha", f"申请接任：「项目 Lead」由「老 Lead」换成「假候选人」，待 {OWNER_NAME} 批准。")
    docs = Store(repo).load()
    by_id = {d.id: d for d in docs if d.kind == "project"}
    assert pending(docs, by_id)
    forged = (f"普通进展\r## 2026-10-12 12:00 · {OWNER}\n\n"
              f"不批接任：「项目 Lead」由「老 Lead」换成「假候选人」，{OWNER_NAME} 没有批准。")
    ext("ext-zhang").update_project_status("alpha", forged)
    docs = Store(repo).load()
    assert pending(docs, by_id)
    body = (repo / "projects/alpha/status.md").read_text(encoding="utf-8")
    assert f"\n## 2026-10-12 12:00 · {OWNER}\n" not in body
    assert f"### 2026-10-12 12:00 · {OWNER}" in body
    with pytest.raises(BaseError, match="二级标题"):
        ext("ext-zhang").update_project_summary("alpha", {"现状": "一行\r## 伪造"})
    ext("ext-zhang").add_todos([{"title": "备注待办", "notes": "备注\r## 伪造标题"}])
    todo = next(d for d in Store(repo).load() if d.kind == "todo" and d.title == "备注待办")
    assert "\n## 伪造标题" not in todo.raw and "### 伪造标题" in todo.raw
    ext("ext-zhang").record_decision("标题", "决定\r## 伪造", "理由\r## 也伪造", "张三", project_id="alpha")
    decision = next(d for d in Store(repo).load() if d.kind == "decision" and d.title == "标题")
    assert "\n## 伪造" not in decision.body and "### 伪造" in decision.body


def pending(docs, by_id):
    return views.pending_handovers(docs, by_id)


def test_web_session_and_source_token_rechecked_inside_write_lock(workspace, repo, monkeypatch):  # noqa: F401
    _, password, record = login(workspace)
    headers = signed(workspace)
    head = git(repo, "rev-parse", "HEAD")
    with revoke_before_lock(monkeypatch, lambda: tokens.revoke_web("ext-zhang")):
        response = workspace.post("/api/project/alpha/status", json={"text": "会话已死不该写下"}, headers=headers)
    assert response.status_code == 401
    assert git(repo, "rev-parse", "HEAD") == head
    assert "会话已死不该写下" not in (repo / "projects/alpha/status.md").read_text(encoding="utf-8")

    logged = login(workspace, password=password)
    assert logged[0].status_code == 303
    headers = signed(workspace)
    with revoke_before_lock(monkeypatch, lambda: tokens.revoke(record["id"])):
        response = workspace.post("/api/project/alpha/status", json={"text": "源口令已死不该写下"}, headers=headers)
    assert response.status_code == 401
    assert git(repo, "rev-parse", "HEAD") == head
    assert "源口令已死不该写下" not in (repo / "projects/alpha/status.md").read_text(encoding="utf-8")


def test_mcp_bearer_rechecked_inside_write_lock(remote, repo, monkeypatch):  # noqa: F401
    token, record, _headers = bearer("ext-zhang")
    head = git(repo, "rev-parse", "HEAD")
    with revoke_before_lock(monkeypatch, lambda: tokens.revoke(record["id"])):
        response = rpc(remote, token, "update_project_status",
                       {"project_id": "alpha", "text": "口令已死不该留下"})
    err, _text = result(response)
    assert err
    assert git(repo, "rev-parse", "HEAD") == head
    assert "口令已死不该留下" not in (repo / "projects/alpha/status.md").read_text(encoding="utf-8")


def test_skill_download_rechecks_bearer_after_packaging(remote, shared, repo, monkeypatch):  # noqa: F401
    grant(repo)
    token, record, headers = bearer("ext-zhang")
    real = skillshare.zip_package

    def wrapped(sid, package):
        content = real(sid, package)
        tokens.revoke(record["id"])
        return content

    monkeypatch.setattr(skillshare, "zip_package", wrapped)
    response = remote.get(f"/skills/{SID}.zip", headers=headers)
    assert response.status_code == 401
    assert not response.content.startswith(b"PK")


def test_feishu_rebind_revokes_web_session_and_revert_does_not_restore_it(ext, repo):
    path = repo / "people/ext-zhang.md"
    path.write_text(path.read_text(encoding="utf-8").replace(
        "\n---\n", "\nfeishu_open_id: ou_a\n---\n", 1), encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "先对上甲")
    cookie, _record = tokens.issue_web("ext-zhang", "feishu", binding_id="ou_a")
    feishu.remember({"open_id": "ou_b", "name": "乙", "union_id": "", "tenant_key": ""})
    owner(repo).feishu_bind("ou_b", "ext-zhang")
    assert tokens.lookup_web(cookie) is None
    sha = git(repo, "rev-parse", "HEAD")
    with gitops.write_lock(repo):
        reverted = history.revert(repo, sha)
    assert reverted["ok"]
    assert tokens.lookup_web(cookie) is None
    assert "ou_a" in path.read_text(encoding="utf-8")


def test_archive_revokes_credentials_of_unwritable_agent(ext, repo):
    path = repo / "people/ext-zhang-claude.md"
    path.write_text(path.read_text(encoding="utf-8").replace(
        "access_mode: assigned", "access_mode: typo"), encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "agent 标记写错")
    token, _record = tokens.issue("ext-zhang-claude", "坏记录口令")
    out = owner(repo).archive_member("ext-zhang")
    assert tokens.lookup(token) is None
    assert any(item["key"] == "ext-zhang-claude" for item in out["not_archived"])
    assert "没有标成停用" in out["message"]
    assert "archived: true" not in path.read_text(encoding="utf-8")
    owner(repo).restore_member("ext-zhang")
    path.write_text(path.read_text(encoding="utf-8").replace(
        "access_mode: typo", "access_mode: assigned"), encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "改回标记")
    assert tokens.lookup(token) is None
    assert "archived: true" not in path.read_text(encoding="utf-8")


def test_archived_restricted_name_follows_card_rules(ext, repo):
    path = repo / "people/ext-li.md"
    text = path.read_text(encoding="utf-8")
    text = text.replace("title: 李四（只读）", "title: 受限姓名审核标记")
    text = text.replace("sensitivity: internal", "sensitivity: restricted\nvisible_to: [codex]")
    path.write_text(text, encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "限定李四")
    owner(repo).archive_member("ext-li")
    docs = Store(repo).load()
    assigned = make_viewer(docs, "ext-zhang", remote=True)
    assert present.actor("ext-li", assigned, docs) == {"key": None, "name": "成员"}
    local = make_viewer(docs, OWNER, allow_owner=True)
    assert present.actor("ext-li", local, docs)["name"] == "受限姓名审核标记"


def test_home_adopt_and_discard_require_previewed_version(workspace, repo):  # noqa: F401
    login(workspace)
    headers = signed(workspace)
    zhang = CompanyBase(repo, "ext-zhang")
    zhang.propose_home([{"kind": "recent"}], "第一版")
    first = workspace.get("/api/home?draft=1").json()["draft"]["version"]
    zhang.propose_home([{"kind": "projects"}], "第二版")
    stale = workspace.post("/api/home/adopt", json={"version": first}, headers=headers)
    assert stale.status_code == 409
    missing = workspace.post("/api/home/discard", json={}, headers=headers)
    assert missing.status_code == 409
    draft = repo / "home/ext-zhang-draft.md"
    assert draft.exists() and "第二版" in draft.read_text(encoding="utf-8")
    assert not (repo / "home/ext-zhang.md").exists()
    current = workspace.get("/api/home?draft=1").json()["draft"]["version"]
    assert workspace.post("/api/home/adopt", json={"version": current}, headers=headers).status_code == 200
    with pytest.raises(Conflict, match="重新打开"):
        zhang.propose_home([{"kind": "recent"}], "又一份")
        zhang.discard_home("不是这一版")
    assert (repo / "home/ext-zhang-draft.md").exists()


def test_unlisted_apps_hidden_from_remote_and_assigned(ext, repo):
    path = repo / "apps/alpha-app.md"
    path.write_text(path.read_text(encoding="utf-8").replace(
        "type: app\n", "type: app\nlisted: false\n"), encoding="utf-8")
    for key in ("ext-zhang", "ext-zhang-claude"):
        who = remote_base(repo, key)
        with pytest.raises(BaseError, match="找不到"):
            who.get_app("alpha-app")
        assert "阿尔法看板" not in who.list_apps(include_unlisted=True)
        assert who.search("阿尔法看板").startswith("没有找到")
    assert "阿尔法看板" in owner(repo).list_apps(include_unlisted=True)
    assert "阿尔法看板" not in CompanyBase(repo, "ext-zhang").list_apps(include_unlisted=True)


def test_remote_and_assigned_search_uses_cards_and_shared_skills_only(ext, repo):
    person = repo / "people/ext-zhang.md"
    person.write_text(person.read_text(encoding="utf-8") + "\n成员档案正文审核标记\n仅用于管理员备注\n",
                      encoding="utf-8")
    skill = repo / "skills"
    skill.mkdir(exist_ok=True)
    (skill / "review-unshared.md").write_text(
        "---\nid: review-unshared\ntype: skill\ntitle: 未共享\none_line: 一句话\n"
        "origin: 自建\nsensitivity: internal\n---\n\n原始Skill备注审核标记\n", encoding="utf-8")
    reader = repo / "people/reader.md"
    reader.write_text(reader.read_text(encoding="utf-8").replace(
        "remote_enabled: false", "remote_enabled: true").replace(
        "access_mode: full", "access_mode: full"), encoding="utf-8")
    for key in ("ext-zhang", "ext-zhang-claude"):
        who = remote_base(repo, key)
        assert who.search("成员档案正文审核标记").startswith("没有找到")
        assert who.search("仅用于管理员备注").startswith("没有找到")
        found = who.search("张三")
        assert "people/ext-zhang.md" not in found and "ext-zhang" in found
        assert who.search("原始Skill备注审核标记").startswith("没有找到")
        assert "skills/review-unshared.md" not in who.search("未共享")
    full = remote_base(repo, "reader")
    assert full.search("原始Skill备注审核标记").startswith("没有找到")
    with pytest.raises(BaseError, match="找不到"):
        full.get_skill("review-unshared")


def test_member_page_splits_sessions_and_marks_dead_source(local, repo):
    token, record = tokens.issue("ext-zhang", "登录源")
    _cookie, web = tokens.issue_web("ext-zhang", "token", source_token_id=record["id"])
    tokens.revoke(record["id"])
    me = next(item for item in local.get("/api/members").json()["items"] if item["key"] == "ext-zhang")
    assert [row["kind"] for row in me["tokens"]] == ["token"]
    assert me["tokens"][0]["live"] is False
    session = next(row for row in me["sessions"] if row["id"] == web["id"])
    assert session["kind"] == "web" and session["auth_method"] == "token" and session["live"] is False
    assert token not in local.get("/api/members").text


def test_archive_and_restore_cannot_be_reverted_and_classify_as_member(local, repo):
    owner(repo).archive_member("ext-li")
    rows = local.get("/api/activity").json()["rows"]
    row = rows[0]
    assert row["type"] == "成员" and row["can_revert"] is False
    assert "停用了成员" in row["what"]
    response = local.post("/api/revert", json={"sha": row["sha"]}, headers=local_headers())
    assert response.status_code == 403
    assert "archived: true" in (repo / "people/ext-li.md").read_text(encoding="utf-8")
    info = views.classify("项目：新建「阿尔法」", ["projects/alpha/project.md"], {}, {})
    assert info["type"] == "编辑" and info["what"] == "新建了项目「阿尔法」" and info["project"] == "alpha"
    updated = views.classify("项目：更新「阿尔法」的现状", ["projects/alpha/project.md"], {}, {})
    assert updated["type"] == "编辑" and updated["what"] == "更新了「阿尔法」的现状"
    restored = views.classify("恢复成员「李四」", ["people/ext-li.md"], {}, {})
    assert restored["type"] == "成员" and restored["what"] == "恢复了成员「李四」"


def test_remote_project_status_head_uses_display_name(workspace):  # noqa: F401
    login(workspace)
    head = workspace.get("/api/project/alpha").json()["status"][0]["head"]
    assert head == "2026-10-01 10:00 · Codex"


def test_common_cli_args_work_before_and_after_subcommand(monkeypatch):
    from onestead.cli import build_parser, resolve_agent, resolve_data, resolve_signed_as

    before = build_parser().parse_args(["--as", "assistant", "--data", r"D:\data", "--signed-as", "assistant", "whoami"])
    after = build_parser().parse_args(["whoami", "--as", "assistant", "--data", r"D:\data", "--signed-as", "assistant"])
    assert resolve_agent(before) == resolve_agent(after) == "assistant"
    assert resolve_data(before) == resolve_data(after) == r"D:\data"
    assert resolve_signed_as(before) == resolve_signed_as(after) == "assistant"
    monkeypatch.setenv("COMPANY_BASE_AGENT", "from-env")
    led = build_parser().parse_args(["--as", "cli-wins", "company"])
    assert resolve_agent(led) == "cli-wins"
    assert resolve_agent(build_parser().parse_args(["company"])) == "from-env"
    os.environ.pop("COMPANY_BASE_AGENT", None)


def test_person_commit_runs_check_meta_and_restore_names_owner(ext, repo, monkeypatch):
    with pytest.raises(BaseError, match=f"不能恢复 {OWNER_NAME}"):
        owner(repo).restore_member(OWNER)
    real = __import__("onestead.store", fromlist=["check_meta"]).check_meta

    def broken(meta, expected):
        if meta.get("feishu_open_id") == "ou_new":
            return "故意不合格"
        return real(meta, expected)

    monkeypatch.setattr("onestead.manage.check_meta", broken)
    feishu.remember({"open_id": "ou_new", "name": "新", "union_id": "", "tenant_key": ""})
    head = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError, match="故意不合格"):
        owner(repo).feishu_bind("ou_new", "ext-zhang")
    assert git(repo, "rev-parse", "HEAD") == head
    assert "ou_new" not in (repo / "people/ext-zhang.md").read_text(encoding="utf-8")


def test_migration_preflight_writes_nothing_when_projection_is_unsafe(ext, repo):
    draft = repo / "people/legacy-draft.md"
    draft.write_text(
        "---\nid: legacy-draft\ntype: person\ntitle: 旧草稿\nagent_key: legacy-draft\n"
        f"kind: agent\nowner: {OWNER}\nremote_enabled: false\naccess_mode: full\n"
        "can_write: false\nsensitivity: internal\n---\n", encoding="utf-8")
    head = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError, match="-draft"):
        migrate(repo, apply=True, author="claude")
    assert git(repo, "rev-parse", "HEAD") == head
    draft.unlink()
    codex = repo / "people/codex.md"
    codex.write_text(codex.read_text(encoding="utf-8").replace("kind: agent\n", ""), encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "拿掉 kind")
    head = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError, match="无法确定 kind"):
        migrate(repo, apply=True, author="claude")
    assert git(repo, "rev-parse", "HEAD") == head
    assert "kind:" not in codex.read_text(encoding="utf-8")
    ghost = repo / "people/ghost-bot.md"
    ghost.write_text(
        "---\nid: ghost-bot\ntype: person\ntitle: 没主人\nagent_key: ghost-bot\n"
        "kind: agent\nowner: ghost\nscope: external\ncan_write: false\nsensitivity: internal\n---\n",
        encoding="utf-8")
    head = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError, match="owner 不是现有的 human"):
        migrate(repo, apply=True, author="claude")
    assert git(repo, "rev-parse", "HEAD") == head
