"""两套投影共用的小件：项目引用和作者显示。名字怎么写由调用方决定。"""

from __future__ import annotations

from .. import config

from ..handover import short
from ..store import Doc, Viewer
from .labels import author_view
from .names import author as colleague_name


def project_ref(pid, by_id, name, *, keep_missing: bool):
    """有项目 id 才返回。名录里没有时，本机留着 id，同事直接丢掉。"""
    if not pid:
        return None
    found = by_id.get(pid)
    if found is None:
        return {"id": pid, "name": pid} if keep_missing else None
    return {"id": found.id, "name": name(found.title)}


def who_view(author: dict, viewer=None) -> dict:
    """显示用的名字：署名优先（alex），否则成员名去掉括号（claude）。"""
    if author["key"] == (viewer.key if viewer else config.owner_key()):
        shown = "你"
    else:
        shown = author["signed"] or short(author["display"])
    return {**author, "name": shown}


def local_who(value, members: dict[str, Doc], viewer: Viewer | None = None) -> dict:
    """本机作者：git 名字先对上成员，再取显示名。"""
    return who_view(author_view(value if isinstance(value, str) else str(value), members, viewer), viewer)


def colleague_author(value, viewer: Viewer, docs: list[Doc]) -> str:
    """同事看见的作者名。"""
    return colleague_name(value, viewer, docs)
