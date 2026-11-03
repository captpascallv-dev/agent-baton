"""停用成员、项目页列出应用、新建项目。"""

from .support import OWNER, OWNER_NAME

import asyncio
import json

import pytest

from onestead import feishu, tokens
from onestead.base import CompanyBase
from onestead.server import TokenGate
from onestead.store import BaseError, Store, check_meta, make_viewer, member_cards, member_keys
from onestead.web.present import actor

from .conftest import FIXED_NOW, git
from .support import APP, WEB_POST_HEADERS as POST_HEADERS, _remote

PERSON = {
    "id": "ada", "type": "person", "title": "艾达", "agent_key": "ada", "kind": "human",
    "remote_enabled": False, "access_mode": "assigned", "sensitivity": "internal",
}


@pytest.fixture
def owner(repo):
    return CompanyBase(repo, OWNER, now=lambda: FIXED_NOW, allow_owner=True)


def test_archived_must_be_bool():
    assert check_meta({**PERSON, "archived": "true"}, "person") == "archived 要是 true 或 false"
    assert check_meta({**PERSON, "archived": True}, "person") is None
    assert check_meta({**PERSON, "archived": False}, "person") is None


def _person(owner_base, doc_id, title, **fields):
    body = fields.pop("body", "")
    owner_base.create_doc("person", {"id": doc_id, "title": title, "sensitivity": "internal", **fields}, body)


