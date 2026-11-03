"""Program JSON contract, real subprocesses, and skill unlisting across readers."""

import asyncio
import io
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

import pytest
from mcp.client.session import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client

from onestead import api, gitops, skillscan, skillshare
from onestead.base import CompanyBase
from onestead.cli import run
from onestead.store import AuthRejected, BaseError, Conflict, StorageError, Store, now_taipei
from .support import OWNER, OWNER_NAME, SID, change, git, grant, skill


@pytest.fixture
def wire(repo, monkeypatch):
    def call(method=None, params=None, *, agent="claude", raw=None, root=None):
        request = {"method": method}
        if params is not None:
            request["params"] = params
        source = raw if raw is not None else json.dumps(request, ensure_ascii=False).encode("utf-8")
        incoming = io.TextIOWrapper(io.BytesIO(source), encoding="ascii")
        outgoing = io.TextIOWrapper(io.BytesIO(), encoding="ascii")
        with monkeypatch.context() as patch:
            patch.setattr(sys, "stdin", incoming)
            patch.setattr(sys, "stdout", outgoing)
            patch.delenv("COMPANY_BASE_AGENT", raising=False)
            args = ["api", "--data", str(root or repo)]
            if agent is not None:
                args += ["--as", agent]
            code = run(args)
            output = outgoing.buffer.getvalue()
        assert output.endswith(b"\n") and output.count(b"\n") == 1
        response = json.loads(output.decode("utf-8"))
        assert code == (0 if response["ok"] else 1)
        return response
    return call


def success(wire, method, params=None, **kwargs):
    response = wire(method, params, **kwargs)
    assert response["ok"], response
    return response["result"]


def failure(wire, code, method=None, params=None, **kwargs):
    response = wire(method, params, **kwargs)
    assert response["ok"] is False
    assert response["error"]["code"] == code, response
    assert isinstance(response["error"]["message"], str) and response["error"]["message"]
    return response


def test_read_methods(wire, repo, skill_home):
    change(repo, "projects/alpha/project.md", paths=["/work/example"], roles=[
        {"role": "Lead", "who": "Alex", "since": "2026-10-01", "key": True},
        {"role": "Builder", "who": "Morgan"}])
    skill(skill_home / ".claude" / "skills" / "example", "扫描名")
    assert success(wire, "owner") == {"key": OWNER, "name": OWNER_NAME}
    projects = success(wire, "list_projects")
    project = next(p for p in projects if p["id"] == "alpha")
    assert project == {"id": "alpha", "title": "阿尔法", "state": "进行中", "goal": "测试项目",
        "roles": [{"role": "Lead", "who": "Alex", "since": "2026-10-01", "key": True},
                  {"role": "Builder", "who": "Morgan", "since": None, "key": False}],
        "paths": ["/work/example"], "summary_as_of": "2026-09-01", "sensitivity": "internal", "problem": None}
    members = success(wire, "list_members")
    member = next(m for m in members if m["agent_key"] == "claude")
    assert member == {"agent_key": "claude", "title": "claude", "kind": "agent", "role": None,
                      "owner": OWNER, "archived": False, "can_write": True}
    assert next(m for m in members if m["agent_key"] == OWNER)["owner"] is None
    skills = success(wire, "list_skills")
    assert skills == [{"id": "example", "title": "扫描名", "one_line": "扫描名 的说明", "origin": None,
                      "maintainer": None, "home": None, "updated": None,
                      "installed_in": ["claude", "grok"], "listed": True}]


