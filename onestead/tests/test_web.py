"""网页界面：不带口令、换了地址、不是从页面发出的请求都要被拒；撤回是新增反向提交，冲突时什么都不动；
首页草稿要主人采用才生效。"""

from .support import OWNER

import io

import pytest
from starlette.testclient import TestClient

from onestead.web.app import build_app
from onestead.web.render import to_html

from .conftest import git
from .support import WEB_BASE as BASE, WEB_PORT as PORT, WEB_POST_HEADERS as POST_HEADERS, WEB_TOKEN as TOKEN


def anonymous(repo, base=BASE):
    return TestClient(build_app(repo, PORT, TOKEN), base_url=base, follow_redirects=False)


def test_without_token_everything_is_refused(repo):
    c = anonymous(repo)
    assert c.get("/api/home").status_code == 401
    assert c.get("/").status_code == 403
    assert c.get("/open?t=wrong").status_code == 403
    assert c.get("/api/home", cookies={f"cb_session_{PORT}": "wrong"}).status_code == 401


def test_other_host_names_are_refused(repo):
    # 防 DNS 重绑定：就算口令对，主机名不是 127.0.0.1:端口 也拒
    c = anonymous(repo, base=f"http://localhost:{PORT}")
    assert c.get(f"/open?t={TOKEN}").status_code == 403
    c = anonymous(repo, base=f"http://evil.example:{PORT}")
    assert c.get("/api/home", cookies={f"cb_session_{PORT}": TOKEN}).status_code == 403


def test_writes_need_origin_and_marker_header(client, as_agent):
    sha = _status_commit(as_agent)
    for path, body in (("/api/revert", {"sha": sha}), ("/api/home/adopt", {}), ("/api/home/discard", {})):
        for headers in ({}, {"X-Requested-By": "company-base"}, {"Origin": "http://evil.example"},
                        {"Origin": BASE}, dict(POST_HEADERS, **{"Sec-Fetch-Site": "cross-site"})):
            r = client.post(path, json=body, headers=headers)
            assert r.status_code == 403, (path, headers)


def test_security_headers(client):
    r = client.get("/api/home")
    assert "frame-ancestors 'none'" in r.headers["content-security-policy"]
    assert "'unsafe-inline'" not in r.headers["content-security-policy"]
    assert r.headers["x-content-type-options"] == "nosniff"


def test_home_default_modules_show_everything_with_reminders(client):
    data = client.get("/api/home").json()
    assert [m["kind"] for m in data["modules"]] == ["today_todos", "apps", "attention", "projects", "skills", "activity_chart"]
    mods = {m["kind"]: m["data"] for m in data["modules"]}
    rows = {r["id"]: r for r in mods["projects"]["rows"]}
    assert rows["secret"]["sensitivity"] == "restricted" and rows["secret"]["access_note"] == "仅限alex"
    assert rows["alpha"]["stale"] is True and rows["alpha"]["lead"]["who"] == "老 Lead"
    kinds = {(x["kind"], x.get("project") or x.get("rel")) for x in mods["attention"]["items"]}
    assert ("过时", "alpha") in kinds and ("标记", "assets/nomark.md") in kinds
    assert data["draft"] is None and data["preview"] is False


def _status_commit(as_agent, text="写错了的一条。"):
    as_agent("codex").update_project_status("alpha", text, signed_as="alex")
    return git(as_agent("codex").root, "rev-parse", "HEAD")


def test_activity_rows_read_like_sentences(client, as_agent):
    _status_commit(as_agent)
    rows = client.get("/api/activity").json()["rows"]
    top = rows[0]
    assert top["who"]["name"] == "alex" and top["who"]["full"] == "Codex · alex"
    assert top["type"] == "进展" and top["what"] == "在「阿尔法」记了一条进展"
    assert top["project"] == {"id": "alpha", "name": "阿尔法"}
    assert top["files"][0]["label"] == "阿尔法 · 进展"
    assert rows[-1]["is_root"] is True


def test_activity_chart_counts_partners_not_owner(client, as_agent):
    sha = _status_commit(as_agent)
    client.post("/api/revert", json={"sha": sha}, headers=POST_HEADERS)  # 主人自己的撤回不算
    chart = next(m for m in client.get("/api/home").json()["modules"] if m["kind"] == "activity_chart")["data"]
    assert chart["days"] == 14 and len(chart["series"]) == 14
    who = {x["name"]: x["count"] for x in chart["by_who"]}
    assert who["alex"] == 1 and "你" not in who
    assert chart["total"] == sum(x["count"] for x in chart["series"])


def test_revert_adds_reverse_commit_by_owner(client, as_agent, repo):
    sha = _status_commit(as_agent)
    r = client.post("/api/revert", json={"sha": sha, "reason": "内容不对"}, headers=POST_HEADERS)
    assert r.status_code == 200 and r.json()["ok"]
    assert not (repo / "projects/alpha/status.md").exists()
    assert git(repo, "log", "-1", "--format=%an") == OWNER
    body = git(repo, "log", "-1", "--format=%B")
    assert f"Reverts: {sha}" in body and "原因：内容不对" in body
    assert git(repo, "rev-list", "--count", "HEAD") == "3"  # 原记录还在
    rows = client.get("/api/activity").json()["rows"]
    assert rows[1]["reverted_by"] == rows[0]["short"]
    assert rows[0]["reverts"] == sha[:7] and rows[0]["type"] == "撤回" and rows[0]["who"]["name"] == "你"
    detail = client.get(f"/api/commit/{rows[0]['short']}").json()
    assert detail["reverts_what"] == "在「阿尔法」记了一条进展"


