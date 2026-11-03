"""同事只看授权给自己的；名下 agent 跟着走，不能超出；写只能在授权项目上记进展和决定。"""

import pytest

from onestead.store import BaseError

from .support import hidden


def test_external_sees_granted_project_and_its_parts(ext):
    zhang = ext("ext-zhang")
    assert "阿尔法" in zhang.list_projects() and "贝塔" not in zhang.list_projects()
    out = zhang.get_project("alpha")
    assert "暗号阿尔法进展" in out
    assert "阿尔法的决定" in zhang.list_decisions()
    assert "阿尔法看板" in zhang.list_apps() and "贝塔看板" not in zhang.list_apps()
    assert "按授权访问的同事" in zhang.whoami() and "`alpha`" in zhang.whoami()


def test_external_does_not_see_ungranted_or_internal_company_content(ext):
    zhang = ext("ext-zhang")
    for term in ("贝塔湖", "内部决定", "内部模板", "公开待办"):
        assert hidden(zhang, term), term
    with pytest.raises(BaseError, match="找不到"):
        zhang.get_project("beta")
    assert "我们是一家测试公司" in zhang.get_handbook("company")
    assert "协作方式" in zhang.get_handbook("collaboration")  # 公开的照样能看
    people = zhang.company()
    assert "张三（合作方）" in people and "Codex" in people and "李四" in people


def test_restricted_needs_visible_to_even_when_granted(ext):
    zhang = ext("ext-zhang")
    # secret 授权给了张三，但它是限定的、visible_to 里没有张三
    with pytest.raises(BaseError, match="找不到"):
        zhang.get_project("secret")
    assert hidden(zhang, "家里决定") and hidden(zhang, "紫藤萝")
    assert "暗号合作条款" in zhang.get_asset("deal")
    # sample 是限定的、没授权，也不在名单里
    assert hidden(zhang, "琴漆")


def test_agent_follows_owner_and_cannot_exceed(ext):
    bot = ext("ext-zhang-claude")
    assert "阿尔法" in bot.list_projects()
    assert "贝塔" not in bot.list_projects()  # 自己写了 beta，但张三没有，不能超出
    assert "暗号合作条款" in bot.get_asset("deal")  # 张三在 visible_to 里，跟着张三看得到
    assert "张三（合作方）" in bot.company()


def test_agent_without_valid_owner_gets_nothing(ext):
    orphan = ext("ext-orphan")
    assert "阿尔法" not in orphan.list_projects()
    with pytest.raises(BaseError, match="不能写入"):
        orphan.update_project_status("alpha", "试试")


def test_bad_external_marker_is_not_trusted(ext):
    typo = ext("ext-typo")
    assert "不在成员名单" in typo.whoami()
    assert "阿尔法" not in typo.list_projects()


def test_external_writes_only_progress_and_decisions_on_granted_projects(ext, repo):
    zhang = ext("ext-zhang")
    assert "已记录到" in zhang.update_project_status("alpha", "合作方的进展", signed_as="张三")
    with pytest.raises(BaseError, match="找不到"):
        zhang.update_project_status("beta", "不该写进去")
    with pytest.raises(BaseError, match="关联一个授权项目"):
        zhang.record_decision("t", "d", "r", "张三")
    with pytest.raises(BaseError, match="不能标公开"):
        zhang.record_decision("t", "d", "r", "张三", project_id="alpha", sensitivity="public")
    assert "已记录决定" in zhang.record_decision("合作方的决定", "d", "r", "张三", project_id="alpha")
    zhang.add_todos([{"title": "同事的本人待办"}])
    assert "同事的本人待办" in zhang.list_todos()
    assert "同事的本人待办" not in ext("codex").list_todos()
    with pytest.raises(BaseError, match="按授权的成员只能"):
        zhang.handover_role("alpha", "采集", "张三", "想接手")
    with pytest.raises(BaseError, match="按授权的成员只能"):
        zhang.register_app({"id": "ext-app", "title": "外部应用", "one_line": "外部登记的", "kind": "网页", "open": {"url": "http://127.0.0.1:3/"}})
    with pytest.raises(BaseError, match="不能选择模块"):
        zhang.propose_home([{"kind": "attention"}], "改首页")


def test_owner_without_write_blocks_agent_writes(ext):
    with pytest.raises(BaseError, match="不能写入"):
        ext("ext-li").update_project_status("alpha", "只读的人")
    with pytest.raises(BaseError, match="不能写入"):
        ext("ext-li-bot").update_project_status("alpha", "只读的人的 bot")
    assert "阿尔法" in ext("ext-li-bot").list_projects()  # 读照样跟着人走


def test_internal_only_reads_are_closed_to_external(ext):
    # Skill 10-12 起按授权共享给同事（见 test_skill_share.py），不再算只在本机用的
    zhang = ext("ext-zhang")
    assert "暗号公开待办" not in zhang.list_todos()
    assert "shared_skills" in zhang.home_layout()
    assert "`attention`" not in zhang.home_layout()


def test_local_partners_unchanged(ext):
    codex = ext("codex")
    assert "贝塔" in codex.list_projects() and "暗号合作条款" in codex.get_asset("deal")
    assert "张三（合作方）" in codex.company()
