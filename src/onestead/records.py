"""经验和决定的状态、取代及修改。所有入口共用同一条加锁写入路径。"""

from __future__ import annotations

import datetime as dt
import re

from . import config, gitops
from .i18n import text as tr
from .redact import safe_text
from .store import BaseError, Doc, Forbidden, NotFound, member_keys
from .textutil import _as_date, _check_text, _dump_front, _strip_title, soften_headings

SECTIONS = {"lesson": {"lesson": "结论", "how": "怎么用", "why": "为什么"},
            "decision": {"decision": "决定", "reason": "理由"}}
SOURCES = ("owner", "approved", "inferred")
LESSON_FIELDS = {"lesson", "title", "how", "why", "project_id", "source", "expires", "happened", "pending",
                 "supersedes", "ref", "origin_id", "sensitivity", "visible_to", "access_note", "signed_as"}


def record_state(doc: Doc, today: dt.date) -> str:
    m = doc.meta
    if m.get("retired"):
        return "retired"
    if m.get("superseded_by"):
        return "superseded"
    if m.get("pending") is True:
        return "pending"
    expires = _as_date(m.get("expires"))
    return "expired" if expires and expires < today else "active"


def state_text(doc: Doc, today: dt.date, visible: list[Doc] | None = None) -> str:
    state = record_state(doc, today)
    text = tr("lesson.state." + state)
    target = doc.meta.get("superseded_by")
    if state == "superseded" and (visible is None or any(d.kind == doc.kind and d.id == target for d in visible)):
        text += tr("lesson.replaced_by", id=target)
    return text



def web_state(doc, today, visible):
    target = doc.meta.get("superseded_by")
    return {"record_state": record_state(doc, today),
            "superseded_by": target if any(d.kind == doc.kind and d.id == target for d in visible) else None}


def record_parts(doc: Doc) -> dict[str, str]:
    """只把系统二级标题当分节；调用者正文的二级标题写入时已降成三级。"""
    chunks = re.split(r"(?m)^## ([^\n]+)\n", doc.body)
    sections = {chunks[i].strip(): chunks[i + 1].strip() for i in range(1, len(chunks), 2)}
    return {key: sections.get(name, "") for key, name in SECTIONS[doc.kind].items()}


def record_body(kind: str, title: str, parts: dict) -> str:
    body = f"\n# {title}\n"
    for key, name in SECTIONS[kind].items():
        if parts.get(key):
            body += f"\n## {name}\n\n{parts[key]}\n"
    return body


def edit_body(doc, title, changes):
    """局部改系统分节；保留手工补充的正文和未知章节。"""
    body = doc.body
    if "title" in changes:
        body, count = re.subn(r"(?m)^# [^\n]*", lambda _: "# " + title, body, count=1)
        if not count:
            body = f"\n# {title}\n" + body
    for key, heading in SECTIONS[doc.kind].items():
        if key not in changes:
            continue
        pattern = re.compile(r"(?m)^## " + heading + r"\n[\s\S]*?(?=^## |\Z)")
        section = f"## {heading}\n\n{changes[key]}\n\n" if changes[key] else ""
        body, count = pattern.subn(lambda _: section, body, count=1)
        if not count and section:
            body = body.rstrip("\n") + "\n\n" + section
    return body.rstrip("\n") + "\n"


def clean_source(value):
    if value is None:
        return None
    sources = {"owner": "owner", "approved": "approved", "inferred": "inferred",
               "主人原话": "owner", "主人认可": "approved", "伙伴推断": "inferred"}
    if not isinstance(value, str) or value not in sources:
        raise BaseError(tr("lesson.bad_source"))
    return sources[value]


