"""成员权限测试：内容授权、个人隔离、接入资格、成员管理和迁移。"""

from .support import OWNER, OWNER_NAME

import asyncio
import json

import pytest

from onestead import feishu, gitops, tokens
from onestead.base import CompanyBase
from onestead.migrate_stage4 import main, migrate
from onestead.server import TokenGate
from onestead.store import BaseError, Store, make_viewer, member_cards, split_front
from onestead.textutil import _dump_front

from .conftest import git


def put(repo, rel, **meta):
    path = repo / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(_dump_front(meta) + "\n测试正文。\n", encoding="utf-8")


def change(repo, key, **changes):
    path = repo / f"people/{key}.md"
    meta, body, _ = split_front(path.read_text(encoding="utf-8"))
    for field, value in changes.items():
        if value is None:
            meta.pop(field, None)
        else:
            meta[field] = value
    path.write_text(_dump_front(meta) + body, encoding="utf-8")


def viewer(repo, key, **kwargs):
    return make_viewer(Store(repo).load(), key, **kwargs)


def admin(repo):
    return CompanyBase(repo, OWNER, allow_owner=True)


def save(repo, key, **changes):
    base = admin(repo)
    source = base.doc_source(f"people/{key}.md")
    return base.save_doc(source["rel"], source["version"], changes, None)


# 同一内容矩阵分别走本机 assigned、远程 assigned 和 full。
@pytest.mark.parametrize("kind,level,granted,listed,expected", [
    ("handbook", "public", False, False, True),
    ("handbook", "internal", False, False, True),
    ("handbook", "restricted", False, False, False),
    ("handbook", "restricted", False, True, True),
    ("project", "public", False, False, True),
    ("project", "internal", False, False, False),
    ("project", "internal", True, False, True),
    ("project", "restricted", True, False, False),
    ("project", "restricted", True, True, True),
    ("asset", "public", False, False, True),
    ("asset", "internal", False, False, False),
    ("asset", "internal", True, False, True),
    ("asset", "restricted", True, False, False),
    ("asset", "restricted", True, True, True),
    ("skill", "public", True, True, False),
    ("skill", "internal", True, True, False),
    ("skill", "restricted", True, True, False),
])
def test_visibility_matrix(ext, repo, kind, level, granted, listed, expected):
    folder = {"handbook": "handbook", "project": "projects", "asset": "assets", "skill": "skills"}[kind]
    rel = f"{folder}/matrix/project.md" if kind == "project" else f"{folder}/matrix.md"
    put(repo, rel, id="matrix", type=kind, title="矩阵", sensitivity=level,
        visible_to=["ext-zhang"] if listed else [])
    change(repo, "ext-zhang", projects=["matrix"] if granted else [],
           assets=["matrix"] if granted else [], skills=["matrix"] if granted else [])
    if kind == "project":
        (repo / "projects/matrix/status.md").write_text("进展", encoding="utf-8")
        (repo / "projects/matrix/links.md").write_text("链接", encoding="utf-8")
    docs = Store(repo).load()
    tested = [d for d in docs if d.id == "matrix"]
    for remote in (False, True):
        v = make_viewer(docs, "ext-zhang", remote=remote)
        assert all(v.can_see(d) == expected for d in tested)
        assert {d.rel for d in v.visible(docs)} == {d.rel for d in viewer(repo, "ext-zhang").visible(docs)}
    assert all(viewer(repo, "codex").can_see(d) for d in tested)


@pytest.mark.parametrize("kind", ["decision", "app"])
@pytest.mark.parametrize("level", ["public", "internal", "restricted"])
@pytest.mark.parametrize("parent", ["alpha", "beta", "secret", "missing", None])
def test_children_require_parent_and_own_level(ext, repo, kind, level, parent):
    rel = f"{'decisions' if kind == 'decision' else 'apps'}/matrix.md"
    put(repo, rel, id="matrix", type=kind, title="子对象", sensitivity=level,
        project=parent, visible_to=["ext-zhang"])
    docs = Store(repo).load()
    doc = next(d for d in docs if d.rel == rel)
    expected = parent == "alpha" or (parent is None and level == "public")
    for key in ("ext-zhang", "ext-zhang-claude"):
        v = make_viewer(docs, key)
        assert v.can_see(doc) == expected
        assert (doc in v.visible(docs)) == expected
    assert viewer(repo, "codex").can_see(doc)


