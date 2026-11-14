"""摘要、原子过目和全读编辑；数据与时钟全部隔离。"""

import datetime as dt
import io
import json
import sys

import pytest

from onestead import api, config, gitops, records
from onestead.base import CompanyBase
from onestead.cli import run
from onestead.digest import SIMILARITY_THRESHOLD, similarity
from onestead.store import BaseError, Store
from onestead.textutil import _dump_front
from .support import FIXED_NOW, OWNER, OWNER_NAME, change, git


def put(repo, key, *, kind="lesson", title=None, conclusion=None, why=None, date="2026-10-10", **fields):
    meta = {"id": key, "type": kind, "title": title or key, "date": date, "sensitivity": "internal", **fields}
    parts = {"lesson": conclusion or key, "why": why} if kind == "lesson" else {"decision": conclusion or key, "reason": "理由"}
    folder = "lessons" if kind == "lesson" else "decisions"
    rel = f"{folder}/{key}.md"
    path = repo / rel
    path.parent.mkdir(exist_ok=True)
    path.write_text(_dump_front(meta) + records.record_body(kind, meta["title"], parts), encoding="utf-8", newline="\n")
    return rel.removesuffix(".md")


def doc(repo, key):
    return next(d for d in Store(repo).load() if d.id == key)


def save(repo):
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "样本")


def test_period_fixed_clock_and_counts(repo, as_agent):
    put(repo, "old", date="2026-10-03", source="inferred")
    put(repo, "week", date="2026-10-04", source="owner")
    put(repo, "today-120000", project="alpha", source="inferred")
    put(repo, "choice", kind="decision", project="alpha", source="owner")
    base = as_agent("claude")
    data = base.lesson_digest_data()
    assert data["since"] == "2026-10-03 23:30"
    assert data["new_records"] == [
        {"project": None, "project_title": "公司共用", "lessons": 1, "decisions": 0},
        {"project": "alpha", "project_title": "阿尔法", "lessons": 1, "decisions": 1}]
    change(repo, f"people/{OWNER}.md", lessons_reviewed_at="2026-10-10 10:00")
    data = base.lesson_digest_data()
    assert data["since"] == "2026-10-10 10:00"
    assert data["new_records"][0]["lessons"] == 1 and len(data["new_records"]) == 1
    assert [r["id"] for r in data["judgments"]] == ["today-120000"]


def test_three_judgment_categories_order_missing_source_and_group(repo, as_agent):
    put(repo, "pending-new", pending=True)
    put(repo, "pending-old", pending=True, date="2026-08-01", kind="decision")
    put(repo, "unsourced", conclusion="配送前先确认车辆")
    put(repo, "same-alpha", title="上线前验证", conclusion="发布前先确认回滚入口", project="alpha", source="owner")
    put(repo, "same-secret", title="上线前验证！", conclusion="发布前，先确认回滚入口。", project="secret", source="owner")
    put(repo, "retired-pending", pending=True, retired="2026-10-09")
    data = as_agent("claude").lesson_digest_data()
    rows = data["judgments"]
    assert [r["category"] for r in rows] == ["pending", "pending", "inferred", "promotion"]
    assert rows[0]["id"] == "pending-old" and rows[0]["waiting_days"] == 70
    assert rows[2]["source_label"] == "出处没填"
    assert {r["project"] for r in rows[3]["records"]} == {"alpha", "secret"}
    assert [r["number"] for r in rows] == [1, 2, 3, 4]
    rendered = as_agent("claude").lesson_digest()
    assert "出处没填" in rendered and "已等 70 天" in rendered and OWNER_NAME in rendered


