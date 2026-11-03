"""本机接入：网页和 init 用的说明，以及 `onestead connect`。"""

from __future__ import annotations

from .i18n import text as tr

import json
import os
import shutil
import sys
import sysconfig
import tempfile
import tomllib
from dataclasses import dataclass

from . import config
from .store import BaseError, Store, member_keys, now_taipei
from pathlib import Path


def tool_path(name: str) -> Path:
    """当前 Python 环境 scripts 目录里的命令。Windows 上带 .exe。"""
    suffix = ".exe" if sys.platform == "win32" else ""
    return Path(sysconfig.get_path("scripts")) / f"{name}{suffix}"


def toml_basic_string(value: str) -> str:
    escaped = value.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{escaped}"'


def connect_prompt(owner_name: str) -> str:
    """四段都能直接复制。身份用环境变量 ONESTEAD_AGENT，不在这里填死。"""
    mcp = tool_path("onestead-mcp")
    cli = tool_path("onestead")
    blob = json.dumps(
        {"mcpServers": {"onestead": {"command": str(mcp), "env": {"ONESTEAD_AGENT": tr("agent.254")}}}},
        ensure_ascii=False, indent=2)
    workbuddy_blob = json.dumps(
        {"mcpServers": {"onestead": {"type": "stdio", "command": str(mcp), "env": {"ONESTEAD_AGENT": tr("agent.254")}}}},
        ensure_ascii=False, indent=2)
    toml = "\n".join([
        "[mcp_servers.onestead]",
        f"command = {toml_basic_string(str(mcp))}",
        tr("agent.245"),
    ])
    claude_cmd = tr("agent.243", v0=mcp)
    intro = "\n\n".join((
        tr("connect.hint"),
        tr("agent.244", v0=config.display_name(), v1=mcp, v2=owner_name, v3=cli),
    ))
    sections = "\n".join([
        "## Claude Code",
        "",
        tr("agent.246"),
        "",
        tr("agent.247", v0=mcp),
        "",
        "`.mcp.json`：",
        "",
        "```json",
        blob,
        "```",
        "",
        tr("agent.248"),
        "",
        "```",
        claude_cmd,
        "```",
        "",
        "## Codex",
        "",
        tr("agent.249"),
        "",
        tr("agent.247", v0=mcp),
        "",
        "```toml",
        toml,
        "```",
        "",
        "## Cursor",
        "",
        tr("agent.250"),
        "",
        tr("agent.247", v0=mcp),
        "",
        "```json",
        blob,
        "```",
        "",
        "## WorkBuddy",
        "",
        tr("connect.workbuddy"),
        "",
        tr("agent.247", v0=mcp),
        "",
        "```json",
        workbuddy_blob,
        "```",
        "",
        "## ChatGPT",
        "",
        tr("agent.251", v0=mcp),
        tr("agent.252"),
        tr("agent.253"),
    ])
    return intro + "\n\n" + sections + "\n"


_CLIENTS = ("claude-code", "codex", "cursor", "workbuddy")
_DEFAULT_AGENT = {"claude-code": "claude", "codex": "codex", "cursor": "cursor", "workbuddy": "workbuddy"}
_TABLES = ("mcp_servers.onestead", "mcp_servers.company-base")
_HEX = frozenset("0123456789abcdefABCDEF")


@dataclass
class _Plan:
    client: str
    path: Path
    status: str
    identity: str = ""
    member_note: str = ""
    replacing: bool = False
    creating: bool = False
    preview: str = ""
    payload: bytes = b""
    reason: str = ""
    backup: str = ""


def run_connect(args) -> int:
    """默认只打印要改的文件。`--apply` 才备份并写入。一家失败不影响其他家。"""
    clients = _selected(getattr(args, "client", None))
    override = getattr(args, "agent_key", None)
    if override is not None:
        override = override.strip()
        if not override or len(clients) != 1:
            print(tr("connect.agent_key_client"), file=sys.stderr)
            return 2
    command, cmd_args = _invocation()
    found = []
    for client in clients:
        path, directory = _locations(client)
        if path.exists() or directory.is_dir():
            found.append((client, path))
    if not found:
        lines = [tr("connect.none")]
        if not args.apply:
            lines.insert(0, tr("connect.preview_banner"))
        sys.stdout.write("\n".join(lines) + "\n")
        return 0
    members = _member_names(args)
    plans = [_plan_client(client, path, command, cmd_args, override, members) for client, path in found]
    failed = False
    if args.apply:
        for plan in plans:
            if plan.status == "change":
                _apply(plan)
                failed = failed or plan.status == "failed"
    parts = [_format_plan(plan) for plan in plans]
    if not args.apply:
        parts.insert(0, tr("connect.preview_banner"))
    sys.stdout.write("\n\n".join(parts) + "\n")
    return 1 if failed else 0


