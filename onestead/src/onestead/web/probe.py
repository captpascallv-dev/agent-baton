"""应用广场的探活：网址能连上就算在线，进程在跑就算在跑。只读，不改任何东西。

只探登记在底座里的网址和进程名。本机网址（127.0.0.1、localhost）探得快；
外网网址只发一个 HEAD 请求，不读内容。探不了的（命令行工具、只登记了路径的）就说“没法探”。
"""

from __future__ import annotations

import csv
import io
import os
import subprocess
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from urllib.parse import urlparse

LOCAL_HOSTS = {"127.0.0.1", "localhost", "::1"}


def _url(url: str) -> str:
    host = (urlparse(url).hostname or "").lower()
    timeout = 1.5 if host in LOCAL_HOSTS else 4
    req = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "company-base-probe"})
    try:
        with urllib.request.urlopen(req, timeout=timeout):
            return "online"
    except urllib.error.HTTPError:
        return "online"  # 有回应（哪怕 401、404）说明服务在
    except (urllib.error.URLError, OSError, ValueError):
        return "offline"


def _processes() -> set[str] | None:
    if os.name != "nt":
        try:
            out = subprocess.run(["ps", "-eo", "comm="], capture_output=True, text=True, timeout=8, check=True)
        except (OSError, subprocess.SubprocessError):
            return None  # 无法枚举进程，不能把“没法探”误报成“已停止”
        return {line.strip().rsplit("/", 1)[-1].lower() for line in out.stdout.splitlines() if line.strip()}
    try:
        out = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, timeout=8,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except (OSError, subprocess.SubprocessError):
        return set()
    text = out.stdout.decode("utf-8", "replace") if out.stdout else ""
    names = set()
    for row in csv.reader(io.StringIO(text)):
        if row:
            names.add(row[0].lower())
            names.add(row[0].lower().removesuffix(".exe"))
    return names


def probe(apps: list[dict]) -> dict[str, dict]:
    """apps：每个 {id, url, process}。返回 id → {state: online/offline/running/stopped/unknown, how}。"""
    procs = _processes() if any(a.get("process") for a in apps) else set()
    out: dict[str, dict] = {}
    with ThreadPoolExecutor(max_workers=8) as pool:
        futures = {a["id"]: pool.submit(_url, a["url"]) for a in apps if a.get("url")}
        for a in apps:
            if a["id"] in futures:
                out[a["id"]] = {"state": futures[a["id"]].result(), "how": "网址"}
            elif a.get("process") and procs is None:
                out[a["id"]] = {"state": "unknown", "how": "没法探：无法读取本机进程列表"}
            elif a.get("process"):
                name = a["process"].lower()
                running = name in procs or name.removesuffix(".exe") in procs
                out[a["id"]] = {"state": "running" if running else "stopped", "how": "进程"}
            else:
                out[a["id"]] = {"state": "unknown", "how": None}
    return out