def test_25_judgments_three_batches(repo, as_agent):
    for i in range(25):
        put(repo, f"pending-{i:02}", pending=True, conclusion="甲" * 200)
    base = as_agent("claude")
    first, last = base.lesson_digest_data(), base.lesson_digest_data(batch=3)
    assert first["judgment_count"] == 25 and first["batches"] == 3 and first["remaining_batches"] == 2
    assert len(first["judgments"]) == 10 and len(last["judgments"]) == 5 and last["remaining_batches"] == 0
    assert len(last["judgments"][0]["conclusion"]) == 120
    assert "还有 2 批。" in base.lesson_digest() and "还有 0 批。" in base.lesson_digest(3)
    for batch in (0, -1, True, "1"):
        with pytest.raises(BaseError):
            base.lesson_digest_data(batch)


def test_similarity_threshold_and_punctuation():
    assert SIMILARITY_THRESHOLD == 0.5
    assert similarity("abcd", "abce") == 0.5
    assert similarity("abcd", "abxy") < 0.5
    assert similarity("甲 乙，丙。丁！", "甲乙丙丁") == 1.0
    assert similarity("", "") == 0


def test_maintenance_and_remote_pointer_skip(repo, tmp_path, as_agent, monkeypatch):
    monkeypatch.setattr(config, "lessons_brief_chars", lambda: 100)
    p1, p2 = tmp_path / "one", tmp_path / "two"
    p1.mkdir()
    p2.mkdir()
    (p2 / "present.md").write_text("存在", encoding="utf-8")
    change(repo, "projects/alpha/project.md", paths=[str(p1), str(p2)])
    put(repo, "expired", expires="2026-10-09", kind="decision")
    put(repo, "absolute", ref=str(tmp_path / "gone.md"), source="owner")
    put(repo, "relative", project="alpha", ref="missing.md", source="owner")
    put(repo, "present", project="alpha", ref="present.md", source="owner")
    (p2 / "sub").mkdir()
    put(repo, "folder-and-two", project="alpha", ref=f"sub/；present.md; {p1}", source="owner")
    put(repo, "one-of-two-gone", project="alpha", ref="present.md；missing.md", source="owner")
    put(repo, "url", ref="https://example.test/missing", source="owner")
    put(repo, "malformed-url", ref="https://[invalid", source="owner")
    put(repo, "dupe1", title="交付", conclusion="交付前先开成品检查", source="owner", why="原因" * 100)
    put(repo, "dupe2", title="交付。", conclusion="交付前，先开成品检查！", source="owner")
    base = as_agent("claude")
    tasks = base.lesson_digest_data()["maintenance"]
    assert [r["id"] for r in tasks["expired"]] == ["expired"]
    assert {r["id"] for r in tasks["broken_refs"]} == {"absolute", "relative", "one-of-two-gone"}
    assert any({r["id"] for r in group["records"]} == {"dupe1", "dupe2"} for group in tasks["duplicates"])
    assert tasks["why_overflow"][0]["chars"] > 100
    assert [r["id"] for r in tasks["why_overflow"][0]["records"]] == ["dupe1"]
    assert tasks["brief_overflow"]
    change(repo, "people/claude.md", remote_enabled=True)
    remote = CompanyBase(repo, "claude", require_remote=True, now=lambda: FIXED_NOW)
    assert remote.lesson_digest_data()["maintenance"]["broken_refs"] == []
    assert "gone.md" not in remote.lesson_digest()


