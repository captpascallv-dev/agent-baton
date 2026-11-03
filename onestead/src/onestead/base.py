"""Onestead 的核心：读接口和受控写接口。MCP 服务和命令行都只调这里，所以两边权限规则完全一样。

输出是跟公司语言的 Markdown；数据正文和协议值保持原样。
"""

from __future__ import annotations

from .i18n import label, text as tr

from . import config

import datetime as dt
import os
import re
from contextlib import contextmanager
from pathlib import Path

from . import gitops
from .apps import AppMixin
from .handover import _status_entries, insiders, open_applies
from .home import HomeMixin
from .manage import ID_RE, MAX_BODY, ManageMixin
from .projectbody import (SUMMARY_SECTIONS, _apply_summary_sections, _clean_summary_sections,
                          _parse_sections, _section_name)
from .redact import documents, safe_text
from .skills import SkillMixin
from .sites import SiteMixin
from .todos import TodoMixin, notice
from .workspace import TokenMixin
from .store import (LEVEL_NAMES, LEVELS, STATUS_ARCHIVE_DIR, BaseError, Doc, Forbidden, NotFound, StorageError, Store,
                    Viewer, archived_status_docs, clean_project_paths, make_viewer, member_cards, member_keys, now_taipei,
                    owner_name_at, project_path_list)
from .textutil import (MAX_TEXT, SECRET_PATTERNS, _ago, _as_date, _cell, _check_signature,  # noqa: F401
                       _check_text, _dump_front, _strip_title, soften_headings)

STALE_DAYS = 14  # 摘要超过这么多天，提示“可能已过时”
NOT_FOUND = "not_found_or_denied"
NEW_PROJECT_STATES = ("进行中", "维护中", "构想中", "暂停")
PROJECT_BODY = "## 目标\n\n## 现状\n\n## 下一步\n\n## 分工\n\n## 入口"
_STATUS_DATE = re.compile(r"^## (\d{4}-\d{2}-\d{2})(?:[ T]\d{2}:\d{2})?(?:$| )")
_STATUS_HEAD = re.compile(r"^## (\d{4}-\d{2}-\d{2} \d{2}:\d{2}) · (.+)$")
STATUS_KEEP = 20  # 归档后 status.md 留最近这么多条
STATUS_ARCHIVE_AT = 40  # 超过这么多条才归档一次：每次追加都会把整个文件存进 git，文件小仓库才不涨
STATUS_RECENT_HOURS = 2  # 同一项目里同一身份和署名这么久内又记，提醒并成一条
STATUS_LONG = 800  # 一条进展超过这么多字，提醒只写结果

KIND_ORDER = {"handbook": 0, "project": 1, "project-status": 2, "project-links": 3,
              "decision": 4, "asset": 5, "person": 6, "home": 7, "todo": 8, "app": 9, "skill": 10}
KIND_NAMES = {"handbook": "手册", "project": "项目", "project-status": "项目进展",
              "project-links": "项目链接", "decision": "决定", "asset": "资产", "person": "成员",
              "home": "首页", "todo": "待办", "app": "应用", "skill": "Skill"}


