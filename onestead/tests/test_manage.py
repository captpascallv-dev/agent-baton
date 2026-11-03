"""网页上的管理：主人改资料、项目、决定、成员，新建资料和成员，批或不批关键角色的接任申请。
agent 用不了这些；打开后被别人改过就不覆盖；限定项目的决定不能放宽。"""

from .support import OWNER, OWNER_NAME

import pytest
from starlette.testclient import TestClient

from onestead.base import CompanyBase
from onestead.store import BaseError
from onestead.web.app import build_app
from onestead.web.views import pending_handovers

from .conftest import FIXED_NOW, git
from .support import (
    MANAGE_BASE as BASE,
    MANAGE_PORT as PORT,
    MANAGE_POST_HEADERS as POST_HEADERS,
    MANAGE_TOKEN as TOKEN,
    make_external,
)


def read(repo, rel):
    return (repo / rel).read_text(encoding="utf-8")


@pytest.fixture
def owner(repo):
    return CompanyBase(repo, OWNER, now=lambda: FIXED_NOW, allow_owner=True)


def pending(base):
    _, _, visible = base._ctx()
    return pending_handovers(visible, {x.id: x for x in visible if x.kind == "project"})


def test_save_doc_changes_level_and_body_then_refuses_stale_version(repo, owner):
    s = owner.doc_source("assets/sample.md")
    assert s["sensitivity"] == "restricted" and s["visible_to"] == ["claude"]
    r = owner.save_doc("assets/sample.md", s["version"],
                        {"title": "样本", "sensitivity": "internal", "access_note": "别外传"}, "新的正文。")
    text = read(repo, "assets/sample.md")
    assert "sensitivity: internal" in text and "visible_to" not in text
    assert "access_note: 别外传" in text and "# 样本\n\n新的正文。\n" in text
    log = git(repo, "log", "-1", "--format=%an|%s")
    assert log.startswith(f"{OWNER}|修改：资料「限定样本」（改名为「样本」，敏感级别改成 internal）")
    assert r["commit"]
    with pytest.raises(BaseError, match="被别人改过"):
        owner.save_doc("assets/sample.md", s["version"], {"title": "再改"}, None)


def test_save_doc_changes_project_state_with_version(repo, owner):
    s = owner.doc_source("projects/alpha/project.md")
    field = next(f for f in s["fields"] if f["key"] == "state")
    target = next(x for x in field["options"] if x and x != field["value"])
    owner.save_doc(s["rel"], s["version"], {"state": target}, None)
    assert f"state: {target}" in read(repo, "projects/alpha/project.md")
    with pytest.raises(BaseError, match="被别人改过"):
        owner.save_doc(s["rel"], s["version"], {"state": field["value"]}, None)


def test_save_without_changes_makes_no_commit(repo, owner):
    s = owner.doc_source("handbook/company.md")
    before = git(repo, "rev-parse", "HEAD")
    owner.save_doc(s["rel"], s["version"], {"title": "公司概况"}, s["body"])
    # 只差 updated 日期也算改动；第二次同样内容就不提交
    s = owner.doc_source("handbook/company.md")
    mid = git(repo, "rev-parse", "HEAD")
    r = owner.save_doc(s["rel"], s["version"], {"title": "公司概况"}, s["body"])
    assert r["commit"] is None and git(repo, "rev-parse", "HEAD") == mid != before


def test_restricted_level_only_names_known_members(owner):
    s = owner.doc_source("handbook/company.md")
    with pytest.raises(BaseError, match="不在成员名单"):
        owner.save_doc(s["rel"], s["version"], {"sensitivity": "restricted", "visible_to": ["typo"]}, None)
    with pytest.raises(BaseError, match="不用勾自己"):
        owner.save_doc(s["rel"], s["version"], {"sensitivity": "restricted", "visible_to": [OWNER]}, None)
    with pytest.raises(BaseError, match="敏感级别只能是"):
        owner.save_doc(s["rel"], s["version"], {"sensitivity": "secret"}, None)


def test_owner_cannot_demote_himself(repo, owner):
    s = owner.doc_source(f"people/{OWNER}.md")
    assert all(f["locked"] for f in s["fields"] if f["key"] in ("kind", "can_write"))
    owner.save_doc(s["rel"], s["version"], {"can_write": False, "kind": "agent", "role": "主人"}, None)
    text = read(repo, f"people/{OWNER}.md")
    assert "can_write: true" in text and "kind: agent" not in text and "role: 主人" in text


def test_project_without_state_still_saves(repo, owner):
    path = repo / "projects/alpha/project.md"
    path.write_text(read(repo, "projects/alpha/project.md").replace("state: 进行中\n", ""), encoding="utf-8", newline="\n")
    git(repo, "commit", "-qam", "去掉状态")
    s = owner.doc_source("projects/alpha/project.md")
    assert next(f for f in s["fields"] if f["key"] == "state")["value"] == ""
    owner.save_doc(s["rel"], s["version"], {"state": "", "goal": "新目标"}, None)
    assert "goal: 新目标" in read(repo, "projects/alpha/project.md")
    s = owner.doc_source("projects/alpha/project.md")
    with pytest.raises(BaseError, match="状态只能是"):
        owner.save_doc(s["rel"], s["version"], {"state": "随便"}, None)


