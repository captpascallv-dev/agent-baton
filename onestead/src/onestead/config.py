"""运行配置：新环境变量优先于旧别名，缺文件视为空配置，导入时不读取本机文件。"""

from __future__ import annotations

import json
import os
import tomllib
from pathlib import Path


def env(suffix: str) -> str | None:
    """统一解析 ONESTEAD_* / COMPANY_BASE_*；显式空值也由新名字优先。"""
    for prefix in ("ONESTEAD_", "COMPANY_BASE_"):
        key = prefix + suffix
        if key in os.environ:
            return os.environ[key]
    return None


def config_path() -> Path:
    override = env("CONFIG")
    if override:
        return Path(override)
    home = Path.home()
    current = home / ".onestead" / "config.toml"
    legacy = home / ".company-base" / "config.toml"
    return legacy if not current.exists() and legacy.is_file() else current


def _error(key: str, **values):
    from .i18n import text
    from .store import BaseError

    # 配置本身损坏时不能再经 lang() 读它；有效环境语言优先，否则用同一文件已读到的语言。
    language = env("LANG")
    if language is None:
        language = (_cached[1].get("lang", "en")
                    if _cached and _cached[0][0] == str(config_path()) else "en")
    # 无效 lang 无法选语言，沿用原中文诊断；不让异常再触发配置解析。
    return BaseError(text(key, lang=language if language in ("zh", "en") else "zh", **values))


# 主人键在权限判断里逐份文档取用，每次都重新解析会拖慢请求；文件的路径、修改时间、大小都没变就沿用上次的结果。
_cached: tuple[tuple, dict] | None = None


def load() -> dict:
    global _cached
    path = config_path()
    try:
        stat = path.stat()
        key = (str(path), stat.st_mtime_ns, stat.st_size)
        if _cached and _cached[0] == key:
            return _cached[1]
        with path.open("rb") as stream:
            data = tomllib.load(stream)
    except FileNotFoundError:
        return {}
    except (OSError, tomllib.TOMLDecodeError):
        raise _error("agent.241") from None
    _cached = (key, data)
    return data


def _text(key: str, suffix: str) -> str | None:
    override = env(suffix)
    value = override if override is not None else load().get(key)
    if value is None:
        return None
    if not isinstance(value, str):
        raise _error("agent.231", v0=key)
    return value.strip() or None


def _required(key: str, suffix: str) -> str:
    value = _text(key, suffix)
    if not value:
        raise _error("agent.232", v0=key, v1=key, v2=suffix, v3=suffix)
    return value


def name() -> str:
    return _text("name", "NAME") or "Onestead"


def lang() -> str:
    value = env("LANG")
    if value is None:
        value = load().get("lang", "en")
    if value not in ("en", "zh"):
        raise _error("agent.233")
    return value


def display_name() -> str:
    value = name()
    if value == "Onestead":
        return value
    return f"{value}（Onestead）" if lang() == "zh" else f"{value} (Onestead)"


def data_dir() -> str:
    return _required("data_dir", "DATA")


def public_host(*, required: bool = False) -> str | None:
    if required:
        return _required("public_host", "PUBLIC_HOST")
    return _text("public_host", "PUBLIC_HOST")


def site_domain() -> str | None:
    import re

    value = _text("site_domain", "SITE_DOMAIN")
    if value and not re.fullmatch(r"[a-z0-9]+(?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9]+(?:[a-z0-9-]*[a-z0-9])?)+", value):
        raise _error("agent.234")
    return value


def reserved_slugs() -> set[str]:
    rows = load().get("reserved_slugs", [])
    if not isinstance(rows, list) or not all(isinstance(row, str) for row in rows):
        raise _error("agent.235")
    builtin = set("www mail api admin auth login base app apps static cdn mcp sites".split())
    host = public_host()
    return builtin | {row.lower() for row in rows} | ({host.split(".")[0]} if host else set())


def owner_key() -> str:
    from .store import valid_person_key

    value = _required("owner_key", "OWNER")
    if not valid_person_key(value):
        raise _error("agent.236")
    return value


