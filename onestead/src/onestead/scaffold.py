"""`onestead init`：新建数据仓库、写本机配置、打印接入说明。

项目节名和状态词是程序规定的中文。英文模板里也照这个写，不在这里改读取规则。
"""

from __future__ import annotations

from .i18n import text as tr

import os
import re
import shutil
from importlib.resources import files
from pathlib import Path

from . import config, gitops
from .connect import connect_prompt, toml_basic_string
from .store import BaseError, now_taipei, valid_person_key
from .textutil import _check_text

_PLACEHOLDER = re.compile(r"\{\{(owner_key|owner_name|owner_name_yaml|today|now)\}\}")
_EMPTY_DIRS = ("decisions", "assets", "apps", "todos", "home", "skills")
_TEMPLATE_FILES = {
    "FORMAT.md": "format.md",
    "handbook/company.md": "company.md",
    "handbook/collaboration.md": "collaboration.md",
    "projects/getting-started/project.md": "getting-started.md",
    "projects/getting-started/status.md": "getting-started-status.md",
}


def init_repo(args) -> None:
    owner = (args.owner or "").strip()
    if not valid_person_key(owner):
        raise BaseError(tr("agent.358"))
    name = _check_text(tr("agent.354"), args.name, limit=80, single_line=True)
    lang = args.lang or "en"
    if lang not in ("en", "zh"):
        raise BaseError(tr("agent.359"))
    raw = (args.data_dir or "").strip()
    if not raw:
        raise BaseError(tr("agent.360"))
    root = Path(raw).expanduser().resolve()
    if root.exists() and not root.is_dir():
        raise BaseError(tr("agent.361", v0=root))
    if root.exists() and any(root.iterdir()):
        raise BaseError(tr("agent.362", v0=root))

    created = not root.exists()
    now = now_taipei()
    try:
        root.mkdir(parents=True, exist_ok=True)
        _write_tree(root, lang, owner, name, now)
        _commit(root, owner, name)
    except Exception:
        _rollback(root, created)
        raise

    config_message = _write_config(root, owner, lang=lang, force=bool(args.force_config))
    print(tr("agent.355", v0=root))
    print(config_message)
    print()
    print(connect_prompt(name).rstrip())
    print()
    print(tr("connect.hint"))
    print(tr("agent.356"))


def _write_tree(root: Path, lang: str, owner: str, name: str, now) -> None:
    values = {
        "owner_key": owner,
        "owner_name": name,
        "owner_name_yaml": _yaml_string(name),
        "today": now.date().isoformat(),
        "now": now.strftime("%Y-%m-%d %H:%M"),
    }
    written = dict(_TEMPLATE_FILES)
    written[f"people/{owner}.md"] = "owner.md"
    for rel, template in written.items():
        text = _fill(_read_template(lang, template), values)
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(text.encode("utf-8"))
    for folder in _EMPTY_DIRS:
        path = root / folder / ".gitkeep"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"\n")


def _read_template(lang: str, name: str) -> str:
    resource = files("onestead").joinpath("init_templates", lang, name)
    try:
        text = resource.read_text(encoding="utf-8")
    except (FileNotFoundError, OSError):
        raise BaseError(tr("agent.367", v0=lang, v1=name)) from None
    return text.replace("\r\n", "\n")


def _fill(text: str, values: dict[str, str]) -> str:
    def repl(match: re.Match) -> str:
        return values[match.group(1)]

    filled = _PLACEHOLDER.sub(repl, text)
    if _PLACEHOLDER.search(filled):
        raise BaseError(tr("agent.363"))
    if not filled.endswith("\n"):
        filled += "\n"
    return filled.replace("\r\n", "\n")


def _yaml_string(value: str) -> str:
    """用户起的显示名放进开头标记时要成合法的 YAML 字符串。"""
    return toml_basic_string(value)


def _commit(root: Path, owner: str, name: str) -> None:
    email = f"{owner}@agents.company-base.local"
    env = dict(os.environ, GIT_AUTHOR_NAME=name, GIT_AUTHOR_EMAIL=email,
               GIT_COMMITTER_NAME=name, GIT_COMMITTER_EMAIL=email)
    message = f"初始化数据仓库\n\n主人：{name}（{owner}）\n"
    gitops._git(root, ["init", "-q", "-b", "main"])
    gitops._git(root, ["config", "core.autocrlf", "false"])
    gitops._git(root, ["add", "-A"])
    gitops._git(root, ["-c", f"user.name={name}", "-c", f"user.email={email}",
                       "commit", "-q", "-F", "-", "--"], env=env, stdin=message)


def _rollback(root: Path, created: bool) -> None:
    """初始化没提交成功时，不把半成品目录留给下一次（下一次会因非空而拒绝）。"""
    if not root.exists():
        return
    if created:
        shutil.rmtree(root)
        return
    git_dir = root / ".git"
    if git_dir.is_dir():
        shutil.rmtree(git_dir)
    for dirpath, dirnames, filenames in os.walk(root, topdown=False):
        current = Path(dirpath)
        for filename in filenames:
            (current / filename).unlink()
        for dirname in dirnames:
            child = current / dirname
            if child.exists():
                child.rmdir()


def _write_config(root: Path, owner: str, *, lang: str = "en", force: bool) -> str:
    path = config.config_path()
    if path.exists() and not path.is_file():
        raise BaseError(tr("agent.364", v0=path))
    if path.exists() and not force:
        return tr("agent.365")
    path.parent.mkdir(parents=True, exist_ok=True)
    backup = None
    if path.exists():
        backup = _backup_path(path)
        path.replace(backup)
    body = "\n".join([
        "# onestead init 写入。环境变量优先于这一份。",
        'name = "Onestead"',
        f"lang = {toml_basic_string(lang)}",
        f"data_dir = {toml_basic_string(str(root))}",
        f"owner_key = {toml_basic_string(owner)}",
        "",
    ])
    try:
        path.write_bytes(body.encode("utf-8"))
    except Exception:
        if backup is not None and not path.exists():
            backup.replace(path)
        raise
    config.invalidate()
    if backup is not None:
        return tr("agent.366", v0=backup.name, v1=path)
    return tr("agent.357", v0=path)


def _backup_path(path: Path) -> Path:
    stamp = now_taipei().strftime("%Y%m%d-%H%M%S")
    candidate = path.parent / f"config.toml.bak-{stamp}"
    n = 2
    while candidate.exists():
        candidate = path.parent / f"config.toml.bak-{stamp}-{n}"
        n += 1
    return candidate
