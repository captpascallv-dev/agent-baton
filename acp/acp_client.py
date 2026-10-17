"""Generic ACP client. Speak Agent Client Protocol over stdio to a command you choose.

No agent command is built in. Permission requests are denied unless --permission-mode approve.
"""
import argparse
import json
import os
import subprocess
import sys
import threading
import time
import queue

REJECT_KINDS = ("reject_once", "reject_always")
ALLOW_KINDS = ("allow_once", "allow_always")


def utc_now():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def config_values(payload):
    found = {}
    if not isinstance(payload, dict):
        return found
    for opt in payload.get("configOptions") or []:
        if isinstance(opt, dict) and opt.get("id"):
            found[str(opt["id"])] = opt.get("currentValue")
    return found


class Client:
    def __init__(self, argv, cwd, log_dir, permission_mode):
        self.permission_mode = permission_mode
        self.log_dir = log_dir
        os.makedirs(log_dir, exist_ok=True)
        self.events_path = os.path.join(log_dir, "events.jsonl")
        self.permissions_path = os.path.join(log_dir, "permissions.jsonl")
        self.transcript_path = os.path.join(log_dir, "transcript.md")
        self.events = open(self.events_path, "a", encoding="utf-8")
        self.permissions = open(self.permissions_path, "a", encoding="utf-8")
        self.transcript = open(self.transcript_path, "a", encoding="utf-8")
        self.stderr_handle = open(os.path.join(log_dir, "stderr.txt"), "ab")
        popen_kwargs = {}
        if os.name != "nt":
            popen_kwargs["start_new_session"] = True
        self.proc = subprocess.Popen(
            argv,
            cwd=cwd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=self.stderr_handle,
            **popen_kwargs,
        )
        self.next_id = 1
        self.pending = {}
        self.write_lock = threading.Lock()
        self.log_lock = threading.Lock()
        self.counts = {"permission_requests": 0, "tool_calls": 0, "agent_chunks": 0, "thought_chunks": 0}
        self.usage = []
        self.tool_calls = {}
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _log(self, direction, obj):
        line = json.dumps({"t": utc_now(), "dir": direction, "msg": obj}, ensure_ascii=False) + "\n"
        with self.log_lock:
            self.events.write(line)
            self.events.flush()

    def _send(self, obj):
        self._log("out", obj)
        data = (json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8")
        with self.write_lock:
            self.proc.stdin.write(data)
            self.proc.stdin.flush()

    def request(self, method, params, timeout):
        rid = self.next_id
        self.next_id += 1
        box = queue.Queue()
        self.pending[rid] = box
        self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        try:
            msg = box.get(timeout=timeout)
        except queue.Empty:
            raise TimeoutError(f"{method} 超时 {timeout}s")
        if "error" in msg:
            raise RuntimeError(f"{method} 出错: {json.dumps(msg['error'], ensure_ascii=False)[:2000]}")
        return msg.get("result")

    def _read(self):
        for raw in self.proc.stdout:
            line = raw.decode("utf-8", errors="replace").strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                self._log("in-nonjson", line[:2000])
                continue
            self._log("in", msg)
            if "id" in msg and ("result" in msg or "error" in msg) and "method" not in msg:
                box = self.pending.pop(msg["id"], None)
                if box is not None:
                    box.put(msg)
            elif "method" in msg and "id" in msg:
                self._handle_request(msg)
            elif isinstance(msg, dict) and "method" in msg:
                self._handle_notification(msg)

    def _choose(self, options):
        kinds = ALLOW_KINDS if self.permission_mode == "approve" else REJECT_KINDS
        for kind in kinds:
            for opt in options:
                if isinstance(opt, dict) and opt.get("kind") == kind:
                    return opt
        return None

    def _handle_request(self, msg):
        method = msg.get("method")
        params = msg.get("params") or {}
        if method != "session/request_permission":
            self._send({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32601, "message": f"client does not support {method}"}})
            return
        self.counts["permission_requests"] += 1
        options = params.get("options") or []
        call = params.get("toolCall") or {}
        known = self.tool_calls.get(call.get("toolCallId")) or {}
        detail = {
            "title": call.get("title") or known.get("title"),
            "kind": call.get("kind") or known.get("kind"),
            "rawInput": call.get("rawInput") if call.get("rawInput") is not None else known.get("rawInput"),
        }
        picked = self._choose(options if isinstance(options, list) else [])
        if picked:
            outcome = {"outcome": "selected", "optionId": picked.get("optionId")}
        else:
            outcome = {"outcome": "cancelled"}
        record = {
            "t": utc_now(),
            "mode": self.permission_mode,
            "title": detail.get("title"),
            "tool_kind": detail.get("kind"),
            "rawInput": detail.get("rawInput"),
            "option_kind": picked.get("kind") if picked else None,
            "option_id": picked.get("optionId") if picked else None,
            "outcome": outcome["outcome"],
        }
        with self.log_lock:
            self.permissions.write(json.dumps(record, ensure_ascii=False) + "\n")
            self.permissions.flush()
            self.transcript.write(f"\n> [权限] {json.dumps(detail, ensure_ascii=False)[:300]} → {outcome['outcome']}\n")
            self.transcript.flush()
        self._log("permission", record)
        self._send({"jsonrpc": "2.0", "id": msg["id"], "result": {"outcome": outcome}})

    def _handle_notification(self, msg):
        if msg.get("method") != "session/update":
            return
        upd = (msg.get("params") or {}).get("update") or {}
        kind = upd.get("sessionUpdate")
        if kind == "agent_message_chunk":
            self.counts["agent_chunks"] += 1
            content = upd.get("content") or {}
            if content.get("type") == "text":
                self.transcript.write(content.get("text") or "")
                self.transcript.flush()
        elif kind == "agent_thought_chunk":
            self.counts["thought_chunks"] += 1
        elif kind == "tool_call":
            self.counts["tool_calls"] += 1
            self.tool_calls[upd.get("toolCallId")] = upd
            title = (upd.get("title") or "")[:300]
            self.transcript.write(f"\n> [工具] {title} ({upd.get('kind')})\n")
            self.transcript.flush()
        elif kind == "usage_update":
            self.usage.append(upd)

    def close(self):
        try:
            if self.proc.stdin:
                self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self._kill()
        for handle in (self.events, self.permissions, self.transcript, self.stderr_handle):
            try:
                handle.close()
            except Exception:
                pass

    def _kill(self):
        if self.proc.poll() is not None:
            return
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(self.proc.pid), "/T", "/F"], capture_output=True)
        else:
            try:
                os.killpg(os.getpgid(self.proc.pid), 15)
            except (ProcessLookupError, PermissionError, OSError):
                self.proc.kill()
        try:
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.kill()