class CompanyBase(TodoMixin, AppMixin, SkillMixin, ManageMixin, HomeMixin, TokenMixin, SiteMixin):
    def __init__(self, root: str | Path, agent_key: str | None, now=now_taipei, allow_owner: bool = False,
                 require_remote: bool = False, auth_check=None):
        config.owner_key()
        self.root = Path(root)
        self.allow_owner = allow_owner  # 只有网页界面传 True
        # 远程接口和同事网页传 True；每次读盘重新核对接入资格，写入在锁内核对。
        self.require_remote = require_remote
        self.auth_check = auth_check  # 网页会话、源口令、飞书绑定或 MCP Bearer；不得再拿写锁
        self.store = Store(self.root)
        self.agent_key = agent_key
        self._now = now

    @property
    def owner_name(self) -> str:
        return owner_name_at(self.root)

    @contextmanager
    def _write(self):
        """先拿数据写锁，再复核这次请求的凭据，通过之后才改文件。"""
        with gitops.write_lock(self.root):
            self._recheck_auth()
            yield

    def _recheck_auth(self) -> None:
        if self.auth_check is not None:
            self.auth_check()

    # ---------- 内部 ----------

    def _ctx(self, docs=None) -> tuple[Viewer, list[Doc], list[Doc]]:
        # 不传 docs 就重新读盘。传入的只用于同一次请求里认证和页面快照共用，写之前和请求结束后的复核不能传。
        if docs is None:
            docs = self.store.load()
        viewer = make_viewer(docs, self.agent_key, self.allow_owner, remote=self.require_remote)
        if self.require_remote and not viewer.can_remote:
            raise Forbidden(tr("agent.079", v0=self.owner_name))
        return viewer, docs, viewer.visible(docs)

    def _today(self) -> dt.date:
        return self._now().date()

    @staticmethod
    def _find(visible: list[Doc], kind: str, doc_id: str) -> Doc | None:
        doc_id = (doc_id or "").strip()
        for d in visible:
            if d.kind == kind and d.id == doc_id:
                return d
        return None

    def _restricted_banner(self, doc: Doc) -> str:
        """限定、使用提示、标记问题：放在内容最前面提醒读的伙伴。"""
        parts = []
        if doc.problem:
            parts.append(tr("agent.113", v0=doc.problem, v1=self.owner_name))
        if doc.sensitivity == "restricted":
            parts.append(tr("agent.114"))
        note = doc.meta.get("access_note")
        if note:
            parts.append(tr("agent.115", v0=note))
        return f"【{''.join(parts)}】\n" if parts else ""

    def _summary_banner(self, meta: dict, remote: bool = False, newer: int = 0) -> str:
        as_of = _as_date(meta.get("summary_as_of"))
        if as_of:
            days = (self._today() - as_of).days
            line = tr("agent.080", v0=as_of.isoformat(), v1=_ago(days))
            if days > STALE_DAYS:
                line += tr("agent.116")
            # 没标日期的仍只说“没有标日期”，不把进展条数说成摘要过期。
            if newer:
                line += tr("agent.117", v0=newer)
        else:
            line = tr("agent.081")
        auth = meta.get("authority")
        if isinstance(auth, dict) and (auth.get("label") or auth.get("path")):
            if remote:  # 远程接入时不告诉本机台账的路径
                line += tr("agent.118", v0=auth.get('label') or tr("agent.163"))
            else:
                line += tr("agent.118", v0=auth.get('label') or auth.get('path'))
                if auth.get("path"):
                    line += f"：{auth['path']}"
        return f"【{line}】\n"

    def _roles_text(self, meta: dict) -> str:
        roles = meta.get("roles") or []
        if not roles:
            return tr("agent.067")
        parts = []
        for r in roles:
            s = f"{r['role']}：{r['who']}"
            since = _as_date(r.get("since"))
            if since:
                s += tr("agent.119", v0=since.isoformat())
            parts.append(s)
        return "；".join(parts)

    def _projects_table(self, visible: list[Doc]) -> str:
        rows = [tr("agent.082"), "|---|---|---|---|---|---|"]
        today = self._today()
        for d in visible:
            if d.kind != "project":
                continue
            as_of = _as_date(d.meta.get("summary_as_of"))
            when = f"{as_of.isoformat()}（{_ago((today - as_of).days)}）" if as_of else tr("agent.120")
            name = d.title
            tags = ([tr("agent.157")] if d.sensitivity == "restricted" else []) + (
                [tr("agent.158", v0=d.meta['access_note'])] if d.meta.get("access_note") else [])
            if tags:
                name += f"（{'；'.join(tags)}）"
            rows.append(f"| `{d.id}` | {_cell(name)} | {_cell(d.meta.get('state'))} | "
                        f"{_cell(d.meta.get('goal'))} | {_cell(self._roles_text(d.meta))} | {when} |")
        if len(rows) == 2:
            return tr("agent.068")
        return "\n".join(rows)

    def _identity_line(self, viewer: Viewer) -> str:
        if viewer.key is None:
            return (tr("agent.069"))
        if not viewer.valid:
            return tr("agent.070", v0=viewer.key)
        if not viewer.known:
            return tr("agent.071", v0=viewer.key)
        if viewer.assigned:
            scope = "、".join(f"`{x}`" for x in sorted(viewer.projects)) or tr("agent.121")
            assets = "、".join(f"`{x}`" for x in sorted(viewer.assets)) or tr("agent.121")
            write = tr("agent.122") if viewer.can_write else tr("agent.123")
            return (tr("agent.072", v0=viewer.key, v1=viewer.member.title, v2=scope, v3=assets, v4=write))
        return (tr("agent.052", v0=viewer.key, v1=viewer.member.title if viewer.member else viewer.key, v2=tr("agent.053") if viewer.can_write else tr("agent.054")))

    # ---------- 读 ----------

    def company(self, cwd: str | None = None) -> str:
        viewer, docs, visible = self._ctx()
        # 远程不认目录，也不把本机路径写进这段。没给 cwd 时用本进程的当前目录。
        located = None if viewer.remote else self._project_at(visible, cwd)
        out = [tr("agent.083", v0=config.display_name()), "", self._identity_line(viewer), ""]
        if self._shared_line(viewer):
            out[-1:-1] = [self._shared_line(viewer)]
        unnotified = sum(notice(d.meta) == "unnotified" for d in self._todos(visible))
        if unnotified:
            out += [tr("ask.company", count=unnotified, owner=self.owner_name), ""]
        company = self._find(visible, "handbook", "company")
        if company:
            out += [_strip_title(company.body), ""]
        out += [tr("agent.084"), "", self._projects_table(visible), "",
                tr("agent.085"), ""]
        collab = self._find(visible, "handbook", "collaboration")
        if collab:
            out += ["## " + collab.title, "", _strip_title(collab.body), ""]
        people = [d for d in visible if d.kind == "person" and d.meta.get("archived") is not True]
        if viewer.known and viewer.assigned:
            cards = member_cards(viewer, docs)
            if cards:
                out += [tr("agent.124"), "", tr("agent.151"), "|---|---|---|---|"]
                for card in cards:
                    out.append("| " + " | ".join(_cell(card[k]) for k in ("key", "title", "kind", "role")) + " |")
                out.append("")
            people = []
        if people:
            out += [tr("agent.124"), "", tr("agent.125"), "|---|---|---|---|"]
            for p in people:
                out.append(f"| `{_cell(p.meta.get('agent_key'))}` | {_cell(p.title)} | "
                           f"{_cell(p.meta.get('role'))} | {tr("agent.164") if p.meta.get('can_write') else tr("agent.165")} |")
            out.append("")
        out += [tr("agent.086"), "",
                tr("agent.087"),
                tr("agent.088")]
        if viewer.assigned:
            out += [tr("agent.126")]
        else:
            out += [tr("agent.127"),
                    tr("agent.128")]
        body = "\n".join(out).strip() + "\n"
        if located is None:
            return body
        return self._located_block(located, visible).rstrip("\n") + "\n\n" + body

    def _project_at(self, visible: list[Doc], cwd: str | None) -> Doc | None:
        current = os.getcwd() if cwd is None else cwd
        if not isinstance(current, str) or not current.strip():
            return None
        here = _normalize_dir(current)
        best: tuple[tuple[int, str], Doc] | None = None
        for doc in visible:
            if doc.kind != "project" or "paths" not in doc.meta:
                continue
            items = project_path_list(doc.meta.get("paths"))
            if not items:
                continue
            for item in items:
                norm = _normalize_dir(item)
                if not _dir_contains(here, norm):
                    continue
                # 同样深（两条路径规整后相同）时取 id 较小的项目，结果稳定。
                rank = (-len(norm), doc.id)
                if best is None or rank < best[0]:
                    best = (rank, doc)
        return None if best is None else best[1]

    def _located_block(self, doc: Doc, visible: list[Doc]) -> str:
        meta = doc.meta
        lines = [
            tr("here.located", title=doc.title, id=doc.id),
            tr("here.located_state", state=meta.get("state") or tr("agent.120")),
            tr("here.located_goal", goal=meta.get("goal") or ""),
            tr("here.located_roles", roles=self._roles_text(meta)),
        ]
        nxt = _clip(_section_text(doc.body, "下一步"))
        if nxt:
            lines.append(tr("here.located_next", text=nxt))
        status = self._find_kind(visible, "project-status", doc)
        for date, first in _recent_progress(status.body if status else ""):
            lines.append(tr("here.located_progress", date=date, line=first))
        lines.append(tr("here.located_detail", id=doc.id))
        return "\n".join(lines)

    def whoami(self) -> str:
        viewer, docs, visible = self._ctx()
        counts = {}
        for d in visible:
            counts[d.kind] = counts.get(d.kind, 0) + 1
        seen = "、".join(f"{label(KIND_NAMES[k])} {n}" for k, n in sorted(counts.items(), key=lambda x: KIND_ORDER[x[0]]))
        where = "" if viewer.remote else tr("agent.089", v0=self.root)  # 本机路径不告诉远程接入的
        shared = self._shared_line(viewer)
        return tr("agent.055", v0=self._identity_line(viewer), v1=seen or tr("agent.051"), v2=shared + chr(10) if shared else '', v3=where)

    def _shared_line(self, viewer: Viewer) -> str:
        signers = viewer.member.meta.get("signers") if viewer.member else None
        if not signers:
            return ""
        return (tr("agent.056", v0='、'.join(map(str, signers)), v1=tr("agent.057").join(map(str, signers))))

    def get_handbook(self, handbook_id: str) -> str:
        _, _, visible = self._ctx()
        doc = self._find(visible, "handbook", handbook_id)
        if not doc:
            ids = "、".join(d.id for d in visible if d.kind == "handbook")
            raise BaseError(tr("agent.090", v0=tr(NOT_FOUND), v1=ids or tr("agent.051")))
        return f"{self._restricted_banner(doc)}# {doc.title}\n\n{_strip_title(doc.body)}\n"

    def list_projects(self) -> str:
        _, _, visible = self._ctx()
        return tr("agent.059") + self._projects_table(visible) + tr("agent.058")

    def get_project(self, project_id: str, status_entries: int = 5) -> str:
        viewer, _, visible = self._ctx()
        doc = self._find(visible, "project", project_id)
        if not doc:
            raise BaseError(tr("agent.091", v0=project_id, v1=tr(NOT_FOUND)))
        meta = doc.meta
        status = self._find_kind(visible, "project-status", doc)
        newer = newer_status_count(meta.get("summary_as_of"), status.body if status else None)
        out = [self._restricted_banner(doc) + self._summary_banner(meta, viewer.remote, newer).rstrip("\n"), "",
               f"# {doc.title}", "",
               tr("agent.035", v0=meta.get('state', tr("agent.120"))),
               tr("agent.022", v0=meta.get('goal', '')), ""]
        roles = meta.get("roles") or []
        out += [tr("agent.092"), ""]
        if roles:
            out += [tr("agent.129"), "|---|---|---|---|"]
            for r in roles:
                since = _as_date(r.get("since"))
                out.append(f"| {_cell(r['role'])} | {_cell(r['who'])} | "
                           f"{since.isoformat() if since else tr("agent.166")} | {tr("agent.164") if r.get('key') else tr("agent.165")} |")
            out.append("")
            out.append(tr("agent.130", v0=self.owner_name))
        else:
            out.append(tr("agent.067"))
        body = _strip_title(doc.body)
        if not viewer.remote and "paths" in meta:
            items = project_path_list(meta.get("paths"))
            if items:
                body = _with_local_dirs(body, items)
        out += ["", body]
        status = self._find_kind(visible, "project-status", doc)
        if status:
            entries = _status_entries(status.body)
            n = max(0, int(status_entries))
            shown = entries[-n:] if n else []
            out += ["", tr("agent.131", v0=len(shown), v1=len(entries)), ""]
            months = [p.stem for p in sorted((self.root / status.rel.rsplit("/", 1)[0] / STATUS_ARCHIVE_DIR).glob("*.md"))]
            if months:
                out += [tr("status.archived", months="、".join(months)), ""]
            out += shown if shown else [tr("agent.152")]
        apps = self._project_app_lines(viewer, visible, doc.id)
        if apps:
            out += apps
        links = self._find_kind(visible, "project-links", doc)
        if links:
            out += ["", tr("agent.132"), "", _strip_title(links.body)]
        return "\n".join(out).strip() + "\n"

    def _project_app_lines(self, viewer: Viewer, visible: list[Doc], project_id: str) -> list[str]:
        """这个身份看得到的应用。同事和远程只看在架的，远程文字再过一遍路径遮盖。"""
        apps = [d for d in visible if d.kind == "app" and not d.problem and d.meta.get("project") == project_id]
        if viewer.remote or viewer.assigned:
            apps = [d for d in apps if d.meta.get("listed", True) is not False]
        apps.sort(key=lambda d: (d.meta.get("listed") is False, d.title))
        if not apps:
            return []
        show = (lambda value: safe_text(value)) if viewer.remote else (lambda value: str(value or ""))
        lines = ["", tr("agent.093"), ""]
        for d in apps:
            line = f"- {show(d.title)}：{show(d.meta.get('one_line'))}（`{d.id}`）"
            if d.meta.get("listed") is False:
                line += tr("agent.133")
            lines.append(line)
        return lines

    @staticmethod
    def _find_kind(visible: list[Doc], kind: str, project: Doc) -> Doc | None:
        prefix = project.rel.rsplit("/", 1)[0] + "/"
        for d in visible:
            if d.kind == kind and d.rel.startswith(prefix):
                return d
        return None

    def search(self, query: str, limit: int = 20) -> str:
        viewer, docs, visible = self._ctx()
        terms = [t.lower() for t in (query or "").split()]
        if not terms:
            raise BaseError(tr("agent.094"))
        limit = max(1, min(int(limit), 50))
        if viewer.remote or viewer.assigned:
            hits = self._projected_hits(viewer, docs, terms)
        else:
            hits = []
            for d in visible + archived_status_docs(self.root, visible):
                if d.kind == "person" and d.meta.get("archived") is True:
                    continue
                if all(t in d.raw.lower() for t in terms):
                    hits.append((label(KIND_NAMES.get(d.kind, d.kind)), d.title, d.rel, _how_to_open(d), d.raw,
                                 KIND_ORDER.get(d.kind, 9)))
            hits.sort(key=lambda item: (item[5], item[2]))
            hits = [item[:5] for item in hits]
        if not hits:
            return tr("agent.073", v0=query)
        out = [tr("agent.134", v0=query, v1=len(hits)) + (tr("agent.153", v0=limit) if len(hits) > limit else ""), ""]
        for kind, title, ref, how, text in hits[:limit]:
            out.append(f"- **{kind}** · {title}（`{ref}`）{how}")
            out.append(f"  …{_snippet(text, terms[0])}…")
        return "\n".join(out) + "\n"

    def _projected_hits(self, viewer: Viewer, docs: list[Doc], terms: list[str]) -> list[tuple]:
        """远程和按授权读只搜网页上能显示的那一套：不是成员原文，也不是 Skill 备注正文。"""
        hits = []
        for d in documents(viewer, docs + archived_status_docs(self.root, viewer.visible(docs))):
            parts = [d.title, d.body]
            if d.kind == "home":
                parts.append(str(d.meta.get("note") or ""))
            text = safe_text("\n".join(parts))
            if all(t in text.lower() for t in terms):
                hits.append((label(KIND_NAMES.get(d.kind, d.kind)), safe_text(d.title), d.rel, _how_to_open(d), text))
        for card in member_cards(viewer, docs):
            text = safe_text("\n".join(str(card.get(k) or "") for k in ("title", "role", "key", "kind")))
            if all(t in text.lower() for t in terms):
                hits.append((tr("agent.159"), safe_text(card["title"]), card["key"], "", text))
        for info in self.shared_skill_list():
            text = safe_text("\n".join(str(info.get(k) or "") for k in ("id", "name", "one_line", "description")))
            if all(t in text.lower() for t in terms):
                sid = info["id"]
                hits.append(("Skill", info["name"], sid, f" → `get_skill('{sid}')`", text))
        return hits

    def list_assets(self) -> str:
        _, _, visible = self._ctx()
        assets = [d for d in visible if d.kind == "asset"]
        if not assets:
            return tr("agent.074")
        rows = [tr("agent.095"), "", tr("agent.096"), "|---|---|---|---|"]
        for d in assets:
            rows.append(f"| `{d.id}` | {_cell(d.title)} | {label(LEVEL_NAMES[d.sensitivity])} | "
                        f"{_cell(d.meta.get('updated'))} |")
        return "\n".join(rows) + tr("agent.060")

    def get_asset(self, asset_id: str) -> str:
        _, _, visible = self._ctx()
        doc = self._find(visible, "asset", asset_id)
        if not doc:
            raise BaseError(tr("agent.097", v0=asset_id, v1=tr(NOT_FOUND)))
        return f"{self._restricted_banner(doc)}# {doc.title}\n\n{_strip_title(doc.body)}\n"

    def list_decisions(self, project_id: str | None = None, limit: int = 20) -> str:
        _, _, visible = self._ctx()
        decisions = [d for d in visible if d.kind == "decision"]
        if project_id:
            decisions = [d for d in decisions if d.meta.get("project") == project_id.strip()]
        decisions.sort(key=lambda d: (str(_as_date(d.meta.get("date")) or ""), d.id), reverse=True)
        limit = max(1, min(int(limit), 100))
        if not decisions:
            return tr("agent.075")
        out = [tr("agent.154", v0=len(decisions)) + (tr("agent.160", v0=limit) if len(decisions) > limit else "") + "）", ""]
        for d in decisions[:limit]:
            m = d.meta
            out.append(f"## {m.get('date')} · {d.title}")
            info = tr("agent.098", v0=m.get('decided_by', tr("agent.161")), v1=m.get('recorded_by', tr("agent.161")))
            if m.get("project"):
                info += tr("agent.135", v0=m['project'])
            out += ["", self._restricted_banner(d) + info, "", _strip_title(d.body).replace("\n## ", "\n### "), ""]
        return "\n".join(out).strip() + "\n"

    # ---------- 写 ----------

    def _writer(self, operation: str = "company") -> tuple[Viewer, list[Doc], list[Doc]]:
        """按操作核对内容写权限；接入方式不授予或剥夺公司写权限。"""
        viewer, docs, visible = self._ctx()
        if operation == "personal" and viewer.known and viewer.valid and (
                viewer.is_owner or viewer.member.meta.get("kind") == "human"):
            return viewer, docs, visible  # 人的 can_write 只管公司事实，不管自己的记事。
        if not viewer.can_write:
            raise Forbidden(self._identity_line(viewer) + tr("agent.099"))
        if viewer.assigned and operation not in (
                "update_project_status", "update_project_summary", "record_decision", "personal"):
            raise Forbidden(tr("agent.100"))
        self._require_signer(viewer)
        return viewer, docs, visible

    def _sign(self, signed_as: str | None) -> str | None:
        """检查署名格式并记下，共用身份要不要署名在 _require_signer 里核。"""
        self._signature = _check_signature(signed_as)
        return self._signature

    def _require_signer(self, viewer: Viewer) -> None:
        # 成员登记了 signers 的是几个助手共用的身份：谁写就得署谁的名，记录里才分得清。
        signers = viewer.member.meta.get("signers") if viewer.member else None
        if signers and getattr(self, "_signature", None) not in signers:
            raise Forbidden(tr("agent.101", v0='、'.join(map(str, signers)), v1=tr("agent.057").join(map(str, signers))))

    def _human_workspace(self):
        """本人确认和发收口令不允许 agent 代做，也不受公司写入开关影响。"""
        viewer, docs, visible = self._ctx()
        if not (viewer.known and viewer.valid and (viewer.is_owner or viewer.member.meta.get("kind") == "human")):
            raise BaseError(tr("agent.102"))
        return viewer, docs, visible

    @staticmethod
    def _granted_project(viewer: Viewer, project: Doc) -> None:
        """同事按授权写入只认授权给自己的项目。公开项目谁都能看，但看得到不等于能往里写。"""
        if viewer.assigned and project.id not in viewer.projects:
            raise Forbidden(tr("agent.103", v0=project.id))

    def _internal_only(self, what: str) -> tuple[Viewer, list[Doc], list[Doc]]:
        """只给自己人看的读接口（会暴露本机路径或 主人 个人事务的）。"""
        viewer, docs, visible = self._ctx()
        if not viewer.known or not viewer.valid or viewer.assigned or viewer.remote:
            raise Forbidden(tr("agent.104", v0=what))
        return viewer, docs, visible

    def _status_file(self, project: Doc, visible: list[Doc]) -> tuple[str, str]:
        status = self._find_kind(visible, "project-status", project)
        rel = project.rel.rsplit("/", 1)[0] + "/status.md"
        current = status.raw if status else f"# {project.title} · 进展\n\n只往后追加，不改旧条目。\n"
        return rel, current

    def _entry(self, key: str, signed_as: str | None, text: str) -> str:
        who = f"{key} · {signed_as}" if signed_as else key
        # 先把回车收成换行，再把行首的二级标题降一级，免得被误拆或冒充别人
        text = soften_headings(text)
        return f"## {self._now():%Y-%m-%d %H:%M} · {who}\n\n{text}\n"

    def _status_hint(self, current: str, entry: str, text: str) -> str:
        """只提醒不拦：同一项目里同一身份和署名两小时内又记，或者一条写得太长。"""
        hints = []
        entries = _status_entries(current)
        last = _STATUS_HEAD.match(entries[-1].split("\n", 1)[0]) if entries else None
        new = _STATUS_HEAD.match(entry.split("\n", 1)[0])
        if last and new and last.group(2).strip() == new.group(2).strip():
            gap = dt.datetime.fromisoformat(new.group(1)) - dt.datetime.fromisoformat(last.group(1))
            if dt.timedelta(0) <= gap < dt.timedelta(hours=STATUS_RECENT_HOURS):
                hints.append(tr("status.hint_recent", hours=STATUS_RECENT_HOURS))
        if len(text) > STATUS_LONG:
            hints.append(tr("status.hint_long", limit=STATUS_LONG))
        return "".join(hints)

    def _archive_status(self, rel: str, content: str, title: str, docs: list[Doc]) -> dict[str, str]:
        """超过 STATUS_ARCHIVE_AT 条时把较早的进展按月移到 status/YYYY-MM.md，status.md 留最近 STATUS_KEEP 条。
        还没处理的接任申请留在原处；没有日期的条目跟着前一条的月份，开头就没日期的也留下。"""
        entries = _status_entries(content)
        if len(entries) <= STATUS_ARCHIVE_AT:
            return {}
        head = re.split(r"\n(?=## )", "\n" + content, maxsplit=1)[0].strip("\n")
        holds = {index for index, _ in open_applies(entries, insiders(docs)).values()}
        cut = len(entries) - STATUS_KEEP
        month, kept, moved = None, [], {}
        for index, entry in enumerate(entries):
            matched = _STATUS_DATE.match(entry.split("\n", 1)[0])
            month = matched.group(1)[:7] if matched else month
            if index >= cut or index in holds or month is None:
                kept.append(entry)
            else:
                moved.setdefault(month, []).append(entry)
        if not moved:
            return {}
        files = {rel: (head + "\n\n" if head else "") + "\n\n".join(kept) + "\n"}
        folder = f"{rel.rsplit('/', 1)[0]}/{STATUS_ARCHIVE_DIR}"
        for month, items in moved.items():
            path = self.root / folder / f"{month}.md"
            old = (path.read_text(encoding="utf-8").rstrip("\n") if path.exists()
                   else f"# {title} · 进展归档 {month}\n\n从 status.md 移来的较早进展，只往后追加。")
            files[f"{folder}/{month}.md"] = old + "\n\n" + "\n\n".join(items) + "\n"
        return files

    def _may_create_project(self, viewer: Viewer) -> bool:
        """全读且能写：主人、本机伙伴，以及远程接入的全读伙伴（如网页版 ChatGPT）。只看指定项目的成员不能建。"""
        return bool(viewer.known and viewer.valid and viewer.can_write
                    and not viewer.assigned and (viewer.is_owner or viewer.access_mode == "full"))

    def create_project(self, project_id: str, title: str, goal: str, state: str = "构想中",
                       body: str | None = None, sensitivity: str = "internal",
                       paths: list[str] | None = None, signed_as: str | None = None) -> str:
        """新建项目，只写 projects/<id>/project.md。主人和全读能写的伙伴（本机或远程）都能调。"""
        commit = self._create_project_commit(project_id, title, goal, state, body, sensitivity, paths, signed_as)
        return tr("agent.061", v0=str(project_id or "").strip(), v1=commit)

    def _create_project_commit(self, project_id: str, title: str, goal: str, state: str = "构想中",
                               body: str | None = None, sensitivity: str = "internal",
                               paths: list[str] | None = None, signed_as: str | None = None) -> str:
        signed_as = self._sign(signed_as)
        with self._write():
            viewer, docs, _ = self._ctx()
            if not self._may_create_project(viewer):
                raise Forbidden(tr("agent.136", v0=self.owner_name))
            self._require_signer(viewer)
            if sensitivity not in LEVELS:
                raise BaseError(tr("agent.137"))
            if not viewer.is_owner and sensitivity == "public":
                raise Forbidden(tr("agent.138"))
            project_id = str(project_id or "").strip()
            if not ID_RE.match(project_id):
                raise BaseError(tr("agent.139"))
            if state not in NEW_PROJECT_STATES:
                raise BaseError(tr("agent.140", v0='、'.join(NEW_PROJECT_STATES)))
            if (self.root / "projects" / project_id).exists() or any(
                    d.kind == "project" and d.id == project_id for d in docs):
                raise BaseError(tr("agent.141", v0=project_id))
            title = _check_text(tr("agent.105"), title, limit=80, single_line=True)
            goal = _check_text(tr("agent.155"), goal, limit=200).replace("\n", " ")
            if body is not None and not isinstance(body, str):
                raise BaseError(tr("agent.142"))
            text = PROJECT_BODY if body is None or not body.strip() else _check_text(
                tr("agent.143"), body, required=False, limit=MAX_BODY)
            cleaned_paths = clean_project_paths(paths) if paths is not None else None
            today = self._today()
            meta = {"id": project_id, "type": "project", "title": title, "state": state, "goal": goal,
                    "roles": []}
            if cleaned_paths:
                meta["paths"] = cleaned_paths
            meta["summary_as_of"] = today
            meta["sensitivity"] = sensitivity
            meta["updated"] = today
            if sensitivity == "restricted":
                meta["visible_to"] = []
            rel = f"projects/{project_id}/project.md"
            content = _dump_front(meta) + f"\n# {title}\n\n{text.strip()}\n"
            tool = "网页界面" if viewer.is_owner else "create_project"
            commit = gitops.write_and_commit(self.root, {rel: content}, f"项目：新建「{title}」",
                                             tool, viewer.key, signed_as)
        return commit

    def update_project_status(self, project_id: str, text: str, signed_as: str | None = None) -> str:
        rel, commit, hint = self._append_status_commit(project_id, text, signed_as)
        return tr("agent.062", v0=rel, v1=commit) + hint

    def _append_status_commit(self, project_id: str, text: str,
                              signed_as: str | None = None) -> tuple[str, str, str]:
        signed_as = self._sign(signed_as)
        text = _check_text(tr("agent.076"), text)
        with self._write():
            viewer, docs, visible = self._writer(operation="update_project_status")
            project = self._find(visible, "project", project_id)
            if not project:
                raise NotFound(tr("agent.144", v0=project_id, v1=tr(NOT_FOUND)))
            self._granted_project(viewer, project)
            rel, current = self._status_file(project, visible)
            entry = self._entry(viewer.key, signed_as, text)
            content = current.rstrip("\n") + "\n\n" + entry
            files = {rel: content} | self._archive_status(rel, content, project.title, docs)
            hint = "" if viewer.is_owner else self._status_hint(current, entry, text)
            first = text.split("\n", 1)[0][:40]
            commit = gitops.write_and_commit(self.root, files, f"进展：{project.id} — {first}",
                                             "update_project_status", viewer.key, signed_as)
        return rel, commit, hint

    def update_project_summary(self, project_id: str, sections: dict, signed_as: str | None = None) -> str:
        """改现状、下一步、分工、入口。能记进展的身份就能改，判断和 update_project_status 同一套。"""
        signed_as = self._sign(signed_as)
        cleaned = _clean_summary_sections(sections, self.owner_name)
        changed = [name for name in SUMMARY_SECTIONS if name in cleaned]
        with self._write():
            viewer, _, visible = self._writer(operation="update_project_summary")
            project = self._find(visible, "project", project_id)
            if not project:
                raise BaseError(tr("agent.144", v0=project_id, v1=tr(NOT_FOUND)))
            self._granted_project(viewer, project)
            today = self._today()
            meta = dict(project.meta)
            if "现状" in cleaned:
                meta["summary_as_of"] = today
            meta["updated"] = today
            body = _apply_summary_sections(project.body, cleaned, today)
            content = _dump_front(meta) + "\n" + body.lstrip("\n")
            if content == project.raw:
                return tr("agent.031")
            subject = f"项目：更新「{project.title}」的{'、'.join(changed)}"
            commit = gitops.write_and_commit(self.root, {project.rel: content}, subject,
                                             "update_project_summary", viewer.key, signed_as)
        return tr("agent.063", v0=project.id, v1='、'.join(changed), v2=commit)

    def record_decision(self, title: str, decision: str, reason: str, decided_by: str,
                        project_id: str | None = None, sensitivity: str = "internal",
                        visible_to: list[str] | None = None, access_note: str | None = None,
                        signed_as: str | None = None) -> str:
        signed_as = self._sign(signed_as)
        title = _check_text(tr("agent.077"), title, limit=80, single_line=True)
        decision = soften_headings(_check_text(tr("agent.106"), decision))
        reason = soften_headings(_check_text(tr("agent.107"), reason))
        decided_by = _check_text(tr("agent.145"), decided_by, limit=40).replace("\n", " ")
        access_note = _check_text(tr("agent.108"), access_note, required=False, limit=200) or None
        if sensitivity not in LEVELS:
            raise BaseError(tr("agent.109"))
        with self._write():
            viewer, docs, visible = self._writer(operation="record_decision")
            keys = set(member_keys(docs))
            if viewer.assigned and not project_id:
                raise BaseError(tr("agent.146"))
            if viewer.assigned and sensitivity == "public":
                raise BaseError(tr("agent.147"))
            if viewer.assigned and visible_to is not None:
                # 同事看不到成员名单，按名单报错会泄露谁存在；能看的人跟着项目定
                raise BaseError(tr("agent.148"))
            project = None
            if project_id:
                project = self._find(visible, "project", project_id)
                if not project:
                    raise BaseError(tr("agent.144", v0=project_id, v1=tr(NOT_FOUND)))
                self._granted_project(viewer, project)
            if sensitivity == "restricted" and viewer.assigned:
                names = list(project.meta.get("visible_to") or []) if project.sensitivity == "restricted" else [viewer.acl]
            elif sensitivity == "restricted":
                names = [k.strip() for k in (visible_to or [viewer.key]) if k.strip()]
                unknown = [k for k in names if k not in keys or k == config.owner_key()]
                if unknown:
                    raise BaseError(tr("agent.162", v0='、'.join(unknown), v1=self.owner_name))
            else:
                names = None
            # 关联的项目是限定的，决定也要限定：将来对外时，不能借决定把限定项目带出去
            if project is not None and project.sensitivity == "restricted":
                allowed = set(project.meta.get("visible_to") or [])
                if sensitivity != "restricted" or not set(names).issubset(allowed):
                    raise BaseError(tr("agent.156"))
            today = self._today().isoformat()
            stamp = f"{today}-{viewer.key}-{self._now():%H%M%S}"
            doc_id, n = stamp, 2
            while (self.root / "decisions" / f"{doc_id}.md").exists():
                doc_id, n = f"{stamp}-{n}", n + 1
            meta = {"id": doc_id, "type": "decision", "title": title, "date": today,
                    "decided_by": decided_by}
            if project:
                meta["project"] = project.id
            meta["recorded_by"] = gitops.author_name(viewer.key, signed_as)
            meta["sensitivity"] = sensitivity
            if names is not None:
                meta["visible_to"] = names
            if access_note:
                meta["access_note"] = access_note
            meta["updated"] = today
            content = (_dump_front(meta) + f"\n# {title}\n\n## 决定\n\n{decision}\n\n## 理由\n\n{reason}\n")
            rel = f"decisions/{doc_id}.md"
            commit = gitops.write_and_commit(self.root, {rel: content}, f"决定：{title}",
                                             "record_decision", viewer.key, signed_as)
        tip = tr("decision_summary_tip", project=project.id) if project else ""
        return tr("agent.064", v0=doc_id, v1=rel, v2=commit) + tip

    def handover_role(self, project_id: str, role: str, who: str, reason: str,
                      owner_approval: str | None = None, signed_as: str | None = None) -> str:
        signed_as = self._sign(signed_as)
        role = _check_text(tr("agent.149"), role, limit=40).replace("\n", " ")
        who = _check_text(tr("agent.150"), who, limit=80).replace("\n", " ")
        reason = _check_text(tr("agent.078"), reason, limit=1000)
        owner_approval = _check_text(tr("agent.110", v0=self.owner_name), owner_approval, required=False, limit=300) or None
        with self._write():
            viewer, _, visible = self._writer()
            project = self._find(visible, "project", project_id)
            if not project:
                raise BaseError(tr("agent.144", v0=project_id, v1=tr(NOT_FOUND)))
            roles = [dict(r) for r in (project.meta.get("roles") or [])]
            current = next((r for r in roles if r["role"].strip() == role), None)
            old_who = current["who"] if current else "（空缺）"
            is_key = bool(current and current.get("key"))
            status_rel, status_now = self._status_file(project, visible)
            if is_key and not owner_approval:
                entry = (f"申请接任：「{role}」由「{old_who}」换成「{who}」，待 {self.owner_name} 批准。\n\n原因：{reason}")
                content = status_now.rstrip("\n") + "\n\n" + self._entry(viewer.key, signed_as, entry)
                commit = gitops.write_and_commit(self.root, {status_rel: content},
                                                 f"接任申请：{project.id} 的{role} → {who}",
                                                 "handover_role", viewer.key, signed_as)
                return (tr("agent.111", v0=role, v1=commit, v2=self.owner_name))
            today = self._today()
            if current:
                current["who"] = who
                current["since"] = today
            else:
                roles.append({"role": role, "who": who, "since": today})
            meta = dict(project.meta)
            meta["roles"] = roles
            meta["updated"] = today
            project_content = _dump_front(meta) + "\n" + project.body.lstrip("\n")
            entry = f"交接：「{role}」由「{old_who}」换成「{who}」。\n\n原因：{reason}"
            if owner_approval:
                entry += f"\n\n{self.owner_name} 批准：{owner_approval}"
            status_content = status_now.rstrip("\n") + "\n\n" + self._entry(viewer.key, signed_as, entry)
            commit = gitops.write_and_commit(
                self.root, {project.rel: project_content, status_rel: status_content},
                f"交接：{project.id} 的{role} → {who}", "handover_role", viewer.key, signed_as)
        return tr("agent.065", v0=role, v1=who, v2=commit)


