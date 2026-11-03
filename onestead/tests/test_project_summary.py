"""摘要日之后有新进展时提示；能记进展的身份可以改正文四节。"""

from .support import OWNER_NAME

import io
import json
from pathlib import Path

import pytest

from onestead.base import CompanyBase
from onestead.server import INSTRUCTIONS
from onestead.store import BaseError, Store
from onestead.web import present

from .conftest import FIXED_NOW, git
from .support import _remote

JS = Path(__file__).parents[1] / "src" / "onestead" / "web" / "static" / "app.js"
LEAK = "LEAKTOKEN只应出现在进展正文"
STATUS_OLD = (
    "# 阿尔法 · 进展\n\n"
    "## 2026-08-01 09:00 · codex\n\n更早。\n\n"
    "## 2026-09-01 23:30 · codex\n\n和摘要同一天。\n\n"
    "## 补记没有日期\n\n这条没有时间。\n"
)
STATUS_NEW = (
    STATUS_OLD + "\n"
    "## 2026-10-11 09:00 · codex\n\n" + LEAK + "\n\n"
    "## 2026-10-12 09:00 · codex\n\n第二条新进展。\n"
)
DATED_BODY = (
    "# 阿尔法\n\n"
    "## 目标\n\n守住这一句。\n\n"
    "## 现状（摘要，截至 2026-09-01，以阿尔法台账为准）\n\n"
    "旧现状。正文里的截至 2026-09-01 不该被改掉，除非整节换掉。\n\n"
    "## 边界与注意\n\n别外传。\n"
)


def write_status(repo, text, project_id="alpha"):
    path = repo / "projects" / project_id / "status.md"
    path.parent.mkdir(parents=True, exist_ok=True)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def set_body(repo, body, project_id="alpha"):
    path = repo / "projects" / project_id / "project.md"
    raw = io.open(path, encoding="utf-8").read()
    head, sep, _rest = raw.partition("\n---\n")
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(head + sep + "\n" + body.lstrip("\n"))


def headings(body):
    return [line for line in body.splitlines() if line.startswith("## ")]


def project_rows(payload):
    if "rows" in payload:
        return {row["id"]: row for row in payload["rows"]}
    return {row["id"]: row for row in next(m["data"]["rows"] for m in payload["modules"] if m["kind"] == "projects")}


def test_hint_when_status_is_newer_and_absent_otherwise(repo, as_agent, client):
    write_status(repo, STATUS_OLD)
    quiet = as_agent("codex").get_project("alpha").splitlines()[0]
    assert "新进展" not in quiet and "截至 2026-09-01" in quiet
    assert client.get("/api/project/alpha").json()["newer_status"] == 0
    assert project_rows(client.get("/api/home").json())["alpha"]["newer_status"] == 0
    assert project_rows(client.get("/api/projects").json())["alpha"]["newer_status"] == 0

    write_status(repo, STATUS_NEW)
    line = as_agent("codex").get_project("alpha").splitlines()[0]
    assert "之后有 2 条新进展，以最近进展为准" in line
    assert "可能已过时" in line and r"C:\x\STATUS.md" in line and LEAK not in line
    page = client.get("/api/project/alpha").json()
    assert page["newer_status"] == 2 and page["status_total"] == 5
    home = project_rows(client.get("/api/home").json())["alpha"]
    listed = project_rows(client.get("/api/projects").json())["alpha"]
    assert home["newer_status"] == 2 and listed["newer_status"] == 2
    js = JS.read_text(encoding="utf-8")
    assert 'uiText("app.104", [p.newer_status])' in js
    assert 'class: "note faint"' in js and 'text: uiText("app.084")' in js
    texts = json.loads(JS.with_name("i18n-zh.js").read_text(encoding="utf-8").split(" = ", 1)[1].strip().removesuffix(";"))
    assert texts["app.104"] == "之后有 {0} 条新进展，以最近进展为准"
    assert texts["app.084"] == "有新进展"


