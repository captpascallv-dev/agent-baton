"""网页上以 主人 的名义做的管理：改资料、项目、决定、成员，新建资料和成员，批或不批关键角色的接任申请。

只有网页界面（is_owner 的读者）能用，MCP 和命令行用不了：这些改动要么只该 主人 做（敏感级别、批准），
要么是他亲手改文字。每次保存都是一次以 主人 为作者的提交，动态里看得到、能撤回。
打开编辑时带一个版本号（文件内容的指纹），保存时文件已经被别人改过就不覆盖，请他重新打开。
"""

from __future__ import annotations

from .i18n import text as tr

from . import config

import hashlib
import re

from . import feishu, gitops, skillscan, skills, tokens
from .store import LEVELS, BaseError, Doc, check_meta, clean_project_paths, is_archived, member_keys
from .handover import pending_handovers
from .textutil import _as_date, _check_text, _dump_front, _strip_title

EDIT_KINDS = {"handbook": "手册", "asset": "资料", "project": "项目", "decision": "决定", "person": "成员"}
STATES = ("进行中", "维护中", "构想中", "暂停", "已结束", "历史")
ID_RE = re.compile(r"^[a-z0-9][a-z0-9-]{1,40}$")
MAX_BODY = 60000
REJECT_ENTRY = "不批接任：「{role}」由「{old}」换成「{who}」，{owner_name} 没有批准。"


def _path_lines(value) -> str:
    if not isinstance(value, list):
        return ""
    return "\n".join(item.strip() for item in value if isinstance(item, str) and item.strip())


def version_of(raw: str) -> str:
    """文件内容的指纹：打开编辑时记下，保存时比一下，防止覆盖别人刚写的。"""
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]


def _fields(d: Doc, members: dict[str, Doc], projects: list[Doc], assets: list[Doc], skill_rows: list[dict]) -> list[dict]:
    m = d.meta
    out = [{"key": "title", "label": "标题", "type": "text", "value": d.title}]
    if d.kind == "project":
        now = m.get("state") or ""
        out += [{"key": "state", "label": "状态", "type": "select", "value": now,
                 "options": ([] if now in STATES else [now]) + list(STATES), "labels": {"": "没写状态"}},
                {"key": "goal", "label": "一句话", "type": "text", "value": m.get("goal") or ""},
                {"key": "paths", "label": "本机目录", "type": "lines", "value": _path_lines(m.get("paths"))},
                {"key": "summary_as_of", "label": "现状摘要截至", "type": "date", "value": str(m.get("summary_as_of") or "")}]
    elif d.kind == "person":
        out += [{"key": "role", "label": "角色", "type": "text", "value": m.get("role") or ""},
                {"key": "kind", "label": "是人还是 agent", "type": "select", "value": m.get("kind") or "agent",
                 "options": ["human", "agent"], "labels": {"human": "人", "agent": "agent"},
                 "locked": m.get("agent_key") == config.owner_key()},
                {"key": "can_write", "label": "能用底座的写接口", "type": "bool", "value": bool(m.get("can_write")),
                 "locked": m.get("agent_key") == config.owner_key()},
                {"key": "connect", "label": "怎么接入的", "type": "text", "value": m.get("connect") or ""}]
        if m.get("agent_key") != config.owner_key():
            out += _access_fields(m, members, projects, assets, skill_rows)
    elif d.kind == "decision":
        out += [{"key": "date", "label": "日期", "type": "date", "value": str(m.get("date") or "")},
                {"key": "decided_by", "label": "谁定的", "type": "text", "value": m.get("decided_by") or ""},
                {"key": "project", "label": "关联的项目", "type": "select", "value": m.get("project") or "",
                 "options": [""] + [p.id for p in projects], "labels": {p.id: p.title for p in projects}}]
    return out


def _human_owners(members: dict[str, Doc], me: str | None) -> list[str]:
    """owner 只能选择有效的人，包括 主人。"""
    return sorted(k for k, v in members.items()
                  if v.meta.get("kind") == "human" and "owner" not in v.meta and k != me
                  and not is_archived(v.meta))


