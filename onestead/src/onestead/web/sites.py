"""子域名分流在工作台 guard 外面；静态站永不调用任何底座路由。"""

from __future__ import annotations

import posixpath
import threading
from pathlib import Path
from urllib.parse import unquote, urlencode

from starlette.concurrency import run_in_threadpool
from starlette.requests import Request
from starlette.responses import FileResponse, PlainTextResponse, RedirectResponse

from .. import config, siteauth, sites
from ..store import BaseError, Store
from .auth import page
from .i18n import text

COOKIE = "__Host-cb_site"
HEADERS = {"X-Content-Type-Options": "nosniff", "Referrer-Policy": "same-origin",
           "Content-Security-Policy": "frame-ancestors 'self'", "Cache-Control": "private, no-store"}
TYPES = {".html": "text/html", ".htm": "text/html", ".css": "text/css", ".js": "text/javascript",
         ".mjs": "text/javascript", ".json": "application/json", ".map": "application/json",
         ".txt": "text/plain", ".xml": "application/xml", ".svg": "image/svg+xml", ".png": "image/png",
         ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".gif": "image/gif", ".webp": "image/webp",
         ".avif": "image/avif", ".ico": "image/x-icon", ".woff": "font/woff", ".woff2": "font/woff2",
         ".ttf": "font/ttf", ".otf": "font/otf", ".mp3": "audio/mpeg", ".mp4": "video/mp4",
         ".webm": "video/webm", ".wasm": "application/wasm", ".csv": "text/csv", ".pdf": "application/pdf",
         ".webmanifest": "application/manifest+json"}


class Snapshot:
    """只缓存解析结果。每次 stat apps/people，改权限（包括未提交的改动）下一请求即重新加载。"""
    def __init__(self, root):
        self.root = Path(root)
        self.key = None
        self.docs = None
        self.lock = threading.Lock()

    def load(self):
        with self.lock:
            key = []
            for folder in ("apps", "people"):
                for path in sorted((self.root / folder).glob("*.md")):
                    info = path.stat()
                    key.append((str(path), info.st_mtime_ns, info.st_ctime_ns, info.st_size, info.st_ino))
            if key != self.key:
                self.docs = Store(self.root).load()
                self.key = key
            return self.docs


def not_found():
    return PlainTextResponse(text("not_found"), status_code=404, headers=HEADERS)


def clean_path(request: Request) -> str | None:
    raw = request.scope.get("raw_path")
    value = unquote(raw.decode("utf-8", "replace")) if raw is not None else request.scope["path"]
    # 先解码再检查，拒绝 Windows 分隔符、ADS、点段和尾点别名，不用 SPA 回退。
    if (not value.startswith("/") or value.startswith("//") or "\\" in value or ":" in value
            or any(ord(char) < 32 or ord(char) == 127 for char in value)
            or any(part.startswith(".") or part.endswith((".", " ")) for part in value.split("/") if part)):
        return None
    normalized = posixpath.normpath(value)
    return normalized + "/" if value.endswith("/") and normalized != "/" else normalized


def clear_cookie(response):
    response.delete_cookie(COOKIE, path="/", secure=True, httponly=True, samesite="lax")
    return response


