"""Skill 广场的扫描：跟踪的几家各装了哪些 Skill、内容一不一样。只读，不改任何文件。

扫描常见本机工具：Claude、Codex、PI、Grok CLI、Cursor、WorkBuddy；额外本机工具和远程快照由配置添加。
联到共用目录 `.agents\\skills` 的算同一份；插件自带的、各家内置的只计数，页面上折叠。
内容比对用 SHA-256：技能目录里的文件按相对路径排序，路径和字节一起算。
不进 .git、node_modules、work 这类目录，不读像凭据的文件。
"""

from __future__ import annotations

import datetime as dt
import hashlib
import os
import threading
import time
from pathlib import Path

import yaml

from . import config
from .store import TAIPEI

SKIP_DIRS = {".git", "node_modules", "__pycache__", ".venv", "venv", "work", "upstream", ".cache", "cache",
             "sessions", "chats", "history", "logs", "tmp", ".tmp"}
SECRET_WORDS = ("token", "credential", "secret", "cookie", "password", "apikey", "api_key")
MAX_FILES = 600
# 文本文件含 NUL 字节视为损坏。
TEXT_EXTS = {".md", ".py", ".js", ".mjs", ".ts", ".json", ".yaml", ".yml", ".txt", ".sh", ".ps1", ".toml", ".html", ".css"}
MAX_BYTES = 40 * 1024 * 1024
CACHE_SECONDS = 300
REPARSE = 0x400


def _home() -> Path:
    return config.skill_home()


def snapshot_roots(row: dict) -> list[Path]:
    """普通快照直接扫 root；分批导出的快照按目录名排序取最新一份，再展开子路径。"""
    root = Path(row["root"])
    layout = row.get("layout")
    if layout is None:
        return [root]
    try:
        snaps = sorted(p for p in root.glob(layout.get("pattern", "*")) if p.is_dir())
    except OSError:
        return []
    if not snaps:
        return []
    return [snaps[-1] / path for path in layout.get("subpaths", ["."])]


def harness_table() -> list[dict]:
    h = _home()
    table = [
        {"key": "claude", "name": "Claude", "user": [h / ".claude" / "skills"],
         "plugins": [h / ".claude" / "plugins" / "cache"]},
        {"key": "codex", "name": "Codex", "user": [h / ".codex" / "skills"], "builtin": [h / ".codex" / "skills" / ".system"],
         "plugins": [h / ".codex" / "plugins" / "cache"]},
        {"key": "pi", "name": "PI", "user": [h / ".pi" / "agent" / "skills", h / ".pi" / "skills"]},
        # Grok CLI（Grok CLI）还会读 Claude 的目录，优先级低于自己的（见 ~/.grok/README.md 的 Skills 一节）
        {"key": "grok", "name": "Grok CLI", "user": [h / ".grok" / "skills"], "also_reads": [h / ".claude" / "skills"],
         "builtin": [h / ".grok" / "bundled" / "skills"]},
        {"key": "cursor", "name": "Cursor", "user": [h / ".cursor" / "skills"], "builtin": [h / ".cursor" / "skills-cursor"],
         "plugins": [h / ".cursor" / "plugins" / "cache"]},
        # WorkBuddy 安装包里自带的那批随系统和安装位置变，需要时在配置里按同一 key 追加 builtin
        {"key": "workbuddy", "name": "WorkBuddy", "user": [h / ".workbuddy" / "skills"],
         "plugins": [h / ".workbuddy" / "plugins" / "cache"]},
    ]
    by_key = {row["key"]: row for row in table}
    for row in config.skill_harnesses():
        entry = by_key.get(row["key"])
        if entry is None:
            entry = {"key": row["key"], "name": row["name"], "user": []}
            table.append(entry)
            by_key[row["key"]] = entry
        for kind in ("user", "builtin", "plugins"):
            if kind in row:
                entry.setdefault(kind, []).extend(Path(p).expanduser() for p in row[kind])
    return table + [{"key": row["key"], "name": row["name"], "snapshot": True,
                    "user": snapshot_roots(row)}
                   for row in config.skill_snapshots()]