def test_archive_revokes_access_and_keeps_names(repo, owner, as_agent, client, feishu_cfg):
    from .support import write_config
    write_config(feishu_cfg)
    _person(owner, "ada", "艾达", kind="human", role="同事", can_write=True, remote_enabled=True,
            access_mode="assigned", projects=["alpha"], body="暗号停用成员正文")
    _person(owner, "ada-bot", "艾达的助手", kind="agent", owner="ada", can_write=True, remote_enabled=True,
            access_mode="assigned", projects=["alpha"])
    feishu.remember({"open_id": "ou_ada", "name": "艾达"})
    owner.feishu_bind("ou_ada", "ada")
    secret_ada, rec_ada = tokens.issue("ada", "艾达的电脑", issued_by=OWNER)
    web_ada, _ = tokens.issue_web("ada", "token", source_token_id=rec_ada["id"])
    feishu_tok, _ = tokens.issue("ada", "飞书登录", kind="feishu", days=7, issued_by=OWNER)
    secret_bot, _ = tokens.issue("ada-bot", "助手", issued_by=OWNER)
    assert asyncio.run(TokenGate(str(repo)).verify_token(secret_ada)) is not None

    ada = as_agent("ada")
    ada.update_project_status("alpha", "艾达记了一条。")
    ada.record_decision("艾达的决定", "就这样", "因为要测署名", "艾达", "alpha")
    ada.add_todos([{"title": "暗号停用待办"}])
    home = repo / "home" / "ada.md"
    home.parent.mkdir(exist_ok=True)
    home.write_text("---\nid: ada\ntype: home\ntitle: 艾达的首页\nowner: ada\nstate: active\n"
                    "sensitivity: internal\n---\n\n# 艾达的首页\n", encoding="utf-8", newline="\n")
    project_bytes = (repo / "projects" / "alpha" / "project.md").read_bytes()
    todo = next(p for p in (repo / "todos").glob("*.md") if "暗号停用待办" in p.read_text(encoding="utf-8"))
    todo_bytes, home_bytes = todo.read_bytes(), home.read_bytes()
    before = git(repo, "rev-parse", "HEAD")

    out = owner.archive_member("ada")
    assert out["agents"] == ["艾达的助手"] and out["revoked"] == 4 and out["message"] == "已停用。"
    assert git(repo, "rev-parse", "HEAD") != before
    assert set(git(repo, "diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD").splitlines()) == {
        "people/ada.md", "people/ada-bot.md"}
    log = git(repo, "log", "-1", "--format=%an%n%s%n%b")
    assert log.startswith(f"{OWNER}\n停用成员「艾达」\n") and "网页界面" in log
    for rel in ("people/ada.md", "people/ada-bot.md"):
        text = (repo / rel).read_text(encoding="utf-8")
        assert "archived: true" in text and "remote_enabled: false" in text and "feishu_open_id" not in text
    assert "暗号停用成员正文" in (repo / "people" / "ada.md").read_text(encoding="utf-8")
    assert project_bytes == (repo / "projects" / "alpha" / "project.md").read_bytes()
    assert todo_bytes == todo.read_bytes() and home_bytes == home.read_bytes()

    docs = Store(repo).load()
    todo_doc = next(d for d in docs if d.kind == "todo" and d.title == "暗号停用待办")
    home_doc = next(d for d in docs if d.rel == "home/ada.md")
    assert home_doc.problem is None and todo_doc.problem is None
    for key in (OWNER, "ada", "ada-bot", "claude"):
        viewer = make_viewer(docs, key, allow_owner=key == OWNER)
        assert not viewer.can_see(todo_doc) and not viewer.can_see(home_doc)
    for key in ("ada", "ada-bot"):
        viewer = make_viewer(docs, key)
        assert viewer.valid is False and viewer.can_remote is False and viewer.can_write is False
    assert "不在成员名单的有效记录" in as_agent("ada").whoami()
    with pytest.raises(BaseError):
        as_agent("ada").get_project("alpha")
    with pytest.raises(BaseError):
        as_agent("ada-bot").update_project_status("alpha", "不该写")
    for secret in (secret_ada, web_ada, feishu_tok, secret_bot):
        assert tokens.lookup(secret) is None
    assert asyncio.run(TokenGate(str(repo)).verify_token(secret_ada)) is None
    with pytest.raises(BaseError, match="只发给允许远程接入"):
        owner.token_issue("ada", "再发")
    assert "没有找到" in owner.search("暗号停用成员正文")
    assert "没有找到" in as_agent("claude").search("暗号停用成员正文")
    assert "艾达" not in as_agent("claude").company()
    assert "ada" not in {c["key"] for c in member_cards(make_viewer(docs, "claude"), docs)}
    assert "ada" not in {c["key"] for c in member_cards(make_viewer(docs, OWNER, allow_owner=True), docs)}
    assert client.get("/api/search", params={"q": "暗号停用成员正文"}).json()["hits"] == []
    row = next(r for r in client.get("/api/activity").json()["rows"] if r["type"] == "进展")
    assert row["who"]["name"] == "艾达"
    decided = next(i for i in client.get("/api/decisions").json()["items"] if i["title"] == "艾达的决定")
    assert decided["recorded_by"] == "艾达"
    assert actor("ada", make_viewer(docs, "claude"), docs)["name"] == "艾达"

    _person(owner, "ext-bei", "北北", kind="human", remote_enabled=True, can_write=False)
    feishu.remember({"open_id": "ou_ada", "name": "艾达"})
    owner.feishu_bind("ou_ada", "ext-bei")
    assert "feishu_open_id: ou_ada" in (repo / "people" / "ext-bei.md").read_text(encoding="utf-8")

    (repo / "people" / "ada2.md").write_text(
        "---\nid: ada2\ntype: person\ntitle: 另一个艾达\nagent_key: ada\nkind: human\n"
        "remote_enabled: false\naccess_mode: assigned\ncan_write: false\nsensitivity: internal\n---\n",
        encoding="utf-8", newline="\n")
    docs = Store(repo).load()
    ada_doc = next(d for d in docs if d.rel == "people/ada.md")
    ada2 = next(d for d in docs if d.rel == "people/ada2.md")
    assert ada_doc.problem is None and ada2.problem and "重了" in ada2.problem
    assert member_keys(docs)["ada"].rel == "people/ada.md"
    with pytest.raises(BaseError, match="已经有"):
        _person(owner, "ada", "再一个", kind="human")
    (repo / "people" / "ada2.md").unlink()

    owner.restore_member("ada")
    restored = (repo / "people" / "ada.md").read_text(encoding="utf-8")
    assert "archived:" not in restored and "remote_enabled: false" in restored
    assert git(repo, "log", "-1", "--format=%s") == "恢复成员「艾达」"
    assert tokens.lookup(secret_ada) is None
    assert "阿尔法" in as_agent("ada").get_project("alpha")
    assert "archived: true" in (repo / "people" / "ada-bot.md").read_text(encoding="utf-8")
    assert "有效记录" in as_agent("ada-bot").whoami()
    with pytest.raises(BaseError, match="只发给允许远程接入"):
        owner.token_issue("ada", "恢复后也不发")
    with pytest.raises(BaseError, match=f"不能停用 {OWNER_NAME}"):
        owner.archive_member(OWNER)


