"""静态网页发布：文档管上线与授权，版本文件只放本机状态目录。"""

from __future__ import annotations

from .i18n import text as tr

import datetime as dt
import hashlib
import io
import os
import re
import secrets
import shutil
import stat
import tempfile
from pathlib import Path

from . import apps, config, gitops, tokens
from .store import BaseError, Doc, make_viewer, now_taipei
from .textutil import SECRET_PATTERNS

SLUG = re.compile(r"[a-z0-9][a-z0-9-]{1,28}[a-z0-9]")
VERSION = re.compile(r"\d{8}-\d{12}-[a-f0-9]{10}")
VISIBILITY = ("指定的人", "所有远程同事", "公开")
EXTENSIONS = set(".html .htm .css .js .mjs .json .map .txt .xml .svg .png .jpg .jpeg .gif .webp .avif .ico .woff .woff2 .ttf .otf .mp3 .mp4 .webm .wasm .csv .pdf .webmanifest".split())
TEXT = set(".html .htm .css .js .mjs .json .map .txt .xml .svg .csv .webmanifest".split())
MAX_FILES, MAX_TOTAL, MAX_FILE = 2000, 50 * 1024 * 1024, 10 * 1024 * 1024


def enabled() -> str:
    domain = config.site_domain()
    if not domain:
        raise BaseError(tr("agent.426"))
    return domain


def url(slug: str) -> str:
    return f"https://{slug}.{enabled()}"


def check_slug(slug, docs: list[Doc], app_id: str) -> str:
    if not isinstance(slug, str) or not SLUG.fullmatch(slug):
        raise BaseError(tr("agent.427"))
    if slug in config.reserved_slugs():
        raise BaseError(tr("agent.428"))
    if any(d.kind == "app" and d.id != app_id and isinstance(d.meta.get("site"), dict)
           and d.meta["site"].get("slug") == slug for d in docs):
        raise BaseError(tr("agent.429"))
    return slug


def no_links(path: Path) -> None:
    """包括祖先的 reparse point；Windows 的目录联接不一定被 is_symlink 识别。"""
    for part in (path, *path.parents):
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise BaseError(tr("agent.440", v0=part))


def slug_dir(slug: str) -> Path:
    if not isinstance(slug, str) or not SLUG.fullmatch(slug):
        raise BaseError(tr("agent.430"))
    base = config.state_dir().absolute() / "sites"
    path = base / slug
    no_links(path)
    if not path.resolve().is_relative_to(base.resolve()):
        raise BaseError(tr("agent.431"))
    return path


def version_dir(slug: str, version: str) -> Path:
    if not isinstance(version, str) or not VERSION.fullmatch(version):
        raise BaseError(tr("agent.432"))
    path = slug_dir(slug) / version
    no_links(path)
    return path


def remove_tree(path: Path, parent: Path) -> None:
    """先独立解析整棵树，再把已核对的绝对路径交给删除；不经过 shell。"""
    no_links(parent)
    no_links(path)
    target, boundary = path.resolve(), parent.resolve()
    if target == boundary or not target.is_relative_to(boundary):
        raise BaseError(tr("agent.433"))
    if not target.exists():
        return
    for root, dirs, files in os.walk(target, followlinks=False):
        for name in dirs + files:
            child = Path(root) / name
            no_links(child)
            if not child.resolve().is_relative_to(boundary):
                raise BaseError(tr("agent.433"))
    shutil.rmtree(target)


def _source(build_dir: str) -> Path:
    path = Path(build_dir)
    if not path.is_absolute() or ".." in path.parts:
        raise BaseError(tr("agent.434"))
    no_links(path)
    if not path.is_dir():
        raise BaseError(tr("agent.435"))
    no_links(path / "index.html")
    if not (path / "index.html").is_file():
        raise BaseError(tr("agent.436"))
    return path


def store_build(build_dir: str, slug: str) -> tuple[str, list[str]]:
    source = _source(build_dir)
    parent = slug_dir(slug)
    if parent.resolve().is_relative_to(source.resolve()):
        raise BaseError(tr("agent.437"))
    files, skipped, bad = [], [], []
    total = 0
    for root, dirs, names in os.walk(source, followlinks=False):
        for name in sorted(dirs + names):
            path = Path(root) / name
            rel = path.relative_to(source).as_posix()
            no_links(path)
            if not path.resolve().is_relative_to(source.resolve()):
                raise BaseError(tr("agent.445", v0=rel))
            if name.startswith("."):
                skipped.append(rel + ("/" if name in dirs else ""))
                if name in dirs:
                    dirs.remove(name)
                continue
            if name in dirs:
                continue
            info = path.stat()
            if not stat.S_ISREG(info.st_mode):
                raise BaseError(tr("agent.446", v0=rel))
            if path.suffix.lower() not in EXTENSIONS:
                bad.append(rel)
            files.append((path, rel))
            total += info.st_size
            if info.st_size > MAX_FILE:
                raise BaseError(tr("agent.447", v0=rel))
            if len(files) > MAX_FILES or total > MAX_TOTAL:
                raise BaseError(tr("agent.448"))
    if bad:
        raise BaseError(tr("agent.438") + "、".join(bad))
    parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=".upload-", dir=parent))
    digest, total = hashlib.sha256(), 0
    try:
        for path, rel in files:
            no_links(path)
            # 只读有界长度；源文件在扫描后变大也不能绕过限额。
            with path.open("rb") as stream:
                content = stream.read(MAX_FILE + 1)
            total += len(content)
            if len(content) > MAX_FILE or total > MAX_TOTAL:
                raise BaseError(tr("agent.449", v0=rel))
            if path.suffix.lower() in TEXT:
                encoding = "utf-16" if content.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8-sig"
                text = content.decode(encoding, errors="replace")
                hits = sorted({text.count("\n", 0, match.start()) + 1
                               for pattern in SECRET_PATTERNS[:-1] for match in pattern.finditer(text)})
                if hits:
                    raise BaseError(tr("agent.455", v0=rel) + "、".join(map(str, hits)))
            target = temporary / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
            digest.update(rel.encode("utf-8") + b"\0" + content)
        version = now_taipei().strftime("%Y%m%d-%H%M%S%f") + "-" + digest.hexdigest()[:10]
        temporary.rename(version_dir(slug, version))
        return version, skipped
    finally:
        if temporary.exists():
            remove_tree(temporary, parent)


