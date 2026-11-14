"""网页和 agent 共用的 git 历史读取及提交说明翻译。"""
from __future__ import annotations
import datetime as dt
import re
import subprocess
from dataclasses import replace
from pathlib import Path
from .gitops import NO_WINDOW
from .git_runtime import run_git
from .i18n import text as tr
from .redact import documents, safe_text
from .store import BaseError, Doc, NotFound, TAIPEI, archived_status_docs, member_keys

REVERTS_RE = re.compile(r"(?:Reverts:|This reverts commit)\s*([0-9a-f]{7,40})")
SHA_RE = re.compile(r"^[0-9a-f]{7,40}$")

def _git(root: Path, *args: str, env: dict | None = None, check: bool = True) -> subprocess.CompletedProcess:
    proc = run_git(["-c", "core.quotePath=false", *args], root=root, capture_output=True, env=env, creationflags=NO_WINDOW)
    if check and proc.returncode != 0:
        raise BaseError(tr("agent.256", v0=args[0], v1=proc.stderr.decode('utf-8', 'replace').strip()[:300]))
    return proc


def _out(proc: subprocess.CompletedProcess) -> str:
    return proc.stdout.decode("utf-8", "replace")


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


def _commit_files(root: Path, limit: int, paths=()) -> dict[str, list[str]]:
    """路径单独读，不和提交说明混在同一次拆分里。记录之间是两个 NUL。"""
    text = _out(_git(root, "log", f"-n{int(limit)}", "-z", "--name-only", "--pretty=format:%H", "--full-diff", "--", *paths))
    found = {}
    for rec in text.split("\0\0"):
        if not rec.strip("\0"):
            continue
        head, _, files = rec.partition("\n")
        sha = head.strip("\0")
        if sha:
            found[sha] = [name for name in files.split("\0") if name]
    return found


def list_commits(root: Path, limit: int = 200, paths=()) -> list[dict]:
    # 元数据五个字段用 NUL 隔开，和文件路径分两次取。不能改成有限次 split：说明里可以含任意可见字符。
    meta = _out(_git(root, "log", f"-n{int(limit)}", "-z", "--format=%H%x00%an%x00%aI%x00%s%x00%b", "--", *paths))
    files_of = _commit_files(root, limit, paths)
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


def commit_diff(root: Path, sha: str, paths=()) -> list[dict]:
    """每个文件一段：增加的行、删掉的行、上下文。"""
    if not SHA_RE.match(sha or ""):
        raise BaseError(tr("agent.627"))
    text = _out(_git(root, "show", "--format=", "--unified=2", "--no-renames", "--no-color", sha, "--", *paths))
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


class HistoryMixin:
    def history_data(self, target, limit=10):
        if not isinstance(target, str) or not target.strip():
            raise NotFound(tr("history.not_found"))
        viewer, docs, visible = self._ctx()
        if viewer.remote or viewer.assigned:
            visible = documents(viewer, docs)
        # 进展归档仍随项目授权，即使当前 status.md 已被手工移走。
        current_paths = {d.rel for d in visible}
        sources = visible + [replace(d, kind="project-status", rel=d.rel.rsplit("/", 1)[0] + "/status.md")
                             for d in visible if d.kind == "project" and
                             d.rel.rsplit("/", 1)[0] + "/status.md" not in current_paths]
        visible += archived_status_docs(self.root, sources)
        target = target.strip().replace("\\", "/")
        matches = [d for d in visible if d.kind not in ("person", "home") and not d.problem
                   and target in (d.id, d.rel, d.rel.removesuffix(".md"))]
        if not matches:
            raise NotFound(tr("history.not_found"))
        paths = {d.rel for d in matches}
        for project in (d for d in matches if d.kind == "project"):
            prefix = project.rel.rsplit("/", 1)[0] + "/"
            paths.update(d.rel for d in visible if d.rel.startswith(prefix) and
                         d.kind in ("project-status", "project-links"))
        pathspecs = [f":(literal){path}" for path in sorted(paths)]
        limit = max(1, min(int(limit), 50))
        projects = {d.id: d for d in visible if d.kind == "project"}
        members = member_keys(docs)
        rows = []
        for c in list_commits(self.root, limit, pathspecs):
            files = commit_diff(self.root, c["sha"], pathspecs)
            key, _, signed = (x.strip() for x in c["author"].partition("·"))
            name = members[key].title if key in members else key
            tool = re.search(r"通过Onestead写入（([^）]+)）", c["body"])
            row = {"commit": c["sha"][:7],
                   "when": dt.datetime.fromisoformat(c["when"]).astimezone(TAIPEI).strftime("%Y-%m-%d %H:%M"),
                   "author": f"{name} · {signed}" if signed else name,
                   "tool": tool[1] if tool else None, "subject": friendly_subject(c["subject"], projects),
                   "files": [{"path": f["path"], "added": sum(l["t"] == "add" for l in f["lines"]),
                              "deleted": sum(l["t"] == "del" for l in f["lines"])} for f in files]}
            if not rows:
                excerpt = "\n".join(f["path"] + "\n" + "\n".join(
                    {"add": "+", "del": "-", "ctx": " ", "gap": "…"}[l["t"]] + l["s"] for l in f["lines"])
                    for f in files)
                row["excerpt"] = (safe_text(excerpt) if viewer.remote or viewer.assigned else excerpt)[:1500]
            if viewer.remote or viewer.assigned:
                from .redact import text_fields
                row = text_fields(row)
            rows.append(row)
        return rows

    def history(self, target, limit=10):
        rows = self.history_data(target, limit)
        lines = [tr("history.title", target=safe_text(target)), ""]
        for row in rows:
            lines += [tr("history.line", when=row["when"], author=row["author"],
                         tool=row["tool"] or tr("history.unknown_tool"), subject=row["subject"]), ""]
            lines += [tr("history.files", **f) for f in row["files"]]
            if row.get("excerpt"):
                lines += ["", tr("history.excerpt"), "", "```diff", row["excerpt"], "```"]
            lines.append("")
        if not rows:
            lines.append(tr("history.empty"))
        return "\n".join(lines) + "\n"