def test_web_archive_restore_and_owner_refused(client, owner, repo):
    _person(owner, "ada", "艾达", kind="human", can_write=True, remote_enabled=True, projects=["alpha"])
    before = (repo / "people" / f"{OWNER}.md").read_bytes()
    refused = client.post(f"/api/members/{OWNER}/archive", json={}, headers=POST_HEADERS)
    assert refused.status_code == 400 and "不能停用" in refused.json()["error"]
    assert (repo / "people" / f"{OWNER}.md").read_bytes() == before
    archived = client.post("/api/members/ada/archive", json={}, headers=POST_HEADERS)
    assert archived.status_code == 200
    assert "archived: true" in (repo / "people" / "ada.md").read_text(encoding="utf-8")
    assert git(repo, "log", "-1", "--format=%an|%s") == f"{OWNER}|停用成员「艾达」"
    restored = client.post("/api/members/ada/restore", json={}, headers=POST_HEADERS)
    assert restored.status_code == 200
    assert "archived:" not in (repo / "people" / "ada.md").read_text(encoding="utf-8")


def test_remote_colleague_archive_and_create_are_closed(ext, repo):
    origin_client, headers = _remote(repo)
    try:
        before = git(repo, "rev-parse", "HEAD")
        for path, body in (("/api/members/ext-li/archive", {}), ("/api/members/ext-li/restore", {}),
                           ("/api/projects", {"id": "nope", "title": "不该建成", "goal": "不该建成"})):
            response = origin_client.post(path, json=body, headers=headers)
            assert response.status_code == 403 and response.json()["error"] == "这个功能远程还没开放", path
        assert git(repo, "rev-parse", "HEAD") == before
    finally:
        origin_client.close()
    with pytest.raises(BaseError, match=f"不能新建项目；请 {OWNER_NAME} 在网页上建"):
        ext("ext-zhang").create_project("nope", "不该建成", "不该建成")
    with pytest.raises(BaseError, match=f"不能新建项目；请 {OWNER_NAME} 在网页上建"):
        CompanyBase(repo, "ext-zhang", require_remote=True).create_project("nope", "不该建成", "不该建成")
    with pytest.raises(BaseError, match=f"不能新建项目；请 {OWNER_NAME} 在网页上建"):
        ext("reader").create_project("nope", "不该建成", "不该建成")


def test_remote_full_agent_can_create_project(repo):
    """远程接入的全读能写伙伴（如网页版 ChatGPT）也能新建项目。"""
    member = repo / "people" / "claude.md"
    member.write_text(member.read_text(encoding="utf-8").replace("remote_enabled: false", "remote_enabled: true"),
                      encoding="utf-8")
    git(repo, "commit", "-qam", "claude 可远程接入")
    message = CompanyBase(repo, "claude", require_remote=True).create_project(
        "remote-made", "远程建的", "远程全读伙伴新建", signed_as="claude")
    assert "remote-made" in message
    assert (repo / "projects" / "remote-made" / "project.md").is_file()


