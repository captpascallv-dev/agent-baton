"""路由能力和写权限。给谁看哪些字段不在这里，在两套投影里。"""

from __future__ import annotations

from ..i18n import text as tr

import re

from starlette.exceptions import HTTPException

from .. import feishu
from ..store import BaseError, Doc, Viewer

# 每个接口都显式列出两种接入能力；新增业务接口远程缺省关闭。
ROUTE_POLICY = {
    ("GET", "/api/ping"): (True, True),
    ("GET", "/api/me"): (True, True),
    ("GET", "/api/home"): (True, True),
    ("POST", "/api/home/adopt"): (True, True),
    ("POST", "/api/home/discard"): (True, True),
    ("GET", "/api/activity"): (True, True),
    ("GET", "/api/commit/{sha}"): (True, True),
    ("GET", "/api/project/{pid}"): (True, True),
    ("POST", "/api/project/{pid}/status"): (True, True),
    ("GET", "/api/doc"): (True, True),
    ("GET", "/api/search"): (True, True),
    ("GET", "/api/todos"): (True, True),
    ("GET", "/api/apps"): (True, True),
    ("GET", "/api/apps/probe"): (True, False),
    ("POST", "/api/apps/{aid}/open"): (True, False),
    ("POST", "/api/apps/{aid}/listed"): (True, False),
    ("POST", "/api/apps/{aid}/site"): (True, False),
    ("GET", "/api/agents"): (True, True),
    ("GET", "/api/projects"): (True, True),
    ("POST", "/api/projects"): (True, False),
    ("GET", "/api/docs"): (True, True),
    ("GET", "/api/decisions"): (True, True),
    ("GET", "/api/members"): (True, True),
    ("POST", "/api/members/{key}/archive"): (True, False),
    ("POST", "/api/members/{key}/restore"): (True, False),
    ("POST", "/api/tokens/issue"): (True, True),
    ("POST", "/api/tokens/revoke"): (True, True),
    ("POST", "/api/feishu/bind"): (True, False),
    ("POST", "/api/feishu/unbind"): (True, False),
    ("POST", "/api/feishu/dismiss"): (True, False),
    ("GET", "/api/approvals"): (True, False),
    ("GET", "/api/source"): (True, False),
    ("POST", "/api/source/save"): (True, False),
    ("POST", "/api/source/new"): (True, False),
    ("POST", "/api/decision/new"): (True, True),
    ("POST", "/api/handover/decide"): (True, False),
    ("POST", "/api/render"): (True, True),
    ("GET", "/api/skills"): (True, True),
    ("GET", "/api/skills/{id}"): (True, True),
    ("GET", "/api/skills/{id}/download"): (True, True),
    ("POST", "/api/skills/open"): (True, False),
    ("POST", "/api/skills/share"): (True, False),
    ("POST", "/api/todos/add"): (True, True),
    ("POST", "/api/todos/{tid}"): (True, True),
    ("POST", "/api/revert"): (True, False),
    ("POST", "/api/heartbeat"): (True, True),
}


def route_allowed(method: str, path: str, remote: bool) -> bool:
    method = "GET" if method == "HEAD" else method
    for (verb, template), policy in ROUTE_POLICY.items():
        if verb == method and re.fullmatch(re.sub(r"\{[^}]+\}", "[^/]+", template), path):
            return policy[1 if remote else 0]
    return not remote


def project_actions(d: Doc, viewer: Viewer) -> dict:
    writable = viewer.can_write and viewer.can_see(d) and (not viewer.assigned or d.id in viewer.projects)
    return {"append_status": writable, "record_decision": writable,
            "edit": viewer.is_owner and not viewer.remote}


def capabilities(viewer: Viewer) -> dict:
    local = viewer.is_owner and not viewer.remote
    human = viewer.known and viewer.valid and (viewer.is_owner or viewer.member.meta.get("kind") == "human")
    # 远程网页会话才退出、请求才带 CSRF。本机桌面这两项都没有。值和现在的 remote 一致。
    session = bool(viewer.remote)
    return {**{k: local for k in ("manage_company", "edit_sources", "approve_handovers", "revert_commits",
                                 "open_local", "probe_local", "manage_skill_share")},
            "write_company": viewer.can_write, "write_own_todos": human,
            "adopt_own_home": human, "manage_own_agent_tokens": human,
            "sign_out": session, "require_csrf": session,
            **({"feishu": True} if feishu.enabled() else {})}


def require_local_owner(principal) -> None:
    if not principal.is_owner or principal.remote:
        raise HTTPException(403, tr("local_owner_action", owner=principal.owner_name))


def reject_remote_visible_to(principal, data: dict) -> None:
    if principal.remote and "visible_to" in data:
        raise BaseError(tr("agent.633"))
