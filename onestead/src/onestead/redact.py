"""给同事看之前先处理文字：本机路径、凭据和只允许的公网链接。

MCP、命令行和网页投影都用这里，核心不再反过来引用网页包。
"""

from __future__ import annotations

import ipaddress
import re
from dataclasses import replace
from urllib.parse import urlsplit

import markdown as md
import nh3

from .store import Doc, Viewer
from .textutil import SECRET_CONTENT, SECRET_PATTERNS, _dump_front

# Markdown 的括号、反引号和中文标点是路径边界，不吃掉链接及代码块的结构。
PATH_START = (r"(?:file:/+|(?<![A-Za-z0-9])[A-Z]:[\\/]|(?<![\w.\\])\\\\(?=[^\\/\s]+[\\/])|"
              r"%[A-Z_][A-Z0-9_]*%[\\/]|~[\\/]|(?<![\w./])/(?:Users|home|tmp|var/tmp)/)")
PATH_PART = r"[^\s`\"'<>|()\[\]{}，。；！？,;]"
LOCAL_PATH = re.compile(r"(?i)" + PATH_START + PATH_PART + r"*(?:[ \t]+" + PATH_PART + r"*[\\/.]" + PATH_PART + r"+)*")
QUOTED_PATH = re.compile(r'''(?i)(["'])(''' + PATH_START + r'''[^\r\n]*?)\1''')
LINK_PATH = re.compile(r"(?i)(?<=\]\()" + PATH_START + r"[^\r\n)]*")

# 页面以 主人 的身份运行。内容里混进脚本就能替他点“撤回”“批准”，所以只放行排版标签。
ALLOWED_TAGS = {"p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "em", "del", "code", "pre",
                "blockquote", "ul", "ol", "li", "table", "thead", "tbody", "tr", "th", "td", "a"}
ALLOWED_ATTRS = {"a": {"href", "title"}, "th": {"align"}, "td": {"align"}}


def mask_local(text) -> str:
    """只替换可识别的本机路径，不按段落丢弃内容。"""
    value = str(text if text is not None else "")
    value = QUOTED_PATH.sub(lambda m: m[1] + "（本机路径）" + m[1], value)
    value = LINK_PATH.sub("（本机路径）", value)
    return LOCAL_PATH.sub("（本机路径）", value)


def safe_text(text) -> str:
    """正文和白名单文字也不能带出已有的可识别凭据。"""
    value = mask_local(text)
    for pattern in (*SECRET_PATTERNS, SECRET_CONTENT):
        value = pattern.sub("（凭据已隐藏）", value)
    return value


def text_fields(value):
    """仅供已经白名单化的结构使用；HTML 必须在这一步之后生成。"""
    if isinstance(value, str):
        return safe_text(value)
    if isinstance(value, dict):
        return {k: text_fields(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [text_fields(v) for v in value]
    return value


def safe_url(value) -> str | None:
    """只生成公网浏览器外链，不解析 DNS、不请求目标。"""
    if not isinstance(value, str) or any(c.isspace() or ord(c) < 32 for c in value) or "\\" in value:
        return None
    try:
        url = urlsplit(value)
        host = (url.hostname or "").lower().rstrip(".")
        if "%" in host:
            return None
        host = host.encode("idna").decode("ascii")
        if (url.scheme not in ("http", "https") or url.username is not None or url.password is not None
                or not host or host == "localhost" or host.endswith((".localhost", ".local"))):
            return None
        url.port  # 同时校验端口，拒绝畸形 URL。
        try:
            address = ipaddress.ip_address(host)
        except ValueError:
            # 单段名及浏览器会解释成数字地址的旧写法均不是公网域名。
            if ("." not in host or ":" in host
                    or re.fullmatch(r"(?:0x[0-9a-f]+|[0-9]+)(?:\.(?:0x[0-9a-f]+|[0-9]+))*", host, re.I)):
                return None
        else:
            if not address.is_global or (getattr(address, "ipv4_mapped", None) and not address.ipv4_mapped.is_global):
                return None
        if safe_text(value) != value:
            return None
        return value
    except (ValueError, UnicodeError):
        return None


def to_html(text: str, *, url_filter=None) -> str:
    """Markdown 转网页，并过滤脚本和危险标签。链接只认 http/https。"""
    rendered = md.markdown(text or "", extensions=["tables", "fenced_code", "sane_lists"])
    return nh3.clean(rendered, tags=ALLOWED_TAGS, attributes=ALLOWED_ATTRS,
                     attribute_filter=(lambda tag, attr, value: url_filter(value) if tag == "a" and attr == "href"
                                       else value) if url_filter else None,
                     url_schemes={"http", "https"}, link_rel="noopener noreferrer")


def html(text) -> str:
    """先遮原文；nh3 仍负责标签及脚本清理，链接另用同一公网规则。"""
    return to_html(safe_text(text), url_filter=safe_url)


def _without_local_paths(doc: Doc) -> Doc:
    """投影里不带 paths。进展文件的正文不含这个字段，只去掉复制来的标记。"""
    if "paths" not in doc.meta:
        return doc
    meta = {key: value for key, value in doc.meta.items() if key != "paths"}
    raw = _dump_front(meta) + doc.body if doc.kind == "project" else doc.raw
    return replace(doc, meta=meta, raw=raw)


def documents(viewer: Viewer, docs: list[Doc]) -> list[Doc]:
    """同事能读的文件：不要原始成员或 Skill，坏标记和下架应用也不进入计数。"""
    out = []
    for doc in viewer.visible(docs):
        if doc.problem or doc.kind in ("person", "skill"):
            continue
        if doc.kind == "app" and doc.meta.get("listed", True) is False:
            continue
        out.append(_without_local_paths(doc))
    return out