def shared_dir() -> Path:
    return _home() / ".agents" / "skills"


def _real(path: Path) -> str:
    try:
        return os.path.realpath(path).lower()
    except OSError:
        return str(path).lower()


def _is_link(path: Path) -> bool:
    try:
        st = os.lstat(path)
    except OSError:
        return False
    return bool(getattr(st, "st_file_attributes", 0) & REPARSE) or path.is_symlink()


def _secret(name: str) -> bool:
    low = name.lower()
    return low.startswith(".env") or ".local." in low or any(w in low for w in SECRET_WORDS)


def _header(skill_md: Path) -> dict:
    try:
        text = skill_md.read_text(encoding="utf-8", errors="replace").lstrip("\ufeff")
    except OSError:
        return {}
    if not text.startswith("---"):
        return {}
    end = text.find("\n---", 3)
    if end < 0:
        return {}
    try:
        meta = yaml.safe_load(text[3:end]) or {}
    except yaml.YAMLError:
        return {}
    return meta if isinstance(meta, dict) else {}


def _fingerprint(skill_dir: Path) -> tuple[str | None, int, float | None, list[str]]:
    """返回（内容指纹前 12 位，文件数，最新修改时间，损坏的文本文件）。太大的不算指纹，也不查损坏。"""
    files = []
    total = 0
    for dirpath, dirnames, filenames in os.walk(skill_dir):
        dirnames[:] = sorted(d for d in dirnames if d.lower() not in SKIP_DIRS and not _is_link(Path(dirpath) / d))
        for fn in filenames:
            if _secret(fn):
                continue
            p = Path(dirpath) / fn
            try:
                st = p.stat()
            except OSError:
                continue
            files.append((p.relative_to(skill_dir).as_posix(), p, st.st_mtime, st.st_size))
            total += st.st_size
    newest = max((f[2] for f in files), default=None)
    if len(files) > MAX_FILES or total > MAX_BYTES:
        return None, len(files), newest, []
    h = hashlib.sha256()
    damaged = []
    for rel, p, _, _ in sorted(files):
        h.update(rel.encode("utf-8") + b"\0")
        try:
            raw = p.read_bytes()
        except OSError:
            return None, len(files), newest, damaged
        h.update(raw)
        h.update(b"\0")
        if p.suffix.lower() in TEXT_EXTS and b"\0" in raw:
            damaged.append(rel)
    return h.hexdigest()[:12], len(files), newest, damaged


def _when(ts: float | None) -> str | None:
    return dt.datetime.fromtimestamp(ts, TAIPEI).isoformat(timespec="minutes") if ts else None


def inspect_dir(skill_dir: Path, shared_real: str) -> dict:
    """一个技能目录的事实。"""
    link = _is_link(skill_dir)
    real = _real(skill_dir)
    exists = skill_dir.exists()
    skill_md = skill_dir / "SKILL.md"
    # borrowed：这个位置其实是借别处的（自己或上一级目录是联接）
    item = {"dir": skill_dir.name, "path": str(skill_dir), "real": real, "link": link,
            "borrowed": os.path.normcase(os.path.abspath(skill_dir)) != real,
            "shared": real.startswith(shared_real + os.sep) or real == shared_real,
            "dangling": link and not exists, "no_skill_md": exists and not skill_md.is_file(),
            "name": None, "description": None, "version": None, "hash": None, "files": 0, "newest": None,
            "damaged": []}
    if item["dangling"]:
        return item
    meta = _header(skill_md) if not item["no_skill_md"] else {}
    item["name"] = str(meta.get("name") or "").strip() or None
    desc = meta.get("description")
    item["description"] = " ".join(str(desc).split())[:400] if desc else None
    extra = meta.get("metadata") if isinstance(meta.get("metadata"), dict) else {}
    version = meta.get("version") or extra.get("version")
    item["version"] = str(version) if version else None
    item["hash"], item["files"], newest, item["damaged"] = _fingerprint(skill_dir)
    item["newest"] = _when(newest)
    return item


