"""共用网页路由：本机快捷方式与远程网页会话使用各自的认证适配器。

这一层防的是外部：别的网页冒充 主人 调接口、借域名绕进来。本机伙伴本来就能直接读写数据仓库，
不靠这一层拦；它们的改动都在 git 里留了作者。
"""

from __future__ import annotations

from ..i18n import text as tr

import hmac
import html as html_text
import re
import time
from pathlib import Path

from starlette.applications import Starlette
from starlette.concurrency import run_in_threadpool
from starlette.middleware import Middleware
from starlette.middleware.base import BaseHTTPMiddleware
from starlette.requests import Request
from starlette.responses import FileResponse, HTMLResponse, JSONResponse, PlainTextResponse, RedirectResponse, Response
from starlette.routing import Route

from .. import config, usage
from ..base import _status_entries, _strip_title
from ..connect import connect_prompt
from ..redact import html, text_fields
from ..store import BaseError, member_keys, now_taipei, owner_name
from . import history, views
from .admin import AdminApi
from .auth import LocalAuth, RemoteAuth
from .guard import Guard
from .home_api import HomeApi
from .policy import ROUTE_POLICY, capabilities, project_actions, reject_remote_visible_to
from .projection import projection_for
from .render import to_html
from .workspace import WorkspaceCore
from .i18n import locale, text

STATIC = Path(__file__).parent / "static"
def not_from_shortcut() -> str:
    name = html_text.escape(config.name())
    return (f'<!doctype html><html lang="{locale()}"><meta charset=utf-8><title>{name}</title>'
            f"<p style='font:15px system-ui;margin:40px'>{text('shortcut', name=name)}</p></html>")