def clean_expires(value):
    if value is None:
        return None
    if not isinstance(value, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", value) or not _as_date(value):
        raise BaseError(tr("lesson.bad_date"))
    return value


def clean_flag(value, field):
    if not isinstance(value, bool):
        raise BaseError(tr("lesson.bad_bool", field=field))
    return value


def clean_text(field, value, *, required=False, limit=4000, single_line=False):
    if value is not None and not isinstance(value, str):
        raise BaseError(tr("lesson.bad_type", field=field))
    return _check_text(tr("lesson.field." + field), value, required=required, limit=limit, single_line=single_line)


def clean_supersedes(value):
    if value is None:
        return []
    if not isinstance(value, list) or not all(isinstance(x, str) and re.fullmatch(r"[a-zA-Z0-9-]+", x) for x in value):
        raise BaseError(tr("lesson.bad_supersedes"))
    return list(dict.fromkeys(value))


def clean_lesson(values: dict) -> dict:
    if not isinstance(values, dict) or values.keys() - LESSON_FIELDS:
        raise BaseError(tr("lesson.bad_fields"))
    v = dict(values)
    v["lesson"] = clean_text("lesson", v.get("lesson"), required=True)
    v["title"] = clean_text("title", v.get("title"), limit=80, single_line=True) or v["lesson"].splitlines()[0][:40]
    v["lesson"] = soften_headings(v["lesson"])
    for field in ("how", "why"):
        v[field] = soften_headings(clean_text(field, v.get(field)))
    for field, limit in (("ref", 300), ("origin_id", 120), ("access_note", 200)):
        v[field] = clean_text(field, v.get(field), limit=limit) or None
    v["source"] = clean_source(v.get("source"))
    v["expires"] = clean_expires(v.get("expires"))
    v["happened"] = clean_expires(v.get("happened"))
    v["pending"] = clean_flag(v.get("pending", False), "pending")
    v["supersedes"] = clean_supersedes(v.get("supersedes"))
    v["project_id"] = clean_text("project", v.get("project_id"), limit=120, single_line=True) or None
    if v.get("sensitivity", "internal") not in ("public", "internal", "restricted"):
        raise BaseError(tr("agent.109"))
    v.setdefault("sensitivity", "internal")
    if v.get("signed_as") is not None and not isinstance(v["signed_as"], str):
        raise BaseError(tr("lesson.bad_type", field="signed_as"))
    names = v.get("visible_to")
    if names is not None and (not isinstance(names, list) or not all(isinstance(x, str) for x in names)):
        raise BaseError(tr("lesson.bad_type", field="visible_to"))
    return v


def record_day(doc) -> str:
    """补录的条目按实际发生的日期排；没填 happened 就用记录日期。"""
    return str(doc.meta.get("happened") or doc.meta.get("date") or "")


def source_text(doc) -> str:
    source = doc.meta.get("source")
    return tr("lesson.source." + source) if source in SOURCES else tr("lesson.source.missing")


def record_order(doc):
    # 同秒撞名的 -10 应晚于 -9，而不是按字符串排在 -2 前面。
    match = re.fullmatch(r"(.+)-(\d{6})(?:-(\d+))?", doc.id)
    return (record_day(doc), str(doc.meta.get("date") or ""), match[2] if match else "",
            match[1] if match else doc.id, int(match[3] or 1) if match else 0)


def sorted_records(docs, kind, project_id=None):
    return sorted((d for d in docs if d.kind == kind and not d.problem
                   and (project_id is None or d.meta.get("project") == project_id)),
                  key=record_order, reverse=True)


def _clause(text) -> str:
    """常读里一条占一行：换行收成空格，去掉句末标点，由模板统一接句号。"""
    return " ".join(str(text or "").split()).rstrip("。．.；;，, ")


def _brief_line(doc):
    parts = record_parts(doc)
    lesson, title = _clause(parts["lesson"]), _clause(doc.title)
    # 标题是从结论截出来的（没给标题时），就只写一遍结论。
    line = "- " + (f"**{title}**：{lesson}" if title and not lesson.startswith(title) else lesson)
    for key in ("how", "why"):
        if parts[key]:
            line += tr(f"lesson.{key}_line", text=_clause(parts[key]))
    ref = tr("lesson.ref_line", ref=doc.meta["ref"]) if doc.meta.get("ref") else ""
    return line + tr("lesson.provenance", source=source_text(doc), date=record_day(doc), ref=ref)


def brief_size(doc) -> int:
    """上限按正文算：结论、怎么用、为什么三段的字数；标题、出处、日期这些排版字不算。和 list_lessons 同一口径。"""
    parts = record_parts(doc)
    return len(parts["lesson"]) + len(parts["how"]) + len(parts["why"])


def _brief_split(docs, today, cap):
    """带“为什么”的是重要或反常理的条目，新接手的最容易把它当错误去改：排最前、总是完整带出，
    即使超过上限也不砍；其余按新到旧完整带出到上限，超出的只列标题。返回 (完整行, 溢出条目, 是否超限)。"""
    current = [d for d in sorted_records(docs, "lesson") if record_state(d, today) == "active"]
    keep = [d for d in current if record_parts(d)["why"]]
    rest = [d for d in current if d not in keep]
    lines = [_brief_line(d) for d in keep]
    used, overflow = sum(map(brief_size, keep)), []
    for d in rest:
        if overflow or used + brief_size(d) > cap:
            overflow.append(d)
        else:
            lines.append(_brief_line(d))
            used += brief_size(d)
    return lines, overflow, bool(overflow) or used > cap


def brief_cap(project) -> int:
    """项目开头标记里的 lessons_brief_chars（非负整数）覆盖全局上限；没写或写坏了用全局的。"""
    value = project.meta.get("lessons_brief_chars") if project else None
    return value if type(value) is int and value >= 0 else config.lessons_brief_chars()


def brief(docs, today, max_chars=None, company=False) -> str:
    """常读：见 _brief_split。溢出的逐条列标题（标题应写成完整的一句规则），标题再占一份上限，剩下的报条数。"""
    cap = config.lessons_brief_chars() if max_chars is None else max_chars
    lines, overflow, over = _brief_split(docs, today, cap)
    shown, used = 0, 0
    for d in overflow:
        if shown and used + len(d.title) > cap:
            break
        lines.append(f"- {d.title}")
        shown, used = shown + 1, used + len(d.title)
    if len(overflow) > shown:
        lines += ["", tr("lesson.more", count=len(overflow) - shown)]
    if over:
        lines += ["", tr("lesson.hint_brief_company" if company else "lesson.hint_brief")]
    pending = sum(record_state(d, today) == "pending" for d in docs if d.kind == "lesson")
    if pending:
        lines += ["", tr("lesson.pending_count", count=pending)]
    return "\n".join(lines)


class RecordMixin:
    def _record_scope(self, viewer, docs, visible, project_id, sensitivity, visible_to):
        if viewer.assigned and not project_id:
            raise Forbidden(tr("lesson.project_required"))
        if viewer.assigned and sensitivity == "public":
            raise Forbidden(tr("agent.147"))
        if viewer.assigned and visible_to is not None:
            raise Forbidden(tr("agent.148"))
        project = self._record_project(viewer, visible, project_id)
        if sensitivity == "restricted" and viewer.assigned:
            names = list(project.meta.get("visible_to") or []) if project.sensitivity == "restricted" else [viewer.acl]
        elif sensitivity == "restricted":
            names = [k.strip() for k in (visible_to or [viewer.key]) if k.strip()]
            unknown = set(names) - (set(member_keys(docs)) - {config.owner_key()})
            if unknown:
                raise BaseError(tr("agent.162", v0='、'.join(sorted(unknown)), v1=self.owner_name))
        else:
            names = None
        self._record_restriction(project, sensitivity, names)
        return project, names

    def _record_project(self, viewer, visible, project_id):
        if not project_id:
            if viewer.assigned:
                raise Forbidden(tr("lesson.project_required"))
            return None
        project = self._find(visible, "project", project_id)
        if project is None:
            raise NotFound(tr("agent.144", v0=project_id, v1=tr("not_found_or_denied")))
        self._granted_project(viewer, project)
        return project

    @staticmethod
    def _record_restriction(project, sensitivity, names):
        if project and project.sensitivity == "restricted" and (
                sensitivity != "restricted" or not set(names or []).issubset(project.meta.get("visible_to") or [])):
            raise BaseError(tr("lesson.restricted"))

    def _supersede_files(self, kind, ids, new_id, viewer, visible, signed_as):
        files = {}
        for old_id in ids:
            old = self._find(visible, kind, old_id)
            if not old:
                raise NotFound(tr("lesson.not_found"))
            self._record_project(viewer, visible, old.meta.get("project"))
            if old.meta.get("superseded_by"):
                target = old.meta["superseded_by"]
                # 跨项目的取代目标可能已经不可见，此时不泄露其 id。
                if self._find(visible, kind, target):
                    raise BaseError(tr("lesson.already_superseded", id=old_id, target=target))
                raise BaseError(tr("lesson.already_inactive"))
            if old.meta.get("retired"):
                raise BaseError(tr("lesson.already_inactive"))
            meta = old.meta | {"superseded_by": new_id, "updated": self._today().isoformat(),
                               "updated_by": gitops.author_name(viewer.key, signed_as)}
            files[old.rel] = _dump_front(meta) + old.body
        return files

    def _lesson_hints(self, doc, visible):
        hints = []
        if not doc.meta.get("source"):
            hints.append(tr("lesson.hint_source"))
        if len(record_parts(doc)["lesson"]) > 500:
            hints.append(tr("lesson.hint_long"))
        project = doc.meta.get("project")
        group = [d for d in visible if d.kind == "lesson" and not d.problem and d.meta.get("project") == project]
        if _brief_split(group, self._today(), brief_cap(self._find(visible, "project", project) if project else None))[2]:
            hints.append(tr("lesson.hint_brief" if project else "lesson.hint_brief_company"))
        return hints

    def _prepare_lesson(self, values, viewer, docs, visible, signed_as):
        project, names = self._record_scope(viewer, docs, visible, values.get("project_id"),
                                             values["sensitivity"], values.get("visible_to"))
        today = self._today().isoformat()
        stamp = f"{today}-{viewer.key}-{self._now():%H%M%S}"
        occupied = {d.id for d in docs if d.kind == "lesson"}
        doc_id, n = stamp, 2
        while doc_id in occupied or (self.root / "lessons" / f"{doc_id}.md").exists():
            doc_id, n = f"{stamp}-{n}", n + 1
        meta = {"id": doc_id, "type": "lesson", "title": values["title"], "date": today}
        if project:
            meta["project"] = project.id
        for key in ("happened", "source", "expires", "pending", "supersedes", "ref", "origin_id"):
            if values.get(key):
                meta[key] = values[key]
        meta |= {"recorded_by": gitops.author_name(viewer.key, signed_as), "sensitivity": values["sensitivity"]}
        if names is not None:
            meta["visible_to"] = names
        if values.get("access_note"):
            meta["access_note"] = values["access_note"]
        meta |= {"updated": today, "updated_by": gitops.author_name(viewer.key, signed_as)}
        rel = f"lessons/{doc_id}.md"
        body = record_body("lesson", values["title"], values)
        doc = Doc(rel, "lesson", meta, body, _dump_front(meta) + body)
        files = self._supersede_files("lesson", values["supersedes"], doc_id, viewer, visible, signed_as)
        files[rel] = doc.raw
        return doc, files

    def record_lesson(self, lesson, title=None, how=None, why=None, project_id=None, source=None,
                      expires=None, pending=False, supersedes=None, ref=None, sensitivity="internal",
                      visible_to=None, access_note=None, signed_as=None, happened=None):
        result = self._record_lesson_commit(lesson=lesson, title=title, how=how, why=why, project_id=project_id,
                    happened=happened,
                    source=source, expires=expires, pending=pending, supersedes=supersedes, ref=ref,
                    sensitivity=sensitivity, visible_to=visible_to, access_note=access_note, signed_as=signed_as)
        return tr("lesson.recorded", **{k: result[k] for k in ("id", "path", "commit")}) + "\n".join(result["hints"])

    def _record_lesson_commit(self, **values):
        v = clean_lesson(values)
        signed_as = self._sign(v.get("signed_as"))
        with self._write():
            viewer, docs, visible = self._writer(operation="record_lesson")
            doc, files = self._prepare_lesson(v, viewer, docs, visible, signed_as)
            commit = gitops.write_and_commit(self.root, files, f"经验：{doc.title}", "record_lesson", viewer.key, signed_as)
            _, _, visible = self._ctx()
            hints = self._lesson_hints(doc, visible)
        return {"id": doc.id, "path": doc.rel, "commit": commit, "hints": hints}

    def import_lessons(self, items, project=None, signed_as=None):
        if not isinstance(items, list) or not 1 <= len(items) <= 200:
            raise BaseError(tr("lesson.import_size"))
        # 整批先清洗，跳过的条目也不能隐藏不合法输入。
        cleaned = []
        for i, item in enumerate(items, 1):
            try:
                if not isinstance(item, dict):
                    raise BaseError(tr("lesson.bad_fields"))
                v = clean_lesson({"project_id": project, **item})
                cleaned.append(v)
            except BaseError as error:
                raise BaseError(tr("lesson.import_error", index=i, error=error)) from None
        signed_as = self._sign(signed_as if signed_as is not None else cleaned[0].get("signed_as"))
        with self._write():
            viewer, docs, visible = self._writer(operation="record_lesson")
            origins = {d.meta["origin_id"]: d for d in docs if d.kind == "lesson" and d.meta.get("origin_id")}
            files, created, skipped = {}, [], []
            for i, v in enumerate(cleaned, 1):
                try:
                    item_sign = self._sign(v.get("signed_as", signed_as))
                    self._require_signer(viewer)
                    self._record_scope(viewer, docs, visible, v.get("project_id"), v["sensitivity"], v.get("visible_to"))
                    origin = v.get("origin_id")
                    if origin in origins:
                        old = origins[origin]
                        if not viewer.can_see(old):
                            raise NotFound(tr("lesson.not_found"))
                        self._record_project(viewer, visible, old.meta.get("project"))
                        skipped.append({"origin_id": origin, "id": old.id})
                        continue
                    doc, changes = self._prepare_lesson(v, viewer, docs, visible, item_sign)
                    files.update(changes)
                    # 下一条看见本批前面已准备的取代和新记录，仍不落盘。
                    for old in docs:
                        if old.rel in changes:
                            from .store import split_front
                            old.meta, old.body, _ = split_front(changes[old.rel])
                            old.raw = changes[old.rel]
                    docs.append(doc)
                    visible = viewer.visible(docs)
                    created.append(doc.id)
                    if origin:
                        origins[origin] = doc
                except BaseError as error:
                    raise BaseError(tr("lesson.import_error", index=i, error=error)) from None
            commit = (gitops.write_and_commit(self.root, files, f"经验：导入 {len(created)} 条", "import_lessons",
                                              viewer.key, signed_as) if created else None)
        return {"created": created, "skipped": skipped, "commit": commit}

    def _lesson_brief(self, visible, project_id, viewer=None):
        docs = [d for d in visible if d.kind == "lesson" and d.meta.get("project") == project_id and not d.problem]
        project = self._find(visible, "project", project_id) if project_id else None
        result = brief(docs, self._today(), brief_cap(project), company=project_id is None)
        return safe_text(result) if viewer and (viewer.remote or viewer.assigned) else result

    def list_lessons(self, project_id=None, include_inactive=False, limit=50):
        return self._list_records("lesson", project_id, include_inactive, limit)

    def _list_records(self, kind, project_id, include_inactive, limit):
        viewer, _, visible = self._ctx()
        include_inactive = clean_flag(include_inactive, "include_inactive")
        records = sorted_records(visible, kind, project_id)
        pending = [d for d in records if record_state(d, self._today()) == "pending"]
        shown = [d for d in records if record_state(d, self._today()) != "pending"
                 and (include_inactive or record_state(d, self._today()) == "active")]
        limit = max(1, min(int(limit), 100))
        out = [tr("lesson.list." + kind) + tr("lesson.count", count=len(shown)), ""]
        groups = sorted({d.meta.get("project") or "" for d in shown[:limit]}) if kind == "lesson" else [None]
        for group in groups:
            if kind == "lesson":
                project = self._find(visible, "project", group)
                out += ["## " + (project.title if project else group or tr("lesson.company")), ""]
            for d in shown[:limit]:
                if kind == "lesson" and (d.meta.get("project") or "") != group:
                    continue
                out += [f"### {d.meta.get('date')} · {d.title} (`{d.id}`)",
                        state_text(d, self._today(), visible), self._restricted_banner(d)]
                if kind == "decision":
                    info = tr("agent.098", v0=d.meta.get("decided_by", tr("agent.161")),
                              v1=d.meta.get("recorded_by", tr("agent.161")))
                    out.append(info + (tr("agent.135", v0=d.meta["project"]) if d.meta.get("project") else ""))
                else:
                    out.append(tr("lesson.record_info", source=source_text(d), author=d.meta.get("recorded_by", "")))
                    if d.meta.get("happened"):
                        out.append(tr("lesson.happened_info", date=d.meta["happened"]))
                    if d.meta.get("ref"):
                        out.append(tr("lesson.ref_info", ref=d.meta["ref"]))
                out += ["", re.sub(r"(?m)^## ", "#### ", _strip_title(d.body)), ""]
        if len(shown) > limit:
            out.append(tr("lesson.list_more", count=len(shown) - limit))
        if pending:
            out += ["## " + tr("lesson.pending_heading"), *[f"- {d.title} (`{d.id}`)" for d in pending[:limit]]]
            if len(pending) > limit:
                out.append(tr("lesson.list_more", count=len(pending) - limit))
        if not shown and not pending:
            out.append(tr("lesson.empty"))
        result = "\n".join(out).strip() + "\n"
        return safe_text(result) if viewer.remote or viewer.assigned else result

    def lessons_data(self, project=None, include_inactive=False, max_chars=None, company_only=False):
        viewer, _, visible = self._ctx()
        clean_flag(include_inactive, "include_inactive")
        # company_only 只给公司共用经验（不带项目的），预算也只算它们；和具体项目同时给是自相矛盾。
        if clean_flag(company_only, "company_only") and project:
            raise BaseError(tr("lesson.company_only_project"))
        cap = (brief_cap(self._find(visible, "project", project) if project else None)
               if max_chars is None else max_chars)
        if type(cap) is not int or cap < 0:
            raise BaseError(tr("lesson.bad_budget"))
        result, pending, rows = [], [], []
        for d in sorted_records(visible, "lesson", project):
            if company_only and d.meta.get("project"):
                continue
            state = record_state(d, self._today())
            if state == "pending":
                pending.append({"id": d.id, "title": d.title})
            elif state == "active" or include_inactive:
                rows.append((d, state, record_parts(d)))
        # 同常读：带“为什么”的总是给全，其余按新到旧给到上限。
        used = sum(len(p["lesson"]) + len(p["how"]) + len(p["why"]) for _, _, p in rows if p["why"])
        cut = False
        keep = set()
        for d, _, parts in rows:
            if parts["why"]:
                keep.add(d.id)
                continue
            size = len(parts["lesson"]) + len(parts["how"])
            if cut or used + size > cap:
                cut = True
                continue
            used += size
            keep.add(d.id)
        truncated = len(rows) - len(keep)
        for d, state, parts in rows:
            if d.id not in keep:
                continue
            row = {key: d.meta.get(key) for key in ("ref", "source", "date", "happened", "expires", "project", "recorded_by")}
            target = d.meta.get("superseded_by")
            row |= {"id": d.id, "title": d.title, **parts, "state": state,
                    "supersedes": [x for x in d.meta.get("supersedes", []) if self._find(visible, "lesson", x)],
                    "superseded_by": target if self._find(visible, "lesson", target or "") else None}
            for key in ("date", "happened", "expires"):
                row[key] = str(row[key]) if row[key] else None
            result.append(row)
        more = [{"id": d.id, "title": d.title} for d, _, _ in rows if d.id not in keep]
        data = {"lessons": result, "pending": pending, "truncated": truncated, "more": more}
        if viewer.remote or viewer.assigned:
            from .redact import text_fields
            data = text_fields(data)
        return data

    def update_record(self, record_id, changes, signed_as=None):
        _, commit = self._update_record_commit(record_id, changes, signed_as)
        return tr("lesson.updated", commit=commit) if commit else tr("agent.031")

    def _update_record_commit(self, record_id, changes, signed_as=None):
        """改一条经验或决定，返回 (改之前的记录, 提交号)；没有变化时提交号是 None。"""
        signed_as = self._sign(signed_as)
        if not isinstance(record_id, str) or not isinstance(changes, dict):
            raise BaseError(tr("lesson.bad_fields"))
        with self._write():
            viewer, _, visible = self._writer(operation="update_record")
            doc = self._resolve_record(visible, record_id)
            prepared = self._prepare_record_update(doc, changes, viewer, visible, signed_as)
            if prepared is None:
                return doc, None
            action = "撤下" if changes.get("retired") is True else "确认" if changes.get("pending") is False else "改"
            noun = "经验" if doc.kind == "lesson" else "决定"
            commit = gitops.write_and_commit(self.root, {doc.rel: prepared["content"]},
                        f"{noun}：{action}「{prepared['title']}」", "update_record", viewer.key, signed_as)
        return doc, commit

    @staticmethod
    def _resolve_record(visible, record_id):
        matches = [d for d in visible if d.kind in SECTIONS and record_id in (d.id, d.rel, d.rel.removesuffix(".md"))]
        if not matches:
            raise NotFound(tr("lesson.not_found"))
        if len(matches) > 1:
            raise BaseError(tr("lesson.ambiguous"))
        return matches[0]

    def _prepare_record_update(self, doc, changes, viewer, visible, signed_as):
        """验证并准备一条修改；批量过目复用它，所有条目通过后才一次落盘。"""
        self._record_project(viewer, visible, doc.meta.get("project"))
        allowed = {"title", "source", "expires", "happened", "pending", "retired", "note", *SECTIONS[doc.kind]}
        if doc.kind == "lesson":
            allowed |= {"ref", "project"}
        if changes.keys() - allowed:
            raise BaseError(tr("lesson.bad_fields"))
        meta, parts = dict(doc.meta), record_parts(doc)
        for key, value in changes.items():
            if key in SECTIONS[doc.kind]:
                parts[key] = soften_headings(clean_text(key, value, required=key in ("lesson", "decision", "reason")))
            elif key in ("title", "ref", "project"):
                value = clean_text(key, value, required=key == "title", limit={"title": 80, "ref": 300, "project": 120}[key], single_line=key != "ref")
                if value:
                    meta[key] = value
                else:
                    meta.pop(key, None)
            elif key in ("source", "expires", "happened"):
                value = clean_source(value) if key == "source" else clean_expires(value)
                if value is None:
                    meta.pop(key, None)
                else:
                    meta[key] = value
            elif key == "pending":
                if clean_flag(value, key):
                    meta[key] = True
                elif meta.get(key) is not False:
                    meta.pop(key, None)
            elif key == "retired":
                if clean_flag(value, key):
                    if not meta.get("retired"):
                        meta |= {"retired": self._today().isoformat(), "retired_by": gitops.author_name(viewer.key, signed_as)}
                else:
                    for field in ("retired", "retired_by", "retire_note"):
                        meta.pop(field, None)
        if "note" in changes:
            if changes.get("retired") is not True:
                raise BaseError(tr("lesson.note_retired"))
            meta["retire_note"] = clean_text("note", changes["note"], limit=200)
        project = self._record_project(viewer, visible, meta.get("project"))
        self._record_restriction(project, meta["sensitivity"], meta.get("visible_to"))
        # 比较语义，避免仅格式变化或 false 与缺字段等价时制造提交。
        if meta == doc.meta and parts == record_parts(doc):
            return None
        meta |= {"updated": self._today().isoformat(), "updated_by": gitops.author_name(viewer.key, signed_as)}
        body_keys = changes.keys() & {"title", *SECTIONS[doc.kind]}
        body_changes = {key: parts[key] for key in body_keys if key != "title"}
        if "title" in body_keys:
            body_changes["title"] = meta["title"]
        body = edit_body(doc, meta["title"], body_changes) if body_keys else doc.body
        return {"content": _dump_front(meta) + body, "title": meta["title"]}
