"""Bounded visible-command guard, explicit-path recycler, and file preimages.

This is advisory unless a client adapter enforces its returned deny decision.
It does not sandbox Windows or inspect arbitrary programs' transitive behavior.
Machine paths and runtimes come from FILE_GUARD_CONFIG, else file_guard.config.json
beside this file, else defaults derived from the home directory.
"""
from __future__ import annotations

import argparse
import ast
import json
import ntpath
import os
from pathlib import Path
import re
import shlex
import shutil
import stat
import subprocess
import sys
import uuid
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any

DELETE_NAMES = {"remove-item", "rm", "ri", "del", "erase", "rmdir", "rd"}
MOVE_NAMES = {"move-item", "mv", "move", "mi", "rename-item", "rename", "ren", "rni"}
TAIPEI = timezone(timedelta(hours=8), "Asia/Taipei")
READONLY_BASE = {"home", "pid", "pshome", "host", "executioncontext", "psversiontable", "psedition", "shellid"}
TRIGGER = re.compile(r"\b(remove-item|rm|ri|del|erase|rmdir|rd|unlink|rmtree|remove|delete|move-item|mv|move|mi|rename-item|rename|ren|rni|set-variable|new-variable|clear-variable|remove-variable|set-item|sv|nv|si|python(?:\d(?:\.\d+)?)?|(?<!\.)py|powershell|pwsh|cmd|bash|sh|eval|foreach)\b|\$(?:[\w]+:)?[\w]+\s*(?:[+*/%-]?=|\+\+|--)|\$(?:[\w]+:)?(?:home|pid|pshome|host|executioncontext|psversiontable|psedition|shellid)\b", re.I)
MAX_DEPTH = 8


def result(decision: str, code: str, reason: str, suggestion: str | None = None) -> dict[str, Any]:
    value: dict[str, Any] = {"decision": decision, "reason": reason, "code": code}
    if suggestion:
        value["safe_suggestion"] = suggestion
    return value


def allow(code: str = "VISIBLE_COMMAND_ALLOWED") -> dict[str, Any]:
    return result("allow", code, "No prohibited operation was found in the directly visible command.")


def deny(code: str, reason: str, suggestion: str | None = None) -> dict[str, Any]:
    return result("deny", code, reason, suggestion)


def native_path(value: str) -> str:
    # Git Bash's conventional drive paths are still explicit absolute paths.
    if re.match(r"^/[A-Za-z]/", value):
        value = value[1].upper() + ":/" + value[3:]
    if value.startswith("\\\\?\\UNC\\"):
        value = "\\\\" + value[8:]
    elif value.startswith("\\\\?\\"):
        value = value[4:]
    return value.replace("/", "\\")


class ConfigError(Exception):
    def __init__(self, reason: str) -> None:
        super().__init__(reason)
        self.reason = reason


_DYNAMIC_CHARS = "$%`*?[]"


def _optional_text(data: dict[str, Any], key: str) -> str | None:
    if key not in data or data[key] in (None, ""):
        return None
    value = data[key]
    if not isinstance(value, str):
        raise ConfigError(key + " must be a string.")
    stripped = value.strip()
    return stripped or None


def _literal_absolute(value: str) -> str:
    if not isinstance(value, str) or not value.strip() or any(char in value for char in _DYNAMIC_CHARS):
        raise ConfigError("Configured paths must be literal absolute paths, without variables or wildcards.")
    converted = native_path(value.strip())
    drive, tail = ntpath.splitdrive(converted)
    if not drive or not tail.startswith("\\") or converted.startswith("\\\\.\\"):
        raise ConfigError("Configured paths must be literal absolute paths, without variables or wildcards.")
    return ntpath.normpath(converted)


def _optional_absolute(data: dict[str, Any], key: str) -> str | None:
    value = _optional_text(data, key)
    if value is None:
        return None
    return _literal_absolute(value)


def _normalize_roots(values: Any) -> tuple[str, ...]:
    if not isinstance(values, list):
        raise ConfigError("protected_roots must be a list of literal absolute paths.")
    output: list[str] = []
    seen: set[str] = set()
    for value in values:
        normalized = ntpath.normcase(_literal_absolute(value))
        if normalized not in seen:
            seen.add(normalized)
            output.append(normalized)
    return tuple(output)


def default_protected_roots() -> tuple[str, ...]:
    """Home, its parent, common agent/profile dirs, and this guard's directory."""
    home = Path.home()
    guard_dir = Path(__file__).resolve().parent
    return _normalize_roots([
        str(home),
        str(home.parent),
        str(home / ".claude"),
        str(home / ".codex"),
        str(home / ".cursor"),
        str(home / "Documents"),
        str(home / "Projects"),
        str(guard_dir),
    ])


