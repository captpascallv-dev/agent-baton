"""“动态”页的数据：读数据仓库的 git 历史，翻成人话；撤回一次改动（新增反向提交，不改历史）。"""

from __future__ import annotations

from ..i18n import text as tr

from .. import config
from ..store import owner_name_at

import os
import re
import subprocess
from pathlib import Path

from ..gitops import NO_WINDOW
from ..redact import documents, safe_text
from ..store import BaseError, Doc, Viewer, member_keys
from . import views
from .labels import author_view, file_label
from .names import actor

REVERTS_RE = re.compile(r"(?:Reverts:|This reverts commit)\s*([0-9a-f]{7,40})")
SHA_RE = re.compile(r"^[0-9a-f]{7,40}$")


def _git(root: Path, *args: str, env: dict | None = None, check: bool = True) -> subprocess.CompletedProcess:
    proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, env=env, creationflags=NO_WINDOW)
    if check and proc.returncode != 0:
        raise BaseError(tr("agent.256", v0=args[0], v1=proc.stderr.decode('utf-8', 'replace').strip()[:300]))
    return proc


def _out(proc: subprocess.CompletedProcess) -> str:
    return proc.stdout.decode("utf-8", "replace")


# ---------- 把路径和作者翻成人话（实现在 labels.py，这里留着原来的名字） ----------

def friendly_subject(subject: str, by_id: dict[str, Doc]) -> str:
    if subject.startswith("撤回："):
        return "撤回：" + friendly_subject(subject[3:], by_id)
    m = re.match(r"^(进展|交接|接任申请)：([a-z0-9-]+)(.*)$", subject)
    if m:
        kind, pid, rest = m.groups()
        name = by_id[pid].title if pid in by_id else pid
        rest = rest.lstrip(" —").lstrip("的")
        return f"{name} · {kind}：{rest}" if rest else f"{name} · {kind}"
    return subject


# ---------- 读历史 ----------

def _nul_groups(blob: str, width: int) -> list[list[str]]:
    """按固定个数切开 NUL 分隔的字段。提交说明里不能出现 NUL，所以内容拆不开边界。"""
    if blob.endswith("\0"):
        blob = blob[:-1]
    if not blob:
        return []
    parts = blob.split("\0")
    if len(parts) % width:
        raise BaseError(tr("agent.626"))
    return [parts[i:i + width] for i in range(0, len(parts), width)]


def _commit_files(root: Path, limit: int) -> dict[str, list[str]]:
    """路径单独读，不和提交说明混在同一次拆分里。记录之间是两个 NUL。"""
    text = _out(_git(root, "log", f"-n{int(limit)}", "-z", "--name-only", "--pretty=format:%H"))
    found = {}
    for rec in text.split("\0\0"):
        if not rec.strip("\0"):
            continue
        head, _, files = rec.partition("\n")
        sha = head.strip("\0")
        if sha:
            found[sha] = [name for name in files.split("\0") if name]
    return found


def list_commits(root: Path, limit: int = 200) -> list[dict]:
    # 元数据五个字段用 NUL 隔开，和文件路径分两次取。不能改成有限次 split：说明里可以含任意可见字符。
    meta = _out(_git(root, "log", f"-n{int(limit)}", "-z", "--format=%H%x00%an%x00%aI%x00%s%x00%b"))
    files_of = _commit_files(root, limit)
    commits = []
    for sha, author, when, subject, body in _nul_groups(meta, 5):
        commits.append({"sha": sha, "author": author, "when": when, "subject": subject,
                        "body": body.strip(), "files": files_of.get(sha, [])})
    # 谁被撤回过
    reverted_by = {}
    for c in commits:
        for target in REVERTS_RE.findall(c["body"]):
            reverted_by.setdefault(target, c["sha"])
    for c in commits:
        c["reverted_by"] = next((v for k, v in reverted_by.items() if c["sha"].startswith(k)), None)
        c["reverts"] = (REVERTS_RE.findall(c["body"]) or [None])[0]
        c["is_root"] = False
    root_sha = _out(_git(root, "rev-list", "--max-parents=0", "HEAD")).split()
    for c in commits:
        c["is_root"] = c["sha"] in root_sha
    return commits


