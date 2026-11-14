"""现场计算经验摘要；过目与呈报时间属于调用身份对应的人。"""

from __future__ import annotations

import datetime as dt
import math
import re
import unicodedata
from pathlib import Path, PureWindowsPath

from . import gitops, records
from .i18n import text as tr
from .redact import text_fields
from .store import BaseError, Forbidden, TAIPEI, member_keys, project_path_list
from .textutil import _as_date, _dump_front

SIMILARITY_THRESHOLD = 0.5
BATCH_SIZE = 10


def bigrams(text):
    text = ''.join(c for c in text if not c.isspace() and not unicodedata.category(c).startswith('P'))
    return {text[i:i + 2] for i in range(len(text) - 1)}


def similarity(left, right):
    a, b = bigrams(left), bigrams(right)
    return len(a & b) / len(a | b) if a or b else 0.0


def _similar_groups(docs, *, cross_project):
    """相近边的连通分量；升规则只比较有项目且项目不同的经验。"""
    grams = [bigrams(d.title + records.record_parts(d)["lesson"]) for d in docs]
    edges = [set() for _ in docs]
    for i, left in enumerate(docs):
        for j in range(i + 1, len(docs)):
            lp, rp = left.meta.get("project"), docs[j].meta.get("project")
            scope = bool(lp and rp and lp != rp) if cross_project else lp == rp
            if scope and grams[i] | grams[j] and len(grams[i] & grams[j]) / len(grams[i] | grams[j]) >= SIMILARITY_THRESHOLD:
                edges[i].add(j)
                edges[j].add(i)
    seen, groups = set(), []
    for i in range(len(docs)):
        if i in seen or not edges[i]:
            continue
        todo, group = [i], set()
        while todo:
            j = todo.pop()
            if j in group:
                continue
            group.add(j)
            todo.extend(edges[j] - group)
        seen |= group
        groups.append([docs[j] for j in sorted(group)])
    return groups


def _timestamp(value):
    if not isinstance(value, str):
        return None
    try:
        return dt.datetime.strptime(value, "%Y-%m-%d %H:%M").replace(tzinfo=TAIPEI)
    except ValueError:
        return None


def _record_time(doc):
    date = _as_date(doc.meta.get("date"))
    if date is None:
        return None
    match = re.search(r"-(\d{6})(?:-\d+)?$", doc.id)
    clock = dt.time()
    if match:
        try:
            clock = dt.datetime.strptime(match[1], "%H%M%S").time()
        except ValueError:
            pass  # 手工 id 的数字后缀不一定是时钟；只有日期时按当天零点计算。
    return dt.datetime.combine(date, clock, TAIPEI)


def _broken_ref(doc, projects):
    # 一条可以指几处（用“；”、“;”或换行隔开），目录也算；任何一处找不到就报。
    refs = [r.strip() for r in re.split(r"[；;\n]", doc.meta.get("ref") or "") if r.strip()]
    project = projects.get(doc.meta.get("project"))
    paths = project_path_list(project.meta.get("paths")) if project else []

    def missing(ref):
        if re.match(r"^[A-Za-z][A-Za-z0-9+.-]*:", ref) and not PureWindowsPath(ref).is_absolute():
            return False
        if Path(ref).is_absolute() or PureWindowsPath(ref).is_absolute():
            return not Path(ref).exists()
        # 没有项目目录就无法解释相对指针；不借进程 cwd 猜位置。
        return bool(paths) and not any((Path(folder) / ref).exists() for folder in paths)

    return any(missing(ref) for ref in refs)


