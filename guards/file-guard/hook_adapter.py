"""Thin native-hook dialect adapter. All deletion policy lives in file_guard."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any

HERE = Path(__file__).resolve().parent


def load_core():
    root = HERE if (HERE / "file_guard.py").is_file() else HERE.parent / "core-candidate"
    sys.path.insert(0, str(root))
    import file_guard
    return file_guard


def reason_text(policy: dict[str, Any]) -> str:
    message = policy.get("code", "FILE_GUARD_UNAVAILABLE") + ": " + policy.get("reason", "The common file guard is unavailable.")
    if policy.get("safe_suggestion"):
        message += "\n" + policy["safe_suggestion"]
    return message


def denied(client: str, reason: str) -> dict[str, Any]:
    if client == "grok":
        return {"decision": "deny", "reason": reason}
    if client == "cursor":
        return {"permission": "deny", "agent_message": reason, "user_message": "已阻止该命令；模型可修正命令后继续。"}
    return {"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny", "permissionDecisionReason": reason}}


def owned_invocation(client: str, event: Any) -> dict[str, Any] | None:
    if not isinstance(event, dict):
        return None
    camel = "toolName" in event or "toolInput" in event
    snake = "tool_name" in event or "tool_input" in event
    event_name = event.get("hook_event_name", event.get("hookEventName"))
    if client == "grok":
        if not camel or event.get("toolName") != "run_terminal_command" or event_name not in {None, "PreToolUse"}:
            return None
        tool_input = event.get("toolInput", {})
        tool_input = tool_input if isinstance(tool_input, dict) else {}
        shell = tool_input.get("shell") or event.get("shell") or "powershell"
        command_key = "command"
        rewritable = True
    elif client == "cursor":
        # Grok sends its native camelCase event even to imported Cursor hooks.
        if camel:
            return None
        if snake:
            if event.get("tool_name") != "Shell" or event_name != "preToolUse":
                return None
            tool_input = event.get("tool_input", {})
            rewritable = True
        else:
            if "command" not in event or event_name not in {None, "beforeShellExecution"}:
                return None
            tool_input = event
            rewritable = False
        shell = tool_input.get("shell") or event.get("shell")
        if not shell and os.environ.get("CURSOR_INVOKED_AS"):
            shell = os.environ.get("SHELL") or ("bash" if os.environ.get("MSYSTEM") else "powershell")
        shell = shell or "unknown"
        command_key = "command"
    else:
        # In particular, imported Claude hooks do not inspect Grok events again.
        if camel or not snake or event_name not in {None, "PreToolUse"}:
            return None
        name = event.get("tool_name")
        if client == "claude" and name not in {"Bash", "PowerShell"}:
            return None
        if client == "codex" and name not in {"Bash", "exec_command"}:
            return None
        tool_input = event.get("tool_input", {})
        tool_input = tool_input if isinstance(tool_input, dict) else {}
        command_key = "command" if "command" in tool_input else "cmd"
        if client == "claude":
            shell = "powershell" if name == "PowerShell" else (tool_input.get("shell") or "bash")
            rewritable = name == "PowerShell"
        else:
            shell = tool_input.get("shell") or ("powershell" if os.name == "nt" else "bash")
            rewritable = True
    cwd = tool_input.get("cwd") or tool_input.get("workdir") or event.get("cwd") or os.getcwd()
    return {"input": tool_input, "command_key": command_key, "command": tool_input.get(command_key), "cwd": cwd, "shell": shell, "rewritable": rewritable}


def wrap_stop(command: str) -> dict[str, Any]:
    core = load_core()
    try:
        executable = core.pwsh_executable()
    except core.ConfigError as exc:
        return {"code": "CONFIG_INVALID", "reason": exc.reason}
    if not executable:
        return {"code": "POWERSHELL_PARSER_UNAVAILABLE", "reason": "The PowerShell parser required for Stop insertion is unavailable."}
    try:
        process = subprocess.run([executable, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(HERE / "powershell_stop.ps1")], input=command.encode("utf-8"), capture_output=True, timeout=12, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        output = json.loads(process.stdout.decode("utf-8-sig"))
        if not isinstance(output, dict) or not isinstance(output.get("command"), str):
            return {"code": output.get("code", "POWERSHELL_REWRITE_FAILED"), "reason": output.get("reason", "PowerShell Stop insertion failed.")}
        return output
    except (OSError, subprocess.SubprocessError, ValueError, AttributeError):
        return {"code": "POWERSHELL_REWRITE_FAILED", "reason": "PowerShell Stop insertion could not complete safely."}


def adapt(client: str, event: Any) -> dict[str, Any] | None:
    if client == "cursor" and isinstance(event, dict) and event.get("hook_event_name") == "sessionStart" and "toolName" not in event:
        try:
            core = load_core()
            rules_path = core.load_settings().get("session_rules_path")
        except Exception:
            return {"additional_context": "FILE_GUARD_UNAVAILABLE: The file guard config could not be read."}
        if not rules_path:
            return None
        recycler = str(Path(core.__file__).resolve())
        missing = "Before work, read the configured session rules."
        truncated = "Before work, read the complete configured session rules. Batch/recursive deletion requires independently resolved, boundary-checked literal absolute targets; prefer the recycler at " + recycler + "."
        try:
            rules = Path(rules_path).read_text("utf-8-sig")
        except OSError:
            rules = missing
        if len(rules) > 9800:
            rules = truncated
        return {"additional_context": rules}
    invocation = owned_invocation(client, event)
    if invocation is None:
        return None
    if not isinstance(invocation["command"], str) or not isinstance(invocation["cwd"], str) or not isinstance(invocation["shell"], str):
        return denied(client, "INVALID_TOOL_INPUT: The native shell tool must provide a visible string command, cwd, and shell.")
    try:
        core = load_core()
        policy = core.inspect({key: invocation[key] for key in ("command", "cwd", "shell")})
        if client == "cursor" and policy.get("code") == "SHELL_UNSUPPORTED":
            # Desktop terminal metadata is absent in this native event. Inspect
            # visible operations under supported grammars, but do not rewrite an
            # unknown terminal as PowerShell or reject a normal command solely
            # because its shell was not supplied.
            candidates = [core.inspect({"command": invocation["command"], "cwd": invocation["cwd"], "shell": kind}) for kind in ("powershell", "bash", "cmd")]
            uncertain = {"POWERSHELL_PARSE_ERROR", "POWERSHELL_PARSER_UNAVAILABLE", "PYTHON_PARSE_ERROR", "NESTED_PAYLOAD_DYNAMIC"}
            definitive = [item for item in candidates if item['decision'] == 'deny' and item.get('code') not in uncertain]
            policy = definitive[0] if definitive else next((item for item in candidates if item['decision'] == 'allow'), policy)
    except Exception:
        return denied(client, "FILE_GUARD_UNAVAILABLE: The common file guard could not inspect this shell command.")
    if policy["decision"] == "deny":
        return denied(client, reason_text(policy))
    if invocation["rewritable"] and core.shell_kind(invocation["shell"]) == "powershell":
        rewrite = wrap_stop(invocation["command"])
        if "command" not in rewrite:
            return denied(client, reason_text(rewrite))
        if client == "cursor":
            return {"permission": "allow", "updated_input": {"command": rewrite["command"]}}
        if client == "codex":
            # Native Bash alias uses command even when exec_command input was cmd.
            updated = {"command": rewrite["command"]}
        else:
            updated = dict(invocation["input"])
            updated[invocation["command_key"]] = rewrite["command"]
        return {"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "allow", "updatedInput": updated}}
    return None


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--client", choices=("grok", "claude", "cursor", "codex"))
    parser.add_argument("--rewrite-powershell", action="store_true")
    args = parser.parse_args(argv)
    try:
        # Windows native pipelines may prefix valid JSON with an encoding BOM.
        # Let the JSON decoder handle the bytes, preserving strict JSON parsing.
        event = json.load(sys.stdin.buffer)
    except (ValueError, TypeError):
        output = {"code": "INVALID_REQUEST", "reason": "Provide one JSON object on stdin."} if args.rewrite_powershell else denied(args.client or "grok", "INVALID_REQUEST: Provide one native hook event on stdin.")
    else:
        if args.rewrite_powershell:
            output = wrap_stop(event["command"]) if isinstance(event, dict) and isinstance(event.get("command"), str) else {"code": "INVALID_REQUEST", "reason": "Provide a string command."}
        elif args.client:
            output = adapt(args.client, event)
        else:
            output = {"code": "INVALID_REQUEST", "reason": "Select --client or --rewrite-powershell."}
    if output is not None:
        print(json.dumps(output, ensure_ascii=False))
    # Hook denials are successful protocol responses, never hook-process crashes.
    return 0


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
