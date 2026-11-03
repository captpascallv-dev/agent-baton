"""以当前快照过滤 git 动态：混合提交不带历史正文，所有计数共用安全集合。"""

from .support import OWNER

import pytest

from onestead.web import history

from .conftest import git
from .support import WEB_POST_HEADERS as POST_HEADERS, mark

MARKERS = ["原始标题秘密", "作者署名秘密", "提交正文秘密", "删除行秘密", "乙待办正文秘密"]


@pytest.fixture
def mixed(business, repo):
    status = repo / "projects/alpha/status.md"
    old = status.read_text(encoding="utf-8")
    status.write_text(old + "\n删除行秘密\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "预备删除行")
    status.write_text(old + "\n## 新进展\n\n正常可见进展。\n", encoding="utf-8")
    hidden = repo / "projects/beta/project.md"
    hidden.write_text(hidden.read_text(encoding="utf-8") + "\n隐藏项目内容秘密\n", encoding="utf-8")
    other = repo / f"todos/{business['other_tid']}.md"
    other.write_text(other.read_text(encoding="utf-8") + "\n乙待办正文秘密\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "-c", "user.name=ext-zhang · 作者署名秘密", "commit", "-q", "-m", "原始标题秘密\n\n提交正文秘密")
    return business | {"sha": git(repo, "rev-parse", "HEAD")}


def assert_safe(text):
    for marker in MARKERS:
        assert marker not in text, marker


def test_mixed_commit_activity_detail_home_and_project_counts(mixed):
    c, sha = mixed["client"], mixed["sha"]
    for path in ("/api/activity", f"/api/commit/{sha}", f"/api/commit/{sha[:7]}", "/api/home?draft=1", "/api/projects"):
        response = c.get(path)
        assert response.status_code == 200, response.text
        assert_safe(response.text)
        assert "隐藏项目内容秘密" not in response.text
    row = c.get(f"/api/commit/{sha}").json()
    assert set(row) == {"sha", "when", "safe_actor", "type", "visible_objects", "what", "allowed_actions"}
    assert row["safe_actor"] == {"key": "ext-zhang", "name": "你"}
    assert row["what"] == "更新了《阿尔法》的进展"
    assert len(row["visible_objects"]) == 1
    assert row["allowed_actions"] == {"view_diff": False, "revert": False}
    chart = next(m["data"] for m in c.get("/api/home?draft=1").json()["modules"] if m["kind"] == "activity_chart")
    assert chart["total"] == sum(d["count"] for d in chart["series"])
    assert "最近 500 次改动里你能看到的" in c.get("/api/activity").text


def only_other_commit(repo, tid):
    path = repo / f"todos/{tid}.md"
    path.write_text(path.read_text(encoding="utf-8") + "\n只有乙知道的秘密\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "-c", "user.name=ext-zhang-claude", "commit", "-q", "-m", "原始标题秘密")
    return git(repo, "rev-parse", "HEAD")


def test_invisible_commit_disappears_without_counting(mixed, repo):
    c = mixed["client"]
    before_activity = c.get("/api/activity").json()
    before_projects = c.get("/api/projects").json()
    before_home = c.get("/api/home?draft=1").json()
    before_agents = c.get("/api/agents").json()
    sha = only_other_commit(repo, mixed["other_tid"])
    assert c.get("/api/activity").json() == before_activity
    assert c.get("/api/projects").json() == before_projects
    assert c.get("/api/agents").json() == before_agents
    after_home = c.get("/api/home?draft=1").json()
    assert before_home["modules"] == after_home["modules"]
    for token in (sha, sha[:7]):
        response = c.get(f"/api/commit/{token}")
        assert response.status_code == 404
        assert_safe(response.text)


def test_project_acl_tightening_removes_history_immediately(mixed, repo):
    c, sha = mixed["client"], mixed["sha"]
    assert c.get(f"/api/commit/{sha}").status_code == 200
    mark(repo, "projects/alpha/project.md", sensitivity="restricted", visible_to=["codex"])
    # 不需要新提交：当前授权马上影响旧动态，不读历史 ACL 补回旧内容。
    assert c.get(f"/api/commit/{sha}").status_code == 404
    assert sha not in c.get("/api/activity").text
    assert "阿尔法" not in c.get("/api/projects").text
    assert "阿尔法" not in c.get("/api/home?draft=1").text


def test_owner_mixed_commit_is_summary_and_cannot_revert(mixed, repo, client):  # noqa: F811
    response = client.get(f"/api/commit/{mixed['sha']}")
    assert response.status_code == 200
    assert_safe(response.text)
    row = response.json()
    assert row["can_revert"] is False and row["allowed_actions"]["revert"] is False
    assert "diff" not in row and "subject" not in row and "body" not in row
    before = git(repo, "rev-parse", "HEAD")
    response = client.post("/api/revert", json={"sha": mixed["sha"]}, headers=POST_HEADERS)
    assert response.status_code == 403 and git(repo, "rev-parse", "HEAD") == before
    hidden = only_other_commit(repo, mixed["other_tid"])
    assert hidden not in client.get("/api/activity").text
    assert client.get(f"/api/commit/{hidden}").status_code == 404
    assert client.post("/api/revert", json={"sha": hidden}, headers=POST_HEADERS).status_code == 404


def test_owner_cannot_audit_mismatched_owner_in_own_home_path(business, repo, client):  # noqa: F811
    from onestead.textutil import _dump_front

    # 先保存夹具已有的成员授权变化，目标提交只能包含这个坏首页。
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "测试准备")
    path = repo / f"home/{OWNER}.md"
    path.write_text(_dump_front({"id": OWNER, "type": "home", "owner": "ext-li", "state": "active",
                                "sensitivity": "internal", "modules": [{"kind": "recent"}]}) + "\n乙待办正文秘密\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "提交正文秘密")
    sha = git(repo, "rev-parse", "HEAD")
    assert client.get(f"/api/commit/{sha}").status_code == 404
    assert sha not in client.get("/api/activity").text


@pytest.mark.parametrize("sha", ["a", "abcdef", "HEAD", "HEAD~1", "--all", "a" * 41, "A" * 40,
                                  "abcdefg", "a" * 7 + ";git"])
def test_commit_identifier_rejects_git_expressions(mixed, monkeypatch, sha):
    monkeypatch.setattr(history, "commit_diff", lambda *args: pytest.fail("同事不可读取 diff"))
    response = mixed["client"].get(f"/api/commit/{sha}")
    assert response.status_code == 404
    assert_safe(response.text)


def test_ambiguous_short_sha_is_not_first_match(mixed, monkeypatch):
    original = history.list_commits
    commits = original(mixed["a"].root, 500)
    a, b = dict(commits[0]), dict(commits[0])
    a["sha"], b["sha"] = "abcdef0" + "1" * 33, "abcdef0" + "2" * 33
    monkeypatch.setattr(history, "list_commits", lambda *args: [a, b])
    c = mixed["client"]
    assert c.get("/api/commit/abcdef0").status_code == 404
    assert c.get(f"/api/commit/{a['sha']}").status_code == 200


def test_unknown_author_is_member_not_raw_git_name(mixed, repo):
    p = repo / "projects/alpha/status.md"
    p.write_text(p.read_text(encoding="utf-8") + "\n## 正常更新\n内容\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "-c", "user.name=未知秘密作者", "commit", "-q", "-m", "原始标题秘密")
    row = mixed["client"].get("/api/activity").json()["rows"][0]
    assert row["safe_actor"] == {"key": None, "name": "成员"}
    assert "未知秘密作者" not in str(row)


def test_scan_limit_is_500_and_not_client_controlled(mixed, monkeypatch):
    original = history.list_commits
    limits = []
    def limited(root, limit):
        limits.append(limit)
        return original(root, limit)
    monkeypatch.setattr(history, "list_commits", limited)
    mixed["client"].get("/api/activity?limit=999999")
    assert limits == [500]