def set_and_confirm(client, session_id, config_id, value, timeout, known):
    if config_id not in known:
        raise RuntimeError(f"不支持配置项 {config_id}")
    result = client.request(
        "session/set_config_option",
        {"sessionId": session_id, "configId": config_id, "value": value},
        timeout=timeout,
    )
    current = config_values(result)
    if current.get(config_id) != value:
        raise RuntimeError(f"回读不一致 {config_id}: {current.get(config_id)!r}")
    known.update(current)
    return result


def parse_args(argv):
    parser = argparse.ArgumentParser(description="Drive one ACP agent over stdio. There is no default agent.")
    parser.add_argument("--agent", required=True, help="Executable to launch. No shell is used.")
    parser.add_argument("--agent-arg", action="append", default=[], help="One argument for the agent. Repeatable.")
    parser.add_argument("--cwd", required=True, help="Working directory for the agent session.")
    parser.add_argument("--log-dir", required=True, help="Directory for events, transcript, permissions, and result.")
    parser.add_argument("--prompt-file", help="UTF-8 prompt. Required unless --probe.")
    parser.add_argument("--model", help="Value for the model config option, set only when the agent advertises it.")
    parser.add_argument("--effort", help="Value for the effort config option, set only when the agent advertises it.")
    parser.add_argument("--model-config-id", default="model")
    parser.add_argument("--effort-config-id", default="reasoning_effort")
    parser.add_argument("--resume-session", help="Resume this ACP session id. Failure does not open a new session.")
    parser.add_argument("--permission-mode", choices=("deny", "approve"), default="deny")
    parser.add_argument("--probe", action="store_true", help="Initialize and open a session, then print the advertised config.")
    parser.add_argument("--rpc-timeout", type=float, default=120, help="Seconds for each handshake and config call.")
    parser.add_argument("--timeout-min", type=float, default=180, help="Minutes to wait for session/prompt.")
    return parser.parse_args(argv)


