"""Skill 广场：现场扫各家目录；共用目录和联接算同一份；落后、缺 SKILL.md、联接断了都能看出来；说明登记是带作者的提交。"""

import json
import os
import shutil
import time

import pytest

from onestead import skillscan
from onestead.store import BaseError, Store

from .conftest import git
from .support import WEB_POST_HEADERS as POST_HEADERS, junction, skill


@pytest.fixture
def machine(skill_home, tmp_path, monkeypatch):
    h = skill_home
    old, new = time.time() - 86400 * 10, time.time() - 3600
    shared = skill(h / ".agents" / "skills" / "shared-one", "shared-one")
    junction(h / ".claude" / "skills" / "shared-one", shared)
    skill(h / ".claude" / "skills" / "own-skill", "own-skill", "旧版", when=old)
    skill(h / ".codex" / "skills" / "own-skill", "own-skill", "新版", when=new, extra={"token.txt": "不算进指纹"})
    (h / ".claude" / "skills" / "no-md").mkdir(parents=True)
    (h / ".claude" / "skills" / "no-md" / "notes.txt").write_text("x", encoding="utf-8")
    skill(h / ".codex" / "skills" / ".system" / "sys1", "sys1")
    skill(h / ".codex" / "plugins" / "cache" / "market" / "plug" / "1.0" / "skills" / "p1", "p1")
    junction(h / ".pi" / "skills", h / ".codex" / "skills")
    monkeypatch.setenv("ONESTEAD_SKILL_HARNESSES", json.dumps([
        {"key": "sample-agent", "name": "Sample Agent", "user": [str(h / ".sample-agent" / "skills")]}]))
    gone = skill(tmp_path / "gone-target", "gone")
    junction(h / ".pi" / "agent" / "skills" / "gone", gone)
    shutil.rmtree(gone)
    snapshot = tmp_path / "snapshots"
    monkeypatch.setenv("ONESTEAD_SKILL_SNAPSHOTS", json.dumps([
        {"key": "assistant", "name": "远程快照", "root": str(snapshot),
         "layout": {"type": "latest-subdir", "pattern": "skills-*", "subpaths": ["exports/user-workflows"]}}]))
    skill(snapshot / "skills-20261011" / "exports" / "user-workflows" / "own-skill",
          "own-skill", "快照", when=time.time())
    return h


def rows():
    data = skillscan.scan()
    return data, {r["id"]: r for r in skillscan.summarize(data, {})}


def test_scan_states(machine):
    data, by = rows()
    states = {k: v["state"] for k, v in by["own-skill"]["installs"].items()}
    assert states == {"claude": "behind", "grok": "behind", "codex": "same", "pi": "same", "assistant": "snapshot"}
    assert by["own-skill"]["installs"]["pi"]["same_copy_as"] == ["codex"]  # PI的目录联到 Codex
    assert by["own-skill"]["ref"]["path"].endswith(os.path.join(".codex", "skills", "own-skill"))
    assert by["own-skill"]["alert"] == "behind"
    shared = by["shared-one"]["installs"]["claude"]
    assert shared["state"] == "same" and shared["in_shared_dir"] and by["shared-one"]["in_shared"]
    assert by["no-md"]["installs"]["claude"]["state"] == "broken" and by["no-md"]["alert"] == "broken"
    assert by["gone"]["installs"]["pi"]["dangling"] and by["gone"]["alert"] == "broken"
    assert "sys1" not in by and "p1" not in by  # 内置、插件的折叠，不进列表
    codex = next(x for x in data["harnesses"] if x["key"] == "codex")
    assert codex["builtin"] == ["sys1"] and codex["plugins"] == [{"plugin": "plug", "skills": ["p1"]}]
    sample = next(x for x in data["harnesses"] if x["key"] == "sample-agent")
    assert sample["items"] == [] and sample["roots"] == []


def test_secret_files_do_not_change_fingerprint(skill_home):
    a = skill(skill_home / "a" / "x", "x", when=1_700_000_000)
    b = skill(skill_home / "b" / "x", "x", when=1_700_000_000, extra={"my-secret.txt": "不算"})
    assert skillscan._fingerprint(a)[0] == skillscan._fingerprint(b)[0]