def test_project_writes_and_git_receipts(wire, repo):
    before = int(git(repo, "rev-list", "--count", "HEAD"))
    created = success(wire, "create_project", {"id": "example", "title": "中文项目", "goal": "验证调用",
                                              "paths": ["/work/example"], "signed_as": "Alex"})
    assert created == {"id": "example", "commit": git(repo, "rev-parse", "--short", "HEAD")}
    assert int(git(repo, "rev-list", "--count", "HEAD")) == before + 1
    doc = next(d for d in Store(repo).load() if d.kind == "project" and d.id == "example")
    assert doc.meta["sensitivity"] == "internal" and doc.meta["state"] == "构想中"
    assert "## 下一步" in doc.body
    appended = success(wire, "append_status", {"project_id": "example", "text": "已完成中文进展", "signed_as": "Alex"})
    assert appended == {"commit": git(repo, "rev-parse", "--short", "HEAD")}
    assert int(git(repo, "rev-list", "--count", "HEAD")) == before + 2
    assert "已完成中文进展" in (repo / "projects/example/status.md").read_text(encoding="utf-8")
    assert git(repo, "log", "-1", "--format=%an") == "claude · Alex"
    message = git(repo, "log", "-1", "--format=%B")
    assert "Agent: claude\nSigned-as: Alex" in message
    failure(wire, "invalid", "create_project", {"id": "example", "title": "重复", "goal": "重复", "signed_as": "Alex"})


def test_project_problem_remains_a_row(wire, repo):
    change(repo, "projects/alpha/project.md", roles="invalid", paths=["relative"], summary_as_of=None)
    row = next(p for p in success(wire, "list_projects") if p["id"] == "alpha")
    assert row["problem"] and row["roles"] == [] and row["paths"] == [] and row["summary_as_of"] is None


@pytest.mark.parametrize("goal", [now_taipei().date(), float("nan")])
def test_unrepresentable_stored_value_still_returns_json(wire, repo, goal):
    change(repo, "projects/alpha/project.md", goal=goal)
    failure(wire, "internal", "list_projects")


def test_skill_writes_and_noop(wire, repo):
    result = success(wire, "register_skill", {"id": "example", "title": "示例技能", "one_line": "完成示例",
        "origin": "自建", "maintainer": "Alex", "home": "/work/example", "signed_as": "Alex"})
    assert result == {"id": "example", "commit": git(repo, "rev-parse", "--short", "HEAD")}
    assert "listed:" not in (repo / "skills/example.md").read_text(encoding="utf-8")
    before = git(repo, "rev-parse", "HEAD")
    params = {"skill_id": "example", "changes": {"one_line": "改过说明"}, "signed_as": "Alex"}
    result = success(wire, "update_skill", params)
    assert result == {"commit": git(repo, "rev-parse", "--short", "HEAD")}
    assert git(repo, "rev-parse", "HEAD") != before
    before = git(repo, "rev-parse", "HEAD")
    assert success(wire, "update_skill", params) == {"commit": None}
    assert git(repo, "rev-parse", "HEAD") == before
    failure(wire, "invalid", "register_skill", {"id": "example", "one_line": "重复", "signed_as": "Alex"})
    row = success(wire, "list_skills")[0]
    assert row["title"] == "示例技能" and row["updated"] == now_taipei().date().isoformat()


@pytest.mark.parametrize("method,params", [
    ("missing", {}), ("owner", {"extra": True}), ("owner", None),
    ("create_project", {"id": "example", "title": "Test", "goal": "Test"}),
    ("append_status", {"project_id": "alpha", "text": "done", "signed_as": "  "}),
    ("register_skill", {"id": "example", "one_line": "Test"}),
    ("update_skill", {"skill_id": "example", "changes": {"listed": False}}),
    ("list_skills", {"include_unlisted": 1}),
    ("create_project", {"id": "example", "title": 4, "goal": "x", "signed_as": "Alex"}),
    ("create_project", {"id": "example", "title": "x", "goal": "x", "paths": [3], "signed_as": "Alex"}),
    ("create_project", {"id": "example", "title": "x", "goal": "x", "state": "历史", "signed_as": "Alex"}),
    ("create_project", {"id": "X", "title": "x", "goal": "x", "signed_as": "Alex"}),
    ("append_status", {"project_id": "alpha", "text": "x" * 4001, "signed_as": "Alex"}),
    ("append_status", {"project_id": "alpha", "text": "api_key=abcdefgh12345", "signed_as": "Alex"}),
    ("append_status", {"project_id": "alpha", "text": "done", "signed_as": "Alex·Morgan"}),
    ("register_skill", {"id": "example", "one_line": "x", "listed": False, "signed_as": "Alex"}),
    ("update_skill", {"skill_id": "example", "changes": {"unknown": True}, "signed_as": "Alex"}),
    ("update_skill", {"skill_id": "example", "changes": {"project": 2}, "signed_as": "Alex"}),
])
def test_invalid_params(wire, method, params):
    if method == "owner" and params is None:
        failure(wire, "invalid", raw=b'{"method":"owner","params":null}')
    else:
        failure(wire, "invalid", method, params)