def site_of(doc: Doc) -> dict:
    value = doc.meta.get("site")
    return value if isinstance(value, dict) else {}


def can_open(site: dict, key: str, docs: list[Doc]) -> bool:
    if site.get("status") != "已发布":
        return False
    if not isinstance(key, str) or key == config.owner_key():
        return False
    viewer = make_viewer(docs, key, remote=True)
    if (not viewer.can_remote or not viewer.member
            or viewer.member.meta.get("kind") != "human"):
        return False
    return site.get("visibility") in ("公开", "所有远程同事") or (
        site.get("visibility") == "指定的人" and key in site.get("members", []))


def safe_next(value: str) -> str:
    from urllib.parse import unquote, urlsplit
    if not isinstance(value, str):
        return "/"
    probe = value
    while True:
        if (not probe.startswith("/") or probe.startswith("//") or "\\" in probe
                or any(ord(char) < 32 or ord(char) == 127 for char in probe)):
            return "/"
        parsed = urlsplit(probe)
        if parsed.scheme or parsed.netloc:
            return "/"
        decoded = unquote(probe)
        if decoded == probe:
            return value
        probe = decoded


def login_target(value: str) -> str:
    # 登录后只支持回到网页发布的站内入口，不扩展其他重定向消费者。
    from urllib.parse import urlsplit
    value = safe_next(value)
    return value if urlsplit(value).path == "/sites/enter" else "/"


def find_site(docs: list[Doc], slug: str) -> Doc | None:
    found = [doc for doc in docs if doc.kind == "app" and not doc.problem and site_of(doc).get("slug") == slug]
    return found[0] if len(found) == 1 else None


def summary(doc: Doc) -> dict | None:
    site = site_of(doc)
    if not config.site_domain() or not site:
        return None
    return {**site, "url": url(site["slug"])}


def write_site(base, doc: Doc, site: dict, subject: str, actor: str, signed_as=None, *, tool="site") -> str:
    meta = {**doc.meta, "site": site, "updated": base._today(),
            "updated_by": gitops.author_name(actor, signed_as)}
    return gitops.write_and_commit(base.root, {doc.rel: apps.file_content(meta, apps.notes_of(doc))},
                                   subject, tool, actor, signed_as)


def people(docs: list[Doc]) -> list[dict]:
    result = []
    for doc in docs:
        if doc.kind != "person" or doc.meta.get("kind") != "human":
            continue
        key = doc.meta.get("agent_key")
        if key != config.owner_key() and make_viewer(docs, key, remote=True).can_remote:
            result.append({"key": key, "name": doc.title})
    return result


def content_dir(site: dict, version: str) -> Path:
    # 地址可以改，已发布版本的位置不改：撤回改名只需恢复文档，也不会留下多份旧版本。
    record = next((item for item in site.get("versions", []) if item.get("id") == version), None)
    if not record:
        raise BaseError(tr("agent.439"))
    return version_dir(record["storage_slug"], version)