def test_notify_and_review_one_commit_each_and_quoted_timestamps(repo, as_agent):
    a = put(repo, "confirm", pending=True)
    b = put(repo, "retire", kind="decision")
    save(repo)
    base = as_agent("claude")
    count = int(git(repo, "rev-list", "--count", "HEAD"))
    data = base.lesson_digest_data(notify=True)
    assert data["commit"] == git(repo, "rev-parse", "--short", "HEAD")
    assert int(git(repo, "rev-list", "--count", "HEAD")) == count + 1
    assert doc(repo, OWNER).meta["lessons_notified_at"] == "2026-10-10 23:30"
    raw = (repo / f"people/{OWNER}.md").read_text(encoding="utf-8")
    assert "lessons_notified_at: '2026-10-10 23:30'" in raw
    result = base._review_lessons_commit(confirm=[a], retire=[{"id": b, "note": "现在不适用"}], done=True, signed_as="Reviewer")
    assert result["commit"] == git(repo, "rev-parse", "--short", "HEAD")
    assert int(git(repo, "rev-list", "--count", "HEAD")) == count + 2
    assert "pending" not in doc(repo, "confirm").meta
    assert doc(repo, "retire").meta["retire_note"] == "现在不适用"
    assert doc(repo, "retire").meta["retired_by"] == "claude · Reviewer"
    meta = doc(repo, OWNER).meta
    assert meta["lessons_reviewed_at"] == "2026-10-10 23:30" and "lessons_notified_at" not in meta
    assert "lessons_reviewed_at: '2026-10-10 23:30'" in (repo / f"people/{OWNER}.md").read_text(encoding="utf-8")
    assert not (doc(repo, "claude").meta.keys() & {"lessons_notified_at", "lessons_reviewed_at"})


@pytest.mark.parametrize("bad", ["missing", "decisions/hidden"])
def test_review_bad_id_rolls_back_everything(repo, as_agent, bad):
    key = put(repo, "good", pending=True)
    save(repo)
    before = (repo / "lessons/good.md").read_bytes()
    head = git(repo, "rev-parse", "HEAD")
    human = (repo / f"people/{OWNER}.md").read_bytes()
    with pytest.raises(BaseError):
        as_agent("claude").review_lessons(confirm=[key, bad], done=True)
    assert (repo / "lessons/good.md").read_bytes() == before
    assert (repo / f"people/{OWNER}.md").read_bytes() == human
    assert git(repo, "rev-parse", "HEAD") == head and git(repo, "status", "--porcelain") == ""


def test_review_validation_and_ambiguous_ids(repo, as_agent):
    base = as_agent("claude")
    for params in ({}, {"confirm": []}, {"retire": []}, {"done": 1}, {"confirm": "x"}, {"retire": [{"id": "x", "extra": 1}]}):
        with pytest.raises(BaseError):
            base.review_lessons(**params)
    put(repo, "same", pending=True)
    put(repo, "same", kind="decision", pending=True)
    with pytest.raises(BaseError, match="两个目录"):
        base.review_lessons(confirm=["same"])
    base.review_lessons(confirm=["lessons/same.md", "decisions/same"])
    with pytest.raises(BaseError):
        base.review_lessons(confirm=["same", "lessons/same"])


def test_company_hint_due_content_notification_and_review(repo, as_agent):
    base = as_agent("claude")
    assert "经验摘要该给" not in base.company()
    put(repo, "pending", pending=True, date="2026-07-01")
    assert f"经验摘要该给{OWNER_NAME}过了（要他判断 1 条，你自己处理 0 条）" in base.company()
    base.lesson_digest_data(notify=True)
    assert "经验摘要该给" not in base.company()
    later = CompanyBase(repo, "claude", now=lambda: FIXED_NOW + dt.timedelta(days=7))
    assert "经验摘要该给" in later.company()
    base.review_lessons(done=True)
    assert "经验摘要该给" not in base.company()
    assert "经验摘要该给" in later.company()
    # 通知比过目早，不抑制到期提示。
    change(repo, f"people/{OWNER}.md", lessons_notified_at="2026-10-01 12:00")
    assert "经验摘要该给" in later.company()


def test_company_maintenance_only_and_no_content_after_owner_new_record(repo, as_agent):
    put(repo, "owner-rule", source="owner")
    assert "经验摘要该给" not in as_agent("claude").company()
    put(repo, "expired", expires="2026-10-01", source="owner")
    assert "要他判断 0 条，你自己处理 1 条" in as_agent("claude").company()


