"""对外服务：在这台电脑上同时开远程接口和 Cloudflare 隧道，外面的人经 base.example.com 进来。

- Windows 可由计划任务在登录时用 pythonw 拉起，不弹窗口；各平台都可手动 `python -m onestead.online`。
  macOS / Linux 的后台拉起建议见 docs/deploy.md（not verified）。
- 远程接口和同事网页只监听 127.0.0.1:8765；cloudflared 从这台电脑往外连到 Cloudflare，不开任何入站端口。
  HTTPS 和域名都在 Cloudflare 那头，隧道的配置（base.example.com → http://127.0.0.1:8765）放在 Cloudflare 上。
- 隧道密钥在状态目录的 cloudflare-tunnel-token.txt，cloudflared 自己读（--token-file），这里不读也不记。
- cloudflared 退出了就隔一会儿重开；连不上超过一分钟就换协议（QUIC / http2）重开；远程接口退出，整个进程跟着退出（隧道也就断了，外面看到的是 Cloudflare 的错误页）。
- 日志：状态目录的 online.log（本程序）、cloudflared.log（隧道）；Windows 新装默认 %LOCALAPPDATA%\\Onestead，已有安装继续用旧状态目录。
"""

from __future__ import annotations

import logging
import os
import shutil
import socket
import subprocess
import sys
import threading
import time
import urllib.request
from pathlib import Path

from . import config
from .gitops import NO_WINDOW

PORT = 8765
METRICS = "127.0.0.1:20241"  # cloudflared 的状态口，只在本机
PROTOCOLS = ("quic", "http2")
RESTART_SECONDS = 15
CHECK_SECONDS = 10
SWITCH_SECONDS = 60
log = logging.getLogger("onestead.online")


def _cloudflared() -> str | None:
    """显式路径优先，再找 PATH；只有 Windows 再找 winget 的安装位置。"""
    found = config.env("CLOUDFLARED") or shutil.which("cloudflared")
    if found:
        return found
    if sys.platform != "win32":
        return None
    pkgs = Path(os.environ.get("LOCALAPPDATA") or "") / "Microsoft" / "WinGet" / "Packages"
    hits = sorted(pkgs.glob("Cloudflare.cloudflared_*/cloudflared.exe"))
    return str(hits[-1]) if hits else None


def _port_free(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        try:
            s.bind(("127.0.0.1", port))
            return True
        except OSError:
            return False


def _stop_stale_tunnels() -> None:
    """上次监督进程被直接结束时，cloudflared 可能不会跟着退，开新的之前先把它关掉。
    只认 cloudflared 且命令行里带 cloudflare-tunnel-token.txt 的，别的进程不碰。"""
    if sys.platform != "win32":
        import signal

        try:
            result = subprocess.run(["ps", "-eo", "pid,args"], capture_output=True, text=True,
                                    timeout=30, check=True)
        except (OSError, subprocess.SubprocessError):
            log.warning("无法用 ps 查找残留隧道进程，跳过清理")
            return
        stopped = []
        for line in result.stdout.splitlines():
            parts = line.strip().split(None, 1)
            if len(parts) != 2 or not parts[0].isdigit():
                continue
            pid, command = int(parts[0]), parts[1]
            executable = command.split(None, 1)[0].rsplit("/", 1)[-1]
            if pid <= 0 or pid == os.getpid() or executable != "cloudflared" or "cloudflare-tunnel-token.txt" not in command:
                continue
            try:
                os.kill(pid, signal.SIGTERM)
                stopped.append(str(pid))
            except ProcessLookupError:
                pass  # ps 返回后进程可能已经自行退出
            except OSError:
                log.warning("无法终止残留隧道进程 %s，跳过", pid)
        if stopped:
            log.info("已向上次留下的隧道进程发送 SIGTERM：%s", "、".join(stopped))
        return
    script = ("Get-CimInstance Win32_Process -Filter \"Name='cloudflared.exe'\" | "
              "Where-Object { $_.CommandLine -like '*cloudflare-tunnel-token.txt*' } | "
              "ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $_.ProcessId }")
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True,
                             timeout=30, creationflags=NO_WINDOW).stdout.split()
    except (OSError, subprocess.SubprocessError):
        return
    if out:
        log.info("关掉了上次留下的隧道进程：%s", "、".join(out))


