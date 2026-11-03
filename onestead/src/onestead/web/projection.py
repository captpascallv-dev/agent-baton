"""按观众选一套投影。两边函数同名同参数，handler 不再写 remote 分支。

本机视图和同事白名单仍是两套实现。合成一个函数再加 if remote 会把本机字段带给同事。
"""

from __future__ import annotations

from .. import config, feishu, sites

from ..redact import documents, html, text_fields
from ..store import make_viewer
from . import present, views
from .policy import require_local_owner
from .refs import colleague_author, local_who
from .render import to_html


class LocalProjection:
    """本机页面：保留路径、问题和操作。脱敏只出现在本来就会遮的那几处，不经过这里。"""

    def narrow(self, viewer, docs, visible):
        return visible

    def text_fields(self, value):
        return value

    def html(self, text):
        return to_html(text)

    def status_head(self, head, viewer, docs):
        return head

    def todo_list(self, visible, viewer, docs, today, by_id, members):
        return views.todo_list(visible, today, by_id, members)

    def apps(self, visible, viewer, docs, by_id, members, *, listed_only, with_actions):
        rows = views.app_list(visible, by_id, members)
        if config.site_domain():
            source = {doc.id: doc for doc in visible if doc.kind == "app"}
            for row in rows:
                if row["kind"] == "网页":
                    row["site"] = sites.summary(source[row["id"]])
                    row["site_people"] = sites.people(docs)
        if listed_only:
            rows = [row for row in rows if row["listed"]]
        if with_actions:
            for row in rows:
                row["allowed_actions"] = {"open_local": True, "probe": True, "listed": True,
                                           "open_url": bool((row.get("open") or {}).get("url"))}
        return rows

    def project_list(self, visible, viewer, docs, commits, today, members, states=()):
        return views.projects(visible, commits, members, today, states)

    def project_row(self, doc, visible, viewer, docs, commits, today, members):
        return next(row for row in views.projects(visible, commits, members, today) if row["id"] == doc.id)

    def project_apps(self, visible, project_id):
        return views.project_app_cards(visible, project_id)

    def doc(self, doc, viewer, docs):
        return views.doc_view(doc)

    def doc_list(self, visible):
        return views.doc_list(visible)

    def search(self, visible, query):
        return views.search(visible, query)

    def decisions_payload(self, visible, viewer, docs, by_id, members, projects):
        return {"items": views.decision_list(visible, by_id, members), "projects": projects,
                "members": [{"key": key, "name": member.title} for key, member in members.items() if key != config.owner_key()]}

    def home_payload(self, now, modules, preview, draft, viewer, docs, by_id, members, visible, pending):
        shown = draft
        if draft and draft.get("by"):
            shown = {**draft, "by_name": local_who(draft["by"], members)["name"]}
        remote_members = [{"key": item.meta["agent_key"], "title": item.title} for item in visible
                          if item.kind == "person" and item.meta.get("archived") is not True
                          and item.meta.get("remote_enabled") is True and item.meta.get("kind") == "human"
                          and item.meta.get("agent_key")]
        result = {"now": now, "modules": modules, "draft": shown, "preview": preview}
        if feishu.enabled():
            result |= {"feishu_pending": pending(), "remote_members": remote_members}
        return result

    def session_fields(self, request, cookie_name):
        return {"access_context": "local", "csrf": None}

    def members_page(self, viewer, docs, visible, principal, tokens, pending, urls):
        # 本机上不是 主人 的人，成员页也只给同事那种简卡。
        if not viewer.is_owner:
            return {"items": present.members(viewer, docs)}
        require_local_owner(principal)
        result = {"items": views.member_list(visible, tokens()), "remote": urls()}
        if feishu.enabled():
            result["feishu_pending"] = pending()
        return result

    async def skill_list(self, visible, by_id, fresh, principal, run, shared_list, include_unlisted=False):
        require_local_owner(principal)
        return await run(views.skill_page, visible, by_id, fresh, include_unlisted)

    def agents_page(self, rows, viewer, docs, principal, prompt):
        for row in rows:
            target = make_viewer(docs, row["key"])
            row["allowed_actions"] = {"issue_token": target.can_remote, "revoke_token": True,
                                       "open_token_drawer": False}
        require_local_owner(principal)
        return {"agents": rows, "connect_prompt": prompt}

    def activity_chart(self, commits, members, days, viewer, docs):
        return views.activity_chart(commits, days, lambda raw: local_who(raw, members, viewer)["name"],
                                    skip_owner=True)


class RemoteProjection:
    """同事页面：只走白名单投影，字段先遮再交出去。"""

    def narrow(self, viewer, docs, visible):
        return documents(viewer, docs)

    def text_fields(self, value):
        return text_fields(value)

    def html(self, text):
        return html(text)

    def status_head(self, head, viewer, docs):
        return present.status_head(head, viewer, docs)

    def todo_list(self, visible, viewer, docs, today, by_id, members):
        return present.todo_list(visible, viewer, docs, today, by_id)

    def apps(self, visible, viewer, docs, by_id, members, *, listed_only, with_actions):
        return [present.app(item, viewer, docs, by_id) for item in visible if item.kind == "app"]

    def project_list(self, visible, viewer, docs, commits, today, members, states=()):
        return present.project_list(visible, viewer, docs, commits, today, states)

    def project_row(self, doc, visible, viewer, docs, commits, today, members):
        return present.project(doc, viewer, docs, commits, today)

    def project_apps(self, visible, project_id):
        return text_fields(views.project_app_cards(visible, project_id))

    def doc(self, doc, viewer, docs):
        return present.doc(doc, viewer, docs)

    def doc_list(self, visible):
        return present.doc_list(visible)

    def search(self, visible, query):
        return present.search(visible, query)

    def decisions_payload(self, visible, viewer, docs, by_id, members, projects):
        return {"items": present.decisions(visible, viewer, docs, by_id), "projects": text_fields(projects)}

    def home_payload(self, now, modules, preview, draft, viewer, docs, by_id, members, visible, pending):
        for module in modules:
            module["options"] = text_fields(module["options"])
            if module["kind"] == "apps":
                ids = {item.id for item in visible if item.kind == "app"}
                module["options"] = {"ids": [sid for sid in module["options"]["ids"] if sid in ids]}
        return {"now": now, "modules": modules,
                "draft": present.draft(draft, viewer, docs, by_id), "preview": preview,
                "scope": "最近 500 次改动里你能看到的"}

    def session_fields(self, request, cookie_name):
        from .auth import csrf_value
        return {"access_context": "remote", "csrf": csrf_value(request.cookies[cookie_name])}

    def members_page(self, viewer, docs, visible, principal, tokens, pending, urls):
        return {"items": present.members(viewer, docs)}

    async def skill_list(self, visible, by_id, fresh, principal, run, shared_list, include_unlisted=False):
        return {"mode": "shared", "skills": await run(shared_list())}

    def agents_page(self, rows, viewer, docs, principal, prompt):
        return {"agents": present.agents(rows, viewer, docs)}

    def activity_chart(self, commits, members, days, viewer, docs):
        return views.activity_chart(commits, days, lambda raw: colleague_author(raw, viewer, docs),
                                    skip_owner=False)


LOCAL = LocalProjection()
REMOTE = RemoteProjection()


def projection_for(viewer):
    """这次请求用哪一套投影。给谁看什么只在这里看 remote。"""
    return REMOTE if viewer.remote else LOCAL
