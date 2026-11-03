"""首页模块：agent 只能提认识的模块和选项；草稿只写草稿文件，不动现在的首页。"""

from .support import OWNER

import pytest

from onestead import home
from onestead.store import BaseError

from .conftest import git


def test_normalize_fills_defaults():
    out = home.normalize([{"kind": "recent"}, {"kind": "projects", "options": {"states": ["进行中"]}}])
    assert out == [{"kind": "recent", "options": {"limit": 6}},
                   {"kind": "projects", "options": {"states": ["进行中"]}}]


@pytest.mark.parametrize("bad", [
    [], "attention", [{"kind": "weather"}], [{"kind": "recent"}, {"kind": "recent"}],
    [{"kind": "recent", "options": {"limit": 99}}], [{"kind": "recent", "options": {"color": "red"}}],
    [{"kind": "projects", "options": {"states": "进行中"}}],
])
def test_normalize_rejects_bad_modules(bad):
    with pytest.raises(BaseError):
        home.normalize(bad)


def test_propose_home_writes_only_the_draft(as_agent, repo):
    msg = as_agent("claude").propose_home([{"kind": "attention"}], "只留要处理的事", signed_as="claude")
    assert "草稿" in msg
    assert (repo / f"home/{OWNER}-draft.md").exists() and not (repo / f"home/{OWNER}.md").exists()
    assert git(repo, "log", "-1", "--format=%an") == "claude · claude"
    assert git(repo, "log", "-1", "--format=%s") == "首页草稿：只留要处理的事"
    text = as_agent("claude").home_layout()
    assert "只留要处理的事" in text and "attention" in text


def test_propose_home_refuses_unknown_module_and_readers(as_agent, repo):
    with pytest.raises(BaseError):
        as_agent("claude").propose_home([{"kind": "weather"}], "加天气")
    with pytest.raises(BaseError):
        as_agent("reader").propose_home([{"kind": "attention"}], "试试")
    assert not (repo / "home").exists()