def _tunnel_ready() -> bool:
    """cloudflared 自己的状态口：至少连上一条到 Cloudflare 的连接时 /ready 回 200。
    不走系统代理（这台电脑开着 Clash），本机地址直连。"""
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(f"http://{METRICS}/ready", timeout=3) as resp:
            return resp.status == 200
    except OSError:
        return False


def _last_protocol() -> str:
    try:
        saved = (config.state_dir() / "tunnel-protocol.txt").read_text(encoding="utf-8").strip()
    except OSError:
        saved = ""
    return saved if saved in PROTOCOLS else PROTOCOLS[0]


def _run_tunnel(exe: str, stop: threading.Event, current: list) -> None:
    # 这台电脑开着 Clash，出去的线路会变：有时 UDP（QUIC）过不去，有时 TCP 7844（http2）过不去。
    # 所以协议不写死：先用上次连上的那种，连不上超过 SWITCH_SECONDS 就换另一种重开。
    protocol = _last_protocol()
    while not stop.is_set():
        args = [exe, "tunnel", "--no-autoupdate", "--protocol", protocol, "--metrics", METRICS,
                "--loglevel", "info", "--logfile", str(config.state_dir() / "cloudflared.log"),
                "run", "--token-file", str(config.state_dir() / "cloudflare-tunnel-token.txt")]
        log.info("开隧道（%s）", protocol)
        proc = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                creationflags=NO_WINDOW)
        current[:] = [proc]
        down_since, was_ready = time.monotonic(), False
        while proc.poll() is None and not stop.wait(CHECK_SECONDS):
            if _tunnel_ready():
                if not was_ready:
                    log.info("隧道连上了（%s）", protocol)
                    (config.state_dir() / "tunnel-protocol.txt").write_text(protocol, encoding="utf-8")
                down_since, was_ready = None, True
                continue
            if was_ready:
                log.warning("隧道断了，等它自己重连")
            was_ready = False
            down_since = down_since or time.monotonic()
            if time.monotonic() - down_since > SWITCH_SECONDS:
                other = PROTOCOLS[1 - PROTOCOLS.index(protocol)]
                log.warning("用 %s 连不上超过 %s 秒，换成 %s", protocol, SWITCH_SECONDS, other)
                protocol = other
                proc.terminate()
                proc.wait()
                break
        else:
            if stop.is_set():
                break
            log.warning("隧道退出了（退出码 %s），%s 秒后重开", proc.returncode, RESTART_SECONDS)
            stop.wait(RESTART_SECONDS)


def main() -> None:
    root = config.data_dir()
    public_host = config.public_host(required=True)
    config.owner_key()
    state_dir = config.state_dir()
    tunnel_token = state_dir / "cloudflare-tunnel-token.txt"
    state_dir.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(filename=state_dir / "online.log", encoding="utf-8", level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    import uvicorn

    from .server import remote_app
    if not _port_free(PORT):
        log.info("端口 %s 已经有程序在用（多半是已经在跑了），这次不起", PORT)
        return
    exe = _cloudflared()
    if not exe or not tunnel_token.exists():
        log.error("缺 cloudflared 或隧道密钥文件，没法对外，只开本机的远程接口")
    stop, current = threading.Event(), []
    if exe and tunnel_token.exists():
        _stop_stale_tunnels()
        threading.Thread(target=_run_tunnel, args=(exe, stop, current), daemon=True).start()
    log.info("启动：远程接口 127.0.0.1:%s，对外域名 %s，数据仓库 %s", PORT, public_host, root)
    try:
        uvicorn.run(remote_app(root, PORT, public_host=public_host), host="127.0.0.1", port=PORT,
                    log_config=None, access_log=False, proxy_headers=False)
    except Exception:
        log.exception("远程接口出错退出")
        sys.exit(1)
    finally:
        stop.set()
        for proc in current:  # 远程接口不在了，隧道也关掉，免得外面连进来只看到 502
            proc.terminate()
        log.info("退出")


if __name__ == "__main__":
    main()
