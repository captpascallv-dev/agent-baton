"""读权限：本机伙伴（成员名单里的身份）什么都能看；没登记的身份只看公开内容。限定和使用提示只做提醒。"""

from .support import OWNER

import pytest

from onestead.store import BaseError


def test_members_see_restricted_content_with_reminder(as_agent):
    codex = as_agent("codex")
    assert "限定样本" in codex.list_assets()
    assert "assets/sample.md" in codex.search("琴漆")
    out = codex.get_asset("sample")
    assert "暗号琴漆" in out and out.startswith("【限定：不要转给公司以外")


def test_restricted_project_listed_for_all_members_with_hint(as_agent):
    claude = as_agent("claude")
    assert "家里的事（限定；提示：仅限alex）" in claude.company()
    out = claude.get_project("secret")
    assert out.startswith("【限定") and "使用提示：仅限alex" in out


def test_unmarked_file_visible_to_members_with_fix_reminder(as_agent):
    out = as_agent("claude").search("无标记")
    assert "assets/nomark.md" in out


def test_unknown_identity_sees_only_public(as_agent):
    stranger = as_agent("stranger")
    assert "协作方式" in stranger.search("规则")
    assert "阿尔法" not in stranger.list_projects()
    for term in ("琴漆", "紫藤萝", "无标记"):
        assert stranger.search(term).startswith("没有找到")
    with pytest.raises(BaseError, match="找不到"):
        stranger.get_asset("sample")
    assert "不在成员名单" in stranger.whoami()


def test_owner_identity_is_reserved(as_agent):
    with pytest.raises(BaseError, match=f"{OWNER} 身份"):
        as_agent(OWNER).company()


def test_project_banner_has_date_staleness_and_authority(as_agent):
    out = as_agent("codex").get_project("alpha")
    first = out.splitlines()[0]
    assert "截至 2026-09-01（39 天前）" in first
    assert "可能已过时" in first
    assert "以「阿尔法台账」为准" in first and "C:\\x\\STATUS.md" in first


def test_roles_listed_with_key_flag(as_agent):
    out = as_agent("codex").get_project("alpha")
    assert "| 项目 Lead | 老 Lead | 未记录 | 是 |" in out
    assert "| 采集 | 老采集 | 未记录 | 否 |" in out
