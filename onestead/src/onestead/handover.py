"""谁的接任申请还算数。网页“待批准”和批准接口共用这一份，不放在网页层。"""

from __future__ import annotations

from . import config

import re

from .store import Doc, make_viewer, member_keys

APPLY_RE = re.compile(r"申请接任：「(.+?)」由「(.+?)」换成「(.+?)」，待 .+? 批准")
HANDOVER_RE = re.compile(r"交接：「(.+?)」由「(.+?)」换成「(.+?)」")
REJECT_RE = re.compile(r"不批接任：「(.+?)」由「(.+?)」换成「(.+?)」")


def short(name: str | None) -> str:
    """“官网改版（Website Refresh）”→“官网改版”；括号前没字就保留原样。"""
    if not name:
        return ""
    head = re.split(r"[（(]", name, maxsplit=1)[0].strip()
    return head or name


def _entry_author(head: str) -> str:
    """进展标题“## 时间 · 身份 · 署名”里的身份。"""
    parts = head[3:].split(" · ")
    return parts[1].strip() if len(parts) > 1 else ""


def _status_entries(body: str) -> list[str]:
    parts = re.split(r"\n(?=## )", "\n" + body)
    return [p.strip("\n") for p in parts if p.startswith("## ")]


def pending_handovers(visible: list[Doc], by_id: dict[str, Doc]) -> list[dict]:
    # 按有效权限认申请作者，不能只看 agent 自填的 full；不批仍只认 主人。
    insiders = {config.owner_key()}
    for key in member_keys(visible):
        viewer = make_viewer(visible, key, allow_owner=True)
        if viewer.valid and not viewer.assigned:
            insiders.add(key)
    out = []
    for d in visible:
        if d.kind != "project-status":
            continue
        pid = d.rel.split("/")[1]
        pending: dict[str, dict] = {}
        for entry in _status_entries(d.body):
            head, _, text = entry.partition("\n")
            author = _entry_author(head)
            if author not in insiders:
                continue
            matched = APPLY_RE.search(text)
            if matched:
                pending[matched.group(1)] = {"project": pid, "role": matched.group(1), "from": matched.group(2),
                                              "to": matched.group(3), "head": head[3:].strip()}
                continue
            matched = HANDOVER_RE.search(text)
            if matched:  # 这个角色已经换过人，之前的申请都算处理了
                pending.pop(matched.group(1), None)
                continue
            matched = REJECT_RE.search(text)
            if matched and author == config.owner_key() and pending.get(matched.group(1), {}).get("to") == matched.group(3):
                pending.pop(matched.group(1), None)
        out.extend(pending.values())
    for item in out:
        item["project_name"] = short(by_id[item["project"]].title) if item["project"] in by_id else item["project"]
    return out