class DigestMixin:
    @staticmethod
    def _digest_human(viewer, docs):
        if not viewer.known or not viewer.valid:
            return None
        human = member_keys(docs).get(viewer.workspace_owner)
        return human if human and human.meta.get("kind") == "human" else None

    def _digest_data(self, viewer, docs, visible, batch=1):
        if type(batch) is not int or batch < 1:
            raise BaseError(tr("digest.bad_batch"))
        now = self._now().astimezone(TAIPEI)
        human = self._digest_human(viewer, docs)
        reviewed = _timestamp(human.meta.get("lessons_reviewed_at")) if human else None
        since = reviewed or now - dt.timedelta(days=7)
        projects = {d.id: d for d in visible if d.kind == "project"}
        all_records = sorted((d for d in visible if d.kind in records.SECTIONS and not d.problem),
                             key=records.record_order)

        def row(doc):
            project = doc.meta.get("project")
            source = doc.meta.get("source")
            return {"id": doc.id, "record_id": doc.rel.removesuffix(".md"), "kind": doc.kind,
                    "project": project, "project_title": projects[project].title if project in projects else project or tr("digest.common"),
                    "title": doc.title, "conclusion": records.record_parts(doc).get(doc.kind, "")[:120],
                    "source": source, "source_label": records.source_text(doc),
                    "state": records.record_state(doc, now.date())}

        new = [d for d in all_records if (stamp := _record_time(d)) and since < stamp <= now]
        counts = {}
        for d in new:
            pid = d.meta.get("project")
            counts.setdefault(pid, {"project": pid, "project_title": row(d)["project_title"], "lessons": 0, "decisions": 0})
            counts[pid]["lessons" if d.kind == "lesson" else "decisions"] += 1
        judgments = []
        for d in all_records:
            if records.record_state(d, now.date()) == "pending":
                date = _as_date(d.meta.get("date"))
                judgments.append({"category": "pending", **row(d), "waiting_days": max(0, (now.date() - date).days) if date else None})
        for d in new:
            # 主人原话和主人认可的他都点过头了，不再让他看；推断和没填出处的才列。
            if records.record_state(d, now.date()) == "active" and d.meta.get("source") not in ("owner", "approved"):
                judgments.append({"category": "inferred", **row(d)})
        active_lessons = [d for d in all_records if d.kind == "lesson" and records.record_state(d, now.date()) == "active"]
        for group in _similar_groups(active_lessons, cross_project=True):
            judgments.append({"category": "promotion", "records": [row(d) for d in group], "suggestion": tr("digest.promote")})

        tasks = {key: [] for key in ("expired", "broken_refs", "duplicates", "why_overflow", "brief_overflow")}
        for d in all_records:
            state = records.record_state(d, now.date())
            if state == "expired":
                tasks["expired"].append({**row(d), "suggestion": tr("digest.fix_expired")})
            if not viewer.remote and state not in ("retired", "superseded") and _broken_ref(d, projects):
                tasks["broken_refs"].append({**row(d), "ref": d.meta["ref"], "suggestion": tr("digest.fix_ref")})
        for group in _similar_groups(active_lessons, cross_project=False):
            tasks["duplicates"].append({"records": [row(d) for d in group], "suggestion": tr("digest.fix_duplicates")})
        for pid in sorted({d.meta.get("project") or "" for d in active_lessons}):
            cap = records.brief_cap(projects.get(pid))
            group = [d for d in active_lessons if (d.meta.get("project") or "") == pid]
            why = [d for d in group if records.record_parts(d)["why"]]
            size = sum(map(records.brief_size, why))
            scope = {"project": pid or None, "project_title": row(group[0])["project_title"], "limit": cap}
            if size > cap:
                tasks["why_overflow"].append({**scope, "chars": size, "records": [row(d) for d in why], "suggestion": tr("digest.fix_why")})
            if records._brief_split(group, now.date(), cap)[2]:
                tasks["brief_overflow"].append({**scope, "suggestion": tr("digest.fix_brief")})
        batches = max(1, math.ceil(len(judgments) / BATCH_SIZE))
        selected = [dict(item, number=i) for i, item in enumerate(judgments[(batch - 1) * BATCH_SIZE:batch * BATCH_SIZE], 1)]
        data = {"since": since.strftime("%Y-%m-%d %H:%M"), "as_of": now.strftime("%Y-%m-%d %H:%M"),
                "human": viewer.workspace_owner, "human_name": human.title if human else viewer.workspace_owner or self.owner_name,
                "new_records": sorted(counts.values(), key=lambda r: r["project"] or ""),
                "judgments": selected, "judgment_count": len(judgments), "batch": batch,
                "batches": batches, "remaining_batches": max(0, batches - batch),
                "maintenance": tasks, "maintenance_counts": {k: len(v) for k, v in tasks.items()},
                "maintenance_count": sum(map(len, tasks.values())), "commit": None}
        data["instructions"] = tr("digest.instructions", owner=data["human_name"])
        return text_fields(data) if viewer.remote or viewer.assigned else data

    def lesson_digest_data(self, batch=1, notify=False, signed_as=None):
        records.clean_flag(notify, "notify")
        if not notify:
            return self._digest_data(*self._ctx(), batch)
        signed_as = self._sign(signed_as)
        with self._write():
            viewer, docs, visible = self._writer(operation="personal")
            human = self._digest_human(viewer, docs)
            if human is None:
                raise Forbidden(tr("digest.human_required"))
            data = self._digest_data(viewer, docs, visible, batch)
            meta = human.meta | {"lessons_notified_at": data["as_of"]}
            content = _dump_front(meta) + human.body
            if content != human.raw:
                data["commit"] = gitops.write_and_commit(self.root, {human.rel: content},
                    "经验：开始呈报摘要", "lesson_digest", viewer.key, signed_as)
        return data

    def lesson_digest(self, batch=1, notify=False, signed_as=None):
        return render_digest(self.lesson_digest_data(batch, notify, signed_as))

    def _digest_hint(self, viewer, docs, visible):
        human = self._digest_human(viewer, docs)
        if human is None:
            return ""
        now = self._now().astimezone(TAIPEI)
        reviewed = _timestamp(human.meta.get("lessons_reviewed_at"))
        notified = _timestamp(human.meta.get("lessons_notified_at"))
        if reviewed and now - reviewed < dt.timedelta(days=7):
            return ""
        if notified and (reviewed is None or notified > reviewed) and now - notified < dt.timedelta(days=7):
            return ""
        data = self._digest_data(viewer, docs, visible)
        if not data["judgment_count"] and not data["maintenance_count"]:
            return ""
        return tr("digest.company_hint", owner=human.title, n=data["judgment_count"], m=data["maintenance_count"])

    def review_lessons(self, confirm=None, retire=None, done=False, signed_as=None):
        result = self._review_lessons_commit(confirm, retire, done, signed_as)
        return tr("digest.reviewed", confirmed=len(result["confirmed"]), retired=len(result["retired"]),
                  done=tr("digest.done") if done else "", commit=result["commit"] or tr("agent.031"))

    def _review_lessons_commit(self, confirm=None, retire=None, done=False, signed_as=None):
        records.clean_flag(done, "done")
        if confirm is not None and (not isinstance(confirm, list) or not all(isinstance(x, str) for x in confirm)):
            raise BaseError(tr("digest.bad_confirm"))
        if retire is not None and (not isinstance(retire, list) or not all(isinstance(x, dict) and isinstance(x.get("id"), str)
                and not x.keys() - {"id", "note"} for x in retire)):
            raise BaseError(tr("digest.bad_retire"))
        if not confirm and not retire and not done:
            raise BaseError(tr("digest.empty_review"))
        signed_as = self._sign(signed_as)
        with self._write():
            viewer, docs, visible = self._writer(operation="personal")
            human = self._digest_human(viewer, docs)
            if human is None:
                raise Forbidden(tr("digest.human_required"))
            if confirm or retire:
                self._writer(operation="update_record")
            edits = {}
            confirmed, retired = [], []
            for record_id, changes in ([(x, {"pending": False}) for x in confirm or []]
                    + [(x["id"], {"retired": True, **({"note": x["note"]} if "note" in x else {})}) for x in retire or []]):
                doc = self._resolve_record(visible, record_id)
                if doc.rel in edits:
                    raise BaseError(tr("digest.duplicate_review"))
                prepared = self._prepare_record_update(doc, changes, viewer, visible, signed_as)
                edits[doc.rel] = prepared["content"] if prepared else None
                (retired if "retired" in changes else confirmed).append(doc.rel.removesuffix(".md"))
            files = {rel: content for rel, content in edits.items() if content is not None}
            reviewed_at = None
            if done:
                reviewed_at = self._now().astimezone(TAIPEI).strftime("%Y-%m-%d %H:%M")
                meta = human.meta | {"lessons_reviewed_at": reviewed_at}
                meta.pop("lessons_notified_at", None)
                content = _dump_front(meta) + human.body
                if content != human.raw:
                    files[human.rel] = content
            commit = gitops.write_and_commit(self.root, files, "经验：主人过目", "review_lessons", viewer.key, signed_as) if files else None
        return {"confirmed": confirmed, "retired": retired, "reviewed_at": reviewed_at, "commit": commit}