@pytest.mark.parametrize("field,value", [
    ("remote_enabled", "false"), ("remote_enabled", 1), ("remote_enabled", []),
    ("access_mode", True), ("access_mode", "FULL"), ("access_mode", []),
    ("kind", "robot"), ("kind", 1), ("kind", []),
    ("can_write", "true"), ("projects", "alpha"), ("skills", [1]),
    ("remote_enabled", None), ("access_mode", None), ("kind", None),
    ("projects", None), ("access_mode", {"full": True}),
])
def test_bad_member_field_disables_record(ext, repo, field, value):
    path = repo / "people/ext-zhang.md"
    meta, body, _ = split_front(path.read_text(encoding="utf-8"))
    meta[field] = value
    path.write_text(_dump_front(meta) + body, encoding="utf-8")
    doc = next(d for d in Store(repo).load() if d.rel == "people/ext-zhang.md")
    assert doc.problem and not viewer(repo, "ext-zhang").known
    assert not viewer(repo, "ext-zhang").can_remote
    assert not viewer(repo, "ext-zhang-claude").can_write


def test_absent_fields_are_conservative_and_scope_is_ignored(ext, repo):
    change(repo, "ext-zhang", access_mode=None, remote_enabled=None, scope="anything")
    v = viewer(repo, "ext-zhang")
    assert v.valid and v.assigned and not v.can_remote
    assert v.projects == frozenset({"alpha", "secret"})
    assert "贝塔" not in ext("ext-zhang").list_projects()
    change(repo, "ext-zhang", scope={"not": "a policy"})
    assert viewer(repo, "ext-zhang").valid


@pytest.mark.parametrize("case", ["missing", "number", "self", "chain", "cycle", "human-owner", "bad-human", "duplicate"])
def test_invalid_owner_sees_and_writes_nothing(ext, repo, case):
    bot = "ext-zhang-claude"
    if case == "missing":
        change(repo, bot, owner=None)
    elif case == "number":
        change(repo, bot, owner=123)
    elif case == "self":
        change(repo, bot, owner=bot)
    elif case == "chain":
        change(repo, bot, owner="ext-li-bot")
    elif case == "cycle":
        change(repo, bot, owner="ext-li-bot")
        change(repo, "ext-li-bot", owner=bot)
    elif case == "human-owner":
        change(repo, "ext-zhang", owner=OWNER)
    elif case == "bad-human":
        change(repo, "ext-zhang", access_mode="wrong")
    else:
        put(repo, "people/duplicate.md", id="duplicate", type="person", kind="human",
            agent_key="ext-zhang", sensitivity="internal")
    v = viewer(repo, bot)
    assert not v.valid and not v.can_write and not v.can_remote
    assert v.visible(Store(repo).load()) == []
    with pytest.raises(BaseError):
        ext(bot).update_project_status("alpha", "不应写入")


def test_owner_caps_mode_grants_acl_and_write(ext, repo):
    change(repo, "ext-zhang", projects=["alpha"], assets=["deal"], skills=["one", "two"])
    change(repo, "ext-zhang-claude", access_mode="full", projects=["alpha", "beta"], skills=["two", "three"])
    v = viewer(repo, "ext-zhang-claude")
    assert v.assigned and v.projects == frozenset({"alpha"})
    assert v.assets == frozenset({"deal"}) and v.skills == frozenset({"two"})
    assert v.acl == "ext-zhang" and v.can_write
    change(repo, "ext-zhang-claude", assets=[], skills=[])
    assert not viewer(repo, "ext-zhang-claude").assets and not viewer(repo, "ext-zhang-claude").skills
    change(repo, "ext-zhang", can_write=False)
    assert not viewer(repo, "ext-zhang-claude").can_write
    change(repo, "ext-zhang", can_write=True)
    change(repo, "ext-zhang-claude", can_write=False)
    assert not viewer(repo, "ext-zhang-claude").can_write