def state_dir() -> Path:
    import sys

    value = _text("state_dir", "STATE_DIR")
    if value:
        return Path(value)
    if sys.platform == "win32":
        parent = Path(os.environ.get("LOCALAPPDATA") or Path.home())
        current, legacy = parent / "Onestead", parent / "CompanyBase"
    elif sys.platform == "darwin":
        parent = Path.home() / "Library" / "Application Support"
        current, legacy = parent / "Onestead", parent / "CompanyBase"
    else:
        parent = Path(os.environ.get("XDG_STATE_HOME") or Path.home() / ".local" / "state")
        current, legacy = parent / "onestead", parent / "company-base"
    # 不搬文件，存量口令、会话、隧道令牌继续从原处读取。
    return legacy if not current.exists() and legacy.is_dir() else current


def skill_home() -> Path:
    return Path(env("SKILL_HOME") or Path.home())


def _snapshot_layout(layout) -> None:
    if layout is None:
        return
    if not isinstance(layout, dict) or layout.get("type") != "latest-subdir":
        raise _error("agent.237")
    pattern = layout.get("pattern", "*")
    subpaths = layout.get("subpaths", ["."])
    if not isinstance(pattern, str) or not pattern or any(c in pattern for c in ("/", "\\", ":")) or pattern in (".", ".."):
        raise _error("agent.238")
    if not isinstance(subpaths, list) or not subpaths or not all(
        isinstance(p, str) and p and not p.startswith(("/", "\\")) and ":" not in p
        and ".." not in p.replace("\\", "/").split("/") for p in subpaths
    ):
        raise _error("agent.239")


def skill_snapshots() -> list[dict]:
    override = env("SKILL_SNAPSHOTS")
    try:
        rows = json.loads(override) if override is not None else load().get("skill_snapshots", [])
    except json.JSONDecodeError:
        raise _error("agent.242") from None
    if not isinstance(rows, list) or not all(
        isinstance(row, dict) and all(isinstance(row.get(k), str) and row[k].strip() for k in ("key", "name", "root"))
        for row in rows
    ):
        raise _error("agent.240")
    rows = [dict(row) for row in rows]
    for row in rows:
        _snapshot_layout(row.get("layout"))
    return rows


def skill_harnesses() -> list[dict]:
    """额外本机工具目录；与内置同 key 的路径追加，新 key 独立登记。"""
    override = env("SKILL_HARNESSES")
    try:
        rows = json.loads(override) if override is not None else load().get("skill_harnesses", [])
    except json.JSONDecodeError:
        raise _error("agent.666") from None
    if not isinstance(rows, list) or not all(
        isinstance(row, dict)
        and all(isinstance(row.get(k), str) and row[k].strip() for k in ("key", "name"))
        and isinstance(row.get("user"), list)
        and all(isinstance(row.get(k, []), list)
                and all(isinstance(p, str) and p.strip() for p in row.get(k, []))
                for k in ("user", "builtin", "plugins"))
        for row in rows
    ):
        raise _error("agent.665")
    return [dict(row) for row in rows]


def shared_folders() -> list[dict]:
    """开放给指定伙伴只读搜索的本机文件夹；每项有 key、name、path、readers，可选 exclude。"""
    import re

    override = env("SHARED_FOLDERS")
    try:
        rows = json.loads(override) if override is not None else load().get("shared_folders", [])
    except json.JSONDecodeError:
        raise _error("agent.668") from None
    if not isinstance(rows, list) or not all(
        isinstance(row, dict)
        and all(isinstance(row.get(k), str) and row[k].strip() for k in ("key", "name", "path"))
        and re.fullmatch(r"[a-z0-9][a-z0-9-]*", row["key"])
        and isinstance(row.get("readers"), list) and row["readers"]
        and all(isinstance(r, str) and r.strip() for r in row["readers"])
        and isinstance(row.get("exclude", []), list)
        and all(isinstance(p, str) and p.strip() for p in row.get("exclude", []))
        for row in rows
    ) or len({row["key"] for row in rows}) != len(rows):
        raise _error("agent.667")
    return [dict(row, exclude=list(row.get("exclude", []))) for row in rows]


def backup_dir() -> Path | None:
    """备份由用户选择本机目录；不配置就不备份。"""
    value = _text("backup_dir", "BACKUP_DIR")
    return Path(value) if value else None


def source_repo() -> Path | None:
    """备份时要打包的代码仓库；不配置就从包所在目录往上找源码 checkout。"""
    value = _text("source_repo", "SOURCE_REPO")
    return Path(value) if value else None


def invalidate() -> None:
    """配置文件刚被写过时清掉缓存，同一次进程里下一回读到新内容。"""
    global _cached
    _cached = None