def test_missing_summary_date_does_not_grow_a_newer_hint(repo, as_agent, client):
    raw = io.open(repo / "projects/alpha/project.md", encoding="utf-8").read()
    raw = raw.replace("summary_as_of: 2026-09-01\n", "")
    with io.open(repo / "projects/alpha/project.md", "w", encoding="utf-8", newline="\n") as f:
        f.write(raw)
    write_status(repo, STATUS_NEW)
    line = as_agent("codex").get_project("alpha").splitlines()[0]
    assert "没有标日期" in line and "新进展" not in line
    page = client.get("/api/project/alpha").json()
    assert page["summary_as_of"] is None and page["newer_status"] == 0


def test_colleague_sees_the_count_and_not_the_extra_text(ext, repo):
    write_status(repo, STATUS_NEW)
    remote = CompanyBase(repo, "ext-zhang", require_remote=True, now=lambda: FIXED_NOW)
    line = remote.get_project("alpha").splitlines()[0]
    assert "之后有 2 条新进展，以最近进展为准" in line
    assert LEAK not in line and "STATUS.md" not in line and "C:\\" not in line
    assert "阿尔法台账" in line
    docs = Store(repo).load()
    viewer_docs = [d for d in docs if d.kind == "project" and d.id == "alpha"][0]
    from onestead.store import make_viewer
    row = present.project(viewer_docs, make_viewer(docs, "ext-zhang", remote=True), docs, [], FIXED_NOW.date())
    dumped = json.dumps(row, ensure_ascii=False)
    assert row["newer_status"] == 2 and LEAK not in dumped and "STATUS.md" not in dumped
    assert "path" not in (row["authority"] or {})

    origin, _headers = _remote(repo)
    try:
        page = origin.get("/api/project/alpha").json()
        assert page["newer_status"] == 2
        assert "path" not in (page.get("authority") or {})
        kept = {k: page[k] for k in page if k not in ("status", "body_html", "apps")}
        blob = json.dumps(kept, ensure_ascii=False)
        assert LEAK not in blob and "C:\\" not in blob and "STATUS.md" not in blob
        home = project_rows(origin.get("/api/home").json())["alpha"]
        assert home["newer_status"] == 2 and LEAK not in json.dumps(home, ensure_ascii=False)
    finally:
        origin.close()


def test_summary_updates_four_sections_dates_and_author(repo, as_agent):
    set_body(repo, DATED_BODY)
    out = as_agent("codex").update_project_summary("alpha", {
        "下一步": "去做下一件。",
        "现状": "已经做完第一步。",
        "分工": "alex写代码。",
        "入口": "见下面。\n\n### 代码\n\n在本仓库。",
    }, signed_as="alex")
    assert "已更新项目 `alpha`" in out
    doc = next(d for d in Store(repo).load() if d.kind == "project" and d.id == "alpha")
    assert doc.problem is None
    assert doc.meta["summary_as_of"].isoformat() == "2026-10-10"
    assert doc.meta["updated"].isoformat() == "2026-10-10"
    assert doc.meta["goal"] == "测试项目" and doc.meta["state"] == "进行中"
    assert doc.meta["roles"][0]["role"] == "项目 Lead"
    assert headings(doc.body) == [
        "## 目标",
        "## 现状（摘要，截至 2026-10-10，以阿尔法台账为准）",
        "## 下一步",
        "## 分工",
        "## 入口",
        "## 边界与注意",
    ]
    assert "守住这一句。" in doc.body and "别外传。" in doc.body
    assert "已经做完第一步。" in doc.body and "旧现状。" not in doc.body
    assert "### 代码" in doc.body and "在本仓库。" in doc.body
    assert git(repo, "log", "-1", "--format=%s") == "项目：更新「阿尔法」的现状、下一步、分工、入口"
    assert git(repo, "log", "-1", "--format=%an <%ae>") == "codex · alex <codex@agents.company-base.local>"
    message = git(repo, "log", "-1", "--format=%B")
    assert "Signed-as: alex" in message and "Agent: codex" in message
    assert "update_project_summary" in message
    assert git(repo, "status", "--porcelain") == ""