class Workspace(HomeApi, AdminApi, Guard, WorkspaceCore):
    """一次拼出来的工作台。首页、本机管理和安全外壳在各自的文件里，这里留下其余页面。"""

    async def open_with_token(self, request: Request):
        if not hmac.compare_digest(request.query_params.get("t", "").encode(), self.auth.token.encode()):
            return HTMLResponse(not_from_shortcut(), status_code=403)
        resp = RedirectResponse("/", status_code=303)
        # 带有效期：浏览器整个关掉再开、点收藏的地址也还认；钥匙由启动器每 30 天换一把。
        resp.set_cookie(self.cookie, self.auth.token, max_age=30 * 86400, httponly=True, samesite="strict", path="/")
        return resp

    async def index(self, request: Request):
        if not self.authed(request):
            if self.auth.remote:
                return RedirectResponse("/login", status_code=303)
            return HTMLResponse(not_from_shortcut(), status_code=403)
        content = (STATIC / "index.html").read_text(encoding="utf-8")
        return HTMLResponse(content.replace("{{onestead_name}}", html_text.escape(config.name()))
                            .replace("{{onestead_lang}}", config.lang()))

    async def static(self, request: Request):
        name = request.path_params["name"]
        if not re.fullmatch(r"[a-z0-9-]+\.(css|js|svg)", name) or not (STATIC / name).exists():
            return PlainTextResponse(text("not_found"), status_code=404)
        return FileResponse(STATIC / name)

    async def me(self, request: Request):
        principal = request.state.principal
        viewer, docs, visible, _, by_id, _ = self.snapshot(request)
        p = viewer.member
        session = projection_for(principal).session_fields(request, self.auth.cookie)
        result = {"key": principal.key, "name": p.title if p else principal.key, "kind": "human",
                  "owner_name": owner_name(docs), "workspace_name": config.name(), "lang": config.lang(),
                  "is_owner": principal.is_owner, "remote": principal.remote,
                  "role": (p.meta.get("role") if p else None) or "",
                  "workspace_owner": viewer.workspace_owner, "access_context": session["access_context"],
                  "capabilities": capabilities(viewer),
                  "navigation": ["home", "activity", "todos", "projects", "apps", "agents", "skills", "docs",
                                 "decisions", "members"] + (["approvals"] if principal.is_owner else []),
                  "csrf": session["csrf"]}
        if principal.is_owner:
            result["site_domain"] = config.site_domain()
            result["pending"] = len(views.pending_handovers(visible, by_id))
        result["csrf_token"] = result["csrf"]
        return self.reply(request, result)

    async def activity(self, request: Request):
        viewer, docs, *_ = self.snapshot(request)
        return JSONResponse({"rows": [e["row"] for e in self.activity_set(request, viewer, docs)],
                             "scope": "最近 500 次改动里你能看到的"})

    async def commit(self, request: Request):
        viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
        sha = request.path_params["sha"]
        entries = self.activity_set(request, viewer, docs)
        entry = history.find_workspace_commit(entries, sha)
        if not entry:
            return JSONResponse({"error": tr("agent.604")}, status_code=404)
        row = entry["row"]
        if not entry["raw"]:
            return JSONResponse(row)
        match = entry["commit"]
        diff = [dict(f, label=history.file_label(f["path"], by_rel)) for f in history.commit_diff(self.root, match["sha"])]
        added = views.added_text(diff)
        body = "\n".join(l for l in match["body"].splitlines()
                         if not re.match(r"^(Agent|Signed-as|Reverts|Co-Authored-By):", l)
                         and not re.match(r"^由 .+(通过(?:公司底座|Onestead)写入|在网页界面撤回提交)", l)).strip()
        if match["reverts"]:
            target = history.find_workspace_commit(entries, match["reverts"])
            row["reverts_what"] = target["row"]["what"] if target else None
        return JSONResponse({**row, "body": body, "added_html": to_html(added) if added else None, "diff": diff})

    async def project(self, request: Request):
        viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
        d = by_id.get(request.path_params["pid"])
        if not d:
            return JSONResponse({"error": tr("agent.611")}, status_code=404)
        commits = [e["commit"] for e in self.activity_set(request, viewer, docs)]
        today = now_taipei().date()
        show = projection_for(viewer)
        row = show.project_row(d, visible, viewer, docs, commits, today, members)
        row["allowed_actions"] = project_actions(d, viewer)
        status = by_rel.get(d.rel.rsplit("/", 1)[0] + "/status.md")
        entries = _status_entries(status.body) if status else []
        row["status"] = []
        for entry in reversed(entries[-6:]):
            raw_head = entry.partition("\n")[0][3:].strip()
            row["status"].append({"head": show.status_head(raw_head, viewer, docs),
                                   "html": show.html(entry.partition("\n")[2])})
        row["status_total"] = len(entries)
        row["body_html"] = show.html(_strip_title(d.body))
        row["apps"] = show.project_apps(visible, d.id)
        return JSONResponse(row)

    async def project_status(self, request: Request):
        data = await request.json()
        if set(data) != {"text"} or not isinstance(data.get("text"), str):
            raise BaseError(tr("agent.607"))
        msg = self.as_actor(request).update_project_status(request.path_params["pid"], data["text"])
        return self.reply(request, {"ok": True, "message": msg.strip()})

    async def doc(self, request: Request):
        viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
        d = by_rel.get(request.query_params.get("rel", ""))
        if not d:
            return JSONResponse({"error": tr("agent.612")}, status_code=404)
        return JSONResponse(projection_for(viewer).doc(d, viewer, docs))

    async def search(self, request: Request):
        viewer, docs, visible, *_ = self.snapshot(request)
        query = request.query_params.get("q", "")
        return JSONResponse({"hits": projection_for(viewer).search(visible, query)})

    async def todo_page(self, request: Request):
        viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
        today = now_taipei().date()
        projects = sorted(({"id": d.id, "name": views.short(d.title), "state": d.meta.get("state")}
                           for d in visible if d.kind == "project"),
                          key=lambda p: (views.STATE_ORDER.get(p["state"], 9), p["name"]))
        show = projection_for(viewer)
        items = show.todo_list(visible, viewer, docs, today, by_id, members)
        for item in items:
            item["allowed_actions"] = {"edit": True}
        return JSONResponse({"today": today.isoformat(), "items": items,
                             "projects": show.text_fields(projects),
                             "allowed_actions": {"add": True}})

    async def app_page(self, request: Request):
        viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
        rows = projection_for(viewer).apps(visible, viewer, docs, by_id, members, listed_only=False, with_actions=True)
        return JSONResponse({"apps": rows})

    async def site_action(self, request: Request):
        from .policy import require_local_owner
        require_local_owner(request.state.principal)
        data = await request.json()
        return JSONResponse(await run_in_threadpool(self.as_actor(request).manage_site,
                                                    request.path_params["aid"], data))

    async def skill_list(self, request: Request):
        viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
        fresh = request.query_params.get("fresh") == "1"
        payload = await projection_for(viewer).skill_list(
            visible, by_id, fresh, request.state.principal, run_in_threadpool,
            lambda: self.as_actor(request).shared_skill_list,
            include_unlisted=request.query_params.get("include_unlisted") == "1")
        return JSONResponse(payload)

    async def skill_detail(self, request: Request):
        try:
            info = await run_in_threadpool(self.as_actor(request).shared_skill, request.path_params["id"], full_text=True)
        except BaseError as e:
            if str(e).startswith(tr("skill_missing_prefix")):
                return self.reply_not_found(e)
            raise
        return JSONResponse({**info, "html": html(info["skill_md"]),
                             "download_url": f"/api/skills/{info['id']}/download"})

    async def skill_download(self, request: Request):
        sid = request.path_params["id"]
        try:
            content = await run_in_threadpool(self.as_actor(request).download_skill, sid)
        except BaseError as e:
            if str(e).startswith(tr("skill_missing_prefix")):
                return self.reply_not_found(e)
            raise
        # 核心核当前共享权限；会话也在发送前重新核对，不能凭旧会话拿缓存包。
        if self.auth.authenticate(request) is None:
            return JSONResponse({"error": tr("agent.613")}, status_code=401)
        usage.record(request.state.principal.key, "skill_download", "web")
        return Response(content, media_type="application/zip", headers={
            "Content-Disposition": f'attachment; filename="{sid}.zip"', "Cache-Control": "no-store"})

    async def projects_page(self, request: Request):
        viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
        commits = [e["commit"] for e in self.activity_set(request, viewer, docs)]
        today = now_taipei().date()
        rows = projection_for(viewer).project_list(visible, viewer, docs, commits, today, members)
        for row in rows:
            row["allowed_actions"] = project_actions(by_id[row["id"]], viewer)
        return JSONResponse({"rows": rows})

    async def docs_page(self, request: Request):
        viewer, docs, visible, *_ = self.snapshot(request)
        return JSONResponse({"items": projection_for(viewer).doc_list(visible)})

    async def decisions_page(self, request: Request):
        viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
        projects = sorted(({"id": d.id, "name": views.short(d.title), "sensitivity": d.sensitivity,
                             "allowed_actions": project_actions(d, viewer)}
                           for d in visible if d.kind == "project" and project_actions(d, viewer)["record_decision"]),
                          key=lambda p: p["name"])
        return JSONResponse(projection_for(viewer).decisions_payload(
            visible, viewer, docs, by_id, members, projects))

    async def members_page(self, request: Request):
        viewer, docs, visible, *_ = self.snapshot(request)
        return JSONResponse(projection_for(viewer).members_page(
            viewer, docs, visible, request.state.principal,
            lambda: self.as_actor(request).token_list(), lambda: self.as_actor(request).feishu_pending(),
            self.remote_urls))

    async def token_issue(self, request: Request):
        data = await request.json()
        if set(data) - {"key", "label", "issued_by"}:
            raise BaseError(tr("agent.608"))
        # 兼容原网页正文；issued_by 即使出现也不透传，发证人只取认证身份。
        out = self.as_actor(request).token_issue(str(data.get("key", "")), str(data.get("label", "")))
        # 发出的原文是唯一允许返回凭据的响应，公开记录仍只含安全元数据。
        return JSONResponse({"token": out["token"], "record": text_fields(out["record"]),
                             "urls": self.remote_urls()})

    async def token_revoke(self, request: Request):
        data = await request.json()
        if set(data) != {"id"}:
            raise BaseError(tr("agent.609"))
        try:
            return JSONResponse(self.as_actor(request).token_revoke(str(data.get("id", ""))))
        except BaseError as e:
            if str(e) == tr("agent.584"):
                return self.reply_not_found(e)
            raise

    async def decision_new(self, request: Request):
        data = await request.json()
        if set(data) - {"title", "decision", "reason", "project", "sensitivity", "visible_to", "access_note", "decided_by"}:
            raise BaseError(tr("agent.610"))
        reject_remote_visible_to(request.state.principal, data)
        msg = self.as_actor(request).record_decision(
            data.get("title"), data.get("decision"), data.get("reason"), request.state.principal.key,
            data.get("project") or None, data.get("sensitivity") or "internal",
            data.get("visible_to") or None, data.get("access_note") or None)
        return self.reply(request, {"ok": True, "message": msg.strip()})

    async def render_preview(self, request: Request):
        data = await request.json()
        show = projection_for(request.state.principal)
        return JSONResponse({"html": show.html(str(data.get("text") or "")[:60000])})

    async def agents_page(self, request: Request):
        base = self.as_actor(request)
        viewer, docs, visible = base._ctx()
        members = member_keys(docs)
        commits = [e["commit"] for e in self.activity_set(request, viewer, docs)]
        rows = views.agent_rows(visible, commits, members, usage.summary(7), viewer, base.agent_token_list())
        prompt = connect_prompt(owner_name(docs))
        return JSONResponse(projection_for(viewer).agents_page(
            rows, viewer, docs, request.state.principal, prompt))

    async def todo_add(self, request: Request):
        data = await request.json()
        self.as_actor(request).add_todos([data])
        return JSONResponse({"ok": True})

    async def todo_update(self, request: Request):
        data = await request.json()
        visible, *_ = self.ctx(request)
        if not any(d.kind == "todo" and d.id == request.path_params["tid"] for d in visible):
            return JSONResponse({"error": tr("agent.614")}, status_code=404)
        msg = self.as_actor(request).update_todo(request.path_params["tid"], data)
        return self.reply(request, {"ok": True, "message": msg.strip()})

    async def home_adopt(self, request: Request):
        data = await request.json()
        return JSONResponse(self.as_actor(request).adopt_home(data.get("version")))

    async def home_discard(self, request: Request):
        data = await request.json()
        return JSONResponse(self.as_actor(request).discard_home(data.get("version")))

    async def heartbeat(self, request: Request):
        if not self.auth.remote:
            request.app.state.last_beat = time.monotonic()
        return JSONResponse({"ok": True})

    async def ping(self, request: Request):
        return JSONResponse({"ok": True})


