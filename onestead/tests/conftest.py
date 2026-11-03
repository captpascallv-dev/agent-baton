"""测试用的临时数据仓库：一个内部项目、一个限定项目、一份限定资产、几位成员、一份没有标记的文件。

共用夹具在 support.py。这里再引入一次，pytest 才能当成夹具。
不在这个文件写 pytest_plugins：仓库根才是 rootdir，写了会直接报错。
"""

from .support import OWNER, OWNER_NAME

import io
from pathlib import Path

import pytest

from onestead.base import CompanyBase

from .support import (  # noqa: F401
    FIXED_NOW,
    isolated_config,
    configured_data,
    business,
    client,
    ext,
    git,
    personal_workspace,
    remote,
    shared,
    workspace,
)

FILES = {
    "FORMAT.md": "---\nid: format\ntype: handbook\ntitle: 格式说明\nsensitivity: internal\n---\n\n# 格式说明\n\n正文。\n",
    "handbook/company.md": "---\nid: company\ntype: handbook\ntitle: 公司概况\nsensitivity: internal\n---\n\n# 公司概况\n\n我们是一家测试公司。\n",
    "handbook/collaboration.md": "---\nid: collaboration\ntype: handbook\ntitle: 协作方式\nsensitivity: public\n---\n\n# 协作方式\n\n规则在别处。\n",
    "people/claude.md": f"---\nid: claude\ntype: person\ntitle: claude\nagent_key: claude\nkind: agent\nowner: {OWNER}\nremote_enabled: false\naccess_mode: full\ncan_write: true\nsensitivity: internal\n---\n",
    "people/codex.md": f"---\nid: codex\ntype: person\ntitle: Codex\nagent_key: codex\nkind: agent\nowner: {OWNER}\nremote_enabled: false\naccess_mode: full\ncan_write: true\nsensitivity: internal\n---\n",
    "people/reader.md": "---\nid: reader\ntype: person\ntitle: 只读成员\nagent_key: reader\nkind: human\nremote_enabled: false\naccess_mode: full\ncan_write: false\nsensitivity: internal\n---\n",
    f"people/{OWNER}.md": f"---\nid: {OWNER}\ntype: person\ntitle: {OWNER_NAME}\nagent_key: {OWNER}\nkind: human\nremote_enabled: false\naccess_mode: full\ncan_write: true\nsensitivity: internal\n---\n",
    "projects/alpha/project.md": (
        "---\nid: alpha\ntype: project\ntitle: 阿尔法\nstate: 进行中\ngoal: 测试项目\n"
        "roles:\n  - role: 项目 Lead\n    who: 老 Lead\n    key: true\n  - role: 采集\n    who: 老采集\n"
        "summary_as_of: 2026-09-01\n"
        "authority:\n  label: 阿尔法台账\n  path: C:\\x\\STATUS.md\n"
        "sensitivity: internal\nupdated: 2026-09-01\n---\n\n# 阿尔法\n\n## 现状\n\n还行。\n"
    ),
    "projects/secret/project.md": (
        "---\nid: secret\ntype: project\ntitle: 家里的事\nstate: 维护中\ngoal: 家庭\n"
        "roles:\n  - role: 负责人\n    who: 某人\n"
        "summary_as_of: 2026-10-10\nsensitivity: restricted\nvisible_to: [codex]\n"
        "access_note: 仅限alex\nupdated: 2026-10-10\n---\n\n# 家里的事\n\n暗号紫藤萝。\n"
    ),
    "assets/sample.md": "---\nid: sample\ntype: asset\ntitle: 限定样本\nsensitivity: restricted\nvisible_to: [claude]\n---\n\n暗号琴漆。\n",
    "assets/nomark.md": "没有标记的文件，暗号无标记。\n",
}

@pytest.fixture(autouse=True)
def isolated_tokens(tmp_path, monkeypatch):
    """包括成员管理撤回操作在内，全部凭据读写只用临时文件。"""
    monkeypatch.setenv("COMPANY_BASE_TOKENS", str(tmp_path / "tokens.json"))


@pytest.fixture(autouse=True)
def feishu_cfg(tmp_path, monkeypatch):
    """飞书配置只走临时目录，测试不读本机 %LOCALAPPDATA%\\CompanyBase。"""
    folder = tmp_path / "feishu-cfg"
    folder.mkdir()
    monkeypatch.setenv("COMPANY_BASE_FEISHU_DIR", str(folder))
    return folder


@pytest.fixture(autouse=True)
def usage_log(tmp_path, monkeypatch):
    """测试里的调用记录写到临时目录，不进真的 %LOCALAPPDATA%。"""
    path = tmp_path / "usage.jsonl"
    monkeypatch.setenv("COMPANY_BASE_USAGE_LOG", str(path))
    return path


@pytest.fixture(autouse=True)
def skill_home(tmp_path, monkeypatch):
    """Skill 扫描只扫临时目录，不碰本机真的各家目录；每个测试清掉扫描缓存。"""
    from onestead import skillscan
    home = tmp_path / "home"
    home.mkdir()
    monkeypatch.setenv("COMPANY_BASE_SKILL_HOME", str(home))
    skillscan._cache["data"] = None
    yield home
    skillscan._cache["data"] = None


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    root = tmp_path / "data"
    for rel, content in FILES.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        with io.open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
    git(root.parent, "init", "-q", "-b", "main", str(root))
    git(root, "config", "user.name", "test")
    git(root, "config", "user.email", "test@example.com")
    git(root, "config", "core.autocrlf", "false")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "初始")
    return root


@pytest.fixture
def as_agent(repo):
    def make(key):
        return CompanyBase(repo, key, now=lambda: FIXED_NOW)
    return make