def _selected(chosen) -> list[str]:
    if not chosen:
        return list(_CLIENTS)
    out = []
    for name in chosen:
        if name not in out:
            out.append(name)
    return out


def _locations(client: str) -> tuple[Path, Path]:
    home = Path.home()
    if client == "claude-code":
        return home / ".claude.json", home / ".claude"
    if client == "codex":
        override = os.environ.get("CODEX_HOME")
        root = Path(override).expanduser() if override else home / ".codex"
        return root / "config.toml", root
    root = home / (".workbuddy" if client == "workbuddy" else ".cursor")
    return root / "mcp.json", root


def _invocation() -> tuple[str, list[str]]:
    path = tool_path("onestead-mcp")
    if path.is_file():
        return str(path), []
    return sys.executable, ["-m", "onestead.server"]


def _member_names(args) -> set[str] | None:
    given = getattr(args, "data", None)
    try:
        root = given or config.data_dir()
    except BaseError:
        return None
    try:
        return set(member_keys(Store(root).load()))
    except (BaseError, OSError):
        return None


def _member_note(names: set[str] | None, key: str) -> str:
    if names is None:
        return tr("connect.member_unknown")
    if key not in names:
        return tr("connect.member_missing", agent=key)
    return ""


def _plan_client(client, path, command, cmd_args, override, members) -> _Plan:
    if path.exists() and not path.is_file():
        return _Plan(client, path, "skip", reason=tr("connect.reason_not_file"))
    if not path.exists():
        identity = override or _DEFAULT_AGENT[client]
        payload, preview = _fresh(client, command, cmd_args, identity)
        return _Plan(client, path, "change", identity=identity, member_note=_member_note(members, identity),
                     creating=True, preview=preview, payload=payload)
    try:
        raw = path.read_bytes()
    except OSError:
        return _Plan(client, path, "skip", reason=tr("connect.reason_read"))
    if client == "codex":
        return _plan_toml(client, path, raw, command, cmd_args, override, members)
    return _plan_json(client, path, raw, command, cmd_args, override, members)


def _fresh(client: str, command: str, cmd_args: list[str], identity: str) -> tuple[bytes, str]:
    if client == "codex":
        block = _toml_block(command, cmd_args, identity)
        return block.encode("utf-8"), block.rstrip("\n")
    entry = _entry(client, command, cmd_args, identity)
    payload = _dump_json({"mcpServers": {"onestead": entry}})
    return payload, json.dumps(entry, ensure_ascii=False, indent=2)


def _plan_json(client, path, raw, command, cmd_args, override, members) -> _Plan:
    try:
        data = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
        return _Plan(client, path, "skip", reason=tr("connect.reason_json"))
    if not isinstance(data, dict):
        return _Plan(client, path, "skip", reason=tr("connect.reason_shape"))
    if "mcpServers" in data and not isinstance(data["mcpServers"], dict):
        return _Plan(client, path, "skip", reason=tr("connect.reason_shape"))
    servers = data.get("mcpServers") or {}
    identity = override or _identity(servers) or _DEFAULT_AGENT[client]
    entry = _entry(client, command, cmd_args, identity)
    note = _member_note(members, identity)
    replacing = "company-base" in servers
    if not replacing and servers.get("onestead") == entry:
        return _Plan(client, path, "current", identity=identity, member_note=note)
    updated = dict(data)
    merged = dict(servers)
    merged.pop("company-base", None)
    merged["onestead"] = entry
    updated["mcpServers"] = merged
    return _Plan(client, path, "change", identity=identity, member_note=note, replacing=replacing,
                 preview=json.dumps(entry, ensure_ascii=False, indent=2), payload=_dump_json(updated))


