"""把 git 作者和文件路径翻成人看的短句。历史和页面视图共用，不互相引用。"""

from __future__ import annotations

from .. import config

from pathlib import Path

from ..store import Doc, Viewer


def file_label(rel: str, by_rel: dict[str, Doc]) -> str:
    parts = rel.split("/")
    if parts[0] == "projects" and len(parts) == 3:
        main = by_rel.get(f"projects/{parts[1]}/project.md")
        name = main.title if main else parts[1]
        kind = {"project.md": "项目信息", "status.md": "进展", "links.md": "相关链接"}.get(parts[2], parts[2])
        return f"{name} · {kind}"
    doc = by_rel.get(rel)
    title = doc.title if doc else Path(rel).stem
    prefix = {"decisions": "决定", "assets": "资产", "people": "成员", "handbook": "手册",
              "todos": "待办", "apps": "应用", "skills": "Skill"}.get(parts[0])
    if rel == "FORMAT.md":
        return "格式说明"
    if parts[0] == "home":
        return "首页草稿" if rel.endswith("-draft.md") else "首页布局"
    return f"{prefix}：{title}" if prefix else rel


def author_view(name: str, members: dict[str, Doc], viewer: Viewer | None = None) -> dict:
    """git 作者名形如 codex 或 codex · alex。"""
    key, _, signed = (s.strip() for s in name.partition("·"))
    if key == (viewer.key if viewer else config.owner_key()):
        display = "你"
    else:
        member = members.get(key)
        display = member.title if member else key
    return {"key": key, "signed": signed or None, "display": display,
            "full": f"{display} · {signed}" if signed else display, "initial": (display or "?")[:1]}
