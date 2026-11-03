"""旧地址 /portal 只保留固定跳转和静态别名；业务与认证由根路径工作台负责。"""

from __future__ import annotations

from urllib.parse import quote

from starlette.requests import Request
from starlette.responses import RedirectResponse


def register(mcp) -> None:
    """不读取旧 cookie、口令或业务内容；跳转目标会重新鉴权。"""
    def redirect(target):
        async def endpoint(request: Request):
            return RedirectResponse(target(request), status_code=303)
        return endpoint

    # 参数只进入固定路径中的一个编码段，不能成为任意跳转地址。
    routes = (
        ("/portal", lambda r: "/"),
        ("/portal/p/{pid}", lambda r: "/#/projects/" + quote(r.path_params["pid"], safe="")),
        ("/portal/a/{aid}", lambda r: "/#/docs/" + quote(r.path_params["aid"], safe="")),
        ("/portal/decisions", lambda r: "/#/decisions"),
        ("/portal/search", lambda r: "/#/search?q=" + quote((r.query_params.get("q") or "").strip()[:100], safe="")),
        ("/portal/skills/{sid}.zip", lambda r: "/api/skills/" + quote(r.path_params["sid"], safe="") + "/download"),
        ("/portal/skills/{sid}", lambda r: "/#/skills/" + quote(r.path_params["sid"], safe="")),
        ("/portal/static/{name:path}", lambda r: "/static/" + quote(r.path_params["name"], safe="")),
    )
    for path, target in routes:
        mcp.custom_route(path, methods=["GET"])(redirect(target))
    # 旧表单不执行登录或退出，也不消费口令；新表单必须带登录挑战/CSRF。
    for path in ("/portal/login", "/portal/logout"):
        mcp.custom_route(path, methods=["GET", "POST"])(redirect(lambda r: "/login"))
