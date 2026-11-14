"""经验全入口、状态及取代的真实临时 git 仓库验证。"""
import datetime as dt
import io
import json
import sys

import pytest

from onestead import config, gitops
from onestead.base import CompanyBase
from onestead.cli import run
from onestead.records import record_parts, record_state
from onestead.store import BaseError, Store, make_viewer
from onestead.textutil import _dump_front
from .support import FIXED_NOW, OWNER_NAME, change, git, put


def add(base, text="先看已有结论", **kw):
    return base._record_lesson_commit(lesson=text, **kw)


def doc(repo, id):
    return next(d for d in Store(repo).load() if d.kind == "lesson" and d.id == id)


def wire(repo, monkeypatch, method, params):
    incoming = io.TextIOWrapper(io.BytesIO(json.dumps({"method": method, "params": params}).encode()), encoding="utf-8")
    outgoing = io.TextIOWrapper(io.BytesIO(), encoding="utf-8")
    with monkeypatch.context() as patch:
        patch.setattr(sys, "stdin", incoming)
        patch.setattr(sys, "stdout", outgoing)
        code = run(["api", "--as", "claude", "--data", str(repo)])
        result = json.loads(outgoing.buffer.getvalue())
    assert code == (0 if result["ok"] else 1)
    return result


def test_lesson_fields_and_body(repo, as_agent):
    result = add(as_agent("claude"), "结论\n## 用户标题", title="经验标题", how="先读\n## 步骤", why="减少返工",
                 source="主人原话", project_id="alpha", ref="docs/rules.md", signed_as="Writer")
    d = doc(repo, result["id"])
    assert d.meta == {"id": d.id, "type": "lesson", "title": "经验标题", "date": "2026-10-10", "project": "alpha",
                      "source": "owner", "ref": "docs/rules.md", "recorded_by": "claude · Writer", "sensitivity": "internal",
                      "updated": "2026-10-10", "updated_by": "claude · Writer"}
    assert d.body == "\n# 经验标题\n\n## 结论\n\n结论\n### 用户标题\n\n## 怎么用\n\n先读\n### 步骤\n\n## 为什么\n\n减少返工\n"
    assert result["commit"] == git(repo, "rev-parse", "--short", "HEAD")
    assert result["hints"] == []


@pytest.mark.parametrize("source,stored", [("owner", "owner"), ("inferred", "inferred"), ("伙伴推断", "inferred")])
def test_minimal_and_sources(repo, as_agent, monkeypatch, source, stored):
    d = doc(repo, add(as_agent("claude"), "短句\n第二行", source=source)["id"])
    assert d.title == "短句" and d.meta["source"] == stored
    assert "怎么用" not in d.body and "为什么" not in d.body
    monkeypatch.setenv("ONESTEAD_LANG", "en")
    assert "Lesson 1" in as_agent("claude").whoami()
    assert "**Lesson**" in as_agent("claude").search("短句")


def test_only_lesson_and_three_hints(repo, as_agent, monkeypatch):
    base = as_agent("claude")
    assert "出处没填" in base.record_lesson("结论")
    monkeypatch.setenv("ONESTEAD_LESSONS_BRIEF_CHARS", "30")
    text = base.record_lesson("长" * 501)
    assert all(x in text for x in ("出处没填", "一条只说一件事", "公司共用经验已超过常读上限"))
    assert doc(repo, add(base, "短" * 60)["id"]).title == "短" * 40


@pytest.mark.parametrize("kw", [{"source": "guessed"}, {"source": []}, {"expires": "2026-02-30"},
    {"expires": "20261010"}, {"expires": "2026-1-1"}, {"pending": "false"}, {"supersedes": "id"},
    {"title": "长" * 81}, {"ref": "x" * 301}, {"how": "x" * 4001}, {"why": "x" * 4001},
    {"origin_id": "x" * 121}, {"title": "两\n行"}, {"lesson": "x" * 4001}, {"visible_to": [False]}])
def test_rejects_invalid_without_writing(repo, as_agent, kw):
    head = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError):
        as_agent("claude")._record_lesson_commit(**({"lesson": "结论"} | kw))
    assert git(repo, "rev-parse", "HEAD") == head and not (repo / "lessons").exists()