def workspace_commits(root: Path, viewer: Viewer, docs: list[Doc], limit: int = 500) -> list[dict]:
    """一次当前快照决定可见路径；摘要、统计、详情和撤回共用这个集合。"""
    local = viewer.is_owner and not viewer.remote
    visible = viewer.visible(docs) if local else documents(viewer, docs)
    by_rel = {d.rel: d for d in visible}
    by_id = {d.id: d for d in visible if d.kind == "project"}
    auditable = set(by_rel)
    if local:
        # 本人首页固定路径、仍存在项目的进展/链接即使已经删除，也能确认其审计归属。
        # 删除的待办不能凭文件名猜 owner，仍不补读历史快照。
        current_paths = {d.rel for d in docs}
        auditable.update(rel for rel in (f"home/{config.owner_key()}.md", f"home/{config.owner_key()}-draft.md") if rel not in current_paths)
        auditable.update(f"projects/{pid}/{name}.md" for pid in by_id for name in ("status", "links"))
    members = member_keys(docs)
    result = []
    for c in list_commits(root, limit):
        paths = list(dict.fromkeys(f for f in c["files"] if f in auditable))
        if not paths:
            continue
        # 本机也不能用 diff 和撤回读写别人的个人工作台；未知路径无法确认，降为摘要。
        raw = local and all(f in auditable for f in c["files"])
        revertable = _revertable(c)
        objects = []
        for path in paths:
            d = by_rel.get(path)
            if d is None:
                continue
            pid = path.split("/")[1] if d.kind.startswith("project") else d.meta.get("project")
            objects.append({"kind": d.kind, "title": safe_text(d.title), "rel": safe_text(path),
                            "project": safe_text(pid) if pid in by_id else None,
                            # 动态里的项目标签要显示名字；只取这个人看得到的项目
                            "project_name": safe_text(views.short(by_id[pid].title)) if pid in by_id else None})
        safe_actor = actor(c["author"].partition("·")[0].strip(), viewer, docs)
        if raw:
            row = views.commit_row(c, by_id, by_rel, members, viewer)
            can_revert = revertable
            row |= {"can_revert": can_revert, "allowed_actions": {"view_diff": True, "revert": can_revert}}
            counting = c
        else:
            if not objects:
                continue
            kinds = {o["kind"] for o in objects}
            if kinds == {"project-status"} and len({o["project"] for o in objects}) == 1:
                what, kind = f"更新了《{objects[0]['title']}》的进展", "进展"
            elif kinds == {"todo"}:
                what, kind = f"更新了你的 {len(objects)} 条待办", "待办"
            elif len(objects) == 1:
                kind = views.KIND_LABEL.get(objects[0]["kind"], "内容")
                what = f"更新了《{objects[0]['title']}》的{kind}"
            else:
                what, kind = f"更新了你可见的 {len(objects)} 项内容", "内容"
            row = {"sha": c["sha"], "when": c["when"], "safe_actor": safe_actor, "type": kind,
                   "visible_objects": objects, "what": what,
                   "allowed_actions": {"view_diff": False, "revert": False}}
            if local:
                row["can_revert"] = False
            # 仅在服务器内部用于计数，丢掉原署名、说明及隐藏文件。
            counting = {"sha": c["sha"], "when": c["when"], "author": safe_actor["key"] or "成员",
                        "files": paths}
        result.append({"row": row, "commit": counting, "raw": raw})
    return result


def _revertable(commit: dict) -> bool:
    """停用、恢复成员只能走成员页，不给 git 撤回把远程接入和飞书绑定一起翻回来。"""
    if commit["is_root"] or commit["reverted_by"]:
        return False
    subject = commit["subject"]
    while subject.startswith("撤回："):
        subject = subject[3:]
    return not (subject.startswith("停用成员") or subject.startswith("恢复成员"))


def find_workspace_commit(entries: list[dict], sha: str) -> dict | None:
    """只认规范完整 SHA 或集合里唯一的有效短 SHA，不执行用户提供的 git 表达式。"""
    if not SHA_RE.fullmatch(sha or ""):
        return None
    matches = [entry for entry in entries if entry["row"]["sha"].startswith(sha)]
    return matches[0] if len(matches) == 1 else None