def _plan_toml(client, path, raw, command, cmd_args, override, members) -> _Plan:
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        return _Plan(client, path, "skip", reason=tr("connect.reason_encoding"))
    try:
        data = tomllib.loads(text)
    except tomllib.TOMLDecodeError:
        return _Plan(client, path, "skip", reason=tr("connect.reason_toml"))
    scanned = _scan_toml(text)
    servers = data.get("mcp_servers") if isinstance(data, dict) else None
    if scanned is None or (servers is not None and not isinstance(servers, dict)):
        return _Plan(client, path, "skip", reason=tr("connect.reason_toml_unsafe"))
    if not isinstance(servers, dict):
        servers = {}
    headers, inline = scanned
    if inline or not _covered(servers, headers):
        return _Plan(client, path, "skip", reason=tr("connect.reason_toml_unsafe"))
    identity = override or _identity(servers) or _DEFAULT_AGENT[client]
    note = _member_note(members, identity)
    block = _toml_block(command, cmd_args, identity)
    new_text = _with_block(_remove_tables(text, headers), block)
    replacing = "company-base" in servers
    if new_text == text:
        return _Plan(client, path, "current", identity=identity, member_note=note)
    return _Plan(client, path, "change", identity=identity, member_note=note, replacing=replacing,
                 preview=block.rstrip("\n"), payload=new_text.encode("utf-8"))


def _entry(client: str, command: str, cmd_args: list[str], identity: str) -> dict:
    """WorkBuddy 的 mcp.json 里每项都写明 type，照它的样子补上；其他家不加，免得已接好的被判成要改。"""
    entry = {"type": "stdio", "command": command} if client == "workbuddy" else {"command": command}
    if cmd_args:
        entry["args"] = list(cmd_args)
    entry["env"] = {"ONESTEAD_AGENT": identity}
    return entry


