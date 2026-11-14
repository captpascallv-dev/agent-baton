import json
import os
from pathlib import Path
import sys

import pytest

from onestead import git_runtime as runtime
from onestead.store import StorageError


def test_override_precedes_frozen_and_path(tmp_path, monkeypatch):
    executable = tmp_path / "explicit-git"
    executable.touch()
    monkeypatch.setenv("ONESTEAD_GIT", str(executable))
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    assert runtime.resolve_git() == executable
    monkeypatch.setenv("ONESTEAD_GIT", "git")
    with pytest.raises(StorageError):
        runtime.resolve_git()


def test_manifest_precedes_path_and_frozen_fails_closed(tmp_path, monkeypatch):
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", str(tmp_path / "onestead"))
    monkeypatch.setattr(runtime.shutil, "which", lambda _: pytest.fail("frozen must not use PATH"))
    with pytest.raises(StorageError):
        runtime.resolve_git()
    git = tmp_path / "git" / "bin" / "git"
    git.parent.mkdir(parents=True)
    git.touch()
    manifest = tmp_path / "runtime.json"
    manifest.write_text(json.dumps({"entries": {"git": "git/bin/git"}}), encoding="utf-8")
    assert runtime.resolve_git() == git
    manifest.write_text(json.dumps({"entries": {"git": "../outside"}}), encoding="utf-8")
    with pytest.raises(StorageError):
        runtime.resolve_git()


def test_source_uses_path(monkeypatch, tmp_path):
    monkeypatch.setattr(sys, "frozen", False, raising=False)
    git = tmp_path / "git"
    monkeypatch.setattr(runtime.shutil, "which", lambda name: str(git) if name in ("git", "git.exe") else None)
    assert runtime.resolve_git() == git


def test_git_for_windows_inner_binary_uses_cmd_launcher(monkeypatch, tmp_path):
    monkeypatch.setattr(sys, "frozen", False, raising=False)
    monkeypatch.setattr(runtime.os, "name", "nt")
    inner = tmp_path / "Git" / "mingw64" / "bin" / "git.exe"
    inner.parent.mkdir(parents=True)
    inner.touch()
    monkeypatch.setattr(runtime.shutil, "which", lambda name: str(inner))
    assert runtime.resolve_git() == inner.resolve()  # 没有 cmd 包装就照用
    wrapper = tmp_path / "Git" / "cmd" / "git.exe"
    wrapper.parent.mkdir(parents=True)
    wrapper.touch()
    assert runtime.resolve_git() == wrapper
    monkeypatch.setenv("ONESTEAD_GIT", str(inner))
    assert runtime.resolve_git() == wrapper


def test_git_environment_clears_redirects_and_global_config(monkeypatch):
    for key in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_EXEC_PATH", "GIT_CONFIG_COUNT",
                "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_CONFIG_PARAMETERS", "GIT_OBJECT_DIRECTORY",
                "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_TEMPLATE_DIR", "GIT_TRACE"):
        monkeypatch.setenv(key, "untrusted")
    original = dict(os.environ)
    clean = runtime.git_environment({"GIT_AUTHOR_NAME": "Example", "GIT_DIR": "bad"})
    assert clean["GIT_AUTHOR_NAME"] == "Example"
    assert clean["GIT_CONFIG_NOSYSTEM"] == "1"
    assert clean["GIT_CONFIG_GLOBAL"] == os.devnull
    assert not any(v == "untrusted" for k, v in clean.items() if k.startswith("GIT_"))
    assert os.environ == original


def test_hostile_git_environment_cannot_redirect_real_write(tmp_path, monkeypatch):
    from onestead.bootstrap import bootstrap
    root = tmp_path / "data"
    monkeypatch.setenv("GIT_DIR", str(tmp_path / "elsewhere"))
    monkeypatch.setenv("GIT_INDEX_FILE", str(tmp_path / "wrong-index"))
    row = bootstrap(root, tmp_path / "config.toml", tmp_path / "state", "assistant")
    assert row["status"] == "created"
    assert (root / ".git" / "HEAD").is_file()
    assert not (tmp_path / "elsewhere").exists()
    assert not (tmp_path / "wrong-index").exists()