@pytest.mark.parametrize("raw", [b"{", b"[]", b"null", b"{}", b' {"method":2}', b'\xff',
                                  b'{"method":"owner","other":1}', b'{"method":"owner","params":NaN}'])
def test_bad_json_and_envelopes(wire, raw):
    failure(wire, "invalid", raw=raw)


@pytest.mark.parametrize("agent", [None, "missing", "bad/key", " ", OWNER])
def test_bad_identity(wire, agent):
    failure(wire, "forbidden", "owner", agent=agent)


def test_permissions_and_not_found(wire, ext, repo):
    params = {"project_id": "alpha", "text": "done", "signed_as": "Alex"}
    failure(wire, "forbidden", "append_status", params, agent="reader")
    failure(wire, "forbidden", "list_skills", agent="ext-zhang")
    failure(wire, "not_found", "append_status", params | {"project_id": "missing"})
    failure(wire, "not_found", "append_status", params | {"project_id": "beta"}, agent="ext-zhang")
    failure(wire, "not_found", "update_skill", {"skill_id": "missing", "changes": {"listed": False}, "signed_as": "Alex"})
    projects = success(wire, "list_projects", agent="ext-zhang")
    assert "beta" not in {p["id"] for p in projects}
    change(repo, "people/claude.md", signers=["Morgan"])
    failure(wire, "forbidden", "append_status", params)
    change(repo, "people/claude.md", archived=True)
    failure(wire, "forbidden", "owner")


def test_invalid_member_and_remote_full_skills(wire, repo):
    change(repo, "people/claude.md", access_mode="typo")
    failure(wire, "forbidden", "owner")
    change(repo, "people/claude.md", access_mode="full")
    with pytest.raises(BaseError):
        api.dispatch(CompanyBase(repo, "claude", require_remote=True), "list_skills", {})
    change(repo, "people/claude.md", remote_enabled=True)
    with pytest.raises(BaseError):
        api.dispatch(CompanyBase(repo, "claude", require_remote=True), "list_skills", {})


def test_conflict_real_lock_timeout(wire, repo, monkeypatch):
    lock = repo / ".git" / gitops.LOCK_NAME
    lock.write_text("test", encoding="utf-8")
    monkeypatch.setattr(gitops, "LOCK_WAIT_SECONDS", -1)
    failure(wire, "conflict", "append_status", {"project_id": "alpha", "text": "done", "signed_as": "Alex"})
    assert not (repo / "projects/alpha/status.md").exists()
    lock.unlink()


@pytest.mark.parametrize("error,code", [(StorageError("private storage detail"), "internal"),
    (RuntimeError("unexpected private detail"), "internal"), (AuthRejected("revoked"), "forbidden"),
    (Conflict("changed"), "conflict")])
def test_storage_and_auth_errors(wire, repo, monkeypatch, error, code):
    before = git(repo, "rev-parse", "HEAD")
    real_git = gitops._git
    def fail_commit(root, args, **kwargs):
        if args[0] == "commit":
            raise error
        return real_git(root, args, **kwargs)
    monkeypatch.setattr(gitops, "_git", fail_commit)
    response = failure(wire, code, "append_status", {"project_id": "alpha", "text": "done", "signed_as": "Alex"})
    if code == "internal":
        assert "private" not in response["error"]["message"]
    assert git(repo, "rev-parse", "HEAD") == before
    assert not (repo / "projects/alpha/status.md").exists()


