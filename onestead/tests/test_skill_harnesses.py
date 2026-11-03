"""额外本机工具与内置工具共用扫描、比对、分享和安装提示；配置只用临时文件。"""

import json

import pytest

from onestead import config, skills, skillscan
from onestead.store import BaseError

from .support import SID, grant, skill


def test_default_harnesses_use_generic_paths(skill_home):
    assert config.skill_harnesses() == []
    table = {row["key"]: row for row in skillscan.harness_table()}
    assert list(table) == ["claude", "codex", "pi", "grok", "cursor", "workbuddy"]
    assert table["pi"] == {"key": "pi", "name": "PI", "user": [
        skill_home / ".pi" / "agent" / "skills", skill_home / ".pi" / "skills"]}


def test_configured_paths_append_to_builtin(isolated_config, skill_home, tmp_path):
    extra = tmp_path / "extra-skills"
    plugins = tmp_path / "extra-plugins"
    isolated_config.write_text(
        'skill_harnesses = [{key="codex", name="Extra Codex paths", '
        f'user=["{extra.as_posix()}"], builtin=["{(extra / ".system").as_posix()}"], '
        f'plugins=["{plugins.as_posix()}"]}}]', encoding="utf-8")
    original = skill(skill_home / ".codex" / "skills" / "original", "original")
    added = skill(extra / "added", "added")
    skill(skill_home / ".codex" / "skills" / ".system" / "original-builtin", "original-builtin")
    skill(extra / ".system" / "extra-builtin", "extra-builtin")
    skill(skill_home / ".codex" / "plugins" / "cache" / "market" / "original-plugin" / "1" / "skills" / "p1", "p1")
    skill(plugins / "market" / "extra-plugin" / "1" / "skills" / "p2", "p2")
    table = skillscan.harness_table()
    codex = next(row for row in table if row["key"] == "codex")
    assert sum(row["key"] == "codex" for row in table) == 1
    assert codex["name"] == "Codex"
    assert codex["user"] == [skill_home / ".codex" / "skills", extra]
    assert codex["builtin"] == [skill_home / ".codex" / "skills" / ".system", extra / ".system"]
    assert codex["plugins"] == [skill_home / ".codex" / "plugins" / "cache", plugins]
    scanned = next(row for row in skillscan.scan()["harnesses"] if row["key"] == "codex")
    assert not scanned["snapshot"]
    assert [row["path"] for row in scanned["items"]] == [str(original), str(added)]
    assert scanned["builtin"] == ["original-builtin", "extra-builtin"]
    assert scanned["plugins"] == [
        {"plugin": "extra-plugin", "skills": ["p2"]}, {"plugin": "original-plugin", "skills": ["p1"]}]
    assert skills.install_dir("codex") == str(skill_home / ".codex" / "skills")


def test_new_harness_scans_compares_and_shares(isolated_config, tmp_path, ext, repo):
    root = tmp_path / "sample-agent" / "skills"
    isolated_config.write_text(
        f'skill_harnesses = [{{key="sample-agent", name="Sample Agent", user=["{root.as_posix()}"]}}]',
        encoding="utf-8")
    installed = skill(root / SID, "示例技能", "额外本机工具的完整技能内容")
    data = skillscan.scan()
    entry = next(row for row in data["harnesses"] if row["key"] == "sample-agent")
    assert entry["name"] == "Sample Agent" and not entry["snapshot"]
    row = next(row for row in skillscan.summarize(data, {}) if row["id"] == SID)
    assert row["ref"]["path"] == str(installed)
    assert row["installs"]["sample-agent"]["state"] == "same"
    assert skills.install_dir("sample-agent") == str(root)
    assert str(root) in skills.install_hint("sample-agent")
    ext("claude").register_skill({"id": SID, "one_line": "示例技能", "home": str(installed)})
    grant(repo)
    assert "额外本机工具的完整技能内容" in ext("ext-zhang").get_skill(SID)


def test_harness_environment_priority(isolated_config, monkeypatch):
    isolated_config.write_text(
        'skill_harnesses = [{key="file-agent", name="File Agent", user=["file-skills"]}]', encoding="utf-8")
    assert config.skill_harnesses()[0]["key"] == "file-agent"
    legacy = [{"key": "legacy-agent", "name": "Legacy Agent", "user": ["legacy-skills"]}]
    current = [{"key": "env-agent", "name": "Environment Agent", "user": ["env-skills"]}]
    monkeypatch.setenv("COMPANY_BASE_SKILL_HARNESSES", json.dumps(legacy))
    assert config.skill_harnesses() == legacy
    monkeypatch.setenv("ONESTEAD_SKILL_HARNESSES", json.dumps(current))
    assert config.skill_harnesses() == current
    assert skillscan.harness_table()[-1]["key"] == "env-agent"
    monkeypatch.setenv("ONESTEAD_SKILL_HARNESSES", "[]")
    assert config.skill_harnesses() == []
    assert [row["key"] for row in skillscan.harness_table()] == ["claude", "codex", "pi", "grok", "cursor", "workbuddy"]
    monkeypatch.setenv("ONESTEAD_SKILL_HARNESSES", "")
    with pytest.raises(BaseError, match="JSON"):
        config.skill_harnesses()  # 新变量即使为空，也不能退回旧变量或文件。


def test_harness_paths_expand_home(skill_home, monkeypatch):
    monkeypatch.setenv("HOME", str(skill_home))
    monkeypatch.setenv("USERPROFILE", str(skill_home))
    monkeypatch.setenv("ONESTEAD_SKILL_HARNESSES", json.dumps([{
        "key": "sample-agent", "name": "Sample Agent", "user": ["~/.sample-agent/skills"],
        "builtin": ["~/.sample-agent/skills/.system"], "plugins": ["~/.sample-agent/plugins/cache"]}]))
    row = skillscan.harness_table()[-1]
    assert row["user"] == [skill_home / ".sample-agent" / "skills"]
    assert row["builtin"] == [skill_home / ".sample-agent" / "skills" / ".system"]
    assert row["plugins"] == [skill_home / ".sample-agent" / "plugins" / "cache"]


@pytest.mark.parametrize("rows", [
    {}, [None], [{"key": "sample-agent", "name": "Sample Agent"}],
    [{"key": "", "name": "Sample Agent", "user": []}],
    [{"key": "sample-agent", "name": " ", "user": []}],
    [{"key": "sample-agent", "name": "Sample Agent", "user": "skills"}],
    [{"key": "sample-agent", "name": "Sample Agent", "user": [1]}],
    [{"key": "sample-agent", "name": "Sample Agent", "user": [" "]}],
    [{"key": "sample-agent", "name": "Sample Agent", "user": [], "builtin": "skills"}],
    [{"key": "sample-agent", "name": "Sample Agent", "user": [], "plugins": [None]}],
])
@pytest.mark.parametrize("lang", ["zh", "en"])
def test_bad_harness_shape_is_rejected(rows, lang, monkeypatch):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    monkeypatch.setenv("ONESTEAD_SKILL_HARNESSES", json.dumps(rows))
    with pytest.raises(BaseError, match="skill_harnesses"):
        config.skill_harnesses()


@pytest.mark.parametrize("prefix", ["ONESTEAD_", "COMPANY_BASE_"])
def test_bad_harness_json_is_rejected(prefix, monkeypatch):
    monkeypatch.setenv(prefix + "SKILL_HARNESSES", "[")
    with pytest.raises(BaseError, match="JSON"):
        config.skill_harnesses()