def test_assigned_writes_and_company_read(repo, ext):
    common = add(ext("claude"), "公司常识")
    assigned = ext("ext-zhang")
    assert "公司常识" in assigned.list_lessons()
    for kw in ({}, {"project_id": "beta"}, {"project_id": "alpha", "sensitivity": "public"}):
        with pytest.raises(BaseError):
            add(assigned, **kw)
    add(assigned, "项目经验", project_id="alpha")
    docs = Store(repo).load()
    assert not make_viewer(docs, None).can_see(doc(repo, common["id"]))
    assert make_viewer(docs, "reader").can_see(doc(repo, common["id"]))
    public = doc(repo, add(ext("claude"), "公开经验", sensitivity="public")["id"])
    assert make_viewer(Store(repo).load(), None).can_see(public)


def test_restricted_and_ref_projection(repo, ext):
    base = ext("codex")
    for kw in ({"sensitivity": "internal"}, {"sensitivity": "restricted", "visible_to": ["claude"]}):
        with pytest.raises(BaseError):
            add(base, project_id="secret", **kw)
    result = add(base, project_id="secret", sensitivity="restricted", visible_to=["codex"])
    assert doc(repo, result["id"]).meta["visible_to"] == ["codex"]
    add(base, "可见经验", project_id="alpha", ref="C:/PrivateMark/details.md")
    remote = CompanyBase(repo, "ext-zhang", require_remote=True, now=lambda: FIXED_NOW)
    for text in (remote.list_lessons(), json.dumps(remote.lessons_data(), ensure_ascii=False), remote.get_project("alpha")):
        assert "PrivateMark" not in text and "本机路径" in text
    common = add(base, "限定公司常识", sensitivity="restricted", visible_to=["codex"])
    assert not make_viewer(Store(repo).load(), "ext-zhang").can_see(doc(repo, common["id"]))


def test_supersede_atomically_and_show_inactive(repo, as_agent):
    base = as_agent("claude")
    old = add(base, "旧规则", project_id="alpha")
    new = add(base, "新规则", project_id="alpha", supersedes=[old["id"]])
    files = git(repo, "show", "--format=", "--name-only", "HEAD").splitlines()
    assert sorted(files) == sorted([old["path"], new["path"]])
    assert doc(repo, old["id"]).meta["superseded_by"] == new["id"]
    assert "旧规则" not in base.list_lessons("alpha")
    text = base.list_lessons("alpha", include_inactive=True)
    assert "已被取代" in text and new["id"] in text and "旧规则" in text
    head = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError, match=new["id"]):
        add(base, supersedes=[old["id"]])
    assert git(repo, "rev-parse", "HEAD") == head


def test_cross_project_and_invalid_supersedes(repo, ext):
    base = ext("claude")
    a = add(base, "甲", project_id="alpha")
    b = add(base, "乙", project_id="beta")
    common = add(base, "共用规则", supersedes=[a["id"], b["id"]])
    assert "project" not in doc(repo, common["id"]).meta
    base.record_decision("一条决定", "做", "因为", OWNER_NAME)
    decision = next(d for d in Store(repo).load() if d.kind == "decision")
    retired = add(base, "撤下规则")
    base.update_record(retired["id"], {"retired": True})
    for ids in ([decision.id], ["missing"], [retired["id"]]):
        head = git(repo, "rev-parse", "HEAD")
        with pytest.raises(BaseError):
            add(base, supersedes=ids)
        assert git(repo, "rev-parse", "HEAD") == head


def test_supersede_hidden_or_unwritable_is_atomic(repo, ext):
    base = ext("claude")
    old = add(base, "隐藏", project_id="beta")
    valid = add(ext("ext-zhang"), "可写", project_id="alpha")
    head = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError):
        add(ext("ext-zhang"), project_id="alpha", supersedes=[valid["id"], old["id"]])
    assert git(repo, "rev-parse", "HEAD") == head
    assert "superseded_by" not in doc(repo, valid["id"]).meta
    change(repo, "projects/beta/project.md", sensitivity="public")
    public = add(base, "可见不可写", project_id="beta", sensitivity="public")
    with pytest.raises(BaseError):
        add(ext("ext-zhang"), project_id="alpha", supersedes=[public["id"]])