def _normalize_dir(path: str) -> str:
    """绝对化并去掉末尾分隔符。Windows 上不分大小写，斜杠看成一样。"""
    text = path.strip()
    if os.name == "nt":
        text = text.replace("/", "\\")
    norm = os.path.normcase(os.path.abspath(text))
    if os.name == "nt":
        drive, tail = os.path.splitdrive(norm)
        tail = tail.rstrip("\\")
        return drive + ("\\" if not tail else tail)
    return norm if norm == "/" else norm.rstrip("/")


def _dir_contains(child: str, parent: str) -> bool:
    if child == parent:
        return True
    sep = "\\" if os.name == "nt" else "/"
    if parent.endswith(sep):
        return child.startswith(parent)
    return child.startswith(parent + sep)


def _section_text(body: str, name: str) -> str:
    _, sections = _parse_sections(body.replace("\r\n", "\n"))
    found = next((section for section in sections if section["name"] == name), None)
    return (found["content"] if found else "").strip()


def _clip(text: str, limit: int = 800) -> str:
    if len(text) <= limit:
        return text
    return text[:limit].rstrip() + "…"


def _join_paths(paths: list[str]) -> str:
    sep = "、" if config.lang() == "zh" else ", "
    return sep.join(f"`{item}`" for item in paths)