def render_digest(data):
    out = [tr("digest.heading", since=data["since"], now=data["as_of"]), "", tr("digest.a")]
    for scope in data["new_records"]:
        out.append(tr("digest.new_line", project=scope["project_title"], lessons=scope["lessons"], decisions=scope["decisions"]))
    if not data["new_records"]:
        out.append(tr("digest.empty"))
    out += ["", tr("digest.b", n=data["judgment_count"], batch=data["batch"], batches=data["batches"])]
    for item in data["judgments"]:
        wait = tr("digest.waiting", days=item["waiting_days"]) if item.get("waiting_days") is not None else ""
        out.append(tr("digest.item", number=item["number"], category=tr("digest.category." + item["category"]), wait=wait))
        for row in item.get("records", [item]):
            out.append(tr("digest.record_line", **{k: row[k] for k in ("id", "project_title", "title", "conclusion", "source_label")}, kind=tr("lesson.kind" if row["kind"] == "lesson" else "digest.decision")))
        if item.get("suggestion"):
            out.append(item["suggestion"])
    if not data["judgments"]:
        out.append(tr("digest.empty"))
    out += [tr("digest.remaining", count=data["remaining_batches"]), "", tr("digest.c", count=data["maintenance_count"])]
    for key, tasks in data["maintenance"].items():
        out.append(tr("digest.task_count", label=tr("digest.task." + key), count=len(tasks)))
        for task in tasks:
            for row in task.get("records", [task]):
                out.append("- " + (f"`{row['record_id']}` {row['title']}" if "record_id" in row else row["project_title"]))
            out.append(task["suggestion"])
    out += ["", data["instructions"]]
    return "\n".join(out) + "\n"
