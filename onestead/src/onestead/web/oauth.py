"""ChatGPT 专用 OAuth：SDK 处理协议，本模块连接本机凭据与现有成员权限。"""

from __future__ import annotations

from ..i18n import text as tr

import base64
import binascii
import datetime as dt
import html
import json
import logging
import re
from pathlib import Path
from urllib.parse import unquote, urlencode, urlsplit

from mcp.server.auth.handlers.authorize import AuthorizationHandler
from mcp.server.auth.handlers.register import RegistrationHandler
from mcp.server.auth.handlers.revoke import RevocationHandler
from mcp.server.auth.handlers.token import TokenHandler
from mcp.server.auth.middleware.client_auth import AuthenticationError, ClientAuthenticator
from mcp.server.auth.provider import (AccessToken, AuthorizationCode, AuthorizationParams,
                                      OAuthAuthorizationServerProvider, RefreshToken,
                                      RegistrationError, TokenError, construct_redirect_uri)
from mcp.server.auth.routes import (build_metadata, cors_middleware, create_protected_resource_routes)
from mcp.server.auth.settings import AuthSettings, ClientRegistrationOptions, RevocationOptions
from mcp.server.transport_security import RequestBodyLimitMiddleware
from mcp.shared.auth import OAuthClientInformationFull, OAuthToken
from starlette.datastructures import FormData
from starlette.requests import Request
from starlette.responses import JSONResponse, RedirectResponse, Response
from starlette.routing import Route

from .. import config, gitops, tokens
from ..store import AuthRejected, BaseError, Store, make_viewer, now_taipei
from .i18n import text
from .auth import page, same_secret
from .guard import CSP

# 回调政策只在这里维护；新增产品的域名与路径必须明确列入。
CALLBACK_ALLOWLIST = (
    r"https://chatgpt\.com/connector_platform_oauth_redirect",
    r"https://chatgpt\.com/connector/oauth/[A-Za-z0-9_-]+",
)
AUTH_PATHS = {"/authorize", "/token", "/register", "/revoke",
              "/.well-known/oauth-authorization-server",
              "/.well-known/oauth-protected-resource/chatgpt/mcp"}
SCOPES = ["company-base"]
FAIL = "这个口令用不了"
COOKIE = "__Host-cb_chatgpt_consent"
# 授权页提交后要 303 跳回 ChatGPT；浏览器对表单提交后的跳转也按 form-action 检查，只在这一页放行回调白名单的域名。
CONSENT_CSP = CSP.replace("form-action 'self'", "form-action 'self' https://chatgpt.com")
LOGGER = logging.getLogger(__name__)


def callback_allowed(uri: str) -> bool:
    return any(re.fullmatch(pattern, uri) for pattern in CALLBACK_ALLOWLIST)


def _log_safe(text: str, limit: int) -> str:
    return re.sub(r"[^A-Za-z0-9_.,:-]", "?", text)[:limit]


def denied_callback(uri: str) -> None:
    try:
        host = urlsplit(uri).hostname or "（无主机名）"
    except ValueError:
        host = "（无效主机名）"
    # 只记主机名和台北时间，清掉控制字符，绝不记查询串或码。
    host = re.sub(r"[^A-Za-z0-9.:-]", "?", host)[:253]
    LOGGER.warning("拒绝 OAuth 回调 host=%s time=%s", host, now_taipei().isoformat(timespec="seconds"))


def _epoch(record: dict) -> int:
    return int(dt.datetime.fromisoformat(record["expires"]).timestamp())


def _error(error: str, *, status: int = 400, **fields) -> Response:
    return JSONResponse({"error": error, "error_description": "授权请求用不了。", **fields}, status_code=status,
                        headers={"Cache-Control": "no-store", "Pragma": "no-cache"})