def test_registered_home_is_the_reference(machine, as_agent, repo):
    home = str(machine / ".claude" / "skills" / "own-skill")
    as_agent("claude").register_skill({"id": "own-skill", "title": "自己的技能", "one_line": "测试用", "origin": "自建",
                                       "home": home, "maintainer": "claude"}, signed_as="Onestead Lead")
    assert git(repo, "log", "-1", "--format=%an|%s") == "claude · Onestead Lead|Skill：登记了「自己的技能」"
    d = next(x for x in Store(repo).load() if x.kind == "skill")
    assert d.meta["home"] == home and not d.problem
    skillscan._cache["data"] = None
    text = as_agent("codex").get_skill("own-skill")
    assert "登记的正本" in text and "和正本不同" in text and "谁维护：claude" in text
    listing = as_agent("codex").list_skills(only_alerts=True)
    assert "`own-skill`" in listing and "`shared-one`" not in listing
    assert "插件共 1 个技能" in listing and "内置 1 个" in listing
    as_agent("codex").update_skill("own-skill", {"one_line": "改过的一句话"})
    assert git(repo, "log", "-1", "--format=%s") == "Skill：改了「自己的技能」"
    assert as_agent("codex").update_skill("own-skill", {"one_line": "改过的一句话"}).startswith("没有变化")


@pytest.mark.parametrize("item", [
    {"id": "bad id", "one_line": "x"}, {"id": "ok-id"}, {"id": "ok-id", "one_line": "x", "origin": "偷的"},
    {"id": "ok-id", "one_line": "x", "project": "nope"}, {"id": "ok-id", "one_line": "x", "color": "red"},
    {"id": "ok-id", "one_line": "password=abcdefgh12345678"},
])
def test_bad_skill_records_are_refused(as_agent, repo, item):
    with pytest.raises(BaseError):
        as_agent("claude").register_skill(item)
    assert not (repo / "skills").exists()


def test_get_unknown_skill(machine, as_agent):
    with pytest.raises(BaseError):
        as_agent("claude").get_skill("nope")
    assert "装的话放" in as_agent("claude").get_skill("shared-one")


def test_web_skills_page_open_and_activity(machine, client, as_agent, monkeypatch):  # noqa: F811
    as_agent("claude").register_skills([{"id": "shared-one", "one_line": "共用的那个", "project": "alpha"},
                                        {"id": "cloud-only", "one_line": "只在云端", "origin": "第三方"}], "测试", None)
    data = client.get("/api/skills").json()
    by = {r["id"]: r for r in data["skills"]}
    assert by["shared-one"]["record"]["project"] == {"id": "alpha", "name": "阿尔法"}
    assert by["cloud-only"]["installs"] == {} and by["cloud-only"]["registered"]
    assert [h["key"] for h in data["harnesses"]] == ["claude", "codex", "pi", "grok", "cursor", "workbuddy", "sample-agent", "assistant"]
    opened = []
    monkeypatch.setattr("os.startfile", lambda p: opened.append(p), raising=False)
    path = by["own-skill"]["installs"]["claude"]["path"]
    assert client.post("/api/skills/open", json={"id": "own-skill", "path": path}, headers=POST_HEADERS).status_code == 200
    assert opened == [path]
    assert client.post("/api/skills/open", json={"id": "own-skill", "path": "C:\\Windows"}, headers=POST_HEADERS).status_code == 400
    gone = by["gone"]["installs"]["pi"]["path"]
    assert client.post("/api/skills/open", json={"id": "gone", "path": gone}, headers=POST_HEADERS).status_code == 400
    rows = client.get("/api/activity").json()["rows"]
    assert rows[0]["type"] == "Skill" and rows[0]["what"] == "登记了 2 个 Skill 的说明（测试）"
    mods = {m["kind"]: m["data"] for m in client.get("/api/home").json()["modules"]}
    assert "behind" not in mods["skills"] and mods["skills"]["broken"] == 2  # 首页只提醒坏了的


def test_zero_filled_files_count_as_broken(skill_home):
    from onestead import skillscan
    d = skill_home / ".claude" / "skills" / "zeroed"
    d.mkdir(parents=True)
    (d / "SKILL.md").write_text("---\nname: zeroed\ndescription: 好的说明\n---\n", encoding="utf-8")
    (d / "notes.md").write_bytes(b"\x00" * 64)
    row = next(r for r in skillscan.summarize(skillscan.cached_scan(True), {}) if r["id"] == "zeroed")
    assert row["installs"]["claude"]["state"] == "broken" and row["alert"] == "broken"
    assert row["installs"]["claude"]["damaged"] == ["notes.md"]


def test_grok_also_reads_claude_skills(skill_home):
    from onestead import skillscan
    d = skill_home / ".claude" / "skills" / "only-in-claude"
    d.mkdir(parents=True)
    (d / "SKILL.md").write_text("---\nname: only-in-claude\ndescription: 说明\n---\n", encoding="utf-8")
    row = next(r for r in skillscan.summarize(skillscan.cached_scan(True), {}) if r["id"] == "only-in-claude")
    assert row["installs"]["grok"]["state"] == "same" and row["installs"]["grok"]["same_copy_as"] == ["claude"]