def commit_diff(root: Path, sha: str) -> list[dict]:
    """每个文件一段：增加的行、删掉的行、上下文。"""
    if not SHA_RE.match(sha or ""):
        raise BaseError(tr("agent.627"))
    text = _out(_git(root, "show", "--format=", "--unified=2", "--no-renames", "--no-color", sha))
    files, cur = [], None
    for line in text.splitlines():
        if line.startswith("diff --git "):
            path = line.split(" b/", 1)[-1]
            cur = {"path": path, "lines": [], "status": "modified"}
            files.append(cur)
        elif cur is None:
            continue
        elif line.startswith("new file"):
            cur["status"] = "added"
        elif line.startswith("deleted file"):
            cur["status"] = "deleted"
        elif line.startswith(("index ", "--- ", "+++ ", "similarity", "rename ")):
            continue
        elif line.startswith("@@"):
            if cur["lines"]:  # 第一段前面不用分隔线
                cur["lines"].append({"t": "gap", "s": ""})
        elif line.startswith("+"):
            cur["lines"].append({"t": "add", "s": line[1:]})
        elif line.startswith("-"):
            cur["lines"].append({"t": "del", "s": line[1:]})
        elif line.startswith("\\"):
            continue
        else:
            cur["lines"].append({"t": "ctx", "s": line[1:] if line.startswith(" ") else line})
    return files


# ---------- 撤回 ----------

def revert(root: Path, sha: str, reason: str | None = None) -> dict:
    """新增一次反向提交，作者是 主人。冲突时不强行覆盖，返回挡住它的后来改动。调用方需持有写锁。"""
    if not SHA_RE.match(sha or ""):
        raise BaseError(tr("agent.627"))
    full = _out(_git(root, "rev-parse", "--verify", f"{sha}^{{commit}}")).strip()
    if full in _out(_git(root, "rev-list", "--max-parents=0", "HEAD")).split():
        raise BaseError(tr("agent.628"))
    if _out(_git(root, "status", "--porcelain")).strip():
        raise BaseError(tr("agent.629"))
    subject = _out(_git(root, "log", "-1", "--format=%s", full)).strip()
    files = [f for f in _out(_git(root, "show", "--format=", "--name-only", "--no-renames", full)).splitlines() if f]
    proc = _git(root, "revert", "--no-commit", "--no-edit", full, check=False)
    if proc.returncode != 0:
        _git(root, "revert", "--abort", check=False)
        _git(root, "reset", "-q", "--hard", "HEAD", check=False)  # 只回到撤回前的状态（工作区事先已确认干净）
        later = _out(_git(root, "log", "-z", "--format=%H%x00%an%x00%aI%x00%s", f"{full}..HEAD", "--", *files))
        blockers = []
        for h, an, when, s in _nul_groups(later, 4):
            blockers.append({"sha": h, "author": an, "when": when, "subject": s})
        return {"ok": False, "blockers": blockers, "subject": subject}
    message = (f"撤回：{subject}\n\n由 {owner_name_at(root)} 在网页界面撤回提交 {full[:7]}。"
               + (f"\n\n原因：{reason.strip()}" if reason and reason.strip() else "")
               + f"\n\nReverts: {full}\nAgent: {config.owner_key()}\n")
    env = dict(os.environ, GIT_AUTHOR_NAME=config.owner_key(), GIT_AUTHOR_EMAIL=f"{config.owner_key()}@company-base.local",
               GIT_COMMITTER_NAME=config.owner_key(), GIT_COMMITTER_EMAIL=f"{config.owner_key()}@company-base.local")
    proc = subprocess.run(["git", "-C", str(root), "commit", "-q", "-F", "-"], input=message.encode("utf-8"),
                          capture_output=True, env=env, creationflags=NO_WINDOW)
    if proc.returncode != 0:
        _git(root, "revert", "--abort", check=False)
        _git(root, "reset", "-q", "--hard", "HEAD", check=False)
        raise BaseError(tr("agent.630", v0=proc.stderr.decode('utf-8', 'replace').strip()[:300]))
    new = _out(_git(root, "rev-parse", "--short", "HEAD")).strip()
    return {"ok": True, "commit": new, "subject": subject}