class OAuthProvider(OAuthAuthorizationServerProvider):
    def __init__(self, root: str, origin: str):
        self.root = Path(root)
        self.origin = origin
        self.resource = origin + "/chatgpt/mcp"

    def agent(self, key: str):
        viewer = make_viewer(Store(self.root).load(), key, remote=True)
        return viewer if viewer.can_remote and viewer.member.meta.get("kind") == "agent" else None

    async def register_client(self, client: OAuthClientInformationFull) -> None:
        for uri in client.redirect_uris or []:
            if not callback_allowed(str(uri)):
                denied_callback(str(uri))
                raise RegistrationError("invalid_redirect_uri", "回调地址不允许。")
        if (not client.redirect_uris or client.token_endpoint_auth_method not in
                ("none", "client_secret_post", "client_secret_basic")
                or set(client.grant_types) - {"authorization_code", "refresh_token"}
                or client.response_types != ["code"]):
            raise RegistrationError("invalid_client_metadata", "客户端登记用不了。")
        # 不保存自报名字或任意扩展字段；客户端身份不参与底座成员认定。
        fields = {k: v for k, v in client.model_dump(mode="json").items() if k in
                  ("client_id", "client_id_issued_at", "redirect_uris", "token_endpoint_auth_method",
                   "grant_types", "response_types", "scope", "client_secret_expires_at")}
        fields["secret_sha256"] = tokens._digest(client.client_secret) if client.client_secret else None
        tokens.oauth_register(fields)

    async def get_client(self, client_id: str) -> OAuthClientInformationFull | None:
        row = tokens.oauth_client(client_id)
        if not row:
            return None
        return OAuthClientInformationFull.model_validate({k: v for k, v in row.items()
                                                          if k not in ("kind", "secret_sha256")})

    async def authorize(self, client: OAuthClientInformationFull, params: AuthorizationParams) -> str:
        value, _ = tokens.oauth_create("oauth_pending", {**params.model_dump(mode="json"),
                                                       "scopes": params.scopes or SCOPES,
                                                       "client_id": client.client_id}, 600)
        return self.origin + "/chatgpt/consent?" + urlencode({"request": value})

    async def load_authorization_code(self, client, authorization_code):
        row = tokens.oauth_get(authorization_code, "oauth_code")
        if not row or row["client_id"] != client.client_id:
            return None
        return AuthorizationCode(code=authorization_code, client_id=row["client_id"],
                                 scopes=row["scopes"], expires_at=_epoch(row),
                                 code_challenge=row["code_challenge"], redirect_uri=row["redirect_uri"],
                                 redirect_uri_provided_explicitly=True, resource=row["resource"], subject=row["key"])

    def exchange(self, value, kind, client, scopes):
        try:
            with gitops.write_lock(self.root):
                row = tokens.oauth_get(value, kind)
                if not row or not self.agent(row["key"]):
                    raise BaseError(FAIL)
                access, refresh = tokens.oauth_exchange(value, kind, client.client_id,
                                                        self.resource, self.origin, scopes)
        except BaseError:
            raise TokenError("invalid_grant", "授权已失效。") from None
        return OAuthToken(access_token=access, token_type="Bearer", expires_in=tokens.OAUTH_ACCESS_SECONDS,
                          refresh_token=refresh, scope=" ".join(scopes))

    async def exchange_authorization_code(self, client, authorization_code):
        return self.exchange(authorization_code.code, "oauth_code", client, authorization_code.scopes)

    async def load_refresh_token(self, client, refresh_token):
        row = tokens.oauth_get(refresh_token, "oauth_refresh")
        if not row or row["client_id"] != client.client_id:
            return None
        return RefreshToken(token=refresh_token, client_id=row["client_id"], scopes=row["scopes"],
                            expires_at=_epoch(row), resource=row["resource"], subject=row["key"])

    async def exchange_refresh_token(self, client, refresh_token, scopes):
        return self.exchange(refresh_token.token, "oauth_refresh", client, scopes)

    def access_record(self, value):
        row = tokens.oauth_get(value, "oauth")
        if (not row or row.get("issuer") != self.origin or row.get("audience") != self.resource
                or row.get("resource") != self.resource or row.get("scopes") != SCOPES):
            return None
        return row if self.agent(row["key"]) else None

    async def load_access_token(self, token):
        try:
            row = self.access_record(token)
        except BaseError:
            return None
        if not row:
            return None
        return AccessToken(token=token, client_id=row["client_id"], subject=row["key"],
                           scopes=row["scopes"], expires_at=_epoch(row), resource=row["resource"],
                           claims={"iss": self.origin, "aud": self.resource})

    async def verify_token(self, token):
        return await self.load_access_token(token)

    async def revoke_token(self, token):
        tokens.oauth_revoke(token.token, "oauth_refresh" if isinstance(token, RefreshToken) else "oauth")

    def auth_check(self, value: str, key: str):
        def check():
            row = self.access_record(value)
            if not row or row["key"] != key:
                raise AuthRejected(tr("agent.632"))
        return check


