"""个人工作台隔离、旧主人数据兼容、本人写入与首页草稿。"""

from .support import OWNER

import json
import threading
from concurrent.futures import ThreadPoolExecutor

import pytest

from onestead import home
from onestead.store import BaseError, Store, make_viewer, personal_owner
from onestead.web import views

from .conftest import FIXED_NOW, git
from .support import change, put


@pytest.fixture
def workspace(personal_workspace):
    """这个文件和口令测试要的是个人工作台工厂，不是同事网页客户端。"""
    return personal_workspace


def own_docs(base, kind):
    return [d for d in base._ctx()[2] if d.kind == kind]


def draft_version(base):
    _, draft = home.current(base._ctx()[2], base._ctx()[0])
    return draft["version"]


@pytest.mark.parametrize("mode", ["assigned", "full"])
@pytest.mark.parametrize("actor", ["a", "b", "a-bot", "b-bot", OWNER, "codex", "reader"])
def test_personal_boundaries_across_lists_search_doc_and_home(workspace, repo, actor, mode):
    for key in ("a", "b", "a-bot", "b-bot"):
        change(repo, f"people/{key}.md", access_mode=mode)
    for key in ("a", "b"):
        base = workspace(key)
        base.add_todos([{"title": "同名待办", "notes": f"{key}私密备注", "project": "alpha"}])
        todo = own_docs(base, "todo")[0]
        change(repo, todo.rel, sensitivity="public")
        base.propose_home([{"kind": "recent"}], f"{key}已采用首页")
        base.adopt_home(draft_version(base))
        base.propose_home([{"kind": "projects"}], f"{key}私密首页草稿")

    base = workspace(actor)
    viewer, docs, visible = base._ctx()
    owner = viewer.workspace_owner
    todos = own_docs(base, "todo")
    homes = own_docs(base, "home")
    assert {personal_owner(d) for d in todos + homes} == ({owner} if owner in ("a", "b") else set())
    assert len(todos) == (1 if owner in ("a", "b") else 0)
    for key in ("a", "b"):
        permitted = key == owner
        assert (f"{key}私密备注" in base.search("私密备注")) == permitted
        assert (f"{key}私密首页草稿" in base.search("私密首页草稿")) == permitted
        assert bool(views.search(visible, f"{key}私密备注")) == permitted
        for d in docs:
            if d.kind in ("todo", "home") and personal_owner(d) == key:
                assert viewer.can_see(d) == permitted
                assert (d.rel in {x.rel for x in visible}) == permitted
                if permitted:
                    assert views.doc_view(next(x for x in visible if x.rel == d.rel))
        other_id = next(d.id for d in docs if d.kind == "todo" and personal_owner(d) == key)
        if not permitted:
            with pytest.raises(BaseError, match="找不到待办"):
                base.update_todo(other_id, {"status": "done"})
    layout, draft = home.current(visible, viewer)
    assert layout
    assert (draft is not None) == (owner in ("a", "b"))
    if draft:
        assert draft["note"] == f"{owner}私密首页草稿"
    assert ("同名待办" in base.list_todos()) == (owner in ("a", "b"))


def test_local_owner_doc_and_home_do_not_expose_colleagues(workspace, client):  # noqa: F811
    workspace("a").add_todos([{"title": "同事独有标题", "notes": "同事私密备注"}])
    todo = own_docs(workspace("a"), "todo")[0]
    workspace("a").propose_home([{"kind": "recent"}], "同事独有草稿")
    for rel in (todo.rel, "home/a-draft.md"):
        assert client.get("/api/doc", params={"rel": rel}).status_code == 404
    assert client.get("/api/search", params={"q": "同事独有"}).json()["hits"] == []
    assert "同事独有" not in client.get("/api/todos").text
    assert "同事独有" not in client.get("/api/home").text


@pytest.mark.parametrize("owner", [None, "", "a-bot", "missing", 5, [OWNER], " a", "../a"])
def test_bad_owner_has_no_owner_fallback(workspace, repo, owner):
    put(repo, "todos/bad.md", dict(id="bad", type="todo", title="坏主人的秘密", owner=owner,
                                  sensitivity="public", project="alpha"))
    for key in (OWNER, "codex", "a", "a-bot", "b", "b-bot"):
        assert "坏主人的秘密" not in workspace(key).list_todos("all")
        assert "没有找到" in workspace(key).search("坏主人的秘密")
    doc = next(d for d in Store(repo).load() if d.rel == "todos/bad.md")
    assert personal_owner(doc) is None