def test_assigned_scope_timestamps_and_unwritable_atomicity(repo, ext):
    own = put(repo, "allowed", project="alpha", pending=True)
    put(repo, "hidden", project="beta", pending=True)
    put(repo, "company", pending=True)
    base = ext("ext-zhang-claude")
    assert "要他判断 2 条" in base.company()
    assert {r["id"] for r in base.lesson_digest_data()["judgments"]} == {"allowed", "company"}
    owner_before = (repo / f"people/{OWNER}.md").read_bytes()
    base.lesson_digest_data(notify=True)
    assert doc(repo, "ext-zhang").meta["lessons_notified_at"] == "2026-10-10 23:30"
    assert (repo / f"people/{OWNER}.md").read_bytes() == owner_before
    # 看得到公司共用不等于能写；自己的条目也不能提前生效。
    before = (repo / "lessons/allowed.md").read_bytes()
    with pytest.raises(BaseError):
        base.review_lessons(confirm=[own, "company"], done=True)
    assert (repo / "lessons/allowed.md").read_bytes() == before
    assert "lessons_reviewed_at" not in doc(repo, "ext-zhang").meta
    for agent in ("ext-li-bot", "ext-orphan"):
        with pytest.raises(BaseError):
            ext(agent).lesson_digest_data(notify=True)


def test_web_member_save_preserves_timestamps_and_cannot_set_them(repo, as_agent):
    base = as_agent("claude")
    base.review_lessons(done=True)
    base.lesson_digest_data(notify=True)
    owner = CompanyBase(repo, OWNER, allow_owner=True, now=lambda: FIXED_NOW)
    source = owner.doc_source(f"people/{OWNER}.md")
    assert not any(f["key"].startswith("lessons_") for f in source["fields"])
    owner.save_doc(source["rel"], source["version"], {"role": "公司负责人"}, None)
    assert doc(repo, OWNER).meta["lessons_reviewed_at"] == "2026-10-10 23:30"
    assert doc(repo, OWNER).meta["lessons_notified_at"] == "2026-10-10 23:30"
    source = owner.doc_source(source["rel"])
    with pytest.raises(BaseError):
        owner.save_doc(source["rel"], source["version"], {"lessons_reviewed_at": "2020-01-01 00:00"}, None)


def test_full_project_fields_and_sections(repo, as_agent):
    base = as_agent("claude")
    base.update_project_summary("alpha", {"目标": "新目标正文", "边界与注意": "先做本期"},
                                state="已结束", title="新标题", goal="新目标")
    d = doc(repo, "alpha")
    assert d.meta["state"] == "已结束" and d.title == "新标题" and d.meta["goal"] == "新目标"
    assert "# 新标题\n" in d.body and "## 目标\n\n新目标正文" in d.body and "## 边界与注意\n\n先做本期" in d.body
    body_before = d.body
    base.update_project_summary("alpha", state="暂停")
    assert doc(repo, "alpha").meta["state"] == "暂停"
    assert doc(repo, "alpha").body == body_before


@pytest.mark.parametrize("kwargs", [{"state": "未知"}, {"title": "甲" * 81}, {"goal": "甲" * 201},
                                   {"title": "多\n行"}, {"goal": "多\n行"}])
def test_bad_project_fields(repo, as_agent, kwargs):
    with pytest.raises(BaseError):
        as_agent("claude").update_project_summary("alpha", **kwargs)


@pytest.mark.parametrize("kwargs", [{"state": "暂停"}, {"title": "标题"}, {"goal": "目标"},
                                   {"sections": {"目标": "正文"}}, {"sections": {"边界与注意": "正文"}}])
def test_assigned_new_project_fields_denied(repo, ext, kwargs):
    with pytest.raises(BaseError, match="全读"):
        ext("ext-zhang").update_project_summary("alpha", **kwargs)