def test_revert_conflict_changes_nothing_and_names_the_blocker(client, as_agent, repo):
    first = _status_commit(as_agent, "第一条。")
    _status_commit(as_agent, "第二条。")
    head = git(repo, "rev-parse", "HEAD")
    before = io.open(repo / "projects/alpha/status.md", encoding="utf-8").read()
    r = client.post("/api/revert", json={"sha": first}, headers=POST_HEADERS)
    assert r.status_code == 409
    blockers = r.json()["blockers"]
    assert len(blockers) == 1 and blockers[0]["sha"] == head
    assert blockers[0]["who"] == "alex" and blockers[0]["what"] == "在「阿尔法」记了一条进展"
    assert git(repo, "rev-parse", "HEAD") == head
    assert git(repo, "status", "--porcelain") == ""
    assert io.open(repo / "projects/alpha/status.md", encoding="utf-8").read() == before


def test_root_commit_cannot_be_reverted(client, repo):
    root = git(repo, "rev-list", "--max-parents=0", "HEAD")
    r = client.post("/api/revert", json={"sha": root}, headers=POST_HEADERS)
    assert r.status_code == 400 and "第一次提交" in r.json()["error"]


def test_commit_detail_shows_added_text_and_diff(client, as_agent):
    sha = _status_commit(as_agent)
    d = client.get(f"/api/commit/{sha[:7]}").json()
    f = d["diff"][0]
    assert f["status"] == "added" and f["label"] == "阿尔法 · 进展"
    assert any(l["t"] == "add" and "写错了的一条" in l["s"] for l in f["lines"])
    assert "写错了的一条" in d["added_html"]


def test_project_and_doc_detail(client, as_agent):
    _status_commit(as_agent)
    p = client.get("/api/project/alpha").json()
    assert p["status_total"] == 1 and "写错了的一条" in p["status"][0]["html"]
    assert [r["role"] for r in p["roles"]] == ["项目 Lead", "采集"] and p["roles"][0]["key"] is True
    assert client.get("/api/project/nope").status_code == 404
    d = client.get("/api/doc", params={"rel": "assets/sample.md"}).json()
    assert d["sensitivity"] == "restricted" and "暗号琴漆" in d["html"]


def test_search_snippet_is_body_text_not_frontmatter(client):
    hits = client.get("/api/search", params={"q": "紫藤萝"}).json()["hits"]
    assert hits and hits[0]["rel"] == "projects/secret/project.md"
    assert "sensitivity" not in hits[0]["snippet"] and "#" not in hits[0]["snippet"]
    assert client.get("/api/search", params={"q": "家里的事"}).json()["hits"][0]["kind"] == "项目"


MODS = [{"kind": "recent", "options": {"limit": 3}}, {"kind": "attention"}]


def test_draft_needs_owner_to_adopt(client, as_agent, repo):
    as_agent("claude").propose_home(MODS, "动态放前面", signed_as="claude")
    data = client.get("/api/home").json()
    assert [m["kind"] for m in data["modules"]][0] == "today_todos"  # 还没生效
    assert data["draft"]["note"] == "动态放前面" and data["draft"]["by_name"] == "claude"
    preview = client.get("/api/home?draft=1").json()
    assert preview["preview"] is True and [m["kind"] for m in preview["modules"]] == ["recent", "attention"]
    assert len(preview["modules"][0]["data"]["rows"]) <= 3

    version = client.get("/api/home?draft=1").json()["draft"]["version"]
    r = client.post("/api/home/adopt", json={"version": version}, headers=POST_HEADERS)
    assert r.status_code == 200
    assert git(repo, "log", "-1", "--format=%an") == OWNER
    assert (repo / f"home/{OWNER}.md").exists() and not (repo / f"home/{OWNER}-draft.md").exists()
    data = client.get("/api/home").json()
    assert [m["kind"] for m in data["modules"]] == ["recent", "attention"] and data["draft"] is None
    assert client.post("/api/home/adopt", json={}, headers=POST_HEADERS).status_code == 400


def test_discarding_a_draft_keeps_the_current_home(client, as_agent, repo):
    as_agent("claude").propose_home(MODS, "试试", signed_as="claude")
    version = client.get("/api/home?draft=1").json()["draft"]["version"]
    r = client.post("/api/home/discard", json={"version": version}, headers=POST_HEADERS)
    assert r.status_code == 200 and not (repo / f"home/{OWNER}-draft.md").exists()
    assert [m["kind"] for m in client.get("/api/home").json()["modules"]][0] == "today_todos"
    rows = client.get("/api/activity").json()["rows"]
    assert rows[0]["type"] == "首页" and rows[1]["what"] == "给你的首页提了一份草稿：试试"


def test_markdown_rendering_strips_scripts():
    html = to_html('hi <script>alert(1)</script> <img src=x onerror=alert(1)> [x](javascript:alert(1)) '
                   '<a href="https://ok.example" onclick="x()">ok</a>')
    assert "<script" not in html and "onerror" not in html and "javascript:" not in html
    assert "onclick" not in html and "<img" not in html
    assert 'href="https://ok.example"' in html