def test_legacy_owner_is_owner_and_first_change_fills_it(workspace, repo):
    put(repo, "todos/legacy.md", dict(id="legacy", type="todo", title="旧待办", created_by="a-bot",
                                     sensitivity="public"), "旧备注")
    before = (repo / "todos/legacy.md").read_text(encoding="utf-8")
    for key in ("a", "a-bot", "b", "b-bot"):
        assert "旧待办" not in workspace(key).list_todos()
    assert "旧待办" in workspace("codex").list_todos()
    assert (repo / "todos/legacy.md").read_text(encoding="utf-8") == before
    workspace("codex").update_todo("legacy", {"notes": "第一次改"})
    doc = own_docs(workspace(OWNER), "todo")[0]
    assert doc.meta["owner"] == OWNER and doc.meta["created_by"] == "a-bot"
    assert doc.meta["updated_by"] == "codex"


@pytest.mark.parametrize("actor", [OWNER, "a", "a-bot"])
def test_caller_cannot_supply_owner(workspace, actor):
    base = workspace(actor)
    with pytest.raises(BaseError, match="owner"):
        base.add_todos([{"title": "派单", "owner": "b"}])
    base.add_todos([{"title": "本人待办"}])
    with pytest.raises(BaseError, match="owner"):
        base.update_todo(own_docs(base, "todo")[0].id, {"owner": "b"})


def test_agent_author_and_owner_are_separate(workspace, repo):
    workspace("a-bot").add_todos([{"title": "代记"}], signed_as="执行者")
    doc = own_docs(workspace("a"), "todo")[0]
    assert doc.meta["owner"] == "a" and doc.meta["created_by"] == "a-bot · 执行者"
    assert git(repo, "log", "-1", "--format=%an") == "a-bot · 执行者"


@pytest.mark.parametrize("disabled", ["a", "a-bot"])
def test_agent_personal_writes_need_both_write_switches(workspace, repo, disabled):
    workspace("a").add_todos([{"title": "已有待办"}])
    tid = own_docs(workspace("a"), "todo")[0].id
    change(repo, f"people/{disabled}.md", can_write=False)
    base = workspace("a-bot")
    for call in (lambda: base.add_todos([{"title": "拒绝代记"}]),
                 lambda: base.update_todo(tid, {"status": "done"}),
                 lambda: base.propose_home([{"kind": "recent"}], "拒绝草稿")):
        with pytest.raises(BaseError, match="不能写入"):
            call()


def test_human_without_company_write_can_write_personal(workspace, repo):
    change(repo, "people/a.md", can_write=False)
    base = workspace("a")
    base.add_todos([{"title": "本人记事"}])
    tid = own_docs(base, "todo")[0].id
    base.update_todo(tid, {"status": "done"})
    base.propose_home([{"kind": "recent"}], "本人布局")
    base.adopt_home(draft_version(base))
    assert own_docs(base, "todo")[0].meta["status"] == "done"
    with pytest.raises(BaseError, match="不能写入"):
        base.update_project_status("alpha", "公司事实")


def test_repeat_inherits_owner_and_deduplicates_in_same_owner(workspace, repo):
    base = workspace("a-bot")
    base.add_todos([{"title": "每周检查", "repeat": "weekly", "due": "2026-10-10"}])
    first = own_docs(base, "todo")[0]
    put(repo, "todos/other-repeat.md", dict(id="other-repeat", type="todo", title="别人的下一次", owner="b",
                                          repeat_from=first.id, sensitivity="public"))
    before = git(repo, "rev-list", "--count", "HEAD")
    base.update_todo(first.id, {"status": "done"})
    assert int(git(repo, "rev-list", "--count", "HEAD")) == int(before) + 1
    assert len(git(repo, "diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD").splitlines()) == 2
    base.update_todo(first.id, {"status": "done"})
    base.update_todo(first.id, {"status": "open"})
    base.update_todo(first.id, {"status": "done"})
    assert len(own_docs(base, "todo")) == 2
    nxt = next(d for d in own_docs(base, "todo") if d.meta.get("repeat_from") == first.id)
    assert nxt.meta["owner"] == "a" and nxt.meta["created_by"] == "a-bot"