def test_colleague_directory_skips_archived(ext, repo):
    owner = CompanyBase(repo, OWNER, now=lambda: FIXED_NOW, allow_owner=True)
    assert "李四" in ext("ext-zhang").company()
    owner.archive_member("ext-li")
    docs = Store(repo).load()
    assert "李四" not in ext("ext-zhang").company()
    assert "ext-li" not in {c["key"] for c in member_cards(make_viewer(docs, "ext-zhang"), docs)}
    assert "没有找到" in ext("ext-zhang").search("李四（合作方）")
    assert next(d for d in docs if d.rel == "people/ext-li-bot.md").meta["archived"] is True


def _links(repo):
    path = repo / "projects" / "alpha" / "links.md"
    path.write_text("---\nid: alpha-links\ntype: project-links\ntitle: 阿尔法链接\n"
                    "sensitivity: internal\n---\n\n# 阿尔法链接\n\nhttps://example.test/alpha\n",
                    encoding="utf-8", newline="\n")


def test_project_lists_only_visible_apps(client, ext, repo):
    _links(repo)
    claude = ext("claude")
    claude.register_app({**APP, "id": "alpha-board", "title": "阿尔法台", "one_line": "看进度",
                         "code_path": "C:\\x\\board"})
    claude.register_app({**APP, "id": "alpha-old", "title": "旧看板", "one_line": "已经不用",
                         "listed": False, "code_path": "C:\\x\\old"})
    claude.register_app({**APP, "id": "beta-only", "title": "贝塔工具", "one_line": "贝塔用", "project": "beta"})
    (repo / "apps" / "alpha-lock.md").write_text(
        "---\nid: alpha-lock\ntype: app\ntitle: 暗号锁应用\none_line: 只有 Codex\nkind: 网页\nproject: alpha\n"
        "listed: true\nsensitivity: restricted\nvisible_to: [codex]\ncode_path: C:\\x\\lock\n---\n",
        encoding="utf-8", newline="\n")

    apps = client.get("/api/project/alpha").json()["apps"]
    by_id = {a["id"]: a for a in apps}
    assert by_id["alpha-board"]["listed"] is True and by_id["alpha-old"]["listed"] is False
    assert "beta-only" not in by_id and by_id["alpha-lock"]["title"] == "暗号锁应用"
    blob = json.dumps(apps, ensure_ascii=False)
    assert "code_path" not in blob and "C:\\" not in blob and "command" not in blob
    assert set(by_id["alpha-board"]) == {"id", "title", "one_line", "kind", "icon", "listed"}
    assert [a["id"] for a in apps if not a["listed"]] == ["alpha-old"]
    assert client.get("/api/project/secret").json()["apps"] == []

    local = claude.get_project("alpha")
    assert local.index("## 最近进展") < local.index("## 应用") < local.index("## 相关链接")
    assert "阿尔法台" in local and "`alpha-board`" in local and "已下架" in local and "旧看板" in local
    assert "## 应用" not in claude.get_project("secret")

    remote = CompanyBase(repo, "ext-zhang", require_remote=True)
    text = remote.get_project("alpha")
    assert "阿尔法台" in text and "`alpha-board`" in text
    assert "旧看板" not in text and "贝塔工具" not in text and "暗号锁应用" not in text
    assert "C:\\" not in text and "code_path" not in text and "已下架" not in text
    assert text.index("## 应用") < text.index("## 相关链接")

    origin_client, headers = _remote(repo)
    try:
        remote_apps = origin_client.get("/api/project/alpha").json()["apps"]
        remote_ids = {a["id"] for a in remote_apps}
        assert "alpha-board" in remote_ids and "alpha-old" not in remote_ids and "alpha-lock" not in remote_ids
        assert "C:\\" not in json.dumps(remote_apps, ensure_ascii=False)
        assert "code_path" not in origin_client.get("/api/project/alpha").text
    finally:
        origin_client.close()
    del headers


