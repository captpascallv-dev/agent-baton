"""Onestead 的本机备份轮转：配置 backup_dir 后启用，保留两份完整版本。

什么时候跑：手动运行 onestead-backup，或配置 backup_dir 后由网页启动器在退出时调用。不做定时任务。
做法：
  - 新槽位放数据仓库和代码仓库各一份：源码 ZIP + 当前分支完整历史 bundle；
  - 先在临时位置做好，bundle 校验、独立克隆还原、ZIP 文件数核对都通过后才轮转；
  - 原 01-current 顺延为 02-previous；原 02-previous 移到 _待删除（不删除）；
  - 更新备份根目录的 INDEX.json。
备份里有限定内容：只能放本机，不能放进会同步到云端的目录。
"""

from __future__ import annotations

import hashlib
import json
import logging
import shutil
import subprocess
import sys
import tempfile
import zipfile
from datetime import datetime
from pathlib import Path

from .gitops import NO_WINDOW
from . import config
from .store import TAIPEI



def _find_checkout(start: Path) -> Path | None:
    """从包所在目录往上找源码 checkout：同时有 .git 和 pyproject.toml 才算。"""
    for folder in (start, *start.parents):
        if (folder / ".git").exists() and (folder / "pyproject.toml").is_file():
            return folder
    return None


CODE_REPO = _find_checkout(Path(__file__).resolve().parent)
PRODUCT = "Company Base"
log = logging.getLogger("onestead.backup")


def _git(*args, cwd: Path) -> str:
    return subprocess.run(["git", "-C", str(cwd), *args], check=True, capture_output=True,
                          text=True, encoding="utf-8", creationflags=NO_WINDOW).stdout.strip()


