"""底座各处共用的小工具：检查写入的文字、日期、表格单元、YAML 开头标记。"""

from __future__ import annotations

from .i18n import label as system_label, text as tr

import datetime as dt
import re
import unicodedata

import yaml

from .store import BaseError

MAX_TEXT = 4000

# 像密钥的内容一律拒绝写入（底座从不存凭据的值）
SECRET_PATTERNS = [
    re.compile(r"sk-[A-Za-z0-9_\-]{20,}"),
    re.compile(r"gh[pousr]_[A-Za-z0-9]{30,}"),
    re.compile(r"github_pat_[A-Za-z0-9_]{30,}"),
    re.compile(r"xox[abposr]-[A-Za-z0-9-]{10,}"),
    re.compile(r"AKIA[0-9A-Z]{16}"),
    re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
    re.compile(r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"),
    re.compile(r"(?i)\b(password|passwd|pwd|api[_-]?key|secret|token|cookie)\s*[:=]\s*\S{8,}"),
]

# 已经写进文件的凭据样式。写入时用上面的规则拒绝；读出来给同事之前，和共享包再扫一遍。
SECRET_CONTENT = re.compile(
    r"-----BEGIN [A-Z ]*PRIVATE KEY-----|\bsk-[A-Za-z0-9_-]{20,}|\bghp_[A-Za-z0-9]{30,}|"
    r"\bgithub_pat_\w{20,}|\bxox[abpr]-[A-Za-z0-9-]{10,}|\bAKIA[0-9A-Z]{16}\b|"
    r"\bAIza[0-9A-Za-z_-]{30,}|\bcbx_[A-Za-z0-9_-]{20,}"
)


def _as_date(value) -> dt.date | None:
    if isinstance(value, dt.datetime):
        return value.date()
    if isinstance(value, dt.date):
        return value
    if isinstance(value, str):
        try:
            return dt.date.fromisoformat(value.strip())
        except ValueError:
            return None
    return None


def _ago(days: int) -> str:
    return tr("agent.549") if days <= 0 else tr("agent.550", v0=days)


def _cell(text) -> str:
    return str(text if text is not None else "").replace("|", "／").replace("\n", " ").strip()


def _strip_title(body: str) -> str:
    """去掉正文开头的一级标题（输出时由底座统一加）。"""
    lines = body.lstrip("\n").split("\n")
    if lines and lines[0].startswith("# "):
        lines = lines[1:]
    return "\n".join(lines).strip("\n")


def normalize_newlines(text: str) -> str:
    """CRLF 和单独的 CR 都收成 LF，后面的标题判断才不会被回车绕开。"""
    return text.replace("\r\n", "\n").replace("\r", "\n")


def soften_headings(text: str) -> str:
    """写入正文前：先统一换行，再把行首的二级标题降成三级。"""
    return re.sub(r"(?m)^## ", "### ", normalize_newlines(text))


def _check_text(label: str, text: str | None, *, required: bool = True, limit: int = MAX_TEXT,
                single_line: bool = False) -> str:
    label = system_label(label)
    text = normalize_newlines(text or "").strip()
    if required and not text:
        raise BaseError(tr("agent.551", v0=label))
    if len(text) > limit:
        raise BaseError(tr("agent.552", v0=label, v1=len(text), v2=limit))
    for ch in text:
        if unicodedata.category(ch) != "Cc" or ch in ("\n", "\t"):
            continue
        raise BaseError(tr("agent.553", v0=label))
    if single_line and any(ch in text for ch in ("\n", "\u2028", "\u2029")):
        raise BaseError(tr("agent.554", v0=label))
    for pat in SECRET_PATTERNS:
        if pat.search(text):
            raise BaseError(tr("agent.556", v0=label))
    return text


def _check_signature(signed_as: str | None) -> str | None:
    if signed_as is None or not str(signed_as).strip():
        return None
    s = _check_text(tr("agent.548"), signed_as, limit=30, single_line=True)
    if "·" in s:
        raise BaseError(tr("agent.555"))
    return s


class _Front(dict):
    """开头标记的最外层：总是一行一个键，方便 主人 手改；里面的短列表、短键值表仍写成一行。"""


class _FrontDumper(yaml.SafeDumper):
    pass


_FrontDumper.add_representer(
    _Front, lambda d, data: d.represent_mapping("tag:yaml.org,2002:map", data, flow_style=False))


def _dump_front(meta: dict) -> str:
    text = yaml.dump(_Front(meta), Dumper=_FrontDumper, allow_unicode=True, sort_keys=False,
                     default_flow_style=None, width=1000)
    return f"---\n{text}---\n"
