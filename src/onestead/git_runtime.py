"""所有 Git 子进程的程序选择及环境边界；不修改主进程 PATH 或用户配置。"""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading

from .i18n import text as tr
from .store import StorageError

NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
_dll_lock = threading.Lock()
_IDENTITY = {"GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL"}


def resolve_git() -> Path:
    explicit = os.environ.get("ONESTEAD_GIT")
    if explicit is not None:
        path = Path(explicit).expanduser()
        if not path.is_absolute() or not path.is_file():
            raise StorageError(tr("bundle.git_override"))
        return _launcher(path.resolve())
    if getattr(sys, "frozen", False):
        root = Path(sys.executable).resolve().parent
        try:
            manifest = json.loads((root / "runtime.json").read_text(encoding="utf-8"))
            rel = manifest["entries"]["git"]
            path = (root / rel).resolve()
            if Path(rel).is_absolute() or not path.is_relative_to(root) or not path.is_file():
                raise ValueError("git entry")
            return path
        except (OSError, ValueError, KeyError, TypeError):
            raise StorageError(tr("bundle.git_missing")) from None
    found = shutil.which("git.exe" if os.name == "nt" else "git")
    if not found:
        raise StorageError(tr("bundle.git_missing"))
    return _launcher(Path(found).resolve())


def _launcher(path: Path) -> Path:
    """Git for Windows 的 mingw64/bin/git.exe 在只有 System32 的 PATH 下找不到自己的辅助程序（本地 clone 会崩）；
    同一安装里有 cmd/git.exe 就用它，它自己设好辅助程序的路径。Git Bash 里启动的进程最先找到的就是前者。"""
    if os.name == "nt" and path.parent.name.lower() == "bin" and path.parent.parent.name.lower().startswith(("mingw", "clang")):
        wrapper = path.parents[2] / "cmd" / "git.exe"
        if wrapper.is_file():
            return wrapper
    return path


def git_environment(identity: dict | None = None) -> dict:
    clean = {k: v for k, v in os.environ.items() if not k.upper().startswith("GIT_")}
    if identity:
        clean.update({k: v for k, v in identity.items() if k in _IDENTITY})
    clean.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_SYSTEM=os.devnull,
                 GIT_CONFIG_GLOBAL=os.devnull, GIT_ATTR_NOSYSTEM="1",
                 GIT_TERMINAL_PROMPT="0", GIT_OPTIONAL_LOCKS="0")
    if os.name == "nt":
        # Windows DLL 搜索不继承下载目录或其他应用的 PATH；Git 自己定位其 helpers。
        clean["PATH"] = str(Path(os.environ["SystemRoot"]) / "System32")
        clean["NoDefaultCurrentDirectoryInExePath"] = "1"
    elif getattr(sys, "frozen", False):
        # PyInstaller 给自身原生依赖加的搜索路径不传给外部 Git。
        for key in ("LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH"):
            original = clean.pop(key + "_ORIG", None)
            clean.pop(key, None)
            if original:
                clean[key] = original
    return clean


def run_git(args, *, root=None, env=None, **kwargs):
    executable = resolve_git()
    command = [str(executable), "-c", "core.hooksPath=" + os.devnull,
               "-c", "commit.gpgsign=false", "-c", "core.fsmonitor=false",
               "-c", "core.attributesFile=" + os.devnull]
    if root is not None:
        command += ["-C", str(root)]
    command += list(args)
    kwargs.setdefault("creationflags", NO_WINDOW)
    kwargs.setdefault("cwd", str(executable.parent))
    child_env = git_environment(env)
    # PyInstaller 在 Windows 设置进程级 DLL 目录。创建子进程时清掉，随后还原。
    if os.name == "nt" and getattr(sys, "frozen", False):
        import ctypes

        with _dll_lock:
            buffer = ctypes.create_unicode_buffer(32768)
            ctypes.windll.kernel32.GetDllDirectoryW(len(buffer), buffer)
            ctypes.windll.kernel32.SetDllDirectoryW(None)
            try:
                return subprocess.run(command, env=child_env, **kwargs)
            finally:
                ctypes.windll.kernel32.SetDllDirectoryW(buffer.value or None)
    return subprocess.run(command, env=child_env, **kwargs)
