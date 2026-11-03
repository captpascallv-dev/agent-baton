"""把同事网页挂到远程服务上。MCP 和旧地址 /portal 的跳转仍由 server 提供，这里只做拼装。"""

from __future__ import annotations

from ..i18n import text as tr

from contextlib import asynccontextmanager

from mcp.server.auth.settings import AuthSettings
from mcp.server.transport_security import TransportSecuritySettings
from starlette.requests import Request
from starlette.responses import Response
from starlette.routing import Mount

from .. import portal, server, usage
from ..base import CompanyBase
from .. import config
from ..server import LOOPBACK, TokenGate, remote_auth_check
from ..skills import shared_not_found
from ..skillshare import PackageTooLarge
from ..store import AuthRejected, BaseError
from .oauth import OAuthProvider, routes as oauth_routes, SCOPES
from .app import build_workspace_app
from .auth import RemoteAuth


def remote_app(root: str, port: int, host: str = "127.0.0.1", public_host: str | None = None,
               *, web_test_origin: str | None = None):
    """共用工作台、远程接口（/mcp）和旧地址 /portal 的固定跳转。

    服务本身只监听本机。对外经 Cloudflare 隧道：隧道程序在这台电脑上连到 127.0.0.1，
    外面的请求带的主机名是 public_host（例如 base.example.com），HTTPS 由 Cloudflare 那头负责。
    """
    if host not in LOOPBACK:
        raise BaseError(tr("agent.631"))

    public_host = public_host or config.public_host(required=True)
    local = [f"{h}:{port}" for h in ("127.0.0.1", "localhost", "[::1]")]
    hosts = local + ([public_host] if public_host else [])
    web_auth = RemoteAuth(
        root, public_host, test_origin=web_test_origin,
        legacy_hosts=set(hosts),
        legacy_origins={f"http://{h}" for h in local} | {f"https://{public_host}"},
    )
    url = f"https://{public_host}" if public_host else f"http://{host}:{port}"
    gate = TokenGate(root)
    # 调用时再取，server.build_server 上的测试补丁才盖得住这次拼装。
    mcp = server.build_server(root, None, remote=True, token_verifier=gate,
                       auth=AuthSettings(issuer_url=url, resource_server_url=f"{url}/mcp",
                                         validate_token_resource=False))
    portal.register(mcp)

    @mcp.custom_route("/skills/{sid}.zip", methods=["GET"])
    async def skill_download(request: Request) -> Response:
        if request.headers.get("host", "") not in hosts:
            return Response("bad host", status_code=400)
        authorization = request.headers.get("authorization", "")
        scheme, _, token = authorization.partition(" ")
        found = await gate.verify_token(token) if scheme.lower() == "bearer" and token else None
        if not found:
            return Response("远程接口要带有效口令。", status_code=401, headers={"Cache-Control": "no-store"})
        sid = request.path_params["sid"]
        try:
            content = CompanyBase(root, found.client_id, require_remote=True,
                                  auth_check=remote_auth_check(root, token, found.client_id)).download_skill(sid)
        except AuthRejected:
            return Response("远程接口要带有效口令。", status_code=401, headers={"Cache-Control": "no-store"})
        except BaseError as error:
            too_large = isinstance(error, PackageTooLarge)
            message = str(error) if too_large else str(shared_not_found(sid))
            return Response(message, status_code=400 if too_large else 404,
                            headers={"Cache-Control": "no-store"})
        except Exception:
            return Response("底座这次没办成，请稍后再试。", status_code=500, headers={"Cache-Control": "no-store"})
        usage.record(found.client_id, "skill_download", "remote")
        return Response(content, media_type="application/zip", headers={
            "Content-Disposition": f'attachment; filename="{sid}.zip"', "Cache-Control": "no-store",
            "X-Content-Type-Options": "nosniff"})
    # 防借域名绕进来（DNS 重绑定）：只认本机地址和对外的那个域名
    security = TransportSecuritySettings(
        enable_dns_rebinding_protection=True, allowed_hosts=hosts,
        allowed_origins=[f"http://{h}" for h in local] + ([f"https://{public_host}"] if public_host else []))
    mcp_app = mcp.streamable_http_app(stateless_http=True, json_response=True, host=host, transport_security=security)
    oauth = OAuthProvider(root, url)
    chatgpt = server.build_server(root, None, remote=True, oauth_provider=oauth, token_verifier=oauth,
                                auth=AuthSettings(issuer_url=url, resource_server_url=oauth.resource,
                                                  required_scopes=SCOPES, validate_token_resource=True))
    chatgpt_app = chatgpt.streamable_http_app(stateless_http=True, json_response=True,
                                             host=host, transport_security=security)
    # 挂到 /chatgpt 后，元数据的规范路径由父应用注册，不能再加一层挂载前缀。
    chatgpt_app.router.routes = [r for r in chatgpt_app.router.routes if not r.path.startswith("/.well-known/")]

    @asynccontextmanager
    async def lifespan(app):
        # MCP 的会话管理器仍由原子应用启停；明确网页路由先匹配，机器路由才交子应用。
        async with mcp_app.router.lifespan_context(mcp_app):
            async with chatgpt_app.router.lifespan_context(chatgpt_app):
                yield

    app = build_workspace_app(root, web_auth)
    app.router.routes.extend(oauth_routes(oauth))
    app.router.routes.append(Mount("/chatgpt", app=chatgpt_app))
    app.router.routes.append(Mount("/", app=mcp_app))
    app.router.lifespan_context = lifespan
    from .sites import SiteRouter
    app.add_middleware(SiteRouter, root=root, base_hosts=hosts, public_host=public_host)
    return app