def build_app(data_root: str | Path, port: int, token: str) -> Starlette:
    """保留桌面快捷方式、cookie 和心跳的本机入口。"""
    return build_workspace_app(data_root, LocalAuth(port, token, root=data_root))


def build_workspace_app(data_root: str | Path, auth: LocalAuth | RemoteAuth) -> Starlette:
    """同一套路由接可信认证适配器；远程业务必须先在策略表明确开放。"""
    ws = Workspace(data_root, auth)
    routes = [
        Route("/", ws.index),
        Route("/static/{name}", ws.static),
        Route("/api/ping", ws.ping),
        Route("/api/me", ws.me),
        Route("/api/home", ws.home_page),
        Route("/api/home/adopt", ws.home_adopt, methods=["POST"]),
        Route("/api/home/discard", ws.home_discard, methods=["POST"]),
        Route("/api/activity", ws.activity),
        Route("/api/commit/{sha}", ws.commit),
        Route("/api/project/{pid}", ws.project),
        Route("/api/project/{pid}/status", ws.project_status, methods=["POST"]),
        Route("/api/doc", ws.doc),
        Route("/api/search", ws.search),
        Route("/api/todos", ws.todo_page),
        Route("/api/apps", ws.app_page),
        Route("/api/apps/probe", ws.app_probe),
        Route("/api/apps/{aid}/open", ws.app_open, methods=["POST"]),
        Route("/api/apps/{aid}/listed", ws.app_listed, methods=["POST"]),
        Route("/api/apps/{aid}/site", ws.site_action, methods=["POST"]),
        Route("/api/agents", ws.agents_page),
        Route("/api/projects", ws.projects_page),
        Route("/api/projects", ws.project_new, methods=["POST"]),
        Route("/api/docs", ws.docs_page),
        Route("/api/decisions", ws.decisions_page),
        Route("/api/members", ws.members_page),
        Route("/api/members/{key}/archive", ws.member_archive, methods=["POST"]),
        Route("/api/members/{key}/restore", ws.member_restore, methods=["POST"]),
        Route("/api/tokens/issue", ws.token_issue, methods=["POST"]),
        Route("/api/tokens/revoke", ws.token_revoke, methods=["POST"]),
        Route("/api/feishu/bind", ws.feishu_bind, methods=["POST"]),
        Route("/api/feishu/unbind", ws.feishu_unbind, methods=["POST"]),
        Route("/api/feishu/dismiss", ws.feishu_dismiss, methods=["POST"]),
        Route("/api/approvals", ws.approvals_page),
        Route("/api/source", ws.source),
        Route("/api/source/save", ws.source_save, methods=["POST"]),
        Route("/api/source/new", ws.source_new, methods=["POST"]),
        Route("/api/decision/new", ws.decision_new, methods=["POST"]),
        Route("/api/handover/decide", ws.handover_decide, methods=["POST"]),
        Route("/api/render", ws.render_preview, methods=["POST"]),
        Route("/api/skills", ws.skill_list),
        Route("/api/skills/{id}/download", ws.skill_download),
        Route("/api/skills/{id}", ws.skill_detail),
        Route("/api/skills/open", ws.skill_open, methods=["POST"]),
        Route("/api/skills/share", ws.skill_share, methods=["POST"]),
        Route("/api/todos/add", ws.todo_add, methods=["POST"]),
        Route("/api/todos/{tid}", ws.todo_update, methods=["POST"]),
        Route("/api/revert", ws.do_revert, methods=["POST"]),
        Route("/api/heartbeat", ws.heartbeat, methods=["POST"]),
    ]
    if auth.remote:
        from .sites import enter
        async def site_enter(request: Request):
            return await enter(request, data_root)
        routes.append(Route("/sites/enter", site_enter))
        routes += auth.routes()
    else:
        routes.insert(0, Route("/open", ws.open_with_token))
    async def missing_page(request, error):
        # 机器路径的默认响应保持原样，只有人看的页面跟公司语言。
        message = "Not Found" if request.url.path.startswith("/api/") else text("not_found")
        return PlainTextResponse(message, status_code=404)

    app = Starlette(routes=routes, middleware=[Middleware(BaseHTTPMiddleware, dispatch=ws.guard)],
                    exception_handlers={404: missing_page})
    app.state.auth = auth
    if not auth.remote:
        app.state.last_beat = time.monotonic()
    return app
