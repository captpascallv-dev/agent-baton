"""一次网页请求用的上下文：数据仓库、认证，以及各页共用的读法。"""

from __future__ import annotations

from ..i18n import text as tr

from pathlib import Path

from starlette.requests import Request
from starlette.responses import JSONResponse

from .. import config
from ..base import CompanyBase, remote_message
from ..redact import safe_text
from ..store import AuthRejected, member_keys
from . import history
from .policy import require_local_owner as _require_local_owner
from .projection import projection_for

HISTORY_LIMIT = 500


class WorkspaceCore:
    def __init__(self, root, auth):
        self.root = Path(root)
        self.auth = auth
        self.cookie = auth.cookie

    def snapshot(self, request: Request):
        # 和这次请求开头的认证共用一份文档。写锁里的凭据复核、请求结束后的权限复查都重新读盘。
        cached = getattr(request.state, "doc_cache", None)
        viewer, docs, visible = self.as_actor(request)._ctx(cached)
        if cached is None:
            request.state.doc_cache = docs
        visible = projection_for(viewer).narrow(viewer, docs, visible)
        by_rel = {d.rel: d for d in visible}
        by_id = {d.id: d for d in visible if d.kind == "project"}
        request.state.content_snapshot = (viewer, frozenset(d.rel for d in visible))
        return viewer, docs, visible, by_rel, by_id, member_keys(docs)

    def ctx(self, request: Request):
        return self.snapshot(request)[2:]

    def require_local_owner(self, request: Request):
        _require_local_owner(request.state.principal)

    def reply(self, request, data):
        return JSONResponse(projection_for(request.state.principal).text_fields(data))

    def activity_set(self, request, viewer, docs):
        return history.workspace_commits(self.root, viewer, docs, HISTORY_LIMIT)

    def authed(self, request: Request) -> bool:
        return getattr(request.state, "principal", None) is not None

    def as_actor(self, request: Request) -> CompanyBase:
        principal = request.state.principal
        auth = self.auth

        def auth_check():
            # 改动前再核一次凭据，不用请求开头缓存的文档。
            again = auth.authenticate(request)
            if (again is None or again.key != principal.key or again.auth_method != principal.auth_method
                    or again.session_id != principal.session_id):
                raise AuthRejected(tr("agent.613"))

        return CompanyBase(self.root, principal.key, allow_owner=principal.is_owner,
                           require_remote=principal.remote, auth_check=auth_check)

    def remote_urls(self) -> dict:
        auth = self.auth
        if auth.remote:
            return {"web": auth.origin + "/", "mcp": auth.origin + "/mcp"}
        host = config.public_host()
        if not host:
            return {"web": tr("public_host_missing"), "mcp": tr("public_host_missing")}
        return {"web": f"https://{host}/", "mcp": f"https://{host}/mcp"}

    def reply_not_found(self, error):
        return JSONResponse({"error": safe_text(remote_message(error, self.root))}, status_code=404)