def test_concurrent_repeat_completion_produces_one_successor(workspace):
    base = workspace("a")
    base.add_todos([{"title": "每日检查", "repeat": "daily", "due": "2026-10-10"}])
    first = own_docs(base, "todo")[0]
    start = threading.Barrier(2)

    def complete(actor):
        start.wait(timeout=10)
        return workspace(actor).update_todo(first.id, {"status": "done"})

    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(complete, ["a", "a-bot"]))
    assert len(results) == 2
    assert len([d for d in own_docs(base, "todo") if d.meta.get("repeat_from") == first.id]) == 1


def test_project_revoked_masks_reference_but_retains_personal_notes(workspace, repo):
    base = workspace("a")
    base.add_todos([{"title": "继续跟进", "notes": "本人写下的备注", "project": "alpha"}])
    first = own_docs(base, "todo")[0]
    change(repo, "projects/alpha/project.md", sensitivity="internal")
    change(repo, "people/a.md", projects=[])
    viewer, _, visible = base._ctx()
    masked = own_docs(base, "todo")[0]
    assert "project" not in masked.meta and "alpha" not in masked.raw
    text = base.list_todos() + base.search("继续跟进") + json.dumps(views.doc_view(masked), ensure_ascii=False)
    assert "alpha" not in text and "阿尔法" not in text and "本人写下的备注" in text
    by_id = {d.id: d for d in visible if d.kind == "project"}
    rows = views.todo_list(visible, FIXED_NOW.date(), by_id, {})
    assert rows[0]["project"] is None
    assert not views.search(visible, "alpha")
    with pytest.raises(BaseError, match="没有 id"):
        base.add_todos([{"title": "新关联", "project": "alpha"}])
    with pytest.raises(BaseError, match="没有 id"):
        base.update_todo(first.id, {"project": "alpha"})
    base.update_todo(first.id, {"notes": "改了别的字段"})
    raw = next(d for d in Store(repo).load() if d.id == first.id and d.kind == "todo")
    assert raw.meta["project"] == "alpha" and "改了别的字段" in raw.body
    assert viewer.workspace_owner == "a"


def test_personal_home_paths_default_and_single_adoption_commit(workspace, repo):
    base = workspace("a")
    head = git(repo, "rev-parse", "HEAD")
    viewer, _, visible = base._ctx()
    layout, draft = home.current(visible, viewer)
    assert [m["kind"] for m in layout] == ["today_todos", "projects", "apps", "shared_skills", "recent"]
    assert draft is None and "shared_skills" in base.home_layout()
    assert git(repo, "rev-parse", "HEAD") == head and not (repo / "home").exists()
    workspace("b").propose_home([{"kind": "projects"}], "乙的草稿")
    workspace("a-bot").propose_home([{"kind": "recent"}], "甲的草稿")
    draft_doc = next(d for d in Store(repo).load() if d.rel == "home/a-draft.md")
    assert draft_doc.meta["owner"] == "a" and draft_doc.id == "a-draft" and draft_doc.meta["state"] == "draft"
    with pytest.raises(BaseError, match="本人操作"):
        workspace("a-bot").adopt_home()
    with pytest.raises(BaseError, match="本人操作"):
        workspace("a-bot").discard_home()
    before = int(git(repo, "rev-list", "--count", "HEAD"))
    base.adopt_home(draft_version(base))
    assert git(repo, "log", "-1", "--format=%an") == "a"
    assert int(git(repo, "rev-list", "--count", "HEAD")) == before + 1
    assert set(git(repo, "diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD").splitlines()) == {
        "home/a.md", "home/a-draft.md"}
    active = own_docs(base, "home")[0]
    assert active.id == "a" and active.meta["owner"] == "a" and active.meta["state"] == "active"
    assert (repo / "home/b-draft.md").exists()
    with pytest.raises(BaseError, match="没有首页草稿"):
        base.discard_home()
    base.propose_home([{"kind": "projects"}], "新草稿")
    base.discard_home(draft_version(base))
    assert [m["kind"] for m in home.current(base._ctx()[2], base._ctx()[0])[0]] == ["recent"]