def test_states_order_and_date_boundary(repo, as_agent):
    base = as_agent("claude")
    cases = [({}, "active"), ({"expires": "2026-10-10"}, "active"), ({"expires": "2026-10-09"}, "expired"),
             ({"expires": "2026-10-09", "pending": True}, "pending"),
             ({"expires": "2026-10-09", "pending": True, "superseded_by": "new"}, "superseded"),
             ({"expires": "2026-10-09", "pending": True, "superseded_by": "new", "retired": "2026-10-01"}, "retired")]
    for i, (meta, expected) in enumerate(cases):
        result = add(base, f"状态{i}")
        change(repo, result["path"], **meta)
        assert record_state(doc(repo, result["id"]), FIXED_NOW.date()) == expected
    text = base.list_lessons()
    assert "状态0" in text and "状态1" in text and "状态2" not in text
    assert text.index("状态3") > text.index("待确认")
    assert all(f"状态{i}" in base.list_lessons(include_inactive=True) for i in range(6))


def test_update_confirm_retire_restore_and_edit(repo, as_agent):
    base = as_agent("claude")
    result = add(base, "旧结论", project_id="alpha", pending=True)
    id = result["id"]
    for changes in ({"pending": False}, {"retired": True, "note": "确认不对"}, {"retired": False},
                    {"expires": "2027-01-01"}, {"expires": None},
                    {"title": "新标题", "lesson": "新结论", "how": "先读", "why": "少返工", "ref": "docs/x", "source": "伙伴推断"},
                    {"project": None}):
        base.update_record(id, changes, "Editor")
        d = doc(repo, id)
        assert d.meta["updated_by"] == "claude · Editor"
        if changes.get("retired"):
            assert d.meta["retire_note"] == "确认不对" and d.meta["retired_by"] == "claude · Editor"
        if changes.get("retired") is False:
            assert all(k not in d.meta for k in ("retired", "retired_by", "retire_note"))
    assert record_parts(d) == {"lesson": "新结论", "how": "先读", "why": "少返工"}
    assert "project" not in d.meta and "expires" not in d.meta and d.meta["source"] == "inferred"
    head = git(repo, "rev-parse", "HEAD")
    assert "没有" in base.update_record(id, {"lesson": "新结论", "pending": False})
    assert git(repo, "rev-parse", "HEAD") == head


@pytest.mark.parametrize("changes", [{"superseded_by": "other"}, {"unknown": 1}, {"date": "2026-01-01"},
    {"recorded_by": "other"}, {"retired": "true"}, {"note": "alone"}, {"retired": True, "note": "x" * 201}])
def test_update_invalid_is_atomic(repo, as_agent, changes):
    base = as_agent("claude")
    result = add(base)
    head = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError):
        base.update_record(result["id"], changes)
    assert git(repo, "rev-parse", "HEAD") == head


def test_update_ambiguous_and_permissions(repo, ext):
    base = ext("claude")
    result = add(base, project_id="alpha")
    d = doc(repo, result["id"])
    put(repo, f"decisions/{d.id}.md", d.meta | {"type": "decision"}, "\n# 决定\n\n## 决定\n\n做\n\n## 理由\n\n好\n")
    with pytest.raises(BaseError, match="两个目录"):
        base.update_record(d.id, {"title": "新"})
    base.update_record("lessons/" + d.id, {"title": "指定经验"})
    base.update_record("decisions/" + d.id, {"decision": "新决定", "reason": "新理由"})
    assigned = ext("ext-zhang")
    for project in (None, "beta", "secret"):
        with pytest.raises(BaseError):
            assigned.update_record("lessons/" + d.id, {"project": project})
    assert doc(repo, d.id).meta["project"] == "alpha"


def test_decision_legacy_exact_bytes_and_new_fields(repo, as_agent):
    base = as_agent("claude")
    base.record_decision("选择甲", "甲\n## 附注", "因为稳", OWNER_NAME, project_id="alpha", signed_as="Writer")
    old = next(d for d in Store(repo).load() if d.kind == "decision")
    expected = _dump_front({"id": "2026-10-10-claude-233000", "type": "decision", "title": "选择甲", "date": "2026-10-10",
        "decided_by": OWNER_NAME, "project": "alpha", "recorded_by": "claude · Writer", "sensitivity": "internal", "updated": "2026-10-10"})
    expected += "\n# 选择甲\n\n## 决定\n\n甲\n### 附注\n\n## 理由\n\n因为稳\n"
    assert old.raw == expected
    base.record_decision("选择乙", "乙", "更稳", OWNER_NAME, source="主人原话", expires="2027-01-01", pending=True, supersedes=[old.id])
    new = next(d for d in Store(repo).load() if d.kind == "decision" and d.id != old.id)
    assert new.meta["source"] == "owner" and new.meta["pending"] and new.meta["expires"] == "2027-01-01"
    assert "选择甲" not in base.list_decisions()
    assert "选择乙" in base.list_decisions() and "更稳" not in base.list_decisions()
    assert "已被取代" in base.list_decisions(include_inactive=True)
    base.update_record("decisions/" + new.id, {"pending": False})
    assert "更稳" in base.list_decisions()


