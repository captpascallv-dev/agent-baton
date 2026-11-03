"""本机网页启动器：Windows 可用 pythonw（不弹黑窗口），POSIX 用 python -m onestead.web。

- 已经有一个在跑：直接用浏览器打开它，不起第二个；
- 没有：在 127.0.0.1 的空闲端口上起服务，生成本次的随机口令，打开浏览器；
- 页面每 20 秒发一次心跳，24 小时收不到就自己退出；
- 配置了 backup_dir 才备份：运行期间启动 5 分钟后备一次、之后每 6 小时一次，退出时再备一次。
"""

from __future__ import annotations

import json
import logging
import os
import secrets
import socket
import sys
import threading
import time
import urllib.request
import webbrowser
from pathlib import Path

from .. import config

# 浏览器会让后台标签页睡着、电脑会合盖过夜，那时页面发不出心跳；不能因此就退，一整天没动静才退出。
IDLE_EXIT = 24 * 3600  # 秒
# 网页可能连开好几天、也可能被强制结束，只靠退出时备份会断档；运行期间启动后先备一次，之后每隔这么久再备。
BACKUP_FIRST = 300  # 秒
BACKUP_EVERY = 6 * 3600  # 秒
_backup_lock = threading.Lock()
# 端口和钥匙跨重启沿用：后台重开过，旧标签页一刷新就能接上，地址也能收藏。钥匙满 30 天换一把。
UI_PORT = 8764
TOKEN_DAYS = 30
log = logging.getLogger("onestead.web")


def _alive(state: dict) -> bool:
    req = urllib.request.Request(f"http://127.0.0.1:{state['port']}/api/ping",
                                 headers={"Cookie": f"cb_session_{state['port']}={state['token']}"})
    try:
        with urllib.request.urlopen(req, timeout=2) as resp:
            return resp.status == 200
    except OSError:
        return False


def _read_state() -> dict | None:
    try:
        return json.loads((config.state_dir() / "ui-state.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _open(state: dict) -> None:
    opened = webbrowser.open(f"http://127.0.0.1:{state['port']}/open?t={state['token']}")
    if not opened and sys.platform != "win32":
        log.warning("没能自动打开浏览器；请在有桌面浏览器的本机会话中运行 python -m onestead.web；无桌面环境可用 CLI、MCP 或远程服务")


def _acquire_start_lock() -> bool:
    """连点两次时，只让一个进程去起服务；另一个等它起好再打开。"""
    start_lock = config.state_dir() / "ui-start.lock"
    deadline = time.monotonic() + 20
    while True:
        try:
            os.close(os.open(start_lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY))
            return True
        except FileExistsError:
            try:
                if time.time() - start_lock.stat().st_mtime > 60:
                    start_lock.unlink(missing_ok=True)  # 上次启动中途崩了留下的
                    continue
            except OSError:
                continue
            if time.monotonic() > deadline:
                return False
            time.sleep(0.3)


def _write_private(path: Path, text: str) -> None:
    """装着本机登录钥匙的文件只给本人读写：Windows 上 LOCALAPPDATA 默认只开给本人，别的系统靠 0600。"""
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        stream.write(text)
    if os.name != "nt":
        os.chmod(path, 0o600)  # 文件早就存在时，os.open 给的权限不生效


def _session_token() -> str:
    path = config.state_dir() / "ui-session.json"
    try:
        saved = json.loads(path.read_text(encoding="utf-8"))
        token = saved["token"]
        if isinstance(token, str) and len(token) >= 32 and time.time() - float(saved["created"]) < TOKEN_DAYS * 86400:
            return token
    except (OSError, ValueError, KeyError, TypeError):
        pass
    token = secrets.token_urlsafe(32)
    _write_private(path, json.dumps({"token": token, "created": time.time()}))
    return token


def _bind() -> socket.socket:
    for port in (UI_PORT, 0):
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):  # Windows：别的程序不能抢绑同一个端口，接走浏览器带的 cookie
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        try:
            sock.bind(("127.0.0.1", port))
            return sock
        except OSError:
            sock.close()
            if port:
                log.info("端口 %s 被占用，这次换一个随机端口", port)
    raise OSError("本机没有可用端口")


def _serve(data_root: str) -> None:
    import uvicorn

    from .app import build_app

    sock = _bind()
    port = sock.getsockname()[1]
    token = _session_token()
    app = build_app(data_root, port, token)
    server = uvicorn.Server(uvicorn.Config(app, log_config=None, access_log=False, lifespan="off"))

    def watchdog():
        while not server.should_exit:
            time.sleep(5)
            if time.monotonic() - app.state.last_beat > IDLE_EXIT:
                log.info("超过 %s 秒没有心跳，退出", IDLE_EXIT)
                server.should_exit = True

    def scheduled_backup():
        wait = BACKUP_FIRST
        while not server.should_exit:
            deadline = time.monotonic() + wait
            while not server.should_exit and time.monotonic() < deadline:
                time.sleep(5)
            if server.should_exit:
                return
            _backup(data_root)  # 提交没变时 rotate 自己跳过
            wait = BACKUP_EVERY

    state ={"pid": os.getpid(), "port": port, "token": token,
             "started_at": time.strftime("%Y-%m-%dT%H:%M:%S")}
    _write_private(config.state_dir() / "ui-state.json", json.dumps(state))
    (config.state_dir() / "ui-start.lock").unlink(missing_ok=True)
    threading.Thread(target=watchdog, daemon=True).start()
    threading.Thread(target=scheduled_backup, daemon=True).start()
    threading.Timer(0.8, _open, args=(state,)).start()
    log.info("启动：端口 %s，数据仓库 %s", port, data_root)
    try:
        server.run(sockets=[sock])
    finally:
        if (_read_state() or {}).get("pid") == os.getpid():
            (config.state_dir() / "ui-state.json").unlink(missing_ok=True)
        _backup(data_root)


def _backup(data_root: str) -> None:
    with _backup_lock:  # 定时备份和退出备份不同时跑
        _backup_once(data_root)


def _backup_once(data_root: str) -> None:
    try:
        if config.backup_dir() is None:
            log.info("没配 backup_dir，不备份")
            return
        if os.path.normcase(os.path.abspath(data_root)) != os.path.normcase(os.path.abspath(config.data_dir())):
            log.info("这次用的不是正式数据仓库，不备份")
            return
        from ..backup import rotate

        log.info("备份：%s", json.dumps(rotate(), ensure_ascii=False))
    except BaseException as e:  # rotate 用 SystemExit 报可预期的失败
        log.error("备份没做成：%s", e)


def main() -> None:
    data_root = config.data_dir()
    config.owner_key()
    state_dir = config.state_dir()
    start_lock = state_dir / "ui-start.lock"
    state_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
    logging.basicConfig(filename=state_dir / "ui.log", encoding="utf-8", level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    state = _read_state()
    if state and _alive(state):
        _open(state)
        return
    if not _acquire_start_lock():
        log.error("等不到启动锁：%s", start_lock)
        return
    state = _read_state()  # 等锁期间可能另一个进程已经起好了
    if state and _alive(state):
        start_lock.unlink(missing_ok=True)
        _open(state)
        return
    try:
        _serve(data_root)
    except Exception:
        log.exception("界面出错退出")
        start_lock.unlink(missing_ok=True)
        sys.exit(1)