@pytest.mark.parametrize("owner", [OWNER, "ext-zhang"])
@pytest.mark.parametrize("mode", ["assigned", "full"])
def test_full_owner_uses_agent_own_mode(ext, repo, owner, mode):
    change(repo, owner, access_mode="full")
    change(repo, "ext-zhang-claude", owner=owner, access_mode=mode, projects=["beta"])
    v = viewer(repo, "ext-zhang-claude")
    assert v.access_mode == mode and v.projects == frozenset({"beta"})
    assert v.acl == owner and v.workspace_owner == owner


@pytest.mark.parametrize("kind,rel,owner", [
    ("todo", "todos/legacy.md", None), ("todo", f"todos/{OWNER}.md", OWNER),
    ("todo", "todos/zhang.md", "ext-zhang"),
    ("home", f"home/{OWNER}.md", None), ("home", f"home/{OWNER}-draft.md", None),
])
def test_personal_owner_precedes_full_public_and_project(ext, repo, kind, rel, owner):
    meta = dict(id="personal", type=kind, title="个人内容", sensitivity="public", project="alpha")
    if owner is not None:
        meta["owner"] = owner
    put(repo, rel, **meta)
    for mode in ("assigned", "full"):
        change(repo, "ext-zhang", access_mode=mode)
        change(repo, "ext-zhang-claude", access_mode=mode)
        docs = Store(repo).load()
        doc = next(d for d in docs if d.rel == rel)
        for key in (OWNER, "codex", "ext-zhang", "ext-zhang-claude", "ext-li", "nobody"):
            v = make_viewer(docs, key, allow_owner=key == OWNER)
            permitted = key in (("ext-zhang", "ext-zhang-claude") if owner == "ext-zhang" else (OWNER, "codex"))
            assert v.can_see(doc) == permitted


def test_member_cards_do_not_expose_source_or_authorization(ext, repo):
    change(repo, "ext-li", role="同事", connect="接法私有", feishu_open_id="测试飞书身份")
    cards = member_cards(viewer(repo, "ext-zhang"), Store(repo).load())
    assert cards and all(set(c) == {"key", "title", "kind", "role"} for c in cards)
    assert "接法私有" not in json.dumps(cards, ensure_ascii=False)
    assert "李四" in ext("ext-zhang").company()
    change(repo, "ext-li", sensitivity="restricted", visible_to=["ext-zhang-claude"])
    assert "ext-li" not in {c["key"] for c in member_cards(viewer(repo, "ext-zhang-claude"), Store(repo).load())}
    change(repo, "ext-li", visible_to=["ext-zhang"])
    assert "ext-li" in {c["key"] for c in member_cards(viewer(repo, "ext-zhang-claude"), Store(repo).load())}
    docs = Store(repo).load()
    raw = {d.meta["agent_key"] for d in viewer(repo, "ext-zhang-claude").visible(docs) if d.kind == "person"}
    assert raw == {"ext-zhang", "ext-zhang-claude"}


def test_disable_remote_preserves_policy_and_revokes_family(ext, repo):
    base = admin(repo)
    issued = [base.token_issue(key, "测试")["token"] for key in ("ext-zhang", "ext-zhang-claude")]
    session = tokens.issue("ext-zhang", "飞书测试", kind="feishu")[0]
    before = viewer(repo, "ext-zhang").visible(Store(repo).load())
    save(repo, "ext-zhang", remote_enabled=False)
    after = viewer(repo, "ext-zhang").visible(Store(repo).load())
    assert {d.rel for d in before} == {d.rel for d in after}
    assert viewer(repo, "ext-zhang").assigned and viewer(repo, "ext-zhang").projects == frozenset({"alpha", "secret"})
    assert all(tokens.lookup_record(t) is None for t in issued + [session])
    assert not viewer(repo, "ext-zhang-claude").can_remote
    assert "阿尔法" in ext("ext-zhang-claude").list_projects()
    with pytest.raises(BaseError, match="用不了"):
        CompanyBase(repo, "ext-zhang-claude", require_remote=True).list_projects()
    save(repo, "ext-zhang", remote_enabled=True)
    assert all(tokens.lookup_record(t) is None for t in issued + [session])