def test_restricted_project_decisions_cannot_be_loosened(repo, owner):
    owner.record_decision("家里的决定", "这么办", "因为", OWNER_NAME, "secret", "restricted", ["codex"])
    rel = next(p for p in (repo / "decisions").iterdir()).relative_to(repo).as_posix()
    s = owner.doc_source(rel)
    with pytest.raises(BaseError, match="关联的项目是限定的"):
        owner.save_doc(rel, s["version"], {"sensitivity": "internal"}, None)
    with pytest.raises(BaseError, match="关联的项目是限定的"):
        owner.save_doc(rel, s["version"], {"sensitivity": "restricted", "visible_to": ["codex", "claude"]}, None)
    # 项目改成限定时，提醒还没跟上的决定
    owner.record_decision("阿尔法的决定", "这么办", "因为", OWNER_NAME, "alpha")
    s = owner.doc_source("projects/alpha/project.md")
    r = owner.save_doc(s["rel"], s["version"], {"sensitivity": "restricted", "visible_to": ["codex"]}, None)
    assert r["warnings"] and "阿尔法的决定" in r["warnings"][0]


def test_create_docs_and_members(repo, owner):
    r = owner.create_doc("asset", {"id": "brand-guide", "title": "品牌规范", "sensitivity": "public"}, "颜色用琴漆。")
    text = read(repo, r["rel"])
    assert r["rel"] == "assets/brand-guide.md" and "type: asset" in text and "sensitivity: public" in text
    assert git(repo, "log", "-1", "--format=%an|%s") == f"{OWNER}|新建：资料「品牌规范」"
    with pytest.raises(BaseError, match="已经有"):
        owner.create_doc("asset", {"id": "brand-guide", "title": "又一份"}, "")
    with pytest.raises(BaseError, match="小写英文"):
        owner.create_doc("handbook", {"id": "品牌", "title": "品牌"}, "")
    with pytest.raises(BaseError, match="只能新建"):
        owner.create_doc("decision", {"id": "x1", "title": "x"}, "")
    owner.create_doc("person", {"id": "kimi", "title": "Kimi", "kind": "agent", "owner": OWNER, "can_write": False, "role": "试用"}, "")
    text = read(repo, "people/kimi.md")
    assert "agent_key: kimi" in text and "can_write: false" in text
    with pytest.raises(BaseError, match="已经有"):
        owner.create_doc("person", {"id": "codex", "title": "又一个 Codex"}, "")


def test_agents_cannot_use_management(as_agent):
    codex = as_agent("codex")
    with pytest.raises(BaseError, match=f"{OWNER_NAME} 自己用"):
        codex.doc_source("handbook/company.md")
    with pytest.raises(BaseError, match="agent 不能代做"):
        codex.save_doc("handbook/company.md", "x", {}, None)
    with pytest.raises(BaseError, match="agent 不能代做"):
        codex.create_doc("asset", {"id": "abc", "title": "t"}, "")
    with pytest.raises(BaseError, match="agent 不能代做"):
        codex.decide_handover("alpha", "项目 Lead", "新 Lead", True)


def test_approve_handover_from_web(repo, as_agent, owner):
    as_agent("codex").handover_role("alpha", "项目 Lead", "新 Lead", "我来接", signed_as="alex")
    assert [p["to"] for p in pending(owner)] == ["新 Lead"]
    owner.decide_handover("alpha", "项目 Lead", "新 Lead", True, "可以")
    assert "| 项目 Lead | 新 Lead |" in owner.get_project("alpha")
    status = read(repo, "projects/alpha/status.md")
    assert "在网页“待批准”里点了批准" in status and "可以" in status
    assert pending(owner) == []
    with pytest.raises(BaseError, match="找不到这条申请"):
        owner.decide_handover("alpha", "项目 Lead", "新 Lead", True)


def test_reject_handover_clears_it_until_asked_again(repo, as_agent, owner):
    codex = as_agent("codex")
    codex.handover_role("alpha", "项目 Lead", "新 Lead", "我来接")
    owner.decide_handover("alpha", "项目 Lead", "新 Lead", False, "再等等")
    assert "| 项目 Lead | 老 Lead |" in owner.get_project("alpha")
    assert "不批接任：「项目 Lead」由「老 Lead」换成「新 Lead」" in read(repo, "projects/alpha/status.md")
    assert git(repo, "log", "-1", "--format=%an|%s") == f"{OWNER}|接任不批：alpha 的项目 Lead → 新 Lead"
    assert pending(owner) == []
    codex.handover_role("alpha", "项目 Lead", "新 Lead", "再申请一次")
    assert [p["to"] for p in pending(owner)] == ["新 Lead"]


@pytest.fixture
def client(repo):
    c = TestClient(build_app(repo, PORT, TOKEN), base_url=BASE, follow_redirects=False)
    assert c.get(f"/open?t={TOKEN}").status_code == 303
    return c


