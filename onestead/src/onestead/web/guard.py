"""进页面之前的安全外壳：地址、会话、路由能力、写来源。策略表在 policy.py。"""

from __future__ import annotations

from ..i18n import text as tr

import json
import re

from starlette.requests import Request
from starlette.responses import JSONResponse, RedirectResponse, Response

from .. import feishu
from ..base import remote_message
from ..redact import documents, safe_text
from ..store import AuthRejected, BaseError, Conflict
from .policy import route_allowed

CSP = ("default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; "
       "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")


class Guard:
    async def refuse(self, request: Request) -> Response | None:
        auth = self.auth
        path = request.url.path
        # 回调在自己的 try 内检查开关，保留配置读取异常时清 state 的原有行为。
        callback = auth.remote and path == "/portal/feishu/callback" and request.method in ("GET", "HEAD")
        # 未配置的集成不存在：旧地址和管理接口也不进入认证、跳转或写处理。
        if not callback and any(path == root or path.startswith(root + "/")
                                for root in ("/auth/feishu", "/portal/feishu", "/api/feishu")) and not feishu.enabled():
            return Response(status_code=404)
        if not auth.handles(path):
            hosts = request.headers.getlist("host")
            if len(hosts) != 1 or hosts[0] not in auth.legacy_hosts:
                # 旧地址 /portal 的登录和退出来源拒绝保留 403；/portal 读页和 MCP 保留原状态码。
                status = 421 if path.startswith("/mcp") else 403 if path in ("/portal/login", "/portal/logout") else 400
                return JSONResponse({"error": tr("agent.618")},
                                    status_code=status)
            if path.startswith(("/mcp", "/skills/")) and request.headers.get("origin") is not None:
                if request.headers.getlist("origin") not in [[o] for o in auth.legacy_origins]:
                    return JSONResponse({"error": tr("agent.622")}, status_code=403)
            return None
        # 顺序固定：Host、会话、路由能力、写来源与 CSRF；被封闭的 handler 不执行。
        if not auth.host_valid(request):
            return JSONResponse({"error": tr("agent.618")}, status_code=403)
        request.state.reuse_docs = True
        principal = auth.authenticate(request)
        request.state.principal = principal
        api = path.startswith("/api/")
        if (api or path == "/auth/logout") and principal is None:
            if request.method in ("GET", "HEAD") and re.fullmatch(r"/api/skills/[^/]+/download", path):
                return RedirectResponse("/login", status_code=303)
            return JSONResponse({"error": tr("agent.613")}, status_code=401)
        if api and not route_allowed(request.method, path, auth.remote):
            return JSONResponse({"error": tr("agent.619")}, status_code=403)
        if request.method not in ("GET", "HEAD"):
            allowed = auth.write_valid(request) if api else True
            if auth.remote and path in ("/auth/login", "/auth/logout"):
                allowed = auth.origin_valid(request) and (path == "/auth/login" or auth.csrf_valid(request))
            if not allowed:
                return JSONResponse({"error": tr("agent.620")}, status_code=403)
            if api or path == "/auth/login":
                limit = 8 * 1024 if path == "/auth/login" else 256 * 1024
                chunks, size = [], 0
                async for chunk in request.stream():
                    size += len(chunk)
                    if size > limit:
                        return JSONResponse({"error": tr("agent.623")}, status_code=413)
                    chunks.append(chunk)
                request._body = b"".join(chunks)
                if api:
                    try:
                        data = json.loads(request._body)
                    except (ValueError, UnicodeError):
                        return JSONResponse({"error": tr("agent.624")}, status_code=400)
                    if not isinstance(data, dict):
                        return JSONResponse({"error": tr("agent.624")}, status_code=400)
        return None

    async def guard(self, request: Request, call_next):
        auth = self.auth
        try:
            resp = await self.refuse(request)
            if resp is None:
                resp = await call_next(request)
                if auth.remote and request.url.path.startswith("/api/"):
                    # 请求结束后重新认证，并在成功响应上重新读权限。不用请求开头缓存的文档。
                    # 处理函数若已按缓存给出 4xx，会话这时无效的，同样不能把那个结果发出去。
                    if auth.authenticate(request) is None:
                        resp = JSONResponse({"error": tr("agent.613")}, status_code=401)
                    elif resp.status_code < 400 and hasattr(request.state, "content_snapshot"):
                        before, paths = request.state.content_snapshot
                        after, current_docs, _ = self.as_actor(request)._ctx()
                        current_paths = {d.rel for d in documents(after, current_docs)}
                        fields = ("access_mode", "projects", "assets", "skills", "names", "acl", "workspace_owner")
                        if any(getattr(before, f) != getattr(after, f) for f in fields) or not paths <= current_paths:
                            resp = JSONResponse({"error": tr("agent.625")}, status_code=403)
        except Conflict as e:
            resp = JSONResponse({"error": safe_text(remote_message(e, self.root)) if auth.remote else str(e)},
                                status_code=409)
        except AuthRejected:
            resp = JSONResponse({"error": tr("agent.613")}, status_code=401)
        except BaseError as e:
            resp = JSONResponse({"error": safe_text(remote_message(e, self.root)) if auth.remote else str(e)},
                                status_code=400)
        except Exception:
            resp = JSONResponse({"error": tr("agent.621")}, status_code=500)
        resp.headers.setdefault("Content-Security-Policy", CSP)
        resp.headers["X-Content-Type-Options"] = "nosniff"
        resp.headers.setdefault("Referrer-Policy", "same-origin" if auth.remote and
                                resp.headers.get("content-type", "").startswith("text/html") else "no-referrer")
        resp.headers["Cache-Control"] = "no-store"
        resp.headers["X-Workspace-Context"] = "remote" if auth.remote else "local"
        if auth.remote:
            # 旧的 /portal cookie 不升级成工作台会话；清掉旧路径和可能残留的根路径副本。
            old_names = {"cb_portal"} | {n for n in request.cookies if n.startswith("cb_portal")}
            for name in old_names:
                for old_path in ("/portal", "/"):
                    resp.delete_cookie(name, path=old_path, secure=auth.secure, httponly=True, samesite="strict")
        return resp