def test_original_four_sections_keep_exact_legacy_output(repo, as_agent):
    # 固定整个文件作为旧四节写法的回归凭据，而不是只核对新字段。
    before = doc(repo, "alpha")
    sections = {"现状": "完成", "下一步": "继续", "分工": "各司其职", "入口": "仓库"}
    today = FIXED_NOW.date()
    expected_meta = before.meta | {"summary_as_of": today, "updated": today}
    expected = (_dump_front(expected_meta) + "\n# 阿尔法\n\n## 现状\n\n完成\n\n## 下一步\n\n继续\n\n"
                "## 分工\n\n各司其职\n\n## 入口\n\n仓库\n")
    as_agent("claude").update_project_summary("alpha", sections)
    assert (repo / before.rel).read_bytes() == expected.encode("utf-8")
    head = git(repo, "rev-parse", "HEAD")
    as_agent("claude").update_project_summary("alpha", sections)
    assert git(repo, "rev-parse", "HEAD") == head


def test_assigned_four_sections_still_work(repo, ext):
    ext("ext-zhang").update_project_summary("alpha", {"入口": "授权入口"})
    assert "授权入口" in doc(repo, "alpha").body


def test_page_create_edit_preserve_metadata_and_receipt(repo, as_agent):
    base = as_agent("claude")
    created = base._update_page_commit("handbook", "new-page", "正文", title="手册页")
    assert created["commit"] == git(repo, "rev-parse", "--short", "HEAD")
    assert git(repo, "log", "-1", "--format=%s") == "手册：新建「手册页」"
    change(repo, "handbook/new-page.md", sensitivity="restricted", visible_to=["claude"], extra="留着")
    base.update_page("handbook", "new-page", "新正文", title="改名")
    d = doc(repo, "new-page")
    assert d.meta["sensitivity"] == "restricted" and d.meta["visible_to"] == ["claude"] and d.meta["extra"] == "留着"
    assert d.title == "改名" and "新正文" in d.body
    assert git(repo, "log", "-1", "--format=%s") == "手册：改「改名」"
    base.update_page("asset", "new-asset", "资产正文", title="资产")
    base.update_page("asset", "new-asset", "改过资产")
    assert "改过资产" in doc(repo, "new-asset").body


@pytest.mark.parametrize("kind,key,body,title", [
    ("lesson", "valid", "正文", "标题"), ("asset", "x", "正文", "标题"), ("asset", "A-b", "正文", "标题"),
    ("asset", "../page", "正文", "标题"), ("asset", "a" * 42, "正文", "标题"),
    ("asset", "valid", "甲" * 60001, "标题"), ("asset", "valid", "正文", "甲" * 81),
    ("asset", "valid", "正文", "两\n行"), ("asset", "valid", "正文", None)], ids=[
        "kind", "short-id", "uppercase-id", "traversal", "long-id", "long-body", "long-title", "multiline-title", "missing-title"])
def test_page_validation(repo, as_agent, kind, key, body, title):
    with pytest.raises(BaseError):
        as_agent("claude").update_page(kind, key, body, title)


def test_page_permissions_and_limits(repo, ext):
    for agent in ("ext-zhang", "ext-li", "ext-li-bot"):
        with pytest.raises(BaseError):
            CompanyBase(repo, agent, require_remote=True).update_page("asset", "valid", "正文", "标题")
    ext("claude").update_page("asset", "a" * 41, "甲" * 60000, "甲" * 80)
    assert doc(repo, "a" * 41).problem is None
    change(repo, "people/claude.md", remote_enabled=True)
    CompanyBase(repo, "claude", require_remote=True).update_page("asset", "remote-page", "正文", "全读远程")