class SiteMixin:
    def manage_site(self, app_id: str, data: dict) -> dict:
        enabled()
        if not isinstance(data, dict) or set(data) - {"action", "visibility", "members", "confirm_public", "version", "slug"}:
            raise BaseError(tr("agent.441"))
        action = data.get("action")
        if action not in ("publish", "visibility", "rollback", "disable", "enable", "rename", "phone"):
            raise BaseError(tr("agent.442"))
        with self._write():
            viewer, docs, visible = self._owner()
            if self.require_remote:
                raise BaseError(tr("agent.450"))
            doc = self._find(visible, "app", app_id)
            if not doc or not site_of(doc):
                raise BaseError(tr("agent.451"))
            site = dict(site_of(doc))
            if action in ("publish", "visibility"):
                visibility = data.get("visibility")
                if visibility not in VISIBILITY:
                    raise BaseError(tr("agent.456"))
                if visibility == "公开" and data.get("confirm_public") is not True:
                    raise BaseError(tr("agent.457"))
                members = data.get("members", [])
                allowed = {person["key"] for person in people(docs)}
                if not isinstance(members, list) or any(not isinstance(k, str) or k not in allowed for k in members):
                    raise BaseError(tr("agent.458"))
                site.update(visibility=visibility, members=sorted(set(members)) if visibility == "指定的人" else [])
            if action in ("publish", "enable"):
                version = site.get("pending") or site.get("current")
                if not version or not (content_dir(site, version) / "index.html").is_file():
                    raise BaseError(tr("agent.459"))
                site.update(status="已发布", current=version, pending=None)
            elif action == "disable":
                site["status"] = "已停用"
            elif action == "rollback":
                version = data.get("version")
                if (version not in {v["id"] for v in site["versions"]}
                        or not (content_dir(site, version) / "index.html").is_file()):
                    raise BaseError(tr("agent.460"))
                if site["status"] != "已发布":
                    raise BaseError(tr("agent.461"))
                site.update(current=version, pending=None)
            elif action == "rename":
                new_slug = check_slug(data.get("slug"), docs, doc.id)
                site["slug"] = new_slug
            elif action == "phone":
                if site["status"] != "已发布":
                    raise BaseError(tr("agent.462"))
                preview = secrets.token_hex(16)
                site["previews"] = [grant for grant in site.get("previews", []) if not tokens._expired(grant)]
                site["previews"].append({"id": preview,
                    "expires": (now_taipei() + dt.timedelta(days=7, minutes=10)).isoformat(timespec="seconds")})
            site.update(updated=now_taipei().isoformat(timespec="microseconds"), updated_by=viewer.key)
            labels = {"publish": "发布", "visibility": "改可见范围", "rollback": "回退版本", "disable": "停用",
                      "enable": "重新启用", "rename": "改短名", "phone": "生成手机链接"}
            commit = write_site(self, doc, site, f"网页：{labels[action]}「{doc.title}」", viewer.key)
            result = {"ok": True, "commit": commit, "message": tr("agent.443")}
            if action == "phone":
                from .siteauth import issue_code
                code = issue_code(site["slug"], viewer.key, "主人预览", preview=preview)
                result["url"] = f"{url(site['slug'])}/_base/callback?code={code}"
                # 同一条一次性链接只在内存中编码；SVG 和链接随本次响应返回，不记日志、不存盘。
                import segno
                svg = io.BytesIO()
                segno.make_qr(result["url"], error="m").save(svg, kind="svg", scale=5, border=4, xmldecl=False)
                result["qr_svg"] = svg.getvalue().decode("utf-8")
                result["message"] = tr("phone_saved")
            if action == "rename":
                result["message"] = tr("slug_renamed")
            return result

    def publish_site(self, app_id: str, build_dir: str, slug: str | None = None,
                     signed_as: str | None = None) -> str:
        if self.require_remote or self.allow_owner:
            raise BaseError(tr("agent.444"))
        enabled()
        signed_as = self._sign(signed_as)
        with self._write():
            viewer, docs, visible = self._writer()
            doc = self._find(visible, "app", app_id)
            if not doc:
                raise BaseError(tr("agent.452"))
            if doc.meta.get("kind") != "网页":
                raise BaseError(tr("agent.453"))
            site = dict(site_of(doc))
            old_slug = site.get("slug")
            if old_slug and slug is not None and old_slug != slug:
                raise BaseError(tr("agent.454"))
            slug = check_slug(old_slug or slug, docs, doc.id)
            version, skipped = store_build(build_dir, slug)
            stamp = now_taipei().isoformat(timespec="seconds")
            author = gitops.author_name(viewer.key, signed_as)
            previous = site.get("versions", [])
            versions = [{"id": version, "created": stamp, "by": author, "storage_slug": slug}, *previous][:5]
            site.update(slug=slug, status=site.get("status", "待发布"),
                        visibility=site.get("visibility", "指定的人"), members=site.get("members", []),
                        versions=versions, updated=stamp, updated_by=author)
            if site["status"] == "已发布":
                site.update(current=version, pending=None)
            else:
                site["pending"] = version
                # 停用期间连续构建也只保留五版；被清理的旧当前版本不能再被引用。
                if site.get("current") not in {v["id"] for v in versions}:
                    site["current"] = None
            try:
                commit = write_site(self, doc, site, f"网页：更新了「{doc.title}」的版本", viewer.key, signed_as, tool="publish_site")
            except Exception:
                remove_tree(version_dir(slug, version), slug_dir(slug))
                raise
            # 只清理本应用刚淘汰的版本；旧短名可能已经分配给另一个应用，不能扫整目录。
            for old in previous[4:]:
                remove_tree(version_dir(old["storage_slug"], old["id"]), slug_dir(old["storage_slug"]))
        message = tr("publish_result", url=url(slug), status=site['status'], commit=commit)
        message += tr("publish_skipped", files="、".join(skipped) if skipped else tr("agent.121"))
        if site["status"] != "已发布":
            message += tr("publish_visibility")
        return message
