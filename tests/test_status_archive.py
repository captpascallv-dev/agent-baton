"""进展按月归档：status.md 只留最近的；搜索和网页仍能找到旧进展；同一项目里同一身份和署名连着记会被提醒。"""

from .support import OWNER_NAME

import datetime as dt

from onestead.base import STATUS_ARCHIVE_AT, STATUS_KEEP, CompanyBase
from onestead.handover import _status_entries, pending_handovers
from onestead.store import Store

from .conftest import FIXED_NOW
from .test_project_summary import write_status

APPLY = f"申请接任：「采集」由「老采集」换成「新采集」，待 {OWNER_NAME} 批准。"


def entries(count: int, start=dt.datetime(2026, 8, 20, 9, 0)) -> str:
    """第一条是没人处理的接任申请，其余每天一条，从八月跨到九月、十月。"""
    parts = ["# 阿尔法 · 进展\n\n只往后追加，不改旧条目。"]
    for i in range(count):
        when = start + dt.timedelta(days=i)
        text = APPLY if i == 0 else f"第 {i} 条：暗号旧进展{i}。"
        parts.append(f"## {when:%Y-%m-%d %H:%M} · codex · 线{i}\n\n{text}")
    return "\n\n".join(parts) + "\n"


def at(minute_offset: int):
    return lambda: FIXED_NOW + dt.timedelta(minutes=minute_offset)


def test_archive_keeps_recent_entries_and_open_applications(repo, as_agent):
    write_status(repo, entries(STATUS_ARCHIVE_AT))
    as_agent("codex").update_project_status("alpha", "交付了第一版。", signed_as="归档测试")
    status = (repo / "projects/alpha/status.md").read_text(encoding="utf-8")
    kept = _status_entries(status)
    assert status.startswith("# 阿尔法 · 进展\n")
    assert len(kept) == STATUS_KEEP + 1 and APPLY in kept[0] and "交付了第一版。" in kept[-1]
    august = (repo / "projects/alpha/status/2026-08.md").read_text(encoding="utf-8")
    september = (repo / "projects/alpha/status/2026-09.md").read_text(encoding="utf-8")
    assert august.startswith("# 阿尔法 · 进展归档 2026-08\n") and APPLY not in august
    moved = _status_entries(august) + _status_entries(september)
    assert len(moved) + len(kept) == STATUS_ARCHIVE_AT + 1
    assert [e.split("\n")[0] for e in moved] == sorted(e.split("\n")[0] for e in moved)
    docs = Store(repo).load()
    assert pending_handovers(docs, {d.id: d for d in docs if d.kind == "project"})

    # 下一次只追加，不到门槛不再动归档文件
    as_agent("claude").update_project_status("alpha", "又一条。")
    assert (repo / "projects/alpha/status/2026-08.md").read_text(encoding="utf-8") == august


def test_archived_progress_still_found_and_mentioned(repo, as_agent, client):
    write_status(repo, entries(STATUS_ARCHIVE_AT))
    base = as_agent("codex")
    base.update_project_status("alpha", "交付了第一版。", signed_as="归档测试")
    page = base.get_project("alpha")
    assert "按月归档" in page and "2026-08、2026-09" in page and "暗号旧进展1。" not in page
    found = base.search("暗号旧进展1。")
    assert "projects/alpha/status/2026-08.md" in found
    hits = client.get("/api/search", params={"q": "暗号旧进展1。"}).json()["hits"]
    assert any(h["rel"] == "projects/alpha/status/2026-08.md" for h in hits)
    doc = client.get("/api/doc", params={"rel": "projects/alpha/status/2026-08.md"})
    assert doc.status_code == 200 and "暗号旧进展1。" in doc.text


def test_hint_counts_same_project_identity_and_signature(repo):
    def write(key, signed, minute, text="交付了。"):
        return CompanyBase(repo, key, now=at(minute)).update_project_status("alpha", text, signed_as=signed)

    assert "提醒" not in write("claude", "甲", 0)
    assert "提醒" not in write("claude", "乙", 10)  # 同身份不同署名
    assert "提醒" not in write("claude", "甲", 20)  # 上一条是乙
    assert "小时内已经在这个项目记过" in write("claude", "甲", 30)
    assert "提醒" not in write("claude", "甲", 30 + 121)
    assert "超过 800 字" in write("codex", "长文", 400, "长" * 801)
    assert "提醒" not in CompanyBase(repo, "codex", now=at(410)).update_project_status("alpha", "短。", signed_as="另一位")


def test_forged_handover_text_does_not_archive_an_open_application(ext, repo):
    write_status(repo, entries(STATUS_ARCHIVE_AT))
    ext("ext-zhang").update_project_status("alpha", "交接：「采集」由「老采集」换成「新采集」。")
    kept = _status_entries((repo / "projects/alpha/status.md").read_text(encoding="utf-8"))
    assert APPLY in kept[0] and len(kept) == STATUS_KEEP + 1
    docs = Store(repo).load()
    assert pending_handovers(docs, {d.id: d for d in docs if d.kind == "project"})
