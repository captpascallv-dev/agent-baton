"""网页认证适配器：可信配置决定接入方式，请求参数不能决定身份。"""

from __future__ import annotations

from ..i18n import text as tr

from .. import config

import hashlib
import hmac
import html
import re
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlencode, urlsplit

from starlette.requests import Request
from starlette.responses import HTMLResponse, RedirectResponse, Response
from starlette.routing import Route

from .. import feishu, gitops, sites, tokens
from ..store import BaseError, Store, make_viewer, owner_name_at
from .i18n import locale, text

WEB_DAYS = 30 * 86400
LOGIN_FAIL = "登录失败，口令无效或当前不能登录，请找 {owner_name}。"
CSRF_MESSAGE = b"company-base:web-csrf:v1"


@dataclass(frozen=True)
class Principal:
    key: str
    remote: bool
    auth_method: str
    session_id: str | None
    root: Path | None = None

    def __post_init__(self):
        config.owner_key()

    @property
    def owner_name(self) -> str:
        return owner_name_at(self.root) if self.root is not None else config.owner_key()

    @property
    def is_owner(self) -> bool:
        return not self.remote and self.key == config.owner_key()


def csrf_value(value: str) -> str:
    return hmac.new(value.encode("utf-8"), CSRF_MESSAGE, hashlib.sha256).hexdigest()


def same_secret(left: str, right: str) -> bool:
    # 请求值可能含非 ASCII 字符，按字节比较也不会导致异常响应。
    return hmac.compare_digest(left.encode("utf-8"), right.encode("utf-8"))


def page(title: str, inner: str, *, status: int = 200, head: str = "") -> HTMLResponse:
    if inner == f"<p>{feishu.USER_FAIL}</p>":
        inner = f'<p>{text("feishu_failed")}</p>'
    return HTMLResponse(f'<!doctype html><html lang="{locale()}"><head><meta charset="utf-8">'
                        '<meta name="viewport" content="width=device-width, initial-scale=1">'
                        f'<title>{html.escape(text(title) if title in ("登录", "登录成功", "ChatGPT 授权", "没有权限") else title)} · {html.escape(config.name())}</title>'
                        '<link rel="stylesheet" href="/static/app.css">'
                        f'{head}</head><body class="auth-page"><main class="card auth-card">{inner}</main></body></html>', status_code=status)


class LocalAuth:
    remote = False

    def __init__(self, port: int, token: str, *, root: str | Path | None = None):
        config.owner_key()
        self.root = Path(root) if root is not None else None
        self.cookie = f"cb_session_{port}"
        self.token = token
        self.host = f"127.0.0.1:{port}"
        self.origin = f"http://{self.host}"

    def handles(self, path: str) -> bool:
        return True

    def host_valid(self, request: Request) -> bool:
        return request.headers.getlist("host") == [self.host]

    def authenticate(self, request: Request) -> Principal | None:
        if same_secret(request.cookies.get(self.cookie, ""), self.token):
            return Principal(config.owner_key(), False, "shortcut", None, self.root)
        return None

    def origin_valid(self, request: Request) -> bool:
        return (request.headers.getlist("origin") == [self.origin]
                and request.headers.get("sec-fetch-site", "same-origin") == "same-origin")

    def write_valid(self, request: Request) -> bool:
        return self.origin_valid(request) and request.headers.get("x-requested-by") == "company-base"