def _children(root: Path) -> list[Path]:
    try:
        return sorted((p for p in root.iterdir() if p.is_dir() or _is_link(p)), key=lambda p: p.name.lower())
    except OSError:
        return []


def _plugin_skills(cache: Path) -> dict[str, set[str]]:
    """插件缓存里每个插件带了哪些技能（只看名字，不算指纹）。"""
    out: dict[str, set[str]] = {}
    if not cache.is_dir():
        return out
    for dirpath, dirnames, filenames in os.walk(cache):
        dirnames[:] = [d for d in dirnames if d.lower() not in SKIP_DIRS]
        if "SKILL.md" in filenames:
            rel = Path(dirpath).relative_to(cache).parts
            plugin = rel[1] if len(rel) > 1 else rel[0]
            out.setdefault(plugin, set()).add(Path(dirpath).name)
            dirnames[:] = []
    return out


def scan() -> dict:
    shared_real = _real(shared_dir())
    result = {"scanned_at": dt.datetime.now(TAIPEI).isoformat(timespec="seconds"), "harnesses": [],
              "shared": {"path": str(shared_dir()), "items": []}}
    for h in harness_table():
        builtin_roots = {_real(p) for p in h.get("builtin", [])}
        entry = {"key": h["key"], "name": h["name"], "snapshot": bool(h.get("snapshot")),
                 "roots": [str(p) for p in h["user"] if p.exists()], "items": [], "builtin": [], "plugins": []}
        for root in h["user"]:
            for child in _children(root):
                if _real(child) in builtin_roots or child.name.startswith("."):
                    continue
                entry["items"].append(inspect_dir(child, shared_real))
        for root in h.get("also_reads", []):  # 读的是别家的目录：算借用，自己目录里有同名的就以自己的为准
            for child in _children(root):
                if not child.name.startswith("."):
                    entry["items"].append({**inspect_dir(child, shared_real), "borrowed": True, "via_other": True})
        for root in h.get("builtin", []):
            entry["builtin"] += [c.name for c in _children(root) if (c / "SKILL.md").is_file()]
        plugins: dict[str, set[str]] = {}
        for cache in h.get("plugins", []):
            for name, skills in _plugin_skills(cache).items():
                plugins.setdefault(name, set()).update(skills)
        entry["plugins"] = [{"plugin": k, "skills": sorted(v)} for k, v in sorted(plugins.items())]
        result["harnesses"].append(entry)
    result["shared"]["items"] = [inspect_dir(c, shared_real) for c in _children(shared_dir())]
    return result


_cache: dict = {"at": 0.0, "data": None}
_lock = threading.Lock()


def cached_scan(fresh: bool = False) -> dict:
    """扫一遍要一两秒，5 分钟内复用上一次的结果；fresh=True 重扫。"""
    with _lock:
        if fresh or _cache["data"] is None or time.monotonic() - _cache["at"] > CACHE_SECONDS:
            _cache["data"] = scan()
            _cache["at"] = time.monotonic()
        return _cache["data"]


# ---------- 汇总：按技能看各家 ----------