@pytest.mark.parametrize("mode", ["assigned", "full"])
def test_remote_gate_independent_of_content_mode(ext, repo, mode):
    change(repo, "ext-zhang", access_mode=mode)
    token = admin(repo).token_issue("ext-zhang", "测试")["token"]
    gate = TokenGate(str(repo))
    assert asyncio.run(gate.verify_token(token)) is not None
    local = ext("ext-zhang")._ctx()[2]
    remote = CompanyBase(repo, "ext-zhang", require_remote=True)._ctx()[2]
    assert {d.rel for d in local} == {d.rel for d in remote}
    change(repo, "ext-zhang", remote_enabled=False)
    assert asyncio.run(gate.verify_token(token)) is None
    with pytest.raises(BaseError, match="用不了"):
        CompanyBase(repo, "ext-zhang", require_remote=True).update_project_status("alpha", "被关闭")


def test_remote_full_skill_uses_safe_package_output(ext, repo, skill_home):
    root = skill_home / ".codex/skills/remote-tool"
    root.mkdir(parents=True)
    (root / "SKILL.md").write_text("---\nname: 共享工具\ndescription: 测试说明\n---\n\n安全步骤。\n", encoding="utf-8")
    put(repo, "skills/remote-tool.md", id="remote-tool", type="skill", title="共享工具", sensitivity="internal", home=str(root))
    change(repo, "ext-zhang", access_mode="full")
    base = CompanyBase(repo, "ext-zhang", require_remote=True)
    text = base.get_skill("remote-tool")
    assert "安全步骤" in text and str(root) not in text and "装的话" not in text
    assert base.download_skill("remote-tool").startswith(b"PK")
    change(repo, "ext-zhang", access_mode="assigned")
    assert "remote-tool" not in base.list_skills()


def test_owner_transfer_revokes_before_commit(ext, repo, monkeypatch):
    token = admin(repo).token_issue("ext-zhang-claude", "旧主人")["token"]
    original = gitops.write_and_commit

    def commit(*args, **kwargs):
        assert tokens.lookup_record(token) is None
        return original(*args, **kwargs)

    monkeypatch.setattr(gitops, "write_and_commit", commit)
    save(repo, "ext-zhang-claude", owner="ext-li")
    assert viewer(repo, "ext-zhang-claude").workspace_owner == "ext-li"
    assert not viewer(repo, "ext-zhang-claude").can_write


def test_new_member_defaults_and_strict_management(ext, repo):
    base = admin(repo)
    base.create_doc("person", {"id": "new-human", "title": "新人", "kind": "human"}, "")
    v = viewer(repo, "new-human")
    assert v.valid and v.assigned and not v.can_remote
    for changes in ({"remote_enabled": "false"}, {"access_mode": "typo"}, {"can_write": 1}, {"owner": OWNER}):
        with pytest.raises(BaseError):
            save(repo, "new-human", **changes)
    with pytest.raises(BaseError):
        base.create_doc("person", {"id": "orphan-new", "title": "无主", "kind": "agent"}, "")
    base.create_doc("person", {"id": "new-agent", "title": "伙伴", "kind": "agent", "owner": OWNER}, "")
    assert viewer(repo, "new-agent").workspace_owner == OWNER
    fields = {f["key"]: f for f in base.doc_source("people/new-agent.md")["fields"]}
    assert "scope" not in fields and OWNER in fields["owner"]["options"]
    assert fields["projects"]["show_if"] == "access_mode"