def test_company_project_brief_budget_and_pending(repo, as_agent, monkeypatch, tmp_path):
    base = as_agent("claude")
    change(repo, "projects/alpha/project.md", paths=[str(tmp_path)])
    add(base, "实用结论。", how="先查。", why="跨页会失败", project_id="alpha", source="owner", ref="docs/details.md")
    add(base, "待确认标题", project_id="alpha", pending=True)
    add(base, "公司结论")
    text = base.company(str(tmp_path / "work"))
    assert "公司共用经验" in text and "公司结论" in text
    assert "另有 1 条待确认" in text and "待确认标题" not in text
    # 带“为什么”的照常带出；句末标点由模板统一接，标题是结论截出来的就只写一遍。
    assert "- 实用结论。怎么用：先查。为什么：跨页会失败。（主人原话，" in text
    assert "；细节见 docs/details.md）" in text and "- 公司结论。（" in text
    # 公司共用经验在第一屏：紧跟置顶规则，排在项目表之前。
    assert text.index("公司共用经验") < text.index("| `alpha` |")
    project = base.get_project("alpha")
    assert "现行经验" in project and "全部经验用 list_lessons" in project
    monkeypatch.setenv("ONESTEAD_LESSONS_BRIEF_CHARS", "0")
    base.import_lessons([{"lesson": f"正文{i}", "title": f"标题{i}", "project_id": "alpha"} for i in range(25)])
    text = base.company(str(tmp_path))
    # 上限为 0 时，带“为什么”的那条仍完整带出且排最前，其余只列标题。
    assert "为什么：跨页会失败" in text and text.index("实用结论") < text.index("- 标题24")
    # 上限为 0 时标题也只占 0 字的份额：至少列一条标题，其余报条数。
    assert "正文24" not in text and "- 标题24" in text and "还有 24 条" in text and "请项目负责人整理" in text
    assert "公司共用经验已超过常读上限" in text
    assert add(base, "再一条公司结论")["hints"][-1].startswith("公司共用经验已超过常读上限")


def test_config_budget_env(isolated_config, monkeypatch):
    assert config.lessons_brief_chars() == 3000
    isolated_config.write_text("lessons_brief_chars = 80", encoding="utf-8")
    assert config.lessons_brief_chars() == 80
    monkeypatch.setenv("ONESTEAD_LESSONS_BRIEF_CHARS", "120")
    assert config.lessons_brief_chars() == 120
    for value in ("-1", "bad", "1.5"):
        monkeypatch.setenv("ONESTEAD_LESSONS_BRIEF_CHARS", value)
        with pytest.raises(BaseError):
            config.lessons_brief_chars()


def test_api_four_methods_import_dedupe_and_atomic_reject(repo, monkeypatch):
    recorded = wire(repo, monkeypatch, "record_lesson", {"lesson": "API结论", "project_id": "alpha"})["result"]
    assert recorded["commit"] == git(repo, "rev-parse", "--short", "HEAD") and recorded["hints"]
    listed = wire(repo, monkeypatch, "list_lessons", {"project": "alpha"})["result"]
    assert listed["lessons"][0]["id"] == recorded["id"] and listed["truncated"] == 0
    assert set(listed["lessons"][0]) == {"id", "title", "lesson", "how", "why", "ref", "source", "date", "happened", "expires", "state", "project", "recorded_by", "supersedes", "superseded_by"}
    batch = {"items": [{"lesson": "导入", "origin_id": "memory-1"}], "project": "alpha"}
    first = wire(repo, monkeypatch, "import_lessons", batch)["result"]
    assert first["commit"] == git(repo, "rev-parse", "--short", "HEAD") and len(first["created"]) == 1
    second = wire(repo, monkeypatch, "import_lessons", batch)["result"]
    assert second == {"created": [], "skipped": [{"origin_id": "memory-1", "id": first["created"][0]}], "commit": None}
    head = git(repo, "rev-parse", "HEAD")
    bad = wire(repo, monkeypatch, "import_lessons", {"items": [{"lesson": "不落盘"}, {"lesson": "坏", "expires": "wrong"}]})
    assert not bad["ok"] and "第 2 条" in bad["error"]["message"]
    assert git(repo, "rev-parse", "HEAD") == head
    history = wire(repo, monkeypatch, "history", {"target": first["created"][0], "limit": 1})["result"]
    assert history[0]["commit"] == first["commit"] and history[0]["tool"] == "import_lessons"