def test_missing_data_and_usage(wire, repo, usage_log):
    failure(wire, "internal", "owner", root=repo / "missing")
    success(wire, "owner")
    failure(wire, "invalid", "unrecognized")
    calls = [json.loads(line) for line in usage_log.read_text(encoding="utf-8").splitlines()]
    assert [(r["agent"], r["tool"], r["via"]) for r in calls] == [
        ("claude", "api:owner", "api"), ("claude", "api:owner", "api"), ("claude", "api:unrecognized", "api")]


def test_subprocess_utf8_bom_and_identity_sources(repo, monkeypatch):
    monkeypatch.setenv("ONESTEAD_DATA", str(repo))
    monkeypatch.setenv("ONESTEAD_AGENT", "reader")
    monkeypatch.setenv("PYTHONIOENCODING", "ascii")
    params = {"method": "create_project", "params": {"id": "example", "title": "中文项目", "goal": "程序调用", "signed_as": "Alex"}}
    request = b"\xef\xbb\xbf" + json.dumps(params, ensure_ascii=False).encode("utf-8")
    process = subprocess.run([sys.executable, "-m", "onestead.cli", "--as", "claude", "api"],
                             input=request, capture_output=True, timeout=30)
    assert process.returncode == 0, process.stderr
    read = subprocess.run([sys.executable, "-m", "onestead.cli", "api"],
                          input=b'{"method":"list_projects"}', capture_output=True, timeout=30)
    assert read.returncode == 0 and read.stdout.count(b"\n") == 1
    assert "中文项目".encode("utf-8") in read.stdout
    assert json.loads(read.stdout)["ok"] is True
    bad = subprocess.run([sys.executable, "-m", "onestead.cli", "api"], input=b"{", capture_output=True, timeout=30)
    assert bad.returncode == 1 and bad.stdout.count(b"\n") == 1
    assert json.loads(bad.stdout)["error"]["code"] == "invalid"


async def mcp_listing(repo, include_unlisted=False):
    params = StdioServerParameters(command=sys.executable, args=["-m", "onestead.server"],
                                  env=dict(os.environ, ONESTEAD_DATA=str(repo), ONESTEAD_AGENT="claude"))
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            result = await session.call_tool("list_skills", {"include_unlisted": include_unlisted})
            assert not result.is_error
            return "".join(item.text for item in result.content if item.type == "text")


def test_unlisting_all_local_surfaces(wire, repo, as_agent, skill_home, client):
    broken = skill_home / ".claude" / "skills" / "example"
    broken.mkdir(parents=True)
    success(wire, "register_skill", {"id": "example", "one_line": "示例", "signed_as": "Alex"})
    base = as_agent("claude")
    assert "`example`" in base.list_skills(only_alerts=True)
    success(wire, "update_skill", {"skill_id": "example", "changes": {"listed": False}, "signed_as": "Alex"})
    assert "撤下" in git(repo, "log", "-1", "--format=%s")
    assert success(wire, "list_skills") == []
    assert success(wire, "list_skills", {"include_unlisted": True})[0]["listed"] is False
    assert "`example`" not in base.list_skills()
    assert "已撤下" in base.list_skills(include_unlisted=True)
    assert "共 0 个；0 个各家版本不一致，0 个有问题" in base.list_skills(include_unlisted=True)
    assert "`example`" not in base.list_skills(only_alerts=True, include_unlisted=True)
    assert "已撤下" in base.get_skill("example")
    row = skillscan.summarize(skillscan.cached_scan(), {"example": {"listed": False}})[0]
    assert row["alert"] is None
    assert client.get("/api/skills").json()["skills"] == []
    assert client.get("/api/skills?include_unlisted=1").json()["skills"][0]["listed"] is False
    modules = client.get("/api/home").json()["modules"]
    assert next(m["data"] for m in modules if m["kind"] == "skills")["broken"] == 0
    assert "`example`" not in asyncio.run(mcp_listing(repo))
    assert "已撤下" in asyncio.run(mcp_listing(repo, True))
    for extra, contains in (([], False), (["--all"], True), (["--all", "--alerts"], False)):
        proc = subprocess.run([sys.executable, "-m", "onestead.cli", "skills", "--data", str(repo), "--as", "claude", *extra],
                              capture_output=True, timeout=30)
        assert proc.returncode == 0, proc.stderr
        assert ("`example`" in proc.stdout.decode("utf-8")) == contains
    proc = subprocess.run([sys.executable, "-m", "onestead.cli", "skill-update", "example", "--data", str(repo),
                          "--as", "claude", "--json", '{"listed":true}'], capture_output=True, timeout=30)
    assert proc.returncode == 0, proc.stderr
    assert "重新上架" in git(repo, "log", "-1", "--format=%s")
    assert success(wire, "list_skills")[0]["listed"] is True