def test_web_edit_and_approve(client, as_agent):
    s = client.get("/api/source", params={"rel": "handbook/company.md"}).json()
    body = {"rel": s["rel"], "version": s["version"], "changes": {"title": "公司概况（新）"}, "body": "改过了。"}
    assert client.post("/api/source/save", json=body).status_code == 403
    r = client.post("/api/source/save", json=body, headers=POST_HEADERS)
    assert r.status_code == 200 and r.json()["commit"]
    r = client.post("/api/source/save", json=body, headers=POST_HEADERS)
    assert r.status_code == 400 and "被别人改过" in r.json()["error"]
    rows = client.get("/api/activity").json()["rows"]
    assert rows[0]["type"] == "编辑" and rows[0]["what"].startswith("改了手册「公司概况」")
    assert client.get("/api/source", params={"rel": "projects/alpha/status.md"}).status_code == 400

    as_agent("codex").handover_role("alpha", "项目 Lead", "新 Lead", "我来接")
    ap = client.get("/api/approvals").json()
    assert [(p["project_name"], p["to"]) for p in ap["handovers"]] == [("阿尔法", "新 Lead")]
    r = client.post("/api/handover/decide", json={"project": "alpha", "role": "项目 Lead", "to": "新 Lead", "approve": False},
                    headers=POST_HEADERS)
    assert r.status_code == 200
    assert client.get("/api/approvals").json()["handovers"] == []
    assert client.get("/api/activity").json()["rows"][0]["type"] == "申请"


def test_web_lists(client, owner):
    owner.record_decision("阿尔法的决定", "这么办", "因为", OWNER_NAME, "alpha")
    docs = client.get("/api/docs").json()["items"]
    assert {d["rel"] for d in docs} >= {"handbook/company.md", "assets/sample.md"}
    assert all(d["kind_key"] in ("handbook", "asset") for d in docs)
    dec = client.get("/api/decisions").json()
    assert dec["items"][0]["project"] == {"id": "alpha", "name": "阿尔法"}
    assert OWNER not in {m["key"] for m in dec["members"]}
    members = client.get("/api/members").json()["items"]
    assert members[-1]["key"] == OWNER or {m["key"] for m in members} >= {"claude", "codex"}
    assert client.post("/api/render", json={"text": "**粗**"}, headers=POST_HEADERS).json()["html"].count("<strong>") == 1


def test_owner_makes_member_external_with_grants(repo, owner, as_agent):
    fields = {f["key"]: f for f in owner.doc_source("people/reader.md")["fields"]}
    assert fields["projects"]["type"] == "multi" and "alpha" in fields["projects"]["options"]
    assert fields["owner"]["show_if"] == "kind"
    make_external(owner, "people/reader.md", projects=["alpha"], assets=[])
    text = (repo / "people/reader.md").read_text(encoding="utf-8")
    assert "remote_enabled: true" in text and "access_mode: assigned" in text and "projects: [alpha]" in text and "owner" not in text
    reader = as_agent("reader")
    assert "阿尔法" in reader.list_projects() and "家里的事" not in reader.list_projects()
    assert git(repo, "log", "-1", "--format=%an") == OWNER


def test_external_settings_are_checked(owner):
    with pytest.raises(BaseError, match="没有 id 为 `nope` 的项目"):
        make_external(owner, "people/reader.md", projects=["nope"])
    with pytest.raises(BaseError, match="human 不能有 owner"):
        make_external(owner, "people/reader.md", owner="codex")  # human 不能挂在 agent 名下
    assert not any(f["key"] == "remote_enabled" for f in owner.doc_source(f"people/{OWNER}.md")["fields"])


def test_agent_follows_external_owner_set_on_web(repo, owner, as_agent):
    make_external(owner, "people/reader.md", projects=["alpha"])
    make_external(owner, "people/codex.md", projects=["alpha", "secret"], owner="reader")
    assert "阿尔法" in as_agent("codex").list_projects()
    assert "家里的事" not in as_agent("codex").list_projects()  # 超出 codex 的不算


def test_remote_disable_preserves_assigned_grants(repo, owner, as_agent):
    make_external(owner, "people/reader.md", projects=["alpha"])
    s = owner.doc_source("people/reader.md")
    owner.save_doc("people/reader.md", s["version"], {"remote_enabled": False}, None)
    text = (repo / "people/reader.md").read_text(encoding="utf-8")
    assert "remote_enabled: false" in text and "projects: [alpha]" in text
    assert "阿尔法" in as_agent("reader").list_projects()
    assert "家里的事" not in as_agent("reader").list_projects()


def test_new_external_member_starts_with_no_grants(repo, owner, as_agent):
    owner.create_doc("person", {"id": "ext-wang", "title": "王五", "kind": "human", "remote_enabled": True,
                                 "can_write": False, "sensitivity": "internal"}, "")
    wang = as_agent("ext-wang")
    assert "按授权访问的同事" in wang.whoami() and "阿尔法" not in wang.list_projects()