def test_feishu_binding_requires_remote_human(ext, repo, monkeypatch):
    monkeypatch.setattr(feishu, "contains", lambda _: True)
    for key in ("ext-zhang-claude", "reader", OWNER):
        with pytest.raises(BaseError, match="human"):
            admin(repo).feishu_bind("测试待登记", key)


def test_assigned_writes_closed_even_on_local_transport(ext, repo):
    base = ext("ext-zhang")
    calls = [lambda: base.register_skill({"id": "denied", "one_line": "不给写"}),
             lambda: base.register_app({"id": "denied", "title": "不给写", "kind": "网页", "one_line": "说明"}),
             lambda: base.handover_role("alpha", "采集", "张三", "不给换")]
    for call in calls:
        with pytest.raises(BaseError, match="按授权的成员只能"):
            call()
    base.add_todos([{"title": "只写本人的待办"}])
    assert "只写本人的待办" not in ext("codex").list_todos()
    with pytest.raises(BaseError, match="不能选择模块"):
        base.propose_home([{"kind": "attention"}], "不给审批模块")
    with pytest.raises(BaseError, match="不能指定 visible_to"):
        base.record_decision("拒绝", "内容", "原因", "张三", project_id="alpha", visible_to=[])


def test_full_other_owner_cannot_write_owner_workspace(ext, repo):
    change(repo, "ext-zhang", access_mode="full")
    change(repo, "ext-zhang-claude", access_mode="full")
    base = ext("ext-zhang-claude")
    assert "已记录到" in base.update_project_status("beta", "全权限写公司内容")
    base.add_todos([{"title": "自己工作台的待办"}])
    assert "自己工作台的待办" in base.list_todos()
    assert "自己工作台的待办" not in ext("codex").list_todos()
    with pytest.raises(BaseError, match="不能选择模块"):
        base.propose_home([{"kind": "attention"}], f"不许改 {OWNER_NAME} 的")


def test_agent_full_claim_cannot_forge_handover(ext, repo):
    from onestead.web.views import pending_handovers

    change(repo, "ext-zhang-claude", access_mode="full")
    ext("ext-zhang-claude").update_project_status("alpha",
        f"申请接任：「项目 Lead」由「老 Lead」换成「假候选人」，待 {OWNER_NAME} 批准。")
    docs = Store(repo).load()
    assert pending_handovers(docs, {d.id: d for d in docs if d.kind == "project"}) == []


def test_migration_list_apply_author_and_idempotence(ext, repo, capsys):
    change(repo, "ext-zhang", remote_enabled=None, access_mode=None, scope="external")
    change(repo, "codex", remote_enabled=None, access_mode=None, owner=None)
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "旧成员格式")
    head = git(repo, "rev-parse", "HEAD")
    before = (repo / "people/ext-zhang.md").read_bytes()
    assert main(["--root", str(repo)]) == 0
    dry = json.loads(capsys.readouterr().out)
    assert dry["commit"] is None
    assert {r["rel"] for r in dry["changes"]} == {"people/ext-zhang.md", "people/codex.md"}
    assert (repo / "people/ext-zhang.md").read_bytes() == before
    assert git(repo, "rev-parse", "HEAD") == head
    with pytest.raises(BaseError, match="author"):
        migrate(repo, apply=True)
    assert main(["--root", str(repo), "--apply", "--author", "claude", "--signed-as", "Onestead Lead"]) == 0
    applied = json.loads(capsys.readouterr().out)
    assert applied["commit"] and applied["changes"] == dry["changes"]
    assert git(repo, "rev-list", "--count", f"{head}..HEAD") == "1"
    assert git(repo, "log", "-1", "--format=%an") == "claude · Onestead Lead"
    human = viewer(repo, "ext-zhang")
    assert human.assigned and human.can_remote and human.projects == frozenset({"alpha", "secret"})
    bot = viewer(repo, "codex")
    assert not bot.assigned and bot.workspace_owner == OWNER and not bot.can_remote
    assert migrate(repo, apply=True, author="claude")["commit"] is None
    assert git(repo, "status", "--porcelain") == ""