def test_create_project_from_web_agent_and_cli(repo, owner, as_agent, client):
    response = client.post("/api/projects", json={
        "id": "quarter-review", "title": "季度复盘", "goal": "看清这一季", "sensitivity": "restricted",
    }, headers=POST_HEADERS)
    assert response.status_code == 200 and response.json()["id"] == "quarter-review"
    doc = next(d for d in Store(repo).load() if d.id == "quarter-review")
    assert doc.problem is None and doc.meta["roles"] == [] and doc.meta["visible_to"] == []
    assert doc.meta["state"] == "构想中" and doc.meta["sensitivity"] == "restricted"
    for heading in ("## 目标", "## 现状", "## 下一步", "## 分工", "## 入口"):
        assert heading in doc.body
    assert not (repo / "projects" / "quarter-review" / "status.md").exists()
    assert git(repo, "log", "-1", "--format=%an|%s") == f"{OWNER}|项目：新建「季度复盘」"
    assert "网页界面" in git(repo, "log", "-1", "--format=%b")
    from onestead.store import now_taipei
    today = now_taipei().date().isoformat()
    assert doc.meta["updated"].isoformat() == today and doc.meta["summary_as_of"].isoformat() == today

    public = client.post("/api/projects", json={
        "id": "pub-web", "title": "公开项目", "goal": "给外面看", "sensitivity": "public", "state": "进行中",
    }, headers=POST_HEADERS)
    assert public.status_code == 200
    assert next(d for d in Store(repo).load() if d.id == "pub-web").meta["sensitivity"] == "public"
    duplicate = client.post("/api/projects", json={"id": "alpha", "title": "重", "goal": "重"}, headers=POST_HEADERS)
    assert duplicate.status_code == 400 and "已经有" in duplicate.json()["error"]
    bad = client.post("/api/projects", json={"id": "Bad", "title": "坏", "goal": "坏"}, headers=POST_HEADERS)
    assert bad.status_code == 400 and "id 用小写" in bad.json()["error"]

    message = as_agent("claude").create_project("brand-kit", "品牌包", "统一说法", signed_as="claude")
    assert "已新建项目 `brand-kit`" in message and "handover_role" in message
    made = next(d for d in Store(repo).load() if d.rel == "projects/brand-kit/project.md")
    assert made.problem is None and made.meta["summary_as_of"].isoformat() == "2026-10-10"
    assert made.meta["roles"] == [] and not (repo / "projects" / "brand-kit" / "status.md").exists()
    assert git(repo, "log", "-1", "--format=%an|%s") == "claude · claude|项目：新建「品牌包」"
    assert "create_project" in git(repo, "log", "-1", "--format=%b")
    with pytest.raises(BaseError, match="已经有项目 `brand-kit`"):
        as_agent("claude").create_project("brand-kit", "又一个", "目标")
    with pytest.raises(BaseError, match="id 用小写"):
        as_agent("claude").create_project("No", "名", "目标")
    with pytest.raises(BaseError, match="不能新建公开"):
        as_agent("claude").create_project("open-one", "公开", "目标", sensitivity="public")
    assert not (repo / "projects" / "open-one").exists()
    with pytest.raises(BaseError, match="像密钥"):
        as_agent("claude").create_project("secret-body", "名", "目标", body="token=abcdefghij")
    assert not (repo / "projects" / "secret-body").exists()
    with pytest.raises(BaseError, match="状态只能是"):
        as_agent("claude").create_project("ended", "名", "目标", state="已结束")
    assert not (repo / "projects" / "ended").exists()

    from onestead.cli import run
    # 照命令行说明的写法：身份、署名、数据路径跟在子命令后面。
    assert run(["project-create", "cli-proj", "--title", "命令行项目", "--goal", "从命令行开",
                "--data", str(repo), "--as", "claude", "--signed-as", "claude"]) == 0
    cli_doc = next(d for d in Store(repo).load() if d.id == "cli-proj")
    assert cli_doc.problem is None and cli_doc.meta["state"] == "构想中"
    assert git(repo, "log", "-1", "--format=%an|%s") == "claude · claude|项目：新建「命令行项目」"