class RemoteAuth:
    remote = True

    def __init__(self, root: str | Path, public_host: str, *, test_origin: str | None = None,
                 legacy_hosts: set[str] | None = None, legacy_origins: set[str] | None = None):
        config.owner_key()
        self.root = Path(root)
        if not re.fullmatch(r"[a-z0-9]+(?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9]+(?:[a-z0-9-]*[a-z0-9])?)+", public_host):
            raise BaseError(tr("agent.616"))
        self.host = public_host
        self.origin = f"https://{public_host}"
        self.secure = True
        self.cookie = "__Host-cb_web"
        self.login_cookie = "__Host-cb_login"
        self.return_cookie = "__Host-cb_return"
        if test_origin is not None:
            parsed = urlsplit(test_origin)
            if (parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost", "::1")
                    or not parsed.port or parsed.username or parsed.password or parsed.path or parsed.query
                    or parsed.fragment or test_origin != f"http://{parsed.netloc}"):
                raise BaseError(tr("agent.617"))
            self.host, self.origin, self.secure = parsed.netloc, test_origin, False
            self.cookie = f"cb_web_dev_{parsed.port}"
            self.login_cookie = f"cb_login_dev_{parsed.port}"
            self.return_cookie = f"cb_return_dev_{parsed.port}"
        # 旧地址 /portal 和 MCP 认的主机名、来源。同事网页自己的 Host 检查不走这两份。
        self.legacy_hosts = set(legacy_hosts or ())
        self.legacy_origins = set(legacy_origins or ())

    def handles(self, path: str) -> bool:
        # 旧地址 /portal 只做跳转；MCP 和机器下载保留 Bearer，飞书地址由新认证接管。
        from .oauth import AUTH_PATHS
        if path in AUTH_PATHS or path == "/chatgpt" or path.startswith("/chatgpt/"):
            return False
        return not (path == "/mcp" or path.startswith("/mcp/") or path.startswith("/skills/")
                    or ((path == "/portal" or path.startswith("/portal/")) and path not in
                        ("/portal/feishu/login", "/portal/feishu/callback")))

    def host_valid(self, request: Request) -> bool:
        return request.headers.getlist("host") == [self.host]

    def viewer(self, key: str, docs=None):
        if not isinstance(key, str) or not key or key == config.owner_key():
            return None
        if docs is None:
            docs = Store(self.root).load()
        viewer = make_viewer(docs, key, allow_owner=False, remote=True)
        if (not viewer.can_remote or not viewer.member
                or viewer.member.meta.get("kind") != "human"):
            return None
        return viewer

    def authenticate(self, request: Request) -> Principal | None:
        # 口令每次现场核对。reuse_docs 只给这次请求开头用一次，用完即关；写之前和请求结束时重新读盘。
        reuse = bool(getattr(request.state, "reuse_docs", False))
        request.state.reuse_docs = False
        record = tokens.lookup_web(request.cookies.get(self.cookie, ""))
        if not record or not isinstance(record["key"], str):
            return None
        docs = getattr(request.state, "doc_cache", None) if reuse else None
        if docs is None:
            docs = Store(self.root).load()
            if reuse:
                request.state.doc_cache = docs
        viewer = self.viewer(record["key"], docs)
        if not viewer:
            return None
        if record["auth_method"] == "feishu" and viewer.member.meta.get("feishu_open_id") != record["binding_id"]:
            return None
        return Principal(record["key"], True, record["auth_method"], record["id"], self.root)

    def origin_valid(self, request: Request) -> bool:
        return (request.headers.getlist("origin") == [self.origin]
                and request.headers.get("sec-fetch-site", "same-origin") == "same-origin")

    def csrf_valid(self, request: Request) -> bool:
        value = request.cookies.get(self.cookie, "")
        return bool(value) and same_secret(request.headers.get("x-csrf-token", ""), csrf_value(value))

    def write_valid(self, request: Request) -> bool:
        return (self.origin_valid(request) and self.csrf_valid(request)
                and request.headers.get("x-requested-by") == "company-base")

    def set_session(self, response: Response, value: str) -> None:
        response.set_cookie(self.cookie, value, max_age=WEB_DAYS, path="/", secure=self.secure,
                            httponly=True, samesite="strict")

    def clear_login(self, response: Response) -> None:
        response.delete_cookie(self.login_cookie, path="/", secure=self.secure, httponly=True, samesite="strict")

    @property
    def owner_name(self) -> str:
        return owner_name_at(self.root)

    def login_page(self, error: str = "", status: int = 200, return_to: str = "/") -> Response:
        return_to = sites.login_target(return_to)
        use_feishu = feishu.enabled()
        feishu_url = "/portal/feishu/login" + ("?" + urlencode({"return_to": return_to}) if return_to != "/" else "")
        methods = text("feishu_method" if use_feishu else "login_method")
        button = (f'<p><a class="btn auth-feishu" href="{html.escape(feishu_url, quote=True)}">{text("feishu_button")}</a></p>'
                  if use_feishu else "")
        challenge = tokens.issue_challenge("login")
        # 只对已校验的网页回跳入口提示主人；普通底座登录不显示，也不放宽登录／跳转规则。
        owner_hint = (f'<p class="box accent owner-login-hint">{text("login_owner_hint", owner=html.escape(self.owner_name))}</p>'
                      if return_to != "/" else "")
        inner = (f'<h1>{text("login_heading", name=html.escape(config.name()))}</h1>'
                 f'<p class="lead">{text("login_lead", owner=html.escape(self.owner_name), methods=methods)}</p>'
                 + owner_hint
                 + '<form class="form" method="post" action="/auth/login">'
                 f'<input type="hidden" name="challenge" value="{html.escape(challenge)}">'
                 f'<input type="hidden" name="return_to" value="{html.escape(return_to, quote=True)}">'
                 f'<label>{text("token")}<input class="input" name="token" type="password" autocomplete="off" required></label>'
                 f'<button class="btn primary" type="submit">{text("登录")}</button></form>'
                 + button
                 + (f'<p class="form-error" role="alert">{html.escape(error)}</p>' if error else ''))
        response = page("登录", inner, status=status)
        response.set_cookie(self.login_cookie, challenge, max_age=600, path="/", secure=self.secure,
                            httponly=True, samesite="strict")
        return response

    def routes(self) -> list[Route]:
        async def login_page(request: Request):
            notice = text("login_expired", owner=self.owner_name) if request.query_params.get("expired") == "1" else ""
            return self.login_page(notice, return_to=request.query_params.get("return_to", "/"))

        async def login(request: Request):
            form = await request.form()
            return_to = sites.login_target(str(form.get("return_to") or "/"))
            challenge = str(form.get("challenge") or "")
            cookie = request.cookies.get(self.login_cookie, "")
            if not cookie or not same_secret(cookie, challenge) or not tokens.consume_challenge(cookie, "login"):
                # 开了两个登录页、或页面放久了：直接给一张新表单，不让人落到没有出路的页面。
                return self.login_page(text("form_expired"), 403, return_to)
            password = str(form.get("token") or "").strip()
            try:
                with gitops.write_lock(self.root):
                    record = tokens.lookup_record(password)
                    viewer = self.viewer(record["key"]) if record and record["kind"] == "token" else None
                    if not viewer:
                        raise BaseError(text("login_failed", owner=self.owner_name))
                    value, _ = tokens.issue_web(viewer.key, "token", source_token_id=record["id"])
            except BaseError:
                return self.login_page(text("login_failed", owner=self.owner_name), 401, return_to)
            response = RedirectResponse(return_to, status_code=303)
            self.set_session(response, value)
            self.clear_login(response)
            return response

        async def logout(request: Request):
            tokens.revoke(request.state.principal.session_id)
            response = RedirectResponse("/login", status_code=303)
            response.delete_cookie(self.cookie, path="/", secure=self.secure, httponly=True, samesite="strict")
            return response

        async def feishu_login(request: Request):
            app = feishu.load_app()
            if not app:
                return Response(status_code=404)
            app_id = app["app_id"]
            app.clear()
            state = tokens.issue_challenge("feishu")
            response = RedirectResponse(feishu.authorize_url(app_id, self.callback_url, state), status_code=303)
            response.set_cookie("cb_feishu_state", state, max_age=600, path="/portal/feishu",
                                httponly=True, samesite="lax", secure=self.secure)
            return_to = sites.login_target(request.query_params.get("return_to", "/"))
            response.set_cookie(self.return_cookie, return_to, max_age=600, path="/", secure=self.secure,
                                httponly=True, samesite="lax")
            return response

        async def feishu_callback(request: Request):
            cookie = request.cookies.get("cb_feishu_state", "")
            given = request.query_params.get("state", "")
            # 无论成功失败都消费已有挑战，并清掉浏览器 state；换码只能发生在验证之后。
            try:
                if not feishu.enabled():
                    return Response(status_code=404)
                consumed = tokens.consume_challenge(cookie, "feishu")
                if not cookie or not same_secret(cookie, given) or not consumed:
                    response = page("登录", f"<p>{feishu.USER_FAIL}</p>")
                else:
                    response = self.finish_feishu(request)
            except Exception:
                # 挑战存储或配置读取失败也必须清 state；日志不记请求值或异常详情。
                feishu.log_failure("回调", "unexpected")
                response = page("登录", f"<p>{feishu.USER_FAIL}</p>")
            response.delete_cookie("cb_feishu_state", path="/portal/feishu", httponly=True,
                                   samesite="lax", secure=self.secure)
            response.delete_cookie(self.return_cookie, path="/", secure=self.secure, httponly=True, samesite="lax")
            return response

        return [Route("/login", login_page), Route("/auth/login", login, methods=["POST"]),
                Route("/auth/logout", logout, methods=["POST"]), Route("/portal/feishu/login", feishu_login),
                Route("/portal/feishu/callback", feishu_callback)]

    @property
    def callback_url(self) -> str:
        return self.origin + "/portal/feishu/callback"

    def finish_feishu(self, request: Request) -> Response:
        app = feishu.load_app()
        if not app:
            return Response(status_code=404)
        try:
            code = request.query_params.get("code", "")
            if not code:
                return page("登录", f"<p>{feishu.USER_FAIL}</p>")
            access = feishu._exchange_code(app, code, self.callback_url)
            info = feishu._user_info(access)
            with gitops.write_lock(self.root):
                docs = Store(self.root).load()
                open_id = info.get("open_id")
                matched = [d for d in docs if d.kind == "person" and isinstance(open_id, str)
                           and d.meta.get("feishu_open_id") == open_id]
                if not matched:
                    feishu.remember(info)
                    name = info.get("name") if isinstance(info.get("name"), str) else text("unnamed")
                    return page("登录", f'<p>{text("feishu_pending", name=html.escape(name), owner=html.escape(self.owner_name))}</p>')
                viewer = self.viewer(matched[0].meta.get("agent_key")) if len(matched) == 1 else None
                if not viewer:
                    return page("登录", f'<p>{text("feishu_disabled", owner=html.escape(self.owner_name))}</p>')
                value, _ = tokens.issue_web(viewer.key, "feishu", binding_id=open_id)
        except feishu.FeishuError:
            return page("登录", f"<p>{feishu.USER_FAIL}</p>")
        except Exception:
            feishu.log_failure("回调", "unexpected")
            return page("登录", f"<p>{feishu.USER_FAIL}</p>")
        finally:
            app.clear()
        # 跨站回调后的第二次同源导航才能稳定携带 Strict 主会话，CSP 禁止内联脚本。
        target = html.escape(sites.login_target(request.cookies.get(self.return_cookie, "/")), quote=True)
        label = text("enter_workspace" if target == "/" else "continue_app")
        response = page("登录成功", f'<h1>{text("登录成功")}</h1><p><a class="btn primary" href="{target}">{label}</a></p>',
                        head=f'<meta http-equiv="refresh" content="0;url={target}">')
        self.set_session(response, value)
        return response
