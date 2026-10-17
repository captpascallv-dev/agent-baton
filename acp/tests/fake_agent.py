"""In-repo ACP stand-in. Speaks newline-delimited JSON-RPC on stdio and exits. No model, no network."""
import json
import os
import sys

CONFIGS = {
    "model": "unset",
    "reasoning_effort": "unset",
}


def read_message():
    line = sys.stdin.buffer.readline()
    if not line:
        return None
    return json.loads(line.decode("utf-8"))


def write_message(obj):
    sys.stdout.buffer.write((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))
    sys.stdout.buffer.flush()


def reply(msg, result):
    write_message({"jsonrpc": "2.0", "id": msg["id"], "result": result})


def fail(msg, message):
    write_message({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32000, "message": message}})


def options():
    return [
        {"id": "model", "currentValue": CONFIGS["model"], "type": "select"},
        {"id": "reasoning_effort", "currentValue": CONFIGS["reasoning_effort"], "type": "select"},
    ]


def session_result(session_id, mode):
    body = {"sessionId": session_id, "modes": []}
    if mode != "no-config":
        body["configOptions"] = options()
    return body


def main():
    mode = os.environ.get("ACP_FAKE_MODE", "full")
    resume_id = os.environ.get("ACP_FAKE_RESUME_ID", "fake-session")
    opened = False
    while True:
        msg = read_message()
        if msg is None:
            return 0
        method = msg.get("method")
        if method == "initialize":
            reply(msg, {
                "protocolVersion": 1,
                "agentInfo": {"name": "fake-acp-agent", "version": "0"},
                "agentCapabilities": {"loadSession": True},
            })
            continue
        if method == "session/new":
            if mode == "resume":
                fail(msg, "this stand-in was started for resume")
                return 1
            reply(msg, session_result("fake-session", mode))
            opened = True
            continue
        if method == "session/resume":
            if mode != "resume":
                fail(msg, "resume was not expected")
                return 1
            if (msg.get("params") or {}).get("sessionId") != resume_id:
                fail(msg, "unexpected session id")
                return 1
            reply(msg, session_result(resume_id, mode))
            opened = True
            continue
        if method == "session/set_config_option":
            params = msg.get("params") or {}
            config_id = params.get("configId")
            if mode == "no-config" or config_id not in CONFIGS:
                fail(msg, "config is not advertised")
                return 1
            if mode == "bad-readback":
                reply(msg, {"configOptions": [{"id": config_id, "currentValue": "not-applied"}]})
                continue
            CONFIGS[config_id] = params.get("value")
            reply(msg, {"configOptions": options()})
            continue
        if method == "session/prompt":
            if not opened:
                fail(msg, "no session")
                return 1
            text = ""
            for block in (msg.get("params") or {}).get("prompt") or []:
                if isinstance(block, dict) and block.get("type") == "text":
                    text += block.get("text") or ""
            write_message({"jsonrpc": "2.0", "method": "session/update", "params": {"update": {
                "sessionUpdate": "agent_message_chunk",
                "content": {"type": "text", "text": text},
            }}})
            write_message({"jsonrpc": "2.0", "method": "session/update", "params": {"update": {
                "sessionUpdate": "tool_call",
                "toolCallId": "call-1",
                "title": "echo smoke",
                "kind": "execute",
                "rawInput": {"cmd": "echo smoke"},
            }}})
            write_message({"jsonrpc": "2.0", "id": 9001, "method": "session/request_permission", "params": {
                "sessionId": (msg.get("params") or {}).get("sessionId"),
                "toolCall": {"toolCallId": "call-1", "title": "echo smoke", "kind": "execute", "rawInput": {"cmd": "echo smoke"}},
                "options": [
                    {"optionId": "allow-1", "kind": "allow_once", "name": "Allow once"},
                    {"optionId": "reject-1", "kind": "reject_once", "name": "Reject once"},
                ],
            }})
            decision = read_message()
            if not decision or decision.get("id") != 9001 or "result" not in decision:
                sys.stderr.buffer.write(b"missing permission response\n")
                return 1
            write_message({"jsonrpc": "2.0", "method": "session/update", "params": {"update": {
                "sessionUpdate": "agent_message_chunk",
                "content": {"type": "text", "text": "permission-settled"},
            }}})
            reply(msg, {"stopReason": "end_turn"})
            return 0
        fail(msg, f"unexpected method {method}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