def test_owner_legacy_layout_and_default_unchanged(workspace, repo):
    base = workspace(OWNER)
    assert home.current(base._ctx()[2], base._ctx()[0])[0] == home.normalize(home.DEFAULT_LAYOUT)
    put(repo, f"home/{OWNER}.md", dict(id=OWNER, type="home", sensitivity="internal", modules=[{"kind": "attention"}]))
    put(repo, f"home/{OWNER}-draft.md", dict(id=f"{OWNER}-draft", type="home", sensitivity="internal",
                                        proposed_by="codex", modules=[{"kind": "recent"}]))
    git(repo, "add", f"home/{OWNER}.md", f"home/{OWNER}-draft.md")
    git(repo, "commit", "-qm", "测试旧首页文件")
    assert home.current(base._ctx()[2], base._ctx()[0])[0] == home.normalize([{"kind": "attention"}])
    base.adopt_home(draft_version(base))
    active = own_docs(base, "home")[0]
    assert active.rel == f"home/{OWNER}.md" and active.meta["owner"] == OWNER and active.meta["state"] == "active"


@pytest.mark.parametrize("kind", ["attention", "skills"])
@pytest.mark.parametrize("actor", ["a", "a-bot"])
def test_colleague_forbidden_modules_rejected_and_old_modules_skipped(workspace, repo, kind, actor):
    base = workspace(actor)
    with pytest.raises(BaseError, match="不能选择模块"):
        base.propose_home([{"kind": kind}], "无能力的模块")
    put(repo, "home/a.md", dict(id="a", type="home", owner="a", state="active", sensitivity="internal",
                               modules=[{"kind": kind}, {"kind": "today_todos"}]))
    layout, _ = home.current(base._ctx()[2], base._ctx()[0])
    assert [m["kind"] for m in layout] == ["today_todos"]
    assert f"`{kind}`" not in home.describe(base._ctx()[0])


@pytest.mark.parametrize("key", ["a-draft", "../a", "a/b", "a\\b"])
def test_unsafe_or_draft_suffix_member_key_rejected(workspace, repo, key):
    put(repo, "people/invalid.md", dict(id="invalid", type="person", title="坏身份", agent_key=key,
                                      kind="human", sensitivity="internal", can_write=True))
    docs = Store(repo).load()
    doc = next(d for d in docs if d.rel == "people/invalid.md")
    assert doc.problem and not make_viewer(docs, key).valid
    with pytest.raises(BaseError):
        home.paths(key)
    if key == "a-draft":
        with pytest.raises(BaseError, match="-draft"):
            workspace(OWNER).create_doc("person", {"id": key, "title": "冲突身份"}, "")


@pytest.mark.parametrize("fields", [{"owner": "b"}, {"owner": None}, {"state": "active"}, {"id": "b-draft"}])
def test_home_path_owner_and_state_must_match(workspace, repo, fields):
    put(repo, "home/a-draft.md", dict(id="a-draft", type="home", owner="a", state="draft", sensitivity="public",
                                     modules=[{"kind": "recent"}]) | fields)
    for actor in ("a", "b", "a-bot", OWNER):
        base = workspace(actor)
        assert "home/a-draft.md" not in {d.rel for d in base._ctx()[2]}


def test_home_app_ids_require_current_visibility(workspace, repo):
    put(repo, "apps/hidden.md", dict(id="hidden", type="app", title="未授权应用", project="secret", sensitivity="internal"))
    with pytest.raises(BaseError, match="看得到的应用"):
        workspace("a").propose_home([{"kind": "apps", "options": {"ids": ["hidden"]}}], "越权应用")


def test_shared_skills_module_uses_authorized_packages(workspace, repo, skill_home):
    from onestead import skills

    for sid in ("granted", "public", "hidden", "broken"):
        folder = skill_home / ".codex/skills" / sid
        folder.mkdir(parents=True)
        if sid != "broken":
            (folder / "SKILL.md").write_text(f"---\nname: {sid}\ndescription: 测试共享\n---\n# 技能\n", encoding="utf-8")
        put(repo, f"skills/{sid}.md", dict(id=sid, type="skill", title=sid,
                                          sensitivity="public" if sid == "public" else "internal"))
    change(repo, "people/a.md", skills=["granted", "broken"])
    base = workspace("a")
    viewer, docs, _ = base._ctx()
    data, rows = base._skill_rows(docs, fresh=True)
    packages = skills.shared_packages(viewer, docs, data, rows)
    items = home.shared_skill_items(viewer, docs, data, rows)
    assert {r["id"] for r in items} == set(packages) == {"granted", "public"}
    assert all(set(r) == {"id", "name", "one_line", "version"} for r in items)
    assert str(skill_home) not in json.dumps(items) and "installs" not in json.dumps(items)