def config_file() -> Path | None:
    override = os.environ.get("FILE_GUARD_CONFIG", "").strip()
    if override:
        return Path(override)
    local = Path(__file__).resolve().with_name("file_guard.config.json")
    if local.is_file():
        return local
    return None


def load_settings() -> dict[str, Any]:
    """FILE_GUARD_CONFIG, else file_guard.config.json beside this file, else defaults.

    A selected config that is missing or unreadable is an error. Empty strings and
    null keep that key's default. protected_roots, when it is a list, replaces the
    derived roots instead of being merged with them.
    """
    path = config_file()
    if path is None:
        raw: dict[str, Any] = {}
    else:
        try:
            loaded = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError) as exc:
            raise ConfigError("The file guard config could not be read.") from exc
        if not isinstance(loaded, dict):
            raise ConfigError("The file guard config must be a JSON object.")
        raw = loaded
    if "protected_roots" not in raw or raw["protected_roots"] is None:
        roots = default_protected_roots()
    else:
        roots = _normalize_roots(raw["protected_roots"])
    preimage = _optional_absolute(raw, "preimage_root")
    if preimage is None:
        preimage = str(Path(__file__).resolve().parent / "preimages")
    return {
        "python": _optional_absolute(raw, "python"),
        "pwsh": _optional_absolute(raw, "pwsh"),
        "protected_roots": roots,
        "preimage_root": preimage,
        "session_rules_path": _optional_absolute(raw, "session_rules_path"),
    }


def protected_roots() -> tuple[str, ...]:
    return load_settings()["protected_roots"]


def python_runtime() -> str:
    try:
        configured = load_settings().get("python")
    except ConfigError:
        return sys.executable
    if configured and Path(configured).is_file():
        return configured
    return sys.executable