def _with_local_dirs(body: str, paths: list[str]) -> str:
    line = tr("here.local_dirs_line", paths=_join_paths(paths))
    text = body.replace("\r\n", "\n")
    matches = list(re.finditer(r"(?m)^## .+$", text))
    for index, matched in enumerate(matches):
        if _section_name(matched.group(0)) != "入口":
            continue
        end = matches[index + 1].start() if index + 1 < len(matches) else len(text)
        section = text[matched.end():end].rstrip("\n")
        return text[:matched.end()] + section + "\n\n" + line + "\n" + text[end:]
    return text.rstrip("\n") + "\n\n" + line + "\n"


def _recent_progress(body: str, limit: int = 2) -> list[tuple[str, str]]:
    found = []
    for entry in _status_entries(body or "")[-limit:]:
        head, _, rest = entry.partition("\n")
        matched = _STATUS_DATE.match(head)
        date = matched.group(1) if matched else ""
        first = next((line.strip() for line in rest.splitlines() if line.strip()), "")
        found.append((date, first))
    return found


def remote_message(e: BaseError, root: str | Path) -> str:
    """给远程接入（远程接口、同事网页）看的报错：存储层的失败只说一句，其余去掉数据仓库的本机路径。"""
    if isinstance(e, StorageError):
        return tr("agent.066", v0=safe_text(owner_name_at(root)))
    text = str(e)
    for form in {str(Path(root)), Path(root).as_posix()}:
        text = text.replace(form, tr("agent.112"))
    return safe_text(text)