def main(argv=None):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    args = parse_args(sys.argv[1:] if argv is None else argv)
    if not args.probe and not args.prompt_file:
        print("缺少 --prompt-file。", file=sys.stderr)
        return 2
    if args.prompt_file and not os.path.isfile(args.prompt_file):
        print("找不到 prompt 文件。", file=sys.stderr)
        return 2
    if not os.path.isdir(args.cwd):
        print("cwd 不是已有目录。", file=sys.stderr)
        return 2
    if args.rpc_timeout <= 0 or args.timeout_min <= 0:
        print("超时必须大于 0。", file=sys.stderr)
        return 2
    client = Client([args.agent, *args.agent_arg], os.path.abspath(args.cwd), args.log_dir, args.permission_mode)
    started = time.time()
    result = {"started_at": utc_now(), "permission_mode": args.permission_mode}
    try:
        init = client.request("initialize", {
            "protocolVersion": 1,
            "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False}, "terminal": False},
            "clientInfo": {"name": "agent-lead-wake-acp", "version": "0.1.0"},
        }, timeout=args.rpc_timeout)
        result["agent_info"] = (init or {}).get("agentInfo")
        result["agent_capabilities"] = (init or {}).get("agentCapabilities")
        if args.resume_session:
            session = client.request("session/resume", {
                "sessionId": args.resume_session, "cwd": os.path.abspath(args.cwd), "mcpServers": [],
            }, timeout=args.rpc_timeout) or {}
            session.setdefault("sessionId", args.resume_session)
            result["resumed"] = args.resume_session
        else:
            session = client.request("session/new", {
                "cwd": os.path.abspath(args.cwd), "mcpServers": [],
            }, timeout=args.rpc_timeout) or {}
        session_id = session.get("sessionId")
        if not session_id:
            raise RuntimeError("会话结果里没有 sessionId")
        result["session_id"] = session_id
        known = config_values(session)
        result["advertised_config"] = known
        if args.probe:
            print(json.dumps({
                "agentInfo": result["agent_info"],
                "agentCapabilities": result["agent_capabilities"],
                "sessionId": session_id,
                "config": known,
            }, ensure_ascii=False, indent=1))
            return 0
        if args.model:
            set_and_confirm(client, session_id, args.model_config_id, args.model, args.rpc_timeout, known)
            result["model"] = args.model
        if args.effort:
            set_and_confirm(client, session_id, args.effort_config_id, args.effort, args.rpc_timeout, known)
            result["effort"] = args.effort
        result["config_current"] = known
        with open(args.prompt_file, encoding="utf-8") as handle:
            prompt = handle.read()
        response = client.request("session/prompt", {
            "sessionId": session_id,
            "prompt": [{"type": "text", "text": prompt}],
        }, timeout=args.timeout_min * 60)
        result["stop_reason"] = (response or {}).get("stopReason")
        result["prompt_response"] = response
    except Exception as exc:
        result["error"] = str(exc)[:2000]
    finally:
        result["elapsed_sec"] = round(time.time() - started, 3)
        result["counts"] = client.counts
        result["usage_updates_last"] = client.usage[-1] if client.usage else None
        result_path = os.path.join(args.log_dir, "result.json")
        with open(result_path, "w", encoding="utf-8") as handle:
            json.dump(result, handle, ensure_ascii=False, indent=1)
        if not args.probe or result.get("error"):
            print(json.dumps({
                key: result.get(key)
                for key in ("session_id", "stop_reason", "error", "elapsed_sec", "counts", "permission_mode")
            }, ensure_ascii=False))
        client.close()
    return 1 if result.get("error") else 0


if __name__ == "__main__":
    sys.exit(main())