class DigestClientAuthenticator(ClientAuthenticator):
    """SDK 协议处理照用，仅把其明文 client_secret 比较替换为本机摘要比较。"""

    async def authenticate_request(self, request):
        form = await request.form()
        client = await self.provider.get_client(str(form.get("client_id") or ""))
        if not client:
            raise AuthenticationError("客户端用不了。")
        method = client.token_endpoint_auth_method
        secret = str(form.get("client_secret") or "")
        if method == "client_secret_basic":
            try:
                scheme, encoded = request.headers.get("authorization", "").split(" ", 1)
                if scheme.lower() != "basic":
                    raise ValueError
                key, secret = base64.b64decode(encoded, validate=True).decode("utf-8").split(":", 1)
                if unquote(key) != client.client_id:
                    raise ValueError
                secret = unquote(secret)
            except (ValueError, UnicodeError, binascii.Error):
                raise AuthenticationError("客户端用不了。") from None
        row = tokens.oauth_client(client.client_id)
        if method != "none" and (not row.get("secret_sha256") or
                                  not same_secret(tokens._digest(secret), row["secret_sha256"])):
            raise AuthenticationError("客户端用不了。")
        return client


def routes(provider: OAuthProvider) -> list[Route]:
    settings = AuthSettings(issuer_url=provider.origin, resource_server_url=provider.resource,
                            validate_token_resource=True)
    registration = ClientRegistrationOptions(enabled=True, valid_scopes=SCOPES, default_scopes=SCOPES)
    metadata = build_metadata(settings.issuer_url, None, registration, RevocationOptions(enabled=True))
    metadata.token_endpoint_auth_methods_supported = ["none", "client_secret_post", "client_secret_basic"]
    metadata.revocation_endpoint_auth_methods_supported = metadata.token_endpoint_auth_methods_supported
    authenticator = DigestClientAuthenticator(provider)
    authorization = AuthorizationHandler(provider)
    registration_handler = RegistrationHandler(provider, registration)
    token_handler = TokenHandler(provider, authenticator)
    revocation = RevocationHandler(provider, authenticator)

    async def discover(request):
        result = metadata.model_dump(mode="json", exclude_none=True)
        result["authorization_response_iss_parameter_supported"] = True
        return JSONResponse(result)

    async def register(request):
        # 先检查原始字符串，避免 URL 解析器的规范化放宽回调白名单。
        try:
            payload = await request.json()
            uris = payload.get("redirect_uris") if isinstance(payload, dict) else None
            if not isinstance(uris, list) or not uris:
                return _error("invalid_redirect_uri")
            for uri in uris:
                if not isinstance(uri, str) or not callback_allowed(uri):
                    denied_callback(uri if isinstance(uri, str) else "")
                    return _error("invalid_redirect_uri")
        except (ValueError, UnicodeError):
            return _error("invalid_client_metadata")
        response = await registration_handler.handle(request)
        response.headers["Cache-Control"] = "no-store"
        return response

    async def authorize(request):
        params = request.query_params if request.method == "GET" else await request.form()
        def rejected(error):
            return _error(error, iss=provider.origin, state=params.get("state"))
        uri = str(params.get("redirect_uri") or "")
        client = await provider.get_client(str(params.get("client_id") or ""))
        if (not callback_allowed(uri) or not client
                or uri not in [str(u) for u in client.redirect_uris or []]):
            denied_callback(uri)
            return rejected("invalid_request")
        if any(len(params.getlist(k)) > 1 for k in params):
            return rejected("invalid_request")
        if params.get("resource") != provider.resource:
            return rejected("invalid_target")
        if (params.get("code_challenge_method") != "S256"
                or not re.fullmatch(r"[A-Za-z0-9_-]{43}", str(params.get("code_challenge") or ""))):
            return rejected("invalid_request")
        response = await authorization.handle(request)
        location = response.headers.get("location")
        if location and callback_allowed(location.split("?", 1)[0]):
            response.headers["location"] = construct_redirect_uri(location, iss=provider.origin)
        return response

    async def token(request):
        form = await request.form()
        # 记日志的都是请求方填的，清掉控制字符等，防伪造日志行。
        grant = _log_safe(str(form.get("grant_type") or ""), 40)
        fields = _log_safe(",".join(sorted(form.keys())), 200)

        def refused(error, why):
            # 只记原因和带了哪些字段名，不记任何字段的值。
            LOGGER.warning("拒绝 OAuth 换令牌 grant=%s why=%s fields=%s", grant, why, fields)
            return _error(error)

        if any(len(form.getlist(k)) > 1 for k in form):
            return refused("invalid_request", "重复字段")
        # 带了 resource 就必须是这个入口；没带时沿用码或刷新令牌登记时绑定的入口（授权那一步已核过）。
        if "resource" in form and form.get("resource") != provider.resource:
            return refused("invalid_target", "resource 不对")
        if grant == "authorization_code":
            row = tokens.oauth_get(str(form.get("code") or ""), "oauth_code")
            if row and form.get("redirect_uri") != row["redirect_uri"]:
                return refused("invalid_grant", "redirect_uri 不一致")
            if not re.fullmatch(r"[A-Za-z0-9._~-]{43,128}", str(form.get("code_verifier") or "")):
                return refused("invalid_grant", "code_verifier 格式不对")
        try:
            response = await token_handler.handle(request)
        except Exception as error:
            LOGGER.error("OAuth 换令牌内部出错 (%s)", type(error).__name__)
            raise
        if response.status_code >= 400:
            try:
                why = _log_safe(str(json.loads(response.body).get("error", "")), 60)
            except (ValueError, AttributeError):
                why = ""
            LOGGER.warning("拒绝 OAuth 换令牌 grant=%s why=SDK:%s status=%s fields=%s", grant, why,
                           response.status_code, fields)
        return response

    async def revoke(request):
        # SDK 2.3 的撤销模型把可空的 client_secret 当成必填字段；公开客户端不发送它。
        form = await request.form()
        if "client_secret" not in form:
            request._form = FormData([*form.multi_items(), ("client_secret", "")])
        return await revocation.handle(request)

    def consent_page(pending_value, pending, error="", status=200):
        challenge = tokens.issue_challenge("chatgpt:" + pending["id"])
        inner = (f'<h1>{text("oauth_heading", name=html.escape(config.name()))}</h1>'
                 f'<p class="lead">{text("oauth_lead", name=html.escape(config.name()))}</p>'
                 f'<p>{text("oauth_details")}</p>'
                 # 授权码会回到发起连接的那个 ChatGPT；别人发来的链接一旦填了，访问就落到他的账号上。
                 f'<p class="form-error">{text("oauth_warning")}</p>'
                 '<form class="form" method="post" action="/chatgpt/consent">'
                 f'<input type="hidden" name="request" value="{html.escape(pending_value)}">'
                 f'<input type="hidden" name="challenge" value="{html.escape(challenge)}">'
                 f'<label>{text("agent_token")}<input class="input" name="token" type="password" autocomplete="off" required></label>'
                 f'<button class="btn primary" type="submit">{text("oauth_button")}</button></form>'
                 + (f'<p class="form-error" role="alert">{html.escape(error)}</p>' if error else ''))
        response = page("ChatGPT 授权", inner, status=status)
        response.headers["Content-Security-Policy"] = CONSENT_CSP
        response.set_cookie(COOKIE, challenge, max_age=600, path="/", secure=True,
                            httponly=True, samesite="strict")
        return response

    async def consent(request):
        form = request.query_params if request.method == "GET" else await request.form()
        value = str(form.get("request") or "")
        pending = tokens.oauth_get(value, "oauth_pending")
        if not pending:
            return page("ChatGPT 授权", f'<p>{text("oauth_expired")}</p>', status=400)
        if request.method == "GET":
            return consent_page(value, pending)
        cookie = request.cookies.get(COOKIE, "")
        if (request.headers.getlist("origin") != [provider.origin]
                or request.headers.get("sec-fetch-site", "same-origin") != "same-origin"
                or not cookie or not same_secret(cookie, str(form.get("challenge") or ""))
                or not tokens.consume_challenge(cookie, "chatgpt:" + pending["id"])):
            return consent_page(value, pending, text("oauth_invalid"), 403)
        try:
            with gitops.write_lock(provider.root):
                source = tokens.lookup_record(str(form.get("token") or "").strip())
                if not source or source["kind"] != "token" or not provider.agent(source["key"]):
                    raise BaseError(FAIL)
                client = await provider.get_client(pending["client_id"])
                if (not client or pending["redirect_uri"] not in [str(u) for u in client.redirect_uris]
                        or not callback_allowed(pending["redirect_uri"]) or pending["resource"] != provider.resource):
                    raise BaseError(FAIL)
                code = tokens.oauth_authorize(value, source)
        except BaseError as error:
            # 只有“已经授权过一次”说具体原因（填的人手里本来就有这个口令），其余一律同一句。
            used = str(error) == tr("agent.583")
            return consent_page(value, pending, text("oauth_used" if used else "oauth_failed"), 401)
        except Exception as error:
            LOGGER.error("OAuth 授权内部出错 (%s)", type(error).__name__)
            raise
        response = RedirectResponse(construct_redirect_uri(pending["redirect_uri"], code=code,
                                                           state=pending.get("state"), iss=provider.origin), 303)
        response.delete_cookie(COOKIE, path="/", secure=True, httponly=True, samesite="strict")
        return response

    result = [Route("/.well-known/oauth-authorization-server", cors_middleware(discover, ["GET", "OPTIONS"]),
                    methods=["GET", "OPTIONS"]),
              Route("/authorize", authorize, methods=["GET", "POST"]),
              Route("/register", cors_middleware(register, ["POST", "OPTIONS"]), methods=["POST", "OPTIONS"]),
              Route("/token", cors_middleware(token, ["POST", "OPTIONS"]), methods=["POST", "OPTIONS"]),
              Route("/revoke", cors_middleware(revoke, ["POST", "OPTIONS"]), methods=["POST", "OPTIONS"]),
              Route("/chatgpt/consent", consent, methods=["GET", "POST"])]
    # SDK 的正文限额覆盖所有认证端点；凭据表单再限制到 8 KiB。
    for route in result:
        route.app = RequestBodyLimitMiddleware(route.app, 8192 if route.path == "/chatgpt/consent" else 65536)
    result.extend(create_protected_resource_routes(settings.resource_server_url, [settings.issuer_url], SCOPES,
                                                   resource_name=f"{config.name()} · ChatGPT"))
    return result