def test_api_budget_pending_and_batch_validation_after_preparation(repo, as_agent):
    base = as_agent("claude")
    add(base, "one", how="two")
    add(base, "hidden pending body", title="待确认", pending=True)
    assert base.lessons_data(max_chars=5)["truncated"] == 1
    assert base.lessons_data(max_chars=6)["lessons"][0]["lesson"] == "one"
    assert base.lessons_data()["pending"][0]["title"] == "待确认"
    add(base, "反常理", why="原因")
    data = base.lessons_data(max_chars=0)
    assert [x["lesson"] for x in data["lessons"]] == ["反常理"] and data["truncated"] == 1
    old = add(base)
    head = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError, match="第 2 条"):
        base.import_lessons([{"lesson": "甲", "supersedes": [old["id"]]}, {"lesson": "乙", "supersedes": [old["id"]]}])
    assert git(repo, "rev-parse", "HEAD") == head and "superseded_by" not in doc(repo, old["id"]).meta


def test_same_second_batch_order_and_localized_body_edit(repo, as_agent):
    base = as_agent("claude")
    imported = base.import_lessons([{"lesson": f"结论{i}"} for i in range(12)])
    assert [d["id"] for d in base.lessons_data()["lessons"]] == list(reversed(imported["created"]))
    earlier = CompanyBase(repo, "codex", now=lambda: FIXED_NOW - dt.timedelta(minutes=1))
    older = add(earlier, "较早的经验")
    assert base.lessons_data()["lessons"][-1]["id"] == older["id"]
    id = imported["created"][0]
    d = doc(repo, id)
    put(repo, d.rel, d.meta | {"pending": False}, d.body + "\n## 手工补充\n\n保留这段。\n")
    base.update_record(id, {"title": "新标题", "lesson": "新措辞"})
    assert "## 手工补充\n\n保留这段。" in doc(repo, id).body
    head = git(repo, "rev-parse", "HEAD")
    assert "没有" in base.update_record(id, {"pending": False})
    assert git(repo, "rev-parse", "HEAD") == head


def test_cli_three_commands(repo, capsys):
    common = ["--as", "claude", "--data", str(repo)]
    assert run(["lesson-add", "--lesson", "CLI结论", "--source", "owner", *common]) == 0
    assert "已记录经验" in capsys.readouterr().out
    assert run(["lessons", *common]) == 0 and "CLI结论" in capsys.readouterr().out
    id = next(d.id for d in Store(repo).load() if d.kind == "lesson")
    assert run(["history", id, "--limit", "1", *common]) == 0
    assert "record_lesson" in capsys.readouterr().out
    assert run(["decide", "--title", "CLI决定", "--decision", "做", "--reason", "好", "--decided-by", OWNER_NAME,
                "--source", "owner", "--expires", "2027-01-01", "--pending", *common]) == 0
    decision = next(d for d in Store(repo).load() if d.kind == "decision")
    assert decision.meta["source"] == "owner" and decision.meta["pending"] and decision.meta["expires"] == "2027-01-01"


