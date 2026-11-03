"""同事看见的人名。本机视图和同事投影都从这里取，避免两边互相引用。"""

from __future__ import annotations

from ..redact import safe_text
from ..store import Doc, Viewer, member_cards, member_keys


def _may_show_member_name(doc: Doc, viewer: Viewer) -> bool:
    """和名录同一套：本机 主人 总能看到；按授权读的人看不到未点名的限定成员。"""
    if viewer.is_owner and not viewer.remote:
        return True
    if not (viewer.known and viewer.valid):
        return False
    if not viewer.assigned:
        return True
    if doc.sensitivity != "restricted":
        return True
    key = doc.meta.get("agent_key")
    return key in viewer.names or viewer.acl in (doc.meta.get("visible_to") or [])


def actor(key: str | None, viewer: Viewer, docs: list[Doc]) -> dict:
    allowed = {c["key"]: c for c in member_cards(viewer, docs)}
    card = allowed.get(key)
    if card:
        return {"key": key, "name": "你" if key == viewer.key else safe_text(card["title"])}
    archived = member_keys(docs).get(key) if key else None
    if archived is not None and archived.meta.get("archived") is True and _may_show_member_name(archived, viewer):
        return {"key": key, "name": "你" if key == viewer.key else safe_text(archived.title)}
    return {"key": None, "name": "成员"}


def status_head(head: str, viewer: Viewer, docs: list[Doc]) -> str:
    """进展标题里的身份键换成名录上的名字，规则和动态里的作者一样。"""
    text = safe_text(head)
    parts = [part.strip() for part in text.split("·")]
    if len(parts) >= 2 and parts[1]:
        parts[1] = actor(parts[1], viewer, docs)["name"]
        return " · ".join(parts)
    return text


def author(value, viewer: Viewer, docs: list[Doc]) -> str:
    return actor(str(value or "").partition("·")[0].strip(), viewer, docs)["name"]
