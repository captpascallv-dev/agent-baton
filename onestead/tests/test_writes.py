"""受控写入：每次写都是一次带作者的 git 提交；不该写的写不进去。"""

from .support import OWNER, OWNER_NAME

import io

import pytest

from onestead.store import BaseError

from .conftest import git


def read(repo, rel):
    return io.open(repo / rel, encoding="utf-8").read()


def test_status_append_creates_commit_with_agent_author(repo, as_agent):
    out = as_agent("codex").update_project_status("alpha", "完成了第一步。", signed_as="alex")
    assert "已记录" in out
    assert git(repo, "log", "-1", "--format=%an <%ae>") == "codex · alex <codex@agents.company-base.local>"
    assert "Agent: codex" in git(repo, "log", "-1", "--format=%B")
    text = read(repo, "projects/alpha/status.md")
    assert "## 2026-10-10 23:30 · codex · alex\n\n完成了第一步。" in text
    assert git(repo, "status", "--porcelain") == ""


def test_status_is_append_only(repo, as_agent):
    codex = as_agent("codex")
    codex.update_project_status("alpha", "第一条")
    codex.update_project_status("alpha", "第二条")
    text = read(repo, "projects/alpha/status.md")
    assert text.index("第一条") < text.index("第二条")
    assert "最新 2 条，共 2 条" in codex.get_project("alpha")


def test_secret_like_text_rejected_and_nothing_written(repo, as_agent):
    before = git(repo, "rev-parse", "HEAD")
    with pytest.raises(BaseError, match="密钥"):
        as_agent("codex").update_project_status("alpha", "key 是 sk-abcdefghijklmnopqrstuvwxyz123456")
    assert git(repo, "rev-parse", "HEAD") == before
    assert not (repo / "projects/alpha/status.md").exists()


def test_member_without_write_right_rejected(as_agent):
    with pytest.raises(BaseError, match="不能写入"):
        as_agent("reader").update_project_status("alpha", "试试")


def test_members_can_write_to_restricted_project_but_not_missing_one(as_agent):
    assert "已记录" in as_agent("claude").update_project_status("secret", "试试")
    with pytest.raises(BaseError, match="找不到"):
        as_agent("claude").update_project_status("no-such-project", "试试")


def test_record_decision_and_restricted_inheritance(repo, as_agent):
    codex = as_agent("codex")
    out = codex.record_decision("用 git 存", "就这么定", "能撤回", OWNER, project_id="alpha")
    assert "已记录决定" in out and "更新它的项目摘要" in out
    assert "用 git 存" in as_agent("claude").list_decisions()
    # 限定项目的决定也要限定：将来对外时不能借决定把项目带出去
    with pytest.raises(BaseError, match="限定"):
        codex.record_decision("家里的决定", "定了", "因为", OWNER, project_id="secret")
    codex.record_decision("家里的决定", "定了", "因为", OWNER, project_id="secret",
                          sensitivity="restricted", visible_to=["codex"], access_note="仅限alex")
    out = as_agent("claude").list_decisions(project_id="secret")  # 本机伙伴都看得到，带提醒
    assert "家里的决定" in out and "使用提示：仅限alex" in out
    # 使用提示不限于限定内容
    codex.record_decision("只是提醒", "定了", "因为", OWNER, project_id="alpha", access_note="仅限alex")


def test_record_decision_rejects_unknown_visible_to(as_agent):
    with pytest.raises(BaseError, match="不在成员名单"):
        as_agent("codex").record_decision("t", "d", "r", OWNER, sensitivity="restricted",
                                          visible_to=["typo"])


def test_handover_normal_role_applies_directly(repo, as_agent):
    out = as_agent("codex").handover_role("alpha", "采集", "新采集", "换人试试", signed_as="alex")
    assert "已交接" in out
    proj = as_agent("claude").get_project("alpha")
    assert "| 采集 | 新采集 | 2026-10-10 | 否 |" in proj
    assert "交接：「采集」由「老采集」换成「新采集」" in read(repo, "projects/alpha/status.md")
    # 项目正文和其他字段原样保留
    assert "## 现状\n\n还行。" in read(repo, "projects/alpha/project.md")
    assert "C:\\x\\STATUS.md" in proj


def test_handover_key_role_needs_owner(repo, as_agent):
    codex = as_agent("codex")
    out = codex.handover_role("alpha", "项目 Lead", "新 Lead", "我来接")
    assert "接任申请" in out
    assert "| 项目 Lead | 老 Lead |" in codex.get_project("alpha")
    assert f"待 {OWNER_NAME} 批准" in read(repo, "projects/alpha/status.md")
    codex.handover_role("alpha", "项目 Lead", "新 Lead", "我来接", owner_approval=f"{OWNER_NAME} 10-10 23:40 说可以")
    proj = codex.get_project("alpha")
    assert "| 项目 Lead | 新 Lead | 2026-10-10 | 是 |" in proj
    assert f"{OWNER_NAME} 批准：{OWNER_NAME} 10-10 23:40 说可以" in read(repo, "projects/alpha/status.md")
    assert f"{OWNER_NAME} 批准" in git(repo, "log", "-1", "-p", "--format=%B")