def _sha256(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _slot(project: Path, prefix: str) -> Path | None:
    found = sorted(project.glob(prefix + "*"))
    if len(found) > 1:
        raise SystemExit(f"{prefix} 槽位不止一个，先人工处理：{found}")
    return found[0] if found else None


def _pack(repo: Path, label: str, staging: Path, verify_dir: Path) -> dict:
    """给一个仓库打源码 ZIP 和完整历史 bundle，并独立克隆核对。"""
    if _git("status", "--porcelain", cwd=repo):
        raise SystemExit(f"{label} 有未提交的改动，先提交再备份：{repo}")
    branch = _git("rev-parse", "--abbrev-ref", "HEAD", cwd=repo)
    sha = _git("rev-parse", "HEAD", cwd=repo)
    short = sha[:7]
    zipf = staging / f"{label}-{short}-source.zip"
    bundle = staging / f"{label}-{short}-{branch}-history.bundle"
    _git("archive", "--format=zip", f"--prefix={label}-{short}/", "-o", str(zipf), sha, cwd=repo)
    _git("bundle", "create", str(bundle), f"refs/heads/{branch}", cwd=repo)
    _git("bundle", "verify", str(bundle), cwd=repo)
    clone = verify_dir / f"clone-{label}"
    subprocess.run(["git", "clone", "--quiet", "--no-checkout", str(bundle), str(clone)], check=True,
                   creationflags=NO_WINDOW)
    if _git("rev-parse", f"refs/remotes/origin/{branch}", cwd=clone) != sha:
        raise SystemExit(f"{label} 克隆出来的 {branch} 不是这个提交")
    tracked = len(_git("ls-tree", "-r", "--name-only", sha, cwd=clone).splitlines())
    with zipfile.ZipFile(zipf) as z:
        if z.testzip() is not None:
            raise SystemExit(f"{label} 的 ZIP 损坏")
        nfiles = len([n for n in z.namelist() if not n.endswith("/")])
    if nfiles != tracked:
        raise SystemExit(f"{label} 的 ZIP 文件数 {nfiles} 和提交文件数 {tracked} 不一致")
    return {
        "repo": str(repo), "branch": branch, "commit": sha, "short": short,
        "commit_subject": _git("log", "-1", "--format=%s", sha, cwd=repo),
        "commit_date": _git("log", "-1", "--format=%cI", sha, cwd=repo), "tracked_files": tracked,
        "files": [
            {"name": zipf.name, "kind": "source_zip", "bytes": zipf.stat().st_size, "sha256": _sha256(zipf)},
            {"name": bundle.name, "kind": "history_bundle", "bytes": bundle.stat().st_size,
             "sha256": _sha256(bundle), "note": f"只含 {branch}，完整历史"},
        ],
    }


def _to_recycle_bin(path: Path) -> None:
    """只回收本次创建的临时目录；回收失败就保留原处并记录，绝不永久删除。"""
    path = path.resolve()
    try:
        if sys.platform == "win32":
            literal = str(path).replace("'", "''")
            script = ("Add-Type -AssemblyName Microsoft.VisualBasic; "
                      "[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory("
                      f"'{literal}', 'OnlyErrorDialogs', 'SendToRecycleBin')")
            result = subprocess.run(["powershell", "-NoProfile", "-Command", script], check=False,
                                    capture_output=True, creationflags=NO_WINDOW)
            if result.returncode == 0:
                return
        else:
            from send2trash import send2trash

            send2trash(str(path))
            return
    except (ImportError, OSError, subprocess.SubprocessError):
        pass
    log.warning("临时目录没能移进回收站，保留在：%s", path)


def _code_repo() -> tuple[Path | None, str | None]:
    """返回 (代码仓库, 没备份的原因)。配置了 source_repo 就只用它，不合格直接报错。"""
    configured = config.source_repo()
    if configured is not None:
        repo = configured.resolve()
        if not (repo / ".git").exists() or not (repo / "pyproject.toml").is_file():
            raise SystemExit(f"source_repo 不是源码 git 仓库（要有 .git 和 pyproject.toml）：{repo}")
        return repo, None
    if CODE_REPO is None:
        return None, "程序不在源码 git 仓库里运行（例如普通 pip install . 安装），往上没找到同时有 .git 和 pyproject.toml 的目录"
    return CODE_REPO, None


def rotate() -> dict:
    root = config.backup_dir()
    if root is None:
        return {"skipped": "没配 backup_dir，不备份"}
    root = root.resolve()  # git -C 会换工作目录，先固定配置里可能给出的相对路径
    project, quarantine = root / "Company-Base", root / "_待删除"
    data_repo = Path(config.data_dir())
    now = datetime.now(TAIPEI)
    data_sha = _git("rev-parse", "HEAD", cwd=data_repo)[:7]
    code_repo, code_skipped = _code_repo()
    code_sha = _git("rev-parse", "HEAD", cwd=code_repo)[:7] if code_repo else "none"
    cur, prev = _slot(project, "01-current-complete-"), _slot(project, "02-previous-complete-")
    tag = f"data{data_sha}-code{code_sha}"
    if cur and tag in cur.name:
        return {"skipped": f"当前槽位已经是 {tag}，不用再备份"}
    project.mkdir(parents=True, exist_ok=True)
    staging = project / f"_staging-{tag}"
    if staging.exists():
        raise SystemExit(f"上次的临时目录还在，先人工处理：{staging}")
    staging.mkdir()
    verify_dir = Path(tempfile.mkdtemp(prefix="company-base-backup-verify-"))
    try:
        data = _pack(data_repo, "company-base-data", staging, verify_dir)
        code = _pack(code_repo, "company-base", staging, verify_dir) if code_repo else None
    except BaseException:
        _to_recycle_bin(staging)  # 本次刚建、还没成型的临时槽位，移进回收站
        raise
    finally:
        _to_recycle_bin(verify_dir)
    manifest = {
        "schema": "company-base.local-code-backup.slot.v1", "product": PRODUCT, "slot": "01-current-complete",
        "created_at": now.isoformat(timespec="seconds"),
        "sensitivity": "LOCAL_ONLY：数据仓库含限定内容，只能放本机，不得放进会同步到云端的目录",
        "repos": {"data": data, "code": code},
        **({"code_not_backed_up": {"reason": code_skipped,
                                   "hint": "这次没有备份代码仓库；要备份可配置 source_repo 或 ONESTEAD_SOURCE_REPO"}}
           if code is None else {}),
        "verification": {"bundle_verify": "pass", "independent_clone": "pass（--no-checkout，分支指向该提交）",
                         "zip_testzip": "pass", "zip_files_equal_tracked_files": "pass"},
    }
    (staging / "MANIFEST.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    (staging / "RESTORE.md").write_text(
        "# 恢复 Onestead\n\n"
        f"- 数据仓库：提交 `{data['commit']}`（{data['commit_subject']}）\n"
        + (f"- 代码仓库：提交 `{code['commit']}`（{code['commit_subject']}）\n" if code else
           f"- 代码仓库：这次没有备份代码仓库，原因：{code_skipped}。要备份可配置 `source_repo`"
           "（或环境变量 `ONESTEAD_SOURCE_REPO`）指向源码 git 仓库。\n") +
        "- 只要文件：解压对应的 `*-source.zip`。\n"
        "- 要完整历史：`git clone \"<本目录>/<对应的 .bundle>\" <新目录>`。\n"
        "- 恢复前先核对 `MANIFEST.json` 里各文件的 sha256。\n"
        "- 代码仓库恢复后，在源码目录用同一个 Python 环境重新运行 `python -m pip install -e .` 装依赖；备份不含虚拟环境。\n", encoding="utf-8")

    moved = {}
    if prev:
        quarantine.mkdir(exist_ok=True)
        dest = quarantine / f"Company-Base-retired-{prev.name.replace('02-previous-complete-', 'complete-')}-retired-{now:%Y%m%d%H%M}"
        shutil.move(str(prev), str(dest))
        moved["retired"] = str(dest)
    if cur:
        new_prev = project / cur.name.replace("01-current-complete-", "02-previous-complete-")
        shutil.move(str(cur), str(new_prev))
        m = json.loads((new_prev / "MANIFEST.json").read_text(encoding="utf-8"))
        m["slot"] = "02-previous-complete"
        (new_prev / "MANIFEST.json").write_text(json.dumps(m, ensure_ascii=False, indent=2), encoding="utf-8")
        moved["previous"] = new_prev.name
    final = project / f"01-current-complete-{tag}-{now:%Y%m%d}"
    shutil.move(str(staging), str(final))
    moved["current"] = final.name

    idx_path = root / "INDEX.json"
    idx = json.loads(idx_path.read_text(encoding="utf-8")) if idx_path.exists() else {"projects": []}
    entry = next((p for p in idx["projects"] if p.get("name") == PRODUCT), None)
    if entry is None:
        entry = {"name": PRODUCT, "directory": project.name}
        idx["projects"].append(entry)
    entry.update({"status": "TWO_COMPLETE_VERSIONS_VERIFIED" if moved.get("previous") else "CURRENT_VERIFIED_NO_PREVIOUS_YET",
                  "current": moved["current"], "previous": moved.get("previous"),
                  "sensitivity": "LOCAL_ONLY", "updated_at": now.isoformat(timespec="seconds")})
    idx_path.write_text(json.dumps(idx, ensure_ascii=False, indent=2), encoding="utf-8")
    result = {"data": data["short"], "code": code["short"] if code else None, **moved}
    if code is None:
        result["code_not_backed_up"] = code_skipped
    return result


def main() -> None:
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except (AttributeError, ValueError):
        pass
    print(json.dumps(rotate(), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
