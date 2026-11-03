"""谁最近用过底座：MCP 和命令行每调一次工具，在本机记一行（时间、身份、工具名），不记内容。

读接口不产生 git 提交，“我的 Agent”页面靠这份记录知道哪个伙伴最近一次什么时候用过。
记录在 %LOCALAPPDATA%\\Onestead\\usage.jsonl（已有安装继续用旧状态目录），只在本机，超过 2MB 换一份（只留上一份）。
测试和临时环境用环境变量 ONESTEAD_USAGE_LOG 指到别处；设成空字符串就不记。
"""

from __future__ import annotations

import datetime as dt
import json
import os
from pathlib import Path

from . import config
from .store import TAIPEI

MAX_BYTES = 2 * 1024 * 1024


def log_path() -> Path | None:
    override = config.env("USAGE_LOG")
    if override is not None:
        return Path(override) if override.strip() else None
    return config.state_dir() / "usage.jsonl"


def record(agent: str | None, tool: str, via: str) -> None:
    """记一行。失败不影响工具本身（记不下来就算了）。"""
    path = log_path()
    if path is None or not agent:
        return
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.exists() and path.stat().st_size > MAX_BYTES:
            path.replace(path.with_suffix(".1.jsonl"))
        line = json.dumps({"t": dt.datetime.now(TAIPEI).isoformat(timespec="seconds"), "agent": agent,
                           "tool": tool, "via": via}, ensure_ascii=False)
        with open(path, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass


def summary(days: int = 7) -> dict[str, dict]:
    """每个身份：最近一次用的时间、用的什么、最近几天调用了多少次。"""
    path = log_path()
    out: dict[str, dict] = {}
    if path is None:
        return out
    since = dt.datetime.now(TAIPEI) - dt.timedelta(days=days)
    for p in (path.with_suffix(".1.jsonl"), path):
        if not p.exists():
            continue
        try:
            lines = p.read_text(encoding="utf-8").splitlines()
        except OSError:
            continue
        for raw in lines:
            try:
                row = json.loads(raw)
                when = dt.datetime.fromisoformat(row["t"])
            except (ValueError, KeyError, TypeError):
                continue
            rec = out.setdefault(row.get("agent", "?"), {"last": None, "last_tool": None, "last_via": None, "recent": 0})
            if rec["last"] is None or when >= dt.datetime.fromisoformat(rec["last"]):
                rec.update(last=when.isoformat(), last_tool=row.get("tool"), last_via=row.get("via"))
            if when >= since:
                rec["recent"] += 1
    return out