def test_api_four_methods_wire_and_commits(repo, monkeypatch):
    put(repo, "pending", pending=True)
    save(repo)
    requests = [
        ("lesson_digest", {"notify": True}),
        ("review_lessons", {"confirm": ["pending"], "done": True}),
        ("update_record", {"record_id": "pending", "changes": {"why": "需要理由"}}),
        ("update_page", {"kind": "asset", "page_id": "api-page", "body": "接口正文", "title": "接口页"}),
    ]
    for method, params in requests:
        incoming = io.TextIOWrapper(io.BytesIO(json.dumps({"method": method, "params": params}).encode()), encoding="utf-8")
        outgoing = io.TextIOWrapper(io.BytesIO(), encoding="utf-8")
        with monkeypatch.context() as patch:
            patch.setattr(sys, "stdin", incoming)
            patch.setattr(sys, "stdout", outgoing)
            code = run(["api", "--data", str(repo), "--as", "claude"])
            response = json.loads(outgoing.buffer.getvalue())
        assert code == 0 and response["ok"], response
        assert response["result"]["commit"] == git(repo, "rev-parse", "--short", "HEAD")
    assert len(api.FIELDS) == 21


def test_cli_four_commands(repo, capsys):
    put(repo, "pending", pending=True)
    save(repo)
    common = ["--data", str(repo), "--as", "claude"]
    assert run(["digest", "--notify", *common]) == 0
    assert "本期新记" in capsys.readouterr().out
    assert run(["review", "--confirm", "pending", "--done", *common]) == 0
    assert not doc(repo, "pending").meta.get("pending")
    assert run(["page-update", "handbook", "cli-page", "--title", "命令行", "--text", "正文", *common]) == 0
    assert doc(repo, "cli-page").title == "命令行"
    assert run(["project-summary", "alpha", "--state", "暂停", *common]) == 0
    assert doc(repo, "alpha").meta["state"] == "暂停"


def test_new_write_auth_rechecked_and_git_failure_rolls_back(repo, as_agent, monkeypatch):
    put(repo, "pending", pending=True)
    save(repo)
    before = (repo / "lessons/pending.md").read_bytes()
    human = (repo / f"people/{OWNER}.md").read_bytes()
    original = gitops._git

    def fail_commit(root, args, **kwargs):
        if args[0] == "commit":
            raise OSError("simulated storage failure")
        return original(root, args, **kwargs)

    monkeypatch.setattr(gitops, "_git", fail_commit)
    with pytest.raises(OSError):
        as_agent("claude").review_lessons(confirm=["pending"], done=True)
    assert (repo / "lessons/pending.md").read_bytes() == before
    assert (repo / f"people/{OWNER}.md").read_bytes() == human
    assert git(repo, "status", "--porcelain") == ""


def test_approved_excluded_project_cap_and_signed_notify(repo, as_agent):
    # 主人认可的他已经点过头，和主人原话一样不进“伙伴推断”；推断和没填的照列。
    put(repo, "said-120000", source="owner")
    put(repo, "nodded-120001", source="approved")
    put(repo, "guessed-120002", source="inferred")
    put(repo, "blank-120003")
    base = as_agent("claude")
    data = base.lesson_digest_data()
    inferred = [r["id"] for r in data["judgments"] if r["category"] == "inferred"]
    assert sorted(inferred) == ["blank-120003", "guessed-120002"]
    # 常读超限按项目自己的上限算。
    for i in range(3):
        put(repo, f"alpha-{i}-12000{i}", project="alpha", conclusion=f"第{i}条阿尔法项目的经验，写得稍微长一点" * 2, source="owner")
    change(repo, "projects/alpha/project.md", lessons_brief_chars=10)
    tasks = base.lesson_digest_data()["maintenance"]["brief_overflow"]
    assert [t["project"] for t in tasks] == ["alpha"] and tasks[0]["limit"] == 10
    change(repo, "projects/alpha/project.md", lessons_brief_chars=100000)
    assert not base.lesson_digest_data()["maintenance"]["brief_overflow"]
    # notify 的提交带署名。
    save(repo)
    assert base.lesson_digest_data(notify=True, signed_as="Presenter")["commit"]
    assert git(repo, "log", "-1", "--format=%an") == "claude · Presenter"