def summarize(data: dict, records: dict[str, dict]) -> list[dict]:
    """按技能目录名合并各家的安装，算出每家的状态。

    records：登记过的技能（id → 标记），有 home 时以 home 那份为准，没有就以最新的一份为准。
    每家的状态：same（和准的一致）、behind（比准的旧）、differs（内容不同但不更旧）、
    broken（联接断了、缺 SKILL.md 或文本文件被 0 填满），没装的不出现。
    """
    shared_by_dir = {i["dir"]: i for i in data["shared"]["items"]}
    skills: dict[str, dict] = {}

    def slot(sid: str) -> dict:
        return skills.setdefault(sid, {"id": sid, "copies": {}, "installs": {}})

    snapshot_keys = {h["key"] for h in data["harnesses"] if h.get("snapshot")}
    for h in data["harnesses"]:
        for it in h["items"]:
            s = slot(it["dir"])
            s["copies"].setdefault(it["real"], {**it, "snapshot": h["key"] in snapshot_keys})
            if it.get("via_other") and h["key"] in s["installs"]:
                continue
            s["installs"][h["key"]] = it
    for it in shared_by_dir.values():
        slot(it["dir"])["copies"].setdefault(it["real"], it)
    for sid, rec in records.items():
        s = slot(sid)
        home = rec.get("home")
        if home and Path(home).exists():
            it = inspect_dir(Path(home), _real(shared_dir()))
            s["copies"].setdefault(it["real"], it)
            s["home_real"] = it["real"]

    out = []
    for sid, s in skills.items():
        # 远程快照不拿来当准，避免把只导出文本的副本当完整版本
        copies = [c for c in s["copies"].values()
                  if not c["dangling"] and not c["no_skill_md"] and not c["damaged"] and not c.get("snapshot")]
        ref = None
        if s.get("home_real"):
            ref = s["copies"].get(s["home_real"])
        if ref is None and copies:
            ref = max(copies, key=lambda c: c["newest"] or "")
        def state_of(it, key=None):
            if it["dangling"] or it["no_skill_md"] or it["damaged"]:
                return "broken"
            if key in snapshot_keys and ref is not None and it["real"] != ref["real"] and it["hash"] != ref["hash"]:
                return "snapshot"  # 云端那份本机只有快照，比不了
            if ref is None or it["real"] == ref["real"] or (it["hash"] and it["hash"] == ref["hash"]):
                return "same"
            return "behind" if (it["newest"] or "") < (ref["newest"] or "") else "differs"

        installs = {}
        for key, it in s["installs"].items():
            state = state_of(it, key)
            same_copy = [k for k, o in s["installs"].items() if k != key and o["real"] == it["real"]]
            installs[key] = {"state": state, "shared": it["shared"] or (it["borrowed"] and bool(same_copy)),
                             "same_copy_as": same_copy if it["borrowed"] else [],
                             "in_shared_dir": it["shared"], "path": it["path"], "real": it["real"],
                             "dangling": it["dangling"], "no_skill_md": it["no_skill_md"],
                             "damaged": it["damaged"][:5], "damaged_count": len(it["damaged"]), "newest": it["newest"], "hash": it["hash"], "files": it["files"]}
        sample = ref or next(iter(s["copies"].values()), None) or {}
        rec = records.get(sid, {})
        listed = rec.get("listed", True) is not False
        in_shared = sid in shared_by_dir
        sc = shared_by_dir.get(sid)
        shared_copy = {"state": state_of(sc), "path": sc["path"], "newest": sc["newest"], "files": sc["files"]} if sc else None
        out.append({
            "id": sid, "name": rec.get("title") or sid, "skill_name": sample.get("name"),
            "description": sample.get("description"), "version": sample.get("version"),
            "installs": installs, "in_shared": in_shared, "shared_copy": shared_copy,
            "ref": {"path": ref["path"], "newest": ref["newest"], "hash": ref["hash"],
                    "broken": bool(ref["dangling"] or ref["no_skill_md"] or ref["damaged"])} if ref else None,
            "copies": [{"path": c["path"], "real": c["real"], "newest": c["newest"], "hash": c["hash"], "files": c["files"],
                        "shared": c["shared"], "dangling": c["dangling"], "no_skill_md": c["no_skill_md"]}
                       for c in s["copies"].values()],
            "listed": listed,
            "alert": None if not listed else "behind" if any(i["state"] in ("behind", "differs") for i in installs.values())
                     else "broken" if any(i["state"] == "broken" for i in installs.values()) else None,
            "registered": sid in records,
        })
    out.sort(key=lambda x: x["id"])
    return out
