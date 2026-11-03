"""项目正文的分节：解析、按固定顺序插入、改写现状、下一步、分工、入口。"""

from __future__ import annotations

from .i18n import text as tr

import datetime as dt
import re

from .store import BaseError
from .textutil import _check_text

SUMMARY_SECTIONS = ("现状", "下一步", "分工", "入口")  # agent 能改的正文节
BODY_ORDER = ("目标", "现状", "下一步", "分工", "入口", "边界与注意")


def _clean_summary_sections(sections, owner_name: str) -> dict[str, str]:
    """只收四节。多出来的节名整次拒绝，一节里的二级标题和像密钥的内容也拒绝。"""
    if not isinstance(sections, dict) or not sections:
        raise BaseError(tr("agent.349"))
    bad = []
    normalized = []
    for key, value in sections.items():
        name = str(key).strip()
        if name not in SUMMARY_SECTIONS:
            bad.append(name or str(key))
        else:
            normalized.append((name, value))
    if bad:
        raise BaseError(tr("agent.350", v0='、'.join(bad), v1=owner_name))
    cleaned = {}
    for name, value in normalized:
        if not isinstance(value, str):
            raise BaseError(tr("agent.351", v0=name))
        text = value.replace("\r\n", "\n").replace("\r", "\n")
        if re.search(r"(?m)^## ", text):
            raise BaseError(tr("agent.352", v0=name))
        cleaned[name] = _check_text(name, text, required=False)
    return cleaned


def _section_name(heading: str) -> str | None:
    matched = re.match(r"^##\s+(.+?)\s*$", heading)
    if not matched:
        return None
    title = matched.group(1).strip()
    for name in sorted(BODY_ORDER, key=len, reverse=True):
        if title == name:
            return name
        if title.startswith(name) and title[len(name)] in "（(:： ":
            return name
    return None


def _parse_sections(body: str) -> tuple[str, list[dict]]:
    matches = list(re.finditer(r"(?m)^## .+$", body))
    if not matches:
        return body, []
    sections = []
    for index, matched in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(body)
        heading = matched.group(0)
        sections.append({"name": _section_name(heading), "heading": heading,
                         "content": body[matched.end():end].strip("\n")})
    return body[:matches[0].start()], sections


def _insert_section(sections: list[dict], name: str, heading: str, content: str) -> None:
    """缺的节按目标、现状、下一步、分工、入口、边界与注意插进去，不认识的节留在原处。"""
    order = {item: index for index, item in enumerate(BODY_ORDER)}
    target = order[name]
    insert_at = None
    for index, section in enumerate(sections):
        rank = order.get(section["name"])
        if rank is None:
            continue
        if rank < target:
            insert_at = index + 1
        elif rank > target:
            if insert_at is None:
                insert_at = index
            break
    if insert_at is None:
        insert_at = 0 if sections and all(section["name"] not in order for section in sections) else len(sections)
    sections.insert(insert_at, {"name": name, "heading": heading, "content": content})


def _refresh_as_of(heading: str, today: dt.date) -> str:
    """只改标题里的「截至 YYYY-MM-DD」，「以…台账为准」这类后缀留着。"""
    return re.sub(r"截至 \d{4}-\d{2}-\d{2}", f"截至 {today.isoformat()}", heading)


def _apply_summary_sections(body: str, cleaned: dict[str, str], today: dt.date) -> str:
    preamble, sections = _parse_sections(body.replace("\r\n", "\n"))
    for name in SUMMARY_SECTIONS:
        if name not in cleaned:
            continue
        found = next((section for section in sections if section["name"] == name), None)
        if found:
            found["content"] = cleaned[name]
            if name == "现状":
                found["heading"] = _refresh_as_of(found["heading"], today)
        else:
            _insert_section(sections, name, f"## {name}", cleaned[name])
    blocks = []
    preamble = preamble.strip("\n")
    if preamble:
        blocks.append(preamble)
    for section in sections:
        text = section["content"].strip("\n")
        blocks.append(section["heading"] + (f"\n\n{text}" if text else ""))
    return "\n\n".join(blocks) + "\n"