def _access_fields(m: dict, members: dict[str, Doc], projects: list[Doc], assets: list[Doc], skill_rows: list[dict]) -> list[dict]:
    owners = _human_owners(members, m.get("agent_key"))
    owner = members.get(m.get("owner"))
    inherited = owner.meta if owner and m.get("owner") != config.owner_key() and owner.meta.get("access_mode", "assigned") == "assigned" else {}
    return [
        {"key": "remote_enabled", "label": tr("agent.282"), "type": "bool", "value": m.get("remote_enabled", False)},
        {"key": "access_mode", "label": tr("agent.283"), "type": "select", "value": m.get("access_mode", "assigned"),
         "options": ["assigned", "full"], "labels": {"assigned": tr("agent.288"), "full": tr("agent.289")}},
        {"key": "projects", "label": tr("agent.284"), "type": "multi", "show_if": "access_mode", "show_value": "assigned",
         "value": list(m.get("projects", inherited.get("projects", [])) or []), "options": [p.id for p in projects], "labels": {p.id: p.title for p in projects}},
        {"key": "assets", "label": tr("agent.285"), "type": "multi", "show_if": "access_mode", "show_value": "assigned",
         "value": list(m.get("assets", inherited.get("assets", [])) or []), "options": [a.id for a in assets], "labels": {a.id: a.title for a in assets}},
        {"key": "skills", "label": tr("agent.286"), "type": "multi", "show_if": "access_mode", "show_value": "assigned",
         "value": list(m.get("skills", inherited.get("skills", [])) or []), "options": [s["id"] for s in skill_rows],
         "labels": {s["id"]: s["name"] for s in skill_rows}},
        {"key": "owner", "label": tr("agent.287"), "type": "select",
         "show_if": "kind", "show_value": "agent", "value": m.get("owner") or "", "options": [""] + owners,
         "labels": {"": tr("agent.290"), **{k: members[k].title for k in owners}}},
    ]


def _pick_ids(value, visible: list[Doc], kind: str, name: str, *, known: set[str] | None = None) -> list[str]:
    if known is None:
        known = {x.id for x in visible if x.kind == kind}
    if value is not None and not (isinstance(value, list) and all(isinstance(x, str) for x in value)):
        raise BaseError(tr("agent.299", v0=name))
    out = []
    for x in value or []:
        x = str(x).strip()
        if x not in known:
            raise BaseError(tr("agent.302", v0=x, v1=name))
        if x not in out:
            out.append(x)
    return out


def _clean_level(changes: dict, keys: set[str]) -> dict:
    out = {}
    if "sensitivity" in changes:
        level = changes["sensitivity"]
        if level not in LEVELS:
            raise BaseError(tr("agent.137"))
        out["sensitivity"] = level
        if level == "restricted":
            names = [str(k).strip() for k in (changes.get("visible_to") or []) if str(k).strip()]
            bad = [k for k in names if k not in keys or k == config.owner_key()]
            if bad:
                raise BaseError(tr("agent.318", v0='、'.join(bad)))
            out["visible_to"] = names
        else:
            out["visible_to"] = None
    if "access_note" in changes:
        out["access_note"] = _check_text(tr("agent.108"), changes.get("access_note"), required=False, limit=200).replace("\n", " ") or None
    return out