def path_policy(value: Any) -> tuple[dict[str, Any] | None, str | None]:
    try:
        roots = protected_roots()
    except ConfigError as exc:
        return deny("CONFIG_INVALID", exc.reason), None
    if not isinstance(value, str) or not value or "\x00" in value:
        return deny("DELETE_TARGET_UNKNOWN", "Deletion needs an explicit literal absolute filesystem path."), None
    if value in {"/", "\\"}:
        return deny("PROTECTED_TARGET", "A disk root must never be a cleanup target."), None
    if any(ch in value for ch in "$%`*?[]"):
        return deny("DELETE_TARGET_DYNAMIC", "Deletion targets cannot contain variables, interpolation, environment placeholders, or wildcards."), None
    converted = native_path(value)
    drive, tail = ntpath.splitdrive(converted)
    if not drive or not tail.startswith("\\") or converted.startswith("\\\\.\\"):
        return deny("DELETE_TARGET_NOT_ABSOLUTE", "Use a literal absolute path that has already been resolved."), None
    # Resolve existing ancestor junctions/symlinks even when the final leaf is missing.
    try:
        canonical = native_path(str(Path(converted).resolve(strict=False))) if os.name == "nt" else ntpath.normpath(converted)
    except (OSError, RuntimeError, ValueError):
        return deny("DELETE_TARGET_UNRESOLVABLE", "The final filesystem target could not be resolved safely."), None
    canonical = ntpath.normpath(canonical)
    normalized = ntpath.normcase(canonical)
    final_drive, final_tail = ntpath.splitdrive(normalized)
    if final_drive and final_tail in {"", "\\"}:
        return deny("PROTECTED_TARGET", "A disk or share root must never be a cleanup target."), None
    for protected in roots:
        if normalized == protected or protected.startswith(normalized.rstrip("\\") + "\\"):
            return deny("PROTECTED_TARGET", "The resolved target is a protected profile, guard, or backup root, or an ancestor of one."), None
    if any(part in {".", ".."} for part in tail.split("\\")):
        return deny("DELETE_TARGET_NOT_RESOLVED", "Resolve dot and parent segments before requesting cleanup."), None
    if ntpath.normcase(ntpath.normpath(converted)) != normalized:
        return deny("DELETE_TARGET_NOT_CANONICAL", "The path resolves through an alias or reparse point. Supply the resolved final target explicitly; the guard will not change deletion semantics for you."), None
    # Windows normalizes trailing spaces/dots; refuse ambiguous device/stream spellings.
    parts = tail.split("\\")
    if any(part and (part.endswith((" ", ".")) or ":" in part or re.fullmatch(r"(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part)) for part in parts):
        return deny("DELETE_TARGET_AMBIGUOUS", "Use an ordinary resolved filesystem path without devices, streams, or trailing dots/spaces."), None
    return None, canonical


def recycler_suggestion(paths: list[str], shell: str) -> str:
    words = [python_runtime(), str(Path(__file__).resolve()), "recycle"]
    for path in paths:
        words.extend(["--path", path])
    kind = shell_kind(shell)
    if kind == "powershell":
        return "& " + " ".join("'" + word.replace("'", "''") + "'" for word in words)
    if kind == "cmd":
        return subprocess.list2cmdline(words)
    if kind == "python":
        return "import subprocess; subprocess.run(" + repr(words) + ", check=True)"
    return " ".join(shlex.quote(word) for word in words)


def deletion(paths: list[Any], shell: str, *, unknown: bool = False) -> dict[str, Any]:
    if unknown or not paths:
        return deny("DELETE_TARGET_UNKNOWN", "The visible deletion does not identify all targets as literal absolute paths. Resolve them and call the recycler explicitly.")
    checked: list[str] = []
    for path in paths:
        failure, canonical = path_policy(path)
        if failure:
            return failure
        assert canonical is not None
        checked.append(canonical)
    output = deny("USE_RECYCLER", "Use reversible cleanup for these explicit targets. The guard does not execute deletion.", recycler_suggestion(checked, shell))
    output["_recycle_paths"] = checked
    return output


def moving(sources: list[Any], destination: Any, cwd: str, *, unknown: bool = False, recursive: bool = False, rename: bool = False) -> dict[str, Any] | None:
    """Check visible movers without imposing a gate on ordinary literal file renames."""
    if unknown or not sources or not isinstance(destination, str):
        return deny("MOVE_TARGET_DYNAMIC", "Resolve all directory/batch move targets to explicit literal absolute paths before retrying.")
    operands = sources + [destination]
    if any(not isinstance(value, str) or not value or any(char in value for char in "$%`*?[]") for value in operands):
        return deny("MOVE_TARGET_DYNAMIC", "Variables, wildcard batches, and computed move targets must be resolved to explicit literal absolute paths first.")
    resolved: list[str] = []
    for index, value in enumerate(operands):
        converted = native_path(value)
        drive, tail = ntpath.splitdrive(converted)
        if not drive or not tail.startswith("\\"):
            base = ntpath.dirname(resolved[0]) if rename and index == len(sources) else native_path(cwd)
            converted = ntpath.normpath(ntpath.join(base, converted))
        failure, canonical = path_policy(converted)
        if failure:
            if failure["code"] == "PROTECTED_TARGET":
                return deny("PROTECTED_MOVE_TARGET", "A protected profile, guard, backup root, or its ancestor cannot be moved or renamed.")
            return deny("MOVE_TARGET_UNRESOLVED", "Resolve move targets independently to ordinary literal absolute paths before retrying.")
        assert canonical is not None
        resolved.append(canonical)
    directory_or_batch = recursive or len(sources) > 1 or any(os.path.isdir(path) for path in resolved[:-1])
    if directory_or_batch:
        for value in operands:
            failure, _ = path_policy(value)
            if failure:
                return deny("MOVE_TARGET_UNRESOLVED", "Directory/batch moves require already-resolved literal absolute source and destination paths. Preserve affected key files with preimage first.")
    return None


def shell_kind(shell: str) -> str:
    name = ntpath.basename(shell.replace("/", "\\")).lower().removesuffix(".exe")
    if name in {"powershell", "pwsh", "powershell.exe", "ps", "powershell7"}:
        return "powershell"
    if name in {"cmd", "cmd.exe", "command"}:
        return "cmd"
    if name in {"bash", "sh", "zsh", "dash", "git-bash"}:
        return "bash"
    if name.startswith("python") or name == "py":
        return "python"
    return "unknown"


def host_name(word: str) -> str:
    return ntpath.basename(word.replace("/", "\\")).lower().removesuffix(".exe")


def literals(node: dict[str, Any]) -> list[Any] | None:
    if node.get("kind") == "literal":
        return [node.get("value")]
    if node.get("kind") == "array":
        output = []
        for item in node.get("values", []):
            values = literals(item)
            if values is None:
                return None
            output.extend(values)
        return output
    return None


def pwsh_executable() -> str | None:
    override = os.environ.get("FILE_GUARD_PWSH", "").strip()
    configured = load_settings().get("pwsh")
    candidates = ([override] if override else []) + ([configured] if configured else []) + [shutil.which("pwsh"), shutil.which("powershell")]
    return next((candidate for candidate in candidates if candidate and Path(candidate).is_file()), None)


def parse_powershell(command: str) -> dict[str, Any] | None:
    executable = pwsh_executable()
    if not executable:
        return None
    try:
        process = subprocess.run([executable, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(Path(__file__).with_name("powershell_ast.ps1"))], input=command.encode("utf-8"), capture_output=True, timeout=12, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if process.returncode:
            return None
        return json.loads(process.stdout.decode("utf-8-sig"))
    except (OSError, subprocess.SubprocessError, ValueError):
        return None


def ps_arguments(elements: list[dict[str, Any]]) -> tuple[list[Any], dict[str, list[Any] | None], bool]:
    positions: list[Any] = []
    options: dict[str, list[Any] | None] = {}
    unknown = False
    current: str | None = None
    for element in elements:
        if element.get("kind") == "parameter":
            current = element.get("name", "").lower()
            argument = element.get("argument", {})
            options[current] = [] if argument.get("kind") == "missing" else literals(argument)
            if argument.get("kind") != "missing" or current in {"recurse", "force", "whatif", "confirm", "verbose", "debug"}:
                current = None
        else:
            value = literals(element)
            if current is not None:
                existing = options[current]
                options[current] = None if existing is None or value is None else existing + value
                current = None
            elif value is None:
                unknown = True
            else:
                positions.extend(value)
    return positions, options, unknown


def inspect_ps(command: str, cwd: str, depth: int) -> dict[str, Any]:
    try:
        tree = parse_powershell(command)
    except ConfigError as exc:
        return deny("CONFIG_INVALID", exc.reason)
    if tree is None:
        return deny("POWERSHELL_PARSER_UNAVAILABLE", "The suspicious PowerShell command could not be parsed. Use the explicit-path recycler for cleanup.")
    if tree.get("parse_error_count"):
        return deny("POWERSHELL_PARSE_ERROR", "The suspicious PowerShell command has parse errors; correct it before execution.")
    readonly = READONLY_BASE | {name.lower() for name in tree.get("readonly_names", [])}
    for name in tree.get("assignments", []):
        parts = name.lower().split(":")
        if parts[-1] in readonly and (len(parts) == 1 or parts[-2] in {"global", "local", "script", "private", "variable"}):
            return deny("READONLY_AUTOMATIC_VARIABLE", "Assigning a readonly PowerShell automatic variable can fail while later statements continue. Use a task-specific variable name.")
    for method in tree.get("methods", []):
        member = literals(method.get("member", {}))
        if method.get("static") and (method.get("type") or "").lower() in {"io.directory", "system.io.directory", "io.file", "system.io.file"} and member and str(member[0]).lower() == "delete":
            args = method.get("arguments", [])
            first = literals(args[0]) if args else None
            return deletion(first or [], "powershell", unknown=first is None)
    for item in tree.get("commands", []):
        elements = item.get("elements", [])
        if not elements:
            continue
        head = literals(elements[0])
        if not head or not isinstance(head[0], str):
            continue
        name = host_name(head[0]).split("\\")[-1]
        positions, options, unknown = ps_arguments(elements[1:])
        if name in DELETE_NAMES:
            if any(key in options for key in {"include", "exclude", "filter"}):
                return deny("DELETE_TARGET_DYNAMIC", "Filtered or wildcard deletion must first be converted to explicit resolved paths.")
            paths: list[Any] = list(positions)
            for key in ("literalpath", "path"):
                if key in options:
                    if options[key] is None:
                        unknown = True
                    else:
                        paths.extend(options[key] or [])
            return deletion(paths, "powershell", unknown=unknown)
        if name in MOVE_NAMES:
            if not positions and not options:
                continue
            if any(key in options for key in {"include", "exclude", "filter"}):
                return deny("MOVE_TARGET_DYNAMIC", "Resolve filtered or wildcard move batches to explicit literal absolute paths before retrying.")
            destination_values = options.get("destination", options.get("newname"))
            if destination_values is None:
                destination = positions[-1] if positions else None
                source_positions = positions[:-1]
            else:
                destination = destination_values[0] if len(destination_values) == 1 else None
                source_positions = positions
            if "literalpath" in options or "path" in options:
                source_values = options.get("literalpath", options.get("path"))
                sources = source_values or []
                unknown = unknown or source_values is None
            else:
                sources = source_positions
            failure = moving(sources, destination, cwd, unknown=unknown, recursive="recurse" in options and options["recurse"] != [False], rename=name in {"rename-item", "rename", "ren", "rni"})
            if failure:
                return failure
        if name in {"set-variable", "new-variable", "clear-variable", "remove-variable", "sv", "nv", "clv", "rv"}:
            names = options.get("name", positions[:1])
            if names and any(str(value).lower().split(":")[-1] in readonly for value in names):
                return deny("READONLY_AUTOMATIC_VARIABLE", "Mutating a readonly PowerShell automatic variable can fail while later statements continue. Use a task-specific variable name.")
        if name in {"set-item", "si"}:
            values = options.get("literalpath", options.get("path", positions[:1]))
            if values and any(str(value).lower().startswith("variable:") and str(value).lower().split(":")[-1] in readonly for value in values):
                return deny("READONLY_AUTOMATIC_VARIABLE", "Mutating a readonly PowerShell automatic variable can fail while later statements continue. Use a task-specific variable name.")
        # PowerShell parameters already expose -Command/-c without reparsing strings.
        if shell_kind(name) in {"powershell", "cmd", "bash", "python"}:
            cooked: list[str | None] = []
            for element in elements[1:]:
                if element.get("kind") == "parameter":
                    cooked.append("-" + element.get("name", ""))
                    argument = element.get("argument", {})
                    if argument.get("kind") != "missing":
                        vals = literals(argument)
                        cooked.extend(str(v) for v in vals) if vals is not None else cooked.append(None)
                else:
                    vals = literals(element)
                    cooked.extend(str(v) for v in vals) if vals is not None else cooked.append(None)
            nested = inspect_host(name, cooked, cwd, depth + 1)
            if nested and nested["decision"] == "deny":
                return nested
    return allow()


@dataclass
class Token:
    value: str
    raw: str
    operator: bool = False


def tokenize(command: str, kind: str) -> list[Token]:
    """Tokenize visible shell command positions, preserving quoted document text."""
    tokens: list[Token] = []
    index = 0
    while index < len(command):
        char = command[index]
        if char in " \t\r":
            index += 1
            continue
        if kind == "bash" and char == "#":
            end = command.find("\n", index)
            index = len(command) if end < 0 else end
            continue
        operators = "\n;&|(){}<>" if kind == "bash" else "\n&|()< >".replace(" ", "")
        if char in operators:
            width = 1
            if command[index:index+3] == "<<-":
                width = 3
            elif command[index:index+2] in {"&&", "||", "<<", ">>"}:
                width = 2
            value = command[index:index+width]
            tokens.append(Token(value, value, True))
            index += width
            continue
        start = index
        cooked = []
        while index < len(command) and command[index] not in " \t\r" + operators:
            char = command[index]
            if char in ("'\"" if kind == "bash" else "\""):
                quote = char
                index += 1
                while index < len(command) and command[index] != quote:
                    char = command[index]
                    if kind == "bash" and quote == '"' and char == "\\" and index + 1 < len(command) and command[index+1] in '$`"\\\n':
                        index += 1
                        char = command[index]
                    cooked.append(char)
                    index += 1
                if index < len(command):
                    index += 1
            elif ((kind == "bash" and char == "\\") or (kind == "cmd" and char == "^")) and index + 1 < len(command):
                index += 1
                cooked.append(command[index])
                index += 1
            else:
                cooked.append(char)
                index += 1
        tokens.append(Token("".join(cooked), command[start:index]))
    return tokens


def strip_heredocs(command: str) -> str:
    lines = command.splitlines(keepends=True)
    output: list[str] = []
    pending: list[tuple[str, bool]] = []
    for line in lines:
        if pending:
            delimiter, strip_tabs = pending[0]
            candidate = line.rstrip("\r\n")
            if strip_tabs:
                candidate = candidate.lstrip("\t")
            if candidate == delimiter:
                pending.pop(0)
            output.append("\n")
            continue
        tokens = tokenize(line, "bash")
        for index, token in enumerate(tokens[:-1]):
            if token.operator and token.value in {"<<", "<<-"}:
                pending.append((tokens[index+1].value, token.value == "<<-"))
        output.append(line)
    return "".join(output)


def inspect_host(name: str, words: list[str | None], cwd: str, depth: int) -> dict[str, Any] | None:
    kind = shell_kind(name)
    if kind == "unknown":
        return None
    for index, word in enumerate(words):
        if word is None:
            continue
        expected = {"cmd": {"/c", "/k"}, "powershell": {"-command", "-c", "-commandwithargs"}, "bash": {"-c", "-lc", "-ic"}, "python": {"-c"}}[kind]
        if word.lower() in expected:
            remaining = words[index+1:]
            if not remaining or remaining[0] is None:
                return deny("NESTED_PAYLOAD_DYNAMIC", "The nested shell/code payload must be visible literal text for this guard to inspect it.")
            payload = remaining[0]
            if kind in {"cmd", "powershell"} and len(remaining) > 1:
                if any(value is None for value in remaining):
                    return deny("NESTED_PAYLOAD_DYNAMIC", "The nested command payload is not fully visible literal text.")
                payload = " ".join(str(value) for value in remaining)
            return _inspect(payload, cwd, kind, depth)
        if kind == "powershell" and word.lower() in {"-encodedcommand", "-enc", "-e"}:
            return deny("NESTED_PAYLOAD_ENCODED", "Use visible -Command text so the deletion guard can inspect the command.")
    return None


def inspect_token_shell(command: str, cwd: str, kind: str, depth: int) -> dict[str, Any]:
    tokens = tokenize(strip_heredocs(command) if kind == "bash" else command, kind)
    segments: list[list[Token]] = []
    current: list[Token] = []
    skip_redirect = False
    for token in tokens:
        if token.operator and token.value in {"\n", ";", "&&", "||", "|", "&", "(", ")", "{", "}"}:
            if current:
                segments.append(current)
            current = []
            skip_redirect = False
        elif token.operator and token.value in {"<", ">", "<<", "<<-", ">>"}:
            skip_redirect = True
        elif skip_redirect:
            skip_redirect = False
        else:
            current.append(token)
    if current:
        segments.append(current)
    for segment in segments:
        words = [token.value for token in segment]
        while words and (words[0].lower() in {"do", "then", "else", "elif", "call", "command", "exec", "nohup"} or (kind == "bash" and re.match(r"^[A-Za-z_][\w]*=", words[0]))):
            words.pop(0)
        if not words:
            continue
        if kind == "cmd" and words[0].lower() in {"rem", "::"}:
            continue
        if words[0].lower() == "if":
            candidate = next((i for i, word in enumerate(words[1:], 1) if host_name(word) in DELETE_NAMES | MOVE_NAMES), None)
            if candidate is not None:
                words = words[candidate:]
        name = host_name(words[0])
        if name == "xargs" and any(host_name(word) in DELETE_NAMES for word in words[1:]):
            return deny("DELETE_TARGET_UNKNOWN", "xargs deletion obtains targets dynamically. Resolve them and call the recycler explicitly.")
        if name == "xargs" and any(host_name(word) in MOVE_NAMES for word in words[1:]):
            return deny("MOVE_TARGET_DYNAMIC", "Resolve xargs move batches to explicit literal absolute paths before retrying.")
        if name in DELETE_NAMES:
            paths = []
            flags_done = False
            for word in words[1:]:
                if word == "--":
                    flags_done = True
                elif not flags_done and ((kind == "bash" and word.startswith("-")) or (kind == "cmd" and re.match(r"^/[a-z](?::.*)?$", word, re.I))):
                    continue
                else:
                    paths.append(word)
            return deletion(paths, kind)
        if name in MOVE_NAMES:
            operands = []
            destination = None
            flags_done = False
            index = 1
            while index < len(words):
                word = words[index]
                if word == "--":
                    flags_done = True
                elif not flags_done and kind == "bash" and word in {"-t", "--target-directory"}:
                    index += 1
                    destination = words[index] if index < len(words) else None
                elif not flags_done and kind == "bash" and word.startswith("--target-directory="):
                    destination = word.split("=", 1)[1]
                elif not flags_done and ((kind == "bash" and word.startswith("-")) or (kind == "cmd" and re.match(r"^/[a-z](?::.*)?$", word, re.I))):
                    pass
                else:
                    operands.append(word)
                index += 1
            if not operands and destination is None:
                continue
            sources = operands if destination is not None else operands[:-1]
            destination = destination if destination is not None else operands[-1]
            failure = moving(sources, destination, cwd, rename=name in {"rename-item", "rename", "ren", "rni"})
            if failure:
                return failure
        if name == "eval" and kind == "bash":
            if any(ch in " ".join(words[1:]) for ch in "$`"):
                return deny("NESTED_PAYLOAD_DYNAMIC", "The eval payload must be visible literal text.")
            nested = _inspect(" ".join(words[1:]), cwd, kind, depth + 1)
        else:
            nested = inspect_host(name, words[1:], cwd, depth + 1)
        if nested and nested["decision"] == "deny":
            return nested
    return allow()


def inspect_python(command: str, cwd: str, depth: int) -> dict[str, Any]:
    try:
        tree = ast.parse(command)
    except SyntaxError:
        return deny("PYTHON_PARSE_ERROR", "The visible Python payload has syntax errors; correct it before execution.")
    aliases = {"os": "os", "shutil": "shutil", "pathlib": "pathlib", "subprocess": "subprocess"}
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for imported in node.names:
                aliases[imported.asname or imported.name] = imported.name
        elif isinstance(node, ast.ImportFrom) and node.module:
            for imported in node.names:
                aliases[imported.asname or imported.name] = node.module + "." + imported.name

    def qualified(node: ast.AST) -> str:
        if isinstance(node, ast.Name):
            return aliases.get(node.id, node.id)
        if isinstance(node, ast.Attribute):
            return qualified(node.value) + "." + node.attr
        return ""

    def constant(node: ast.AST) -> Any:
        return node.value if isinstance(node, ast.Constant) else None

    for node in ast.walk(tree):
        if not isinstance(node, ast.Call):
            continue
        name = qualified(node.func)
        target: ast.AST | None = None
        known_delete = name in {"os.remove", "os.unlink", "os.rmdir", "shutil.rmtree"}
        if known_delete:
            target = node.args[0] if node.args else next((keyword.value for keyword in node.keywords if keyword.arg in {"path", "filename"}), None)
        elif isinstance(node.func, ast.Attribute) and node.func.attr in {"unlink", "rmdir"} and isinstance(node.func.value, ast.Call) and qualified(node.func.value.func) in {"pathlib.Path", "Path"}:
            known_delete = True
            target = node.func.value.args[0] if node.func.value.args else None
        if known_delete:
            value = constant(target) if target is not None else None
            return deletion([value] if value is not None else [], "python", unknown=value is None)
        if name == "os.system" and node.args and isinstance(node.args[0], ast.Constant) and isinstance(node.args[0].value, str):
            nested = _inspect(node.args[0].value, cwd, "cmd" if os.name == "nt" else "bash", depth + 1)
            if nested["decision"] == "deny":
                return nested
        if name in {"subprocess.run", "subprocess.call", "subprocess.check_call", "subprocess.check_output", "subprocess.Popen"} and node.args:
            value = node.args[0]
            if isinstance(value, (ast.List, ast.Tuple)) and all(isinstance(element, ast.Constant) and isinstance(element.value, str) for element in value.elts):
                words = [element.value for element in value.elts]
                if words:
                    nested = inspect_host(words[0], words[1:], cwd, depth + 1)
                    if nested and nested["decision"] == "deny":
                        return nested
    return allow()


def _inspect(command: str, cwd: str, kind: str, depth: int) -> dict[str, Any]:
    if depth > MAX_DEPTH:
        return deny("NESTING_LIMIT", "The visible nested command exceeds the inspection depth limit; simplify it.")
    if not TRIGGER.search(command):
        return allow("FAST_ALLOW")
    if kind == "powershell":
        return inspect_ps(command, cwd, depth)
    if kind in {"cmd", "bash"}:
        return inspect_token_shell(command, cwd, kind, depth)
    if kind == "python":
        return inspect_python(command, cwd, depth)
    return deny("SHELL_UNSUPPORTED", "Declare powershell, cmd, bash, or python so this suspicious visible command can be parsed.")


def inspect(request: dict[str, Any]) -> dict[str, Any]:
    if not isinstance(request, dict) or not isinstance(request.get("command"), str) or not isinstance(request.get("cwd"), str) or not isinstance(request.get("shell"), str):
        return deny("INVALID_REQUEST", "inspect requires string command, cwd, and shell fields.")
    output = _inspect(request["command"], request["cwd"], shell_kind(request["shell"]), 0)
    # The corrective invocation must match the original caller, not its nested shell.
    paths = output.pop("_recycle_paths", None)
    if paths:
        output["safe_suggestion"] = recycler_suggestion(paths, request["shell"])
    return output


def recycle(paths: list[str]) -> tuple[dict[str, Any], int]:
    """Preflight the complete batch, then recycle canonical final targets only."""
    if not paths:
        return deny("INVALID_REQUEST", "At least one --path literal absolute target is required."), 2
    checked: list[tuple[str, str]] = []
    seen: set[str] = set()
    for path in paths:
        failure, canonical = path_policy(path)
        if failure:
            return failure, 2
        assert canonical is not None
        normalized = ntpath.normcase(canonical)
        if normalized not in seen:
            checked.append((path, canonical))
            seen.add(normalized)
    try:
        from send2trash import send2trash
    except ImportError:
        return deny("RECYCLER_UNAVAILABLE", "send2trash is unavailable in this runtime. Nothing was permanently deleted."), 3
    entries: list[dict[str, Any]] = []
    for original, canonical in checked:
        failure, current = path_policy(original)
        if failure or current is None or ntpath.normcase(current) != ntpath.normcase(canonical):
            output = failure or deny("DELETE_TARGET_CHANGED", "The resolved cleanup target changed after preflight.")
            output["results"] = entries
            return output, 3
        if not os.path.lexists(canonical):
            entries.append({"path": canonical, "status": "missing"})
            continue
        try:
            send2trash(canonical)
            entries.append({"path": canonical, "status": "recycled"})
        except Exception:
            output = deny("RECYCLE_FAILED", "Windows recycle failed. No permanent-delete fallback was used.")
            output["results"] = entries + [{"path": canonical, "status": "failed"}]
            return output, 3
    output = result("allow", "RECYCLED", "Explicit cleanup completed through the recycle bin; missing targets were harmless no-ops.")
    output["results"] = entries
    return output, 0


def preimage_source(path: str) -> tuple[dict[str, Any] | None, str | None]:
    failure, canonical = path_policy(path)
    if failure or canonical is None:
        return deny("PREIMAGE_TARGET_INVALID", "Preimage sources must be independently resolved literal absolute paths, without aliases, variables, or wildcards."), None
    try:
        metadata = os.lstat(canonical)
    except OSError:
        return deny("PREIMAGE_SOURCE_MISSING", "Every preimage source must already exist as an ordinary file."), None
    if not stat.S_ISREG(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
        return deny("PREIMAGE_SOURCE_NOT_REGULAR", "Preimage copies ordinary files only; directories and reparse/link sources are not followed."), None
    return None, canonical


def preimage(paths: list[str], root: str | None = None) -> tuple[dict[str, Any], int]:
    """Retain specified old files before replacement; never write the sources."""
    if not paths:
        return deny("INVALID_REQUEST", "At least one --path literal absolute source is required."), 2
    checked: list[tuple[str, str]] = []
    seen: set[str] = set()
    # Validate the entire source batch before creating any destination directory.
    for original in paths:
        failure, canonical = preimage_source(original)
        if failure:
            return failure, 2
        assert canonical is not None
        normalized = ntpath.normcase(canonical)
        if normalized not in seen:
            checked.append((original, canonical))
            seen.add(normalized)
    try:
        default_root = load_settings()["preimage_root"]
    except ConfigError as exc:
        return deny("CONFIG_INVALID", exc.reason), 2
    failure, destination = path_policy(root or default_root)
    if failure or destination is None:
        return deny("PREIMAGE_ROOT_INVALID", "The preimage root must be an independently resolved literal absolute directory path."), 2
    destination_path = Path(destination)
    if destination_path.exists() and not destination_path.is_dir():
        return deny("PREIMAGE_ROOT_INVALID", "The preimage root must be a directory."), 2
    batch = destination_path / (datetime.now(TAIPEI).strftime("%Y%m%dT%H%M%S.%f%z") + "-" + uuid.uuid4().hex[:12])
    manifest = batch / "manifest.json"
    entries: list[dict[str, str]] = []
    try:
        destination_path.mkdir(parents=True, exist_ok=True)
        batch.mkdir()
        for index, (original, canonical) in enumerate(checked, 1):
            failure, current = preimage_source(original)
            if failure or current is None or ntpath.normcase(current) != ntpath.normcase(canonical):
                raise OSError("source changed after preflight")
            backup = batch / (f"{index:04d}--" + ntpath.basename(canonical))
            shutil.copy2(canonical, backup, follow_symlinks=False)
            copied = os.lstat(backup)
            if not stat.S_ISREG(copied.st_mode) or getattr(copied, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
                raise OSError("source became a link during copy")
            entries.append({"source": canonical, "backup": str(backup)})
            manifest.write_text(json.dumps(entries, ensure_ascii=False, indent=2), encoding="utf-8")
    except OSError:
        output = deny("PREIMAGE_COPY_FAILED", "Preimage did not finish. Do not replace originals; any completed copies remain in the batch.")
        if manifest.exists():
            output["manifest"] = str(manifest)
        return output, 3
    output = result("allow", "PREIMAGE_SAVED", "The specified old ordinary files were retained; source files were not modified.")
    output["manifest"] = str(manifest)
    output["count"] = len(entries)
    return output, 0


def main(argv: list[str] | None = None) -> int:
    class JsonArgumentParser(argparse.ArgumentParser):
        def error(self, message):
            raise ValueError("invalid arguments")

    parser = JsonArgumentParser(add_help=False)
    parser.add_argument("action", nargs="?", choices=("inspect", "recycle", "preimage"))
    parser.add_argument("--path", action="append", default=[])
    parser.add_argument("--preimage-root")
    parser.add_argument("--help", action="store_true")
    try:
        args = parser.parse_args(argv)
    except (SystemExit, ValueError):
        print(json.dumps(deny("INVALID_REQUEST", "Use inspect with JSON on stdin, or recycle/preimage with repeated --path arguments.")))
        return 2
    if args.help or not args.action:
        print(json.dumps({"usage": "file_guard.py inspect < request.json | file_guard.py recycle --path ABSOLUTE [--path ABSOLUTE] | file_guard.py preimage --path ABSOLUTE [--path ABSOLUTE] [--preimage-root ABSOLUTE]", "inspect_contract": {"command": "string", "cwd": "string", "shell": "powershell|cmd|bash|python"}}))
        return 0 if args.help else 2
    if args.action == "inspect":
        try:
            output = inspect(json.load(sys.stdin.buffer))
        except (ValueError, TypeError):
            output = deny("INVALID_REQUEST", "stdin must contain one JSON inspect request.")
        print(json.dumps(output, ensure_ascii=False))
        return 0 if output["decision"] == "allow" else 2
    output, code = preimage(args.path, args.preimage_root) if args.action == "preimage" else recycle(args.path)
    print(json.dumps(output, ensure_ascii=False))
    return code


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
