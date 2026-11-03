"""受控写入：加锁 → 写文件 → 以写入者身份提交一次 git；任何一步失败都把文件恢复原样。"""

from __future__ import annotations

from .i18n import text as tr

import io
import os
import subprocess
import time
from contextlib import contextmanager
from pathlib import Path

from .store import Busy, StorageError

LOCK_NAME = "company-base-write.lock"
LOCK_WAIT_SECONDS = 30
LOCK_STALE_SECONDS = 120  # 锁文件超过这么久还在，视为上次写入崩溃留下的
# 网页用 pythonw 跑、没有控制台，不加这个每调一次 git 都会闪一个黑窗口
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


@contextmanager
def write_lock(root: Path):
    """多个 agent 可能同时写，用 .git 里的锁文件排队。"""
    with file_lock(root / ".git" / LOCK_NAME):
        yield


@contextmanager
def file_lock(lock: Path):
    """用一个锁文件排队（跨进程）。锁文件放在要保护的东西旁边。"""
    deadline = time.monotonic() + LOCK_WAIT_SECONDS
    while True:
        try:
            fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            break
        except FileExistsError:
            try:
                if time.time() - lock.stat().st_mtime > LOCK_STALE_SECONDS:
                    lock.unlink(missing_ok=True)
                    continue
            except FileNotFoundError:
                continue
            if time.monotonic() > deadline:
                raise Busy(tr("agent.257")) from None
            time.sleep(0.2)
    try:
        os.write(fd, str(os.getpid()).encode())
        os.close(fd)
        yield
    finally:
        lock.unlink(missing_ok=True)


def _git(root: Path, args: list[str], env: dict | None = None, stdin: str | None = None) -> str:
    proc = subprocess.run(
        ["git", "-C", str(root), *args],
        input=stdin.encode("utf-8") if stdin is not None else None,
        capture_output=True,
        env=env,
        creationflags=NO_WINDOW,
    )
    if proc.returncode != 0:
        err = proc.stderr.decode("utf-8", "replace").strip()
        raise StorageError(tr("agent.256", v0=args[0], v1=err[:300]))
    return proc.stdout.decode("utf-8", "replace").strip()


def author_name(agent_key: str, signed_as: str | None) -> str:
    return f"{agent_key} · {signed_as}" if signed_as else agent_key


def write_and_commit(root: Path, files: dict[str, str | None], subject: str, tool: str,
                     agent_key: str, signed_as: str | None) -> str:
    """把 files（相对路径 → 完整新内容；None 表示删掉这个文件）写进仓库并提交，返回短提交号。
    调用方需已持有 write_lock。"""
    old: dict[str, str | None] = {}
    for rel in files:
        path = root / rel
        old[rel] = io.open(path, encoding="utf-8").read() if path.exists() else None
    name = author_name(agent_key, signed_as)
    email = f"{agent_key}@agents.company-base.local"
    env = dict(os.environ, GIT_AUTHOR_NAME=name, GIT_AUTHOR_EMAIL=email,
               GIT_COMMITTER_NAME=name, GIT_COMMITTER_EMAIL=email)
    message = f"{subject}\n\n由 {name} 通过Onestead写入（{tool}）。\n\nAgent: {agent_key}\n"
    if signed_as:
        message += f"Signed-as: {signed_as}\n"
    paths = list(files)
    try:
        for rel, content in files.items():
            path = root / rel
            if content is None:
                path.unlink(missing_ok=True)
                continue
            path.parent.mkdir(parents=True, exist_ok=True)
            with io.open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(content)
        _git(root, ["add", "-A", "--", *paths])
        _git(root, ["commit", "-q", "-F", "-", "--", *paths], env=env, stdin=message)
        return _git(root, ["rev-parse", "--short", "HEAD"])
    except Exception:
        # 恢复原样：改过的写回旧内容，新建的删掉，并取消暂存
        for rel, content in old.items():
            path = root / rel
            if content is None:
                path.unlink(missing_ok=True)
            else:
                with io.open(path, "w", encoding="utf-8", newline="\n") as f:
                    f.write(content)
        subprocess.run(["git", "-C", str(root), "reset", "-q", "--", *paths], capture_output=True,
                       creationflags=NO_WINDOW)
        raise