class SiteRouter:
    def __init__(self, app, root, base_hosts, public_host):
        self.app = app
        self.snapshot = Snapshot(root)
        self.base_hosts = set(base_hosts)
        self.public_host = public_host

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http":
            return await self.app(scope, receive, send)
        hosts = [value.decode("latin-1") for key, value in scope["headers"] if key.lower() == b"host"]
        if len(hosts) == 1 and hosts[0] in self.base_hosts:
            return await self.app(scope, receive, send)
        response = not_found()
        try:
            domain = config.site_domain()
            host = hosts[0] if len(hosts) == 1 else ""
            suffix = "." + domain if domain else ""
            slug = host[:-len(suffix)] if suffix and host.endswith(suffix) else ""
            if domain and host == host.lower() and sites.SLUG.fullmatch(slug):
                request = Request(scope, receive)
                response = await run_in_threadpool(self.respond, request, slug)
        except (BaseError, OSError, ValueError):
            # 不把本机存储路径或解析错误暴露在静态子域名上。
            response = not_found()
        await response(scope, receive, send)

    def respond(self, request: Request, slug: str):
        docs = self.snapshot.load()
        doc = sites.find_site(docs, slug)
        # 不认底座的任何 cookie 或 Bearer，即使客户端手工带过来也无效。
        value = request.cookies.get(COOKIE, "")
        if not doc:
            siteauth.session(value, {}, docs) if value else None
            return clear_cookie(not_found()) if value else not_found()
        site = sites.site_of(doc)
        session = siteauth.session(value, site, docs) if value else None
        path = clean_path(request)
        if path is None:
            return not_found()
        if request.method not in ("GET", "HEAD"):
            return PlainTextResponse(text("static_only"), status_code=405, headers=HEADERS)
        if path == "/_base/logout":
            siteauth.revoke(value)
            return clear_cookie(PlainTextResponse(text("signed_out"), headers=HEADERS))
        if site.get("status") != "已发布":
            return clear_cookie(not_found()) if value else not_found()
        if path == "/_base/callback":
            value = siteauth.exchange(request.query_params.get("code", ""), site, docs)
            if not value:
                return PlainTextResponse(text("site_expired"), status_code=403, headers=HEADERS)
            response = RedirectResponse(sites.safe_next(request.query_params.get("next", "/")), status_code=302, headers=HEADERS)
            response.set_cookie(COOKIE, value, max_age=7 * 86400, secure=True, httponly=True, samesite="lax", path="/")
            return response
        if site.get("visibility") != "公开" and not session:
            target = sites.safe_next(path + ("?" + request.url.query if request.url.query else ""))
            response = RedirectResponse(f"https://{self.public_host}/sites/enter?" +
                                        urlencode({"slug": slug, "next": target}), status_code=302, headers=HEADERS)
            return clear_cookie(response) if value else response
        version = site.get("current")
        root = sites.content_dir(site, version)
        file = root.joinpath(*path.strip("/").split("/"))
        sites.no_links(file)
        if not file.resolve().is_relative_to(root.resolve()):
            return not_found()
        if file.is_dir():
            if not path.endswith("/"):
                # 目录页的相对 CSS/JS 地址要落在该目录，而不是上一级。
                target = path + "/" + ("?" + request.url.query if request.url.query else "")
                return RedirectResponse(sites.safe_next(target), status_code=302, headers=HEADERS)
            file = file / "index.html"
            sites.no_links(file)
        if not file.is_file() or file.suffix.lower() not in TYPES:
            return not_found()
        headers = {**HEADERS, "Cache-Control": "private, no-cache" if file.suffix.lower() in (".html", ".htm") else "private, max-age=60"}
        response = FileResponse(file, media_type=TYPES[file.suffix.lower()], headers=headers)
        return clear_cookie(response) if value and not session else response


async def enter(request: Request, root):
    if not config.site_domain():
        return not_found()
    slug = request.query_params.get("slug", "")
    if not sites.SLUG.fullmatch(slug):
        return not_found()
    docs = await run_in_threadpool(Store(root).load)
    doc = sites.find_site(docs, slug)
    if not doc or sites.site_of(doc).get("status") != "已发布":
        return not_found()
    target = sites.safe_next(request.query_params.get("next", "/"))
    principal = request.state.principal
    if not principal:
        return_to = "/sites/enter?" + urlencode({"slug": slug, "next": target})
        return RedirectResponse("/login?" + urlencode({"return_to": return_to}), status_code=302)
    if not principal.remote or not sites.can_open(sites.site_of(doc), principal.key, docs):
        return page("没有权限", f'<p>{text("site_denied")}</p>', status=403)
    code = await run_in_threadpool(siteauth.issue_code, slug, principal.key, principal.session_id)
    return RedirectResponse(sites.url(slug) + "/_base/callback?" + urlencode({"code": code, "next": target}), status_code=302)