class ManageMixin:
    """CompanyBase 的管理部分（只给网页上的 主人 用）。"""

    def _owner(self):
        viewer, docs, visible = self._writer()
        if not viewer.is_owner:
            raise BaseError(tr("agent.303", v0=self.owner_name))
        return viewer, docs, visible

    def _skill_options(self, docs: list[Doc]) -> list[dict]:
        data = skillscan.cached_scan()
        scanned = {i["dir"] for h in data["harnesses"] for i in h["items"]}
        scanned |= {i["dir"] for i in data["shared"]["items"]}
        rows = skillscan.summarize(data, skills.records_of(docs))
        return [{"id": r["id"], "name": r["name"] if r["registered"] else r["skill_name"] or r["id"]}
                for r in rows if r["id"] in scanned]

    def doc_source(self, rel: str) -> dict:
        viewer, docs, visible = self._ctx()
        if not viewer.is_owner:
            raise BaseError(tr("agent.304", v0=self.owner_name))
        d = next((x for x in visible if x.rel == rel), None)
        if not d or d.kind not in EDIT_KINDS:
            raise BaseError(tr("agent.305"))
        members = member_keys(docs)
        projects = sorted((x for x in visible if x.kind == "project"), key=lambda x: x.title)
        assets = sorted((x for x in visible if x.kind == "asset"), key=lambda x: x.title)
        return {"rel": d.rel, "kind": d.kind, "kind_name": EDIT_KINDS[d.kind], "version": version_of(d.raw),
                "fields": _fields(d, members, projects, assets, self._skill_options(docs) if d.kind == "person" else []),
                "body": _strip_title(d.body).strip("\n"),
                "sensitivity": d.meta.get("sensitivity", "internal"), "visible_to": d.meta.get("visible_to") or [],
                "access_note": d.meta.get("access_note") or "",
                "members": [{"key": k, "name": v.title} for k, v in members.items() if k != config.owner_key()],
                "problem": d.problem}

    def save_doc(self, rel: str, version: str, changes: dict, body: str | None) -> dict:
        if not isinstance(changes, dict):
            raise BaseError(tr("agent.306"))
        with self._write():
            viewer, docs, visible = self._owner()
            d = next((x for x in visible if x.rel == rel), None)
            if not d or d.kind not in EDIT_KINDS:
                raise BaseError(tr("agent.305"))
            if d.problem:
                raise BaseError(tr("agent.319", v0=d.problem))
            if version != version_of(d.raw):
                raise BaseError(tr("agent.320"))
            members = member_keys(docs)
            keys = set(members)
            meta = dict(d.meta)
            title = _check_text(tr("agent.077"), changes.get("title", d.title), limit=80, single_line=True)
            meta["title"] = title
            meta |= _clean_level(changes, keys)
            warnings = []
            if d.kind == "project":
                if "state" in changes and changes["state"] != (d.meta.get("state") or ""):
                    if changes["state"] not in STATES:
                        raise BaseError(tr("agent.140", v0='、'.join(STATES)))
                    meta["state"] = changes["state"]
                if "goal" in changes:
                    meta["goal"] = _check_text(tr("agent.332"), changes["goal"], limit=200).replace("\n", " ")
                if "summary_as_of" in changes:
                    when = _as_date(changes["summary_as_of"]) if changes["summary_as_of"] else None
                    if changes["summary_as_of"] and when is None:
                        raise BaseError(tr("agent.330"))
                    meta["summary_as_of"] = when
                if "paths" in changes:
                    cleaned = clean_project_paths(changes.get("paths"))
                    if cleaned:
                        meta["paths"] = cleaned
                    else:
                        meta.pop("paths", None)
                if meta.get("sensitivity") == "restricted":
                    loose = [x.title for x in visible if x.kind == "decision" and x.meta.get("project") == d.id
                             and x.sensitivity != "restricted"]
                    if loose:
                        warnings.append(tr("project_restricted_warning", count=len(loose), names='、'.join(loose[:5])))
            elif d.kind == "person":
                is_owner = meta.get("agent_key") == config.owner_key()
                if "role" in changes:
                    meta["role"] = _check_text(tr("agent.149"), changes["role"], required=False, limit=120).replace("\n", " ") or None
                if "connect" in changes:
                    meta["connect"] = _check_text(tr("agent.337"), changes["connect"], required=False, limit=300).replace("\n", " ") or None
                if "kind" in changes and not is_owner:
                    if changes["kind"] not in ("human", "agent"):
                        raise BaseError(tr("agent.333"))
                    meta["kind"] = changes["kind"]
                if "can_write" in changes and not is_owner:
                    if not isinstance(changes["can_write"], bool):
                        raise BaseError(tr("agent.334"))
                    meta["can_write"] = changes["can_write"]
                if not is_owner:
                    self._apply_access(meta, changes, members, visible)
                if meta.get("kind") != "human" and any(x.kind == "person" and x.meta.get("owner") == meta.get("agent_key") for x in docs):
                    raise BaseError(tr("agent.331"))
            elif d.kind == "decision":
                if "date" in changes:
                    when = _as_date(changes["date"])
                    if when is None:
                        raise BaseError(tr("agent.335"))
                    meta["date"] = when.isoformat()
                if "decided_by" in changes:
                    meta["decided_by"] = _check_text(tr("agent.338"), changes["decided_by"], limit=40).replace("\n", " ")
                if "project" in changes:
                    pid = (changes["project"] or "").strip() or None
                    if pid and not self._find(visible, "project", pid):
                        raise BaseError(tr("agent.011", v0=pid))
                    meta["project"] = pid
                project = self._find(visible, "project", meta.get("project") or "") if meta.get("project") else None
                if project is not None and project.sensitivity == "restricted":
                    allowed = set(project.meta.get("visible_to") or [])
                    if meta.get("sensitivity") != "restricted" or not set(meta.get("visible_to") or []).issubset(allowed):
                        raise BaseError(tr("agent.336"))
            for k in ("visible_to", "access_note", "role", "connect", "project", "summary_as_of", "goal", "owner"):
                if k in meta and meta[k] in (None, ""):
                    meta.pop(k)
            meta["updated"] = self._today()
            text = _strip_title(d.body).strip("\n") if body is None else _check_text(tr("agent.143"), body, required=False, limit=MAX_BODY)
            content = _dump_front(meta) + f"\n# {title}\n" + (f"\n{text.strip()}\n" if text.strip() else "")
            if content == d.raw:
                return {"commit": None, "message": tr("agent.307"), "warnings": []}
            what = [] if title == d.title else [f"改名为「{title}」"]
            if meta.get("sensitivity") != d.meta.get("sensitivity"):
                what.append(f"敏感级别改成 {meta['sensitivity']}")
            subject = f"修改：{EDIT_KINDS[d.kind]}「{d.title}」" + (f"（{'，'.join(what)}）" if what else "")
            if d.kind == "person":
                disabled = d.meta.get("remote_enabled") is True and meta.get("remote_enabled") is False
                moved = d.meta.get("owner") != meta.get("owner")
                revoke = {meta["agent_key"]} if disabled or moved else set()
                if disabled and d.meta.get("kind") == "human":
                    revoke |= {x.meta["agent_key"] for x in docs if x.kind == "person"
                               and x.meta.get("owner") == meta["agent_key"] and x.meta.get("agent_key")}
                # 先撤回旧凭据，再改变主人或接入资格；与发口令共用数据写锁。
                n = sum(tokens.revoke_key(k) for k in sorted(revoke))
                if n:
                    kinds = tr("token_feishu_web" if feishu.enabled() else "token_web")
                    warnings.append(tr("credentials_revoked", count=n, kind=kinds))
            commit = gitops.write_and_commit(self.root, {d.rel: content}, subject, "网页界面", config.owner_key(), None)
        return {"commit": commit, "message": tr("agent.291"), "warnings": warnings}

    def _apply_access(self, meta: dict, changes: dict, members: dict[str, Doc], visible: list[Doc]) -> None:
        """接入、授权分别保存；关闭远程不清空列表，也不改变 access_mode。"""
        for key, default in (("remote_enabled", False), ("access_mode", "assigned")):
            meta[key] = changes.get(key, meta.get(key, default))
        if "owner" in changes:
            if changes["owner"] == "" and meta.get("kind") == "human":
                meta.pop("owner", None)
            else:
                meta["owner"] = changes["owner"]
        for key, kind, label in (("projects", "project", "项目"), ("assets", "asset", "资产"), ("skills", "skill", "Skill")):
            if key in changes:
                if not isinstance(changes[key], list):
                    raise BaseError(tr("agent.299", v0=label))
                known = {r["id"] for r in self._skill_options(visible)} if key == "skills" else None
                meta[key] = _pick_ids(changes[key], visible, kind, label, known=known)
        problem = check_meta(meta, "person")
        if problem:
            raise BaseError(problem)
        if meta["kind"] == "agent" and meta.get("owner") not in _human_owners(members, meta.get("agent_key")):
            raise BaseError(tr("agent.308"))

    def share_skill(self, skill_id: str, public: bool) -> dict:
        if not isinstance(public, bool):
            raise BaseError(tr("agent.309"))
        with self._write():
            _, docs, _ = self._owner()
            if not isinstance(skill_id, str) or not skills.ID_RE.fullmatch(skill_id):
                raise BaseError(tr("agent.321"))
            row = next((r for r in self._skill_options(docs) if r["id"] == skill_id), None)
            if row is None:
                raise BaseError(tr("agent.321"))
            d = next((x for x in docs if x.kind == "skill" and x.id == skill_id), None)
            if d and d.problem:
                raise BaseError(tr("agent.322"))
            scanned = skillscan.summarize(skillscan.cached_scan(), skills.records_of(docs))
            source = next(r for r in scanned if r["id"] == skill_id)
            meta = dict(d.meta) if d else {"id": skill_id, "type": "skill",
                "title": source["skill_name"] or skill_id,
                "one_line": (source["description"] or source["skill_name"] or skill_id)[:160],
                "registered_by": config.owner_key()}
            meta |= {"sensitivity": "public" if public else "internal", "updated": self._today(), "updated_by": config.owner_key()}
            meta.pop("visible_to", None)
            content = skills.file_content(meta, skills.notes_of(d) if d else "")
            if d and content == d.raw:
                return {"commit": None, "message": tr("agent.307")}
            rel = d.rel if d else f"skills/{skill_id}.md"
            commit = gitops.write_and_commit(self.root, {rel: content},
                f"Skill：{'公开共享' if public else '取消公开共享'}「{meta.get('title') or skill_id}」",
                "网页界面", config.owner_key(), None)
        return {"commit": commit, "message": tr("agent.292")}

    # ---------- 口令清点（只给网页上的 主人；不进 git，见 tokens.py） ----------

    def token_list(self) -> list[dict]:
        self._owner()
        return tokens.list_tokens()

    # ---------- 飞书登录：待登记、对上、解绑（只给网页上的 主人） ----------

    def _render_person(self, d: Doc, meta: dict) -> str:
        """按现有正文重写成员文件。空字符串去掉，false 和空列表留着。"""
        title = _check_text(tr("agent.077"), d.title, limit=80, single_line=True)
        meta = dict(meta)
        meta["title"] = title
        meta["updated"] = self._today()
        meta = {k: v for k, v in meta.items() if v not in (None, "")}
        problem = check_meta(meta, "person")
        if problem:
            raise BaseError(problem)
        text = _strip_title(d.body).strip("\n")
        return _dump_front(meta) + f"\n# {title}\n" + (f"\n{text.strip()}\n" if text.strip() else "")

    def _owned_agent_records(self, docs: list[Doc], owner_key: str) -> list[Doc]:
        """名下还没停用的 agent，包括标记坏了、现在登不了录的记录。"""
        found = []
        for d in docs:
            if d.kind != "person" or d.meta.get("kind") != "agent" or d.meta.get("owner") != owner_key:
                continue
            if is_archived(d.meta):
                continue
            found.append(d)
        return found

    def archive_member(self, key: str) -> dict:
        """停用一个成员：不删文件。人的名下 agent 先收口令；能安全重写的才标成停用。"""
        key = str(key or "").strip()
        if not key or key == config.owner_key():
            raise BaseError(tr("agent.310", v0=self.owner_name))
        with self._write():
            _, docs, _ = self._owner()
            member = member_keys(docs).get(key)
            if member is None:
                raise BaseError(tr("agent.323"))
            if is_archived(member.meta):
                raise BaseError(tr("agent.324"))
            targets = [member]
            if member.meta.get("kind") == "human":
                targets += self._owned_agent_records(docs, key)
            files = {}
            archived = []
            not_archived = []
            use_feishu = feishu.enabled()
            for d in targets:
                meta = dict(d.meta)
                meta["archived"] = True
                meta["remote_enabled"] = False
                # 未启用的绑定不从网页改动；停用和收回全部凭据仍照旧。
                if use_feishu:
                    meta.pop("feishu_open_id", None)
                try:
                    content = self._render_person(d, meta)
                except BaseError as error:
                    not_archived.append({"key": d.meta.get("agent_key"), "title": d.title, "reason": str(error)})
                    continue
                files[d.rel] = content
                archived.append(d)
            # 先收回再落文件。坏记录写不回去，口令也先收回。
            revoked = 0
            for d in targets:
                agent_key = d.meta.get("agent_key")
                if isinstance(agent_key, str) and agent_key.strip():
                    revoked += tokens.revoke_key(agent_key)
            commit = gitops.write_and_commit(self.root, files, f"停用成员「{member.title}」",
                                             "网页界面", config.owner_key(), None) if files else None
        names = [d.title for d in archived if d is not member]
        message = tr("member_deactivated")
        if not_archived:
            shown = "、".join(str(item["title"]) for item in not_archived)
            message += tr("member_not_archived", names=shown)
        return {"commit": commit, "revoked": revoked, "agents": names,
                "not_archived": not_archived, "message": message}

    def restore_member(self, key: str) -> dict:
        """去掉停用。远程接入保持关闭，旧口令不复活，名下 agent 要各自恢复。"""
        key = str(key or "").strip()
        if not key or key == config.owner_key():
            raise BaseError(tr("agent.311", v0=self.owner_name))
        with self._write():
            _, docs, _ = self._owner()
            member = member_keys(docs).get(key)
            if member is None:
                raise BaseError(tr("agent.323"))
            if not is_archived(member.meta):
                raise BaseError(tr("agent.325"))
            meta = dict(member.meta)
            meta.pop("archived", None)
            meta["remote_enabled"] = False
            content = self._render_person(member, meta)
            commit = gitops.write_and_commit(self.root, {member.rel: content}, f"恢复成员「{member.title}」",
                                             "网页界面", config.owner_key(), None)
        return {"commit": commit, "message": tr("agent.293")}

    def _commit_person(self, d: Doc, meta: dict, subject: str) -> str:
        """改成员的开头标记并提交。调用方已持有写锁，并且已经过 _owner。"""
        content = self._render_person(d, meta)
        if content == d.raw:
            return ""
        return gitops.write_and_commit(self.root, {d.rel: content}, subject, "网页界面", config.owner_key(), None)

    def feishu_pending(self) -> list[dict]:
        self._owner()
        return feishu.list_pending()

    def feishu_bind(self, open_id: str, key: str) -> dict:
        open_id = str(open_id or "").strip()
        key = str(key or "").strip()
        if not open_id or not key:
            raise BaseError(tr("agent.312"))
        with self._write():
            _, docs, _ = self._owner()
            member = member_keys(docs).get(key)
            if (member is None or key == config.owner_key() or is_archived(member.meta)
                    or member.meta.get("kind") != "human" or member.meta.get("remote_enabled") is not True):
                raise BaseError(tr("agent.326"))
            if any(d.kind == "person" and d.rel != member.rel and d.meta.get("feishu_open_id") == open_id for d in docs):
                raise BaseError(tr("agent.327"))
            if not feishu.contains(open_id):
                raise BaseError(tr("agent.314"))
            old = member.meta.get("feishu_open_id")
            meta = dict(member.meta)
            meta["feishu_open_id"] = open_id
            # 换绑前先收掉飞书会话和由飞书登录留下的网页会话，再写成员文件。
            if isinstance(old, str) and old.strip() and old != open_id:
                tokens.revoke_key(key, "feishu")
                tokens.revoke_web(key, "feishu")
            commit = self._commit_person(member, meta, f"成员「{member.title}」对上飞书账号")
        feishu.dismiss(open_id)
        return {"commit": commit or None, "message": tr("agent.295")}

    def feishu_unbind(self, key: str) -> dict:
        key = str(key or "").strip()
        if not key:
            raise BaseError(tr("agent.313"))
        with self._write():
            _, docs, _ = self._owner()
            member = member_keys(docs).get(key)
            if member is None:
                raise BaseError(tr("agent.323"))
            meta = dict(member.meta)
            had = "feishu_open_id" in meta
            meta.pop("feishu_open_id", None)
            # 先收会话后落成员变更，沿用数据锁到凭据锁的顺序；长期口令不受影响。
            n = tokens.revoke_key(key, "feishu") + tokens.revoke_web(key, "feishu")
            commit = self._commit_person(member, meta, f"成员「{member.title}」解除飞书绑定") if had else ""
        return {"commit": commit or None, "revoked": n, "message": tr("agent.296") if had else tr("agent.297")}

    def feishu_dismiss(self, open_id: str) -> dict:
        self._owner()
        open_id = str(open_id or "").strip()
        if not open_id or not feishu.dismiss(open_id):
            raise BaseError(tr("agent.314"))
        return {"ok": True}

    def create_doc(self, kind: str, fields: dict, body: str) -> dict:
        if kind not in ("asset", "handbook", "person"):
            raise BaseError(tr("agent.315"))
        if not isinstance(fields, dict):
            raise BaseError(tr("agent.316"))
        with self._write():
            viewer, docs, visible = self._owner()
            keys = set(member_keys(docs))
            doc_id = str(fields.get("id") or "").strip()
            if not ID_RE.match(doc_id):
                raise BaseError(tr("agent.139"))
            title = _check_text(tr("agent.077"), fields.get("title"), limit=80, single_line=True)
            folder = {"asset": "assets", "handbook": "handbook", "person": "people"}[kind]
            rel = f"{folder}/{doc_id}.md"
            if (self.root / rel).exists() or any(x.id == doc_id and x.kind == kind for x in docs):
                raise BaseError(tr("agent.328", v0=doc_id))
            meta = {"id": doc_id, "type": kind, "title": title}
            if kind == "person":
                if doc_id in keys:
                    raise BaseError(tr("agent.329", v0=doc_id))
                meta |= {"kind": fields.get("kind", "human"),
                         "role": _check_text(tr("agent.149"), fields.get("role"), required=False, limit=120).replace("\n", " ") or None,
                         "agent_key": doc_id, "can_write": fields.get("can_write", False),
                         "connect": _check_text(tr("agent.337"), fields.get("connect"), required=False, limit=300).replace("\n", " ") or None}
            level = _clean_level({"sensitivity": fields.get("sensitivity", "internal"), "visible_to": fields.get("visible_to"),
                                  "access_note": fields.get("access_note")}, keys)
            meta |= {"sensitivity": level["sensitivity"]}
            if level.get("visible_to") is not None:
                meta["visible_to"] = level["visible_to"]
            if level.get("access_note"):
                meta["access_note"] = level["access_note"]
            if kind == "person":
                self._apply_access(meta, fields, member_keys(docs), visible)
            meta["updated"] = self._today()
            meta = {k: v for k, v in meta.items() if v not in (None, "")}
            text = _check_text(tr("agent.143"), body, required=False, limit=MAX_BODY)
            content = _dump_front(meta) + f"\n# {title}\n" + (f"\n{text.strip()}\n" if text.strip() else "")
            commit = gitops.write_and_commit(self.root, {rel: content}, f"新建：{EDIT_KINDS[kind]}「{title}」",
                                             "网页界面", config.owner_key(), None)
        return {"commit": commit, "rel": rel}

    def decide_handover(self, project_id: str, role: str, to: str, approve: bool, note: str | None = None) -> str:
        """批或不批一条还在等的接任申请。批：按申请换人，进展里写明是 主人 在网页上批的。"""
        # 申请怎么认，和网页“待批准”用 handover.pending_handovers 这一份。
        note = _check_text(tr("agent.008"), note, required=False, limit=300) or None
        _, _, visible = self._owner()
        by_id = {x.id: x for x in visible if x.kind == "project"}
        p = next((x for x in pending_handovers(visible, by_id)
                  if x["project"] == project_id and x["role"] == role and x["to"] == to), None)
        if not p:
            raise BaseError(tr("agent.317"))
        stamp = self._now().strftime("%Y-%m-%d %H:%M")
        if approve:
            return self.handover_role(project_id, role, to, f"{self.owner_name} 在网页上批准了 {p['head']} 提的接任申请。" + (f"\n\n{note}" if note else ""),
                                      owner_approval=f"在网页“待批准”里点了批准（{stamp}）")
        with self._write():
            viewer, _, visible = self._owner()
            project = self._find(visible, "project", project_id)
            status_rel, status_now = self._status_file(project, visible)
            entry = REJECT_ENTRY.format(role=role, old=p["from"], who=to, owner_name=self.owner_name) + (f"\n\n{note}" if note else "")
            content = status_now.rstrip("\n") + "\n\n" + self._entry(viewer.key, None, entry)
            commit = gitops.write_and_commit(self.root, {status_rel: content}, f"接任不批：{project_id} 的{role} → {to}",
                                             "网页界面", config.owner_key(), None)
        return tr("agent.298", v0=commit)