def status_entry_dates(body: str | None) -> list[dt.date]:
    """进展标题「## YYYY-MM-DD HH:MM · …」里的日期。对不上这个样子的条目不计入。"""
    dates = []
    for entry in _status_entries(body or ""):
        matched = _STATUS_DATE.match(entry.split("\n", 1)[0])
        if not matched:
            continue
        try:
            dates.append(dt.date.fromisoformat(matched.group(1)))
        except ValueError:
            continue
    return dates


def newer_status_count(summary_as_of, status_body: str | None) -> int:
    """摘要日之后的进展条数。最近一条不比摘要日更晚时是 0；没标摘要日也是 0。"""
    as_of = _as_date(summary_as_of)
    if as_of is None or not status_body:
        return 0
    dates = status_entry_dates(status_body)
    if not dates or max(dates) <= as_of:
        return 0
    return sum(1 for day in dates if day > as_of)


def _snippet(raw: str, term: str, width: int = 60) -> str:
    text = re.sub(r"\s+", " ", raw)
    pos = text.lower().find(term)
    if pos < 0:
        return text[: width * 2]
    return text[max(0, pos - width): pos + len(term) + width]


def _how_to_open(d: Doc) -> str:
    if d.kind in ("project", "project-status", "project-links"):
        return f" → `get_project('{d.rel.split('/')[1]}')`"
    if d.kind == "asset":
        return f" → `get_asset('{d.id}')`"
    if d.kind == "handbook":
        return f" → `get_handbook('{d.id}')`"
    if d.kind == "decision":
        return " → `list_decisions()`"
    return ""