@pytest.mark.parametrize("listed", [None, 0, 1, "false", [], {}])
def test_listed_rejects_non_boolean(wire, as_agent, listed):
    base = as_agent("claude")
    base.register_skill({"id": "example", "one_line": "示例"})
    failure(wire, "invalid", "update_skill", {"skill_id": "example", "changes": {"listed": listed}, "signed_as": "Alex"})
    with pytest.raises(BaseError):
        base.update_skill("example", {"listed": listed})


def test_unlisted_shared_skill_is_hidden(shared, repo, ext):
    grant(repo)
    base = ext("ext-zhang")
    assert base.shared_skill(SID)["id"] == SID
    ext("claude").update_skill(SID, {"listed": False})
    assert base.shared_skill_list() == []
    assert SID not in base.list_skills(include_unlisted=True)
    with pytest.raises(BaseError):
        base.shared_skill(SID)
    with pytest.raises(BaseError):
        base.download_skill(SID)
    change(repo, "people/claude.md", remote_enabled=True)
    remote = CompanyBase(repo, "claude", require_remote=True)
    assert remote.shared_skill_list() == []
    ext("claude").update_skill(SID, {"listed": True})
    assert base.shared_skill(SID)["id"] == SID


def test_unlisting_during_download_rechecks_the_note(shared, repo, ext, monkeypatch):
    grant(repo)
    original = skillshare.zip_package
    def unlist_while_packing(*args, **kwargs):
        content = original(*args, **kwargs)
        ext("claude").update_skill(SID, {"listed": False})
        return content
    monkeypatch.setattr(skillshare, "zip_package", unlist_while_packing)
    with pytest.raises(BaseError):
        ext("ext-zhang").download_skill(SID)


def test_demo_subprocess_timings(tmp_path, monkeypatch):
    root = tmp_path / "demo"
    shutil.copytree(Path(__file__).parents[1] / "examples/demo-company", root)
    git(tmp_path, "init", "-q", "-b", "main", str(root))
    monkeypatch.setenv("ONESTEAD_DATA", str(root))
    monkeypatch.setenv("ONESTEAD_OWNER", "alex")
    monkeypatch.setenv("ONESTEAD_AGENT", "codex")
    for method in ("owner", "list_projects", "list_skills"):
        start = time.perf_counter()
        proc = subprocess.run([sys.executable, "-m", "onestead.cli", "api"],
            input=json.dumps({"method": method}).encode("utf-8"), capture_output=True, timeout=30)
        elapsed = time.perf_counter() - start
        assert proc.returncode == 0, proc.stderr
        assert json.loads(proc.stdout)["ok"] is True
        print(f"demo api:{method}: {elapsed:.3f}s")