def test_approved_source_happened_titles_and_project_cap(repo, as_agent, monkeypatch, tmp_path):
    base = as_agent("claude")
    change(repo, "projects/alpha/project.md", paths=[str(tmp_path)])
    approved = add(base, "认可的做法", project_id="alpha", source="主人认可")
    assert doc(repo, approved["id"]).meta["source"] == "approved"
    assert "认可的做法。（主人认可，2026-" in base.company(str(tmp_path))
    # 补录：排序和显示都按 happened；没填的按记录日期。
    early = add(base, "九月定的规矩", project_id="alpha", happened="2026-09-12", source="owner")
    assert doc(repo, early["id"]).meta["happened"] == "2026-09-12"
    data = base.lessons_data(project="alpha")
    assert [x["id"] for x in data["lessons"]][-1] == early["id"] and data["lessons"][-1]["happened"] == "2026-09-12"
    assert "九月定的规矩。（主人原话，2026-09-12）" in base.company(str(tmp_path))
    assert "发生日期：2026-09-12" in base.list_lessons("alpha")
    base.update_record(early["id"], {"happened": None})
    assert "happened" not in doc(repo, early["id"]).meta
    with pytest.raises(BaseError):
        add(base, "坏日期", happened="2026-13-01")
    base.record_decision("补录决定", "定了", "因为", "主人", project_id="alpha", happened="2026-10-20")
    assert any(d.meta.get("happened") == "2026-10-20" for d in Store(repo).load() if d.kind == "decision")
    # 溢出的标题逐条列出（标题另占一份上限），api 把被截掉的标题也带回来。
    monkeypatch.setenv("ONESTEAD_LESSONS_BRIEF_CHARS", "120")
    base.import_lessons([{"lesson": f"规则正文第{i}条比较长" * 3, "title": f"规则{i}", "project_id": "alpha"} for i in range(30)])
    text = base.company(str(tmp_path))
    assert "规则29" in text and "- 规则0\n" in text and "还有" not in text.split("公司共用经验")[0]
    data = base.lessons_data(project="alpha")
    assert data["truncated"] == len(data["more"]) > 20 and {"id", "title"} == set(data["more"][0])
    # 项目自己的上限覆盖全局；写坏了回到全局。
    change(repo, "projects/alpha/project.md", lessons_brief_chars=100000)
    assert base.lessons_data(project="alpha")["truncated"] == 0 and "规则正文第29条" in base.company(str(tmp_path))
    change(repo, "projects/alpha/project.md", lessons_brief_chars="bad")
    assert base.lessons_data(project="alpha")["truncated"] > 0


def test_budget_counts_body_only_everywhere(repo, as_agent, tmp_path):
    # 上限只数结论、怎么用、为什么；标题、出处、日期不算。开工带出、list_lessons、摘要三处同一口径。
    base = as_agent("claude")
    change(repo, "projects/alpha/project.md", paths=[str(tmp_path)], lessons_brief_chars=80)
    for i in range(2):
        add(base, f"{i}" + "甲" * 29, how="乙" * 10, title=f"第{i}条很长很长的标题" * 5, project_id="alpha", source="owner")
    assert base.lessons_data(project="alpha")["truncated"] == 0
    assert base.company(str(tmp_path)).count("甲" * 29) == 2
    assert not [t for t in base.lesson_digest_data()["maintenance"]["brief_overflow"] if t["project"] == "alpha"]
    change(repo, "projects/alpha/project.md", lessons_brief_chars=79)
    assert base.lessons_data(project="alpha")["truncated"] == 1
    assert [t["limit"] for t in base.lesson_digest_data()["maintenance"]["brief_overflow"] if t["project"] == "alpha"] == [79]


def test_update_record_over_api_and_cli(repo, monkeypatch, capsys):
    recorded = wire(repo, monkeypatch, "record_lesson", {"lesson": "旧标题的结论", "project_id": "alpha", "source": "inferred"})["result"]
    changed = wire(repo, monkeypatch, "update_record", {"record_id": recorded["id"], "signed_as": "Writer",
                   "changes": {"title": "发布前按顾客路径走一遍", "source": "主人认可", "happened": "2026-09-01"}})["result"]
    assert changed == {"id": recorded["id"], "path": recorded["path"], "commit": git(repo, "rev-parse", "--short", "HEAD")}
    meta = doc(repo, recorded["id"]).meta
    assert (meta["title"], meta["source"], meta["happened"]) == ("发布前按顾客路径走一遍", "approved", "2026-09-01")
    again = wire(repo, monkeypatch, "update_record", {"record_id": recorded["id"], "changes": {"source": "approved"}})
    assert again["result"]["commit"] is None
    assert wire(repo, monkeypatch, "update_record", {"record_id": "nope", "changes": {"title": "x"}})["error"]["code"] == "not_found"
    assert wire(repo, monkeypatch, "update_record", {"record_id": recorded["id"], "changes": {"id": "x"}})["error"]["code"] == "invalid"
    common = ["--as", "claude", "--data", str(repo)]
    capsys.readouterr()
    assert run(["record-update", recorded["id"], "--json", '{"pending": true}', *common]) == 0
    assert doc(repo, recorded["id"]).meta["pending"] is True