def _dump_json(data: dict) -> bytes:
    return (json.dumps(data, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def _toml_block(command: str, cmd_args: list[str], identity: str) -> str:
    lines = ["[mcp_servers.onestead]", f"command = {toml_basic_string(command)}"]
    if cmd_args:
        lines.append("args = [" + ", ".join(toml_basic_string(item) for item in cmd_args) + "]")
    lines.extend(["", "[mcp_servers.onestead.env]", f"ONESTEAD_AGENT = {toml_basic_string(identity)}", ""])
    return "\n".join(lines)


def _identity(servers: dict) -> str | None:
    """迁移时旧名 company-base 上的身份优先，这样替换不会改掉原来的成员键。"""
    for name in ("company-base", "onestead"):
        entry = servers.get(name)
        if not isinstance(entry, dict):
            continue
        env = entry.get("env")
        if not isinstance(env, dict):
            continue
        for key in ("ONESTEAD_AGENT", "COMPANY_BASE_AGENT"):
            value = env.get(key)
            if isinstance(value, str) and value.strip():
                return value.strip()
    return None


def _covered(servers: dict, headers: list[tuple[int, int, str]]) -> bool:
    keys = [key for _, _, key in headers]
    for name in ("onestead", "company-base"):
        if name not in servers:
            continue
        target = "mcp_servers." + name
        if not any(key == target or key.startswith(target + ".") for key in keys):
            return False
    return True


def _with_block(text: str, block: str) -> str:
    if not text:
        return block
    if not text.endswith("\n"):
        text += "\n"
    if not text.endswith("\n\n"):
        text += "\n"
    return text + block


def _remove_tables(text: str, headers: list[tuple[int, int, str]]) -> str:
    ranges = []
    index = 0
    total = len(headers)
    while index < total:
        start, _, key = headers[index]
        target = _match_target(key, _TABLES)
        if target is None:
            index += 1
            continue
        nxt = index + 1
        while nxt < total and _match_target(headers[nxt][2], (target,)) is not None:
            nxt += 1
        end = headers[nxt][0] if nxt < total else len(text)
        ranges.append((start, end))
        index = nxt
    parts = []
    prev = 0
    for start, end in ranges:
        parts.append(text[prev:start])
        prev = end
    parts.append(text[prev:])
    return "".join(parts)


def _match_target(key: str, targets: tuple[str, ...]) -> str | None:
    found = None
    for target in targets:
        if key == target or key.startswith(target + "."):
            if found is None or len(target) > len(found):
                found = target
    return found


def _apply(plan: _Plan) -> None:
    try:
        if plan.path.is_file():
            plan.backup = str(_backup(plan.path))
        _atomic_write(plan.path, plan.payload)
    except OSError:
        plan.status = "failed"
        plan.reason = tr("connect.reason_write")
        return
    plan.status = "wrote"


def _backup(path: Path) -> Path:
    stamp = now_taipei().strftime("%Y%m%d-%H%M%S")
    candidate = path.with_name(f"{path.name}.onestead-backup-{stamp}")
    number = 2
    while candidate.exists():
        candidate = path.with_name(f"{path.name}.onestead-backup-{stamp}-{number}")
        number += 1
    shutil.copy2(path, candidate)
    return candidate


def _atomic_write(path: Path, payload: bytes) -> None:
    fd, tmp = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
        os.replace(tmp, path)
    except BaseException:
        Path(tmp).unlink(missing_ok=True)
        raise


def _format_plan(plan: _Plan) -> str:
    lines = [tr("connect.client", client=plan.client), tr("connect.file", path=plan.path)]
    if plan.identity:
            lines.append(tr("connect.identity", agent=plan.identity))
    if plan.member_note:
        lines.append(plan.member_note)
    if plan.status == "skip":
        lines.append(tr("connect.skipped", reason=plan.reason))
        return "\n".join(lines)
    if plan.status == "failed":
        lines.append(tr("connect.skipped", reason=plan.reason))
        if plan.backup:
            lines.append(tr("connect.backup", path=plan.backup))
        return "\n".join(lines)
    if plan.status == "current":
        lines.append(tr("connect.current"))
        return "\n".join(lines)
    if plan.replacing:
        lines.append(tr("connect.replace_old"))
    if plan.creating:
        lines.append(tr("connect.create"))
    lines.append(tr("connect.will_write"))
    lines.append(plan.preview)
    if plan.status == "wrote":
        lines.append(tr("connect.wrote"))
        if plan.backup:
            lines.append(tr("connect.backup", path=plan.backup))
    return "\n".join(lines)


def _scan_toml(text: str) -> tuple[list[tuple[int, int, str]], bool] | None:
    """表头按行识别，字符串里的括号不当成表。返回 None 表示不敢改。"""
    headers: list[tuple[int, int, str]] = []
    inline = False
    current = None
    opened = None
    index = 0
    length = len(text)
    while index < length:
        line_start = index
        newline = text.find("\n", index)
        limit = length if newline < 0 else newline
        segment_end = limit - 1 if limit > index and text[limit - 1] == "\r" else limit
        segment = text[index:segment_end]
        index = length if newline < 0 else newline + 1
        if opened:
            opened, error = _resume_multiline(segment, opened)
            if error:
                return None
            continue
        header, root, opened, error = _inspect_line(segment)
        if error:
            return None
        if header is not None:
            headers.append((line_start, index, header))
            current = header
        elif current == "mcp_servers" and root in ("onestead", "company-base"):
            inline = True
    if opened:
        return None
    return headers, inline


def _inspect_line(segment: str):
    index = 0
    length = len(segment)
    while index < length and segment[index] in " \t":
        index += 1
    if index >= length or segment[index] == "#":
        return None, None, None, False
    if segment[index] == "[":
        key, index, error = _parse_header_at(segment, index)
        if error or not key:
            return None, None, None, True
        while index < length and segment[index] in " \t":
            index += 1
        if index < length and segment[index] != "#":
            return None, None, None, True
        return key, None, None, False
    root = _assignment_root(segment)
    multiline, error = _scan_code(segment, 0)
    if error:
        return None, None, None, True
    return None, root, multiline, False


def _assignment_root(segment: str) -> str | None:
    index = 0
    length = len(segment)
    while index < length and segment[index] in " \t":
        index += 1
    if index >= length or segment[index] in "#[":
        return None
    key, index = _parse_key_path(segment, index)
    if not key:
        return None
    while index < length and segment[index] in " \t":
        index += 1
    if index < length and segment[index] == "=":
        return key.split(".", 1)[0]
    return None


def _parse_header_at(segment: str, index: int):
    array = segment.startswith("[[", index)
    index += 2 if array else 1
    key, index = _parse_key_path(segment, index)
    if not key:
        return None, index, True
    length = len(segment)
    while index < length and segment[index] in " \t":
        index += 1
    closer = "]]" if array else "]"
    if not segment.startswith(closer, index):
        return None, index, True
    return key, index + len(closer), False


def _parse_key_path(segment: str, index: int) -> tuple[str | None, int]:
    parts = []
    length = len(segment)
    while True:
        while index < length and segment[index] in " \t":
            index += 1
        if index >= length:
            return None, index
        if segment[index] == '"':
            read = _read_basic_string(segment, index)
            if read is None:
                return None, index
            part, index = read
        elif segment[index] == "'":
            read = _read_literal_string(segment, index)
            if read is None:
                return None, index
            part, index = read
        else:
            end = index
            while end < length and _bare(segment[end]):
                end += 1
            if end == index:
                return None, index
            part = segment[index:end]
            index = end
        parts.append(part)
        while index < length and segment[index] in " \t":
            index += 1
        if index < length and segment[index] == ".":
            index += 1
            continue
        return ".".join(parts), index


def _bare(char: str) -> bool:
    return char.isascii() and (char.isalnum() or char in "_-")


def _read_basic_string(segment: str, index: int) -> tuple[str, int] | None:
    index += 1
    length = len(segment)
    chars = []
    while index < length:
        char = segment[index]
        if char == '"':
            return "".join(chars), index + 1
        if char == "\\":
            if index + 1 >= length:
                return None
            esc = segment[index + 1]
            mapped = {"b": "\b", "t": "\t", "n": "\n", "f": "\f", "r": "\r", "\\": "\\", '"': '"'}
            if esc in mapped:
                chars.append(mapped[esc])
                index += 2
                continue
            if esc == "u" and index + 6 <= length and all(item in _HEX for item in segment[index + 2:index + 6]):
                chars.append(chr(int(segment[index + 2:index + 6], 16)))
                index += 6
                continue
            if esc == "U" and index + 10 <= length and all(item in _HEX for item in segment[index + 2:index + 10]):
                chars.append(chr(int(segment[index + 2:index + 10], 16)))
                index += 10
                continue
            return None
        chars.append(char)
        index += 1
    return None


def _read_literal_string(segment: str, index: int) -> tuple[str, int] | None:
    end = segment.find("'", index + 1)
    if end < 0:
        return None
    return segment[index + 1:end], end + 1


def _scan_code(segment: str, index: int) -> tuple[str | None, bool]:
    length = len(segment)
    while index < length:
        if segment.startswith('"""', index):
            index, still = _skip_ml_basic(segment, index + 3)
            if still:
                return '"""', False
            continue
        if segment.startswith("'''", index):
            index, still = _skip_ml_literal(segment, index + 3)
            if still:
                return "'''", False
            continue
        char = segment[index]
        if char == "#":
            return None, False
        if char == '"':
            read = _read_basic_string(segment, index)
            if read is None:
                return None, True
            index = read[1]
            continue
        if char == "'":
            read = _read_literal_string(segment, index)
            if read is None:
                return None, True
            index = read[1]
            continue
        index += 1
    return None, False


def _resume_multiline(segment: str, delim: str) -> tuple[str | None, bool]:
    if delim == '"""':
        index, still = _skip_ml_basic(segment, 0)
    else:
        index, still = _skip_ml_literal(segment, 0)
    if still:
        return delim, False
    return _scan_code(segment, index)


def _skip_ml_basic(segment: str, index: int) -> tuple[int, bool]:
    length = len(segment)
    while index < length:
        if segment.startswith('"""', index):
            return index + 3, False
        if segment[index] == "\\":
            if index + 1 >= length:
                return length, True
            index += 2
            continue
        index += 1
    return length, True


def _skip_ml_literal(segment: str, index: int) -> tuple[int, bool]:
    found = segment.find("'''", index)
    if found < 0:
        return len(segment), True
    return found + 3, False