def test_other_sections_do_not_move_summary_date(repo, as_agent):
    set_body(repo, DATED_BODY)
    as_agent("claude").update_project_summary("alpha", {"下一步": "只改下一步。"})
    doc = next(d for d in Store(repo).load() if d.kind == "project" and d.id == "alpha")
    assert doc.problem is None
    assert doc.meta["summary_as_of"].isoformat() == "2026-09-01"
    assert doc.meta["updated"].isoformat() == "2026-10-10"
    assert "## 现状（摘要，截至 2026-09-01，以阿尔法台账为准）" in doc.body
    assert "正文里的截至 2026-09-01" in doc.body and "只改下一步。" in doc.body
    assert git(repo, "log", "-1", "--format=%s") == "项目：更新「阿尔法」的下一步"
    assert git(repo, "log", "-1", "--format=%an") == "claude"


def test_summary_rejects_forbidden_sections_headings_and_secrets(repo, as_agent):
    path = repo / "projects/alpha/project.md"
    before = path.read_bytes()
    head = git(repo, "rev-parse", "HEAD")
    codex = as_agent("codex")
    samples = [
        {"目标": "不许改目标", "现状": "这条也不该写上"},
        {"状态": "暂停"},
        {"标题": "新名字"},
        {"敏感级别": "public"},
        {"roles": "[]"},
        {"visible_to": "[]"},
        {"入口": "可以\n\n## 偷偷加一节\n\n不行"},
        {"分工": "token=abcdefghij"},
        {"现状": "甲" * 4001},
        [],
    ]
    for sections in samples:
        with pytest.raises(BaseError, match=f"这些请找 {OWNER_NAME} 在网页上改|## |像密钥|太长|要写要改的节"):
            codex.update_project_summary("alpha", sections)
        assert path.read_bytes() == before and git(repo, "rev-parse", "HEAD") == head
    out = codex.update_project_summary("alpha", {"入口": "甲" * 4000})
    assert "已更新" in out
    doc = next(d for d in Store(repo).load() if d.id == "alpha" and d.kind == "project")
    assert doc.problem is None and "甲" * 4000 in doc.body


def test_summary_write_follows_status_permission(ext, repo, as_agent):
    with pytest.raises(BaseError, match="不能写入"):
        as_agent("reader").update_project_summary("alpha", {"入口": "只读"})
    with pytest.raises(BaseError, match="不能写入"):
        CompanyBase(repo, "ext-li", require_remote=True).update_project_summary("alpha", {"入口": "远程只读"})
    with pytest.raises(BaseError, match="找不到"):
        ext("ext-zhang").update_project_summary("beta", {"现状": "没授权"})
    beta = io.open(repo / "projects/beta/project.md", encoding="utf-8").read()
    beta = beta.replace("sensitivity: internal", "sensitivity: public")
    with io.open(repo / "projects/beta/project.md", "w", encoding="utf-8", newline="\n") as f:
        f.write(beta)
    before = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError, match="不在你的授权里"):
        ext("ext-zhang").update_project_summary("beta", {"现状": "公开也不给写"})
    assert git(repo, "rev-parse", "HEAD") == before

    out = ext("ext-zhang").update_project_summary("alpha", {"分工": "同事改分工。"}, signed_as="张三")
    assert "已更新" in out
    assert git(repo, "log", "-1", "--format=%an") == "ext-zhang · 张三"
    assert "Signed-as: 张三" in git(repo, "log", "-1", "--format=%B")
    doc = next(d for d in Store(repo).load() if d.kind == "project" and d.id == "alpha")
    assert doc.problem is None and "同事改分工。" in doc.body
    assert "update_project_summary" in ext("ext-zhang").company()
    assert "update_project_summary" in as_agent("codex").company()
    from onestead.i18n import text
    assert "update_project_summary" in text(INSTRUCTIONS, title="Onestead", owner_name=OWNER_NAME)


def test_project_summary_cli_reads_text_file(repo, tmp_path):
    from onestead.cli import run

    path = tmp_path / "sections.json"
    path.write_text(json.dumps({"入口": "从文件改的入口。"}, ensure_ascii=False), encoding="utf-8")
    assert run(["project-summary", "alpha", "--text-file", str(path),
                "--data", str(repo), "--as", "codex", "--signed-as", "alex"]) == 0
    doc = next(d for d in Store(repo).load() if d.kind == "project" and d.id == "alpha")
    assert doc.problem is None and "从文件改的入口。" in doc.body
    assert git(repo, "log", "-1", "--format=%an") == "codex · alex"
