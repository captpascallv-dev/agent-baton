"""待办：本人自己记的事，也可以让所属的 agent 帮忙整理。

每条一份文件 `todos/<id>.md`：开头标记放标题、日期、状态、关联项目，正文放备注。
有日期的会出现在网页的日历里。新增、改、做完都是一次带作者的 git 提交，动态里看得到、能撤回。
重复的待办（repeat）做完或不做了一次，同一次提交里自动加上下一次；把 repeat 改成空就不再重复。
"""

from __future__ import annotations

from .i18n import label as system_label, text as tr

from . import config

import calendar
import datetime as dt
import re

from . import gitops
from .store import BaseError, Doc, NotFound, TAIPEI, personal_owner
from .textutil import _as_date, _cell, _check_text, _dump_front, _strip_title, soften_headings

FOLDER = "todos"
STATUSES = ("open", "done", "dropped")
STATUS_NAMES = {"open": "待做", "done": "做完了", "dropped": "不做了"}
TIME_RE = re.compile(r"^([01]\d|2[0-3]):[0-5]\d$")
MAX_BATCH = 50
ASKS = ("approve", "act", "fyi")
ASK_FIELDS = {"context": 1000, "why": 400, "if_skipped": 400, "action": 400}
EDITABLE = ("title", "due", "time", "project", "notes", "status", "repeat", "ask", *ASK_FIELDS, "notified")
REPEATS = {"daily": "每天", "weekdays": "工作日", "weekly": "每周", "monthly": "每月", "yearly": "每年"}
BY_DAY = ("monthly", "yearly")  # 按几号重复的：记住原来是几号，小月落到月底，下个月回到原来那天


def _date(value, label="日期") -> dt.date | None:
    if value in (None, ""):
        return None
    d = _as_date(value)
    if d is None:
        raise BaseError(tr("agent.560", v0=system_label(label)))
    return d


def _time(value) -> str | None:
    if value in (None, ""):
        return None
    value = str(value).strip()
    if not TIME_RE.match(value):
        raise BaseError(tr("agent.561"))
    return value


def _repeat(value) -> str | None:
    if value in (None, ""):
        return None
    if value not in REPEATS:
        raise BaseError(tr("agent.562"))
    return value


def _on_day(y: int, m: int, day: int) -> dt.date:
    return dt.date(y, m, min(day, calendar.monthrange(y, m)[1]))


def _advance(d: dt.date, repeat: str, day: int | None = None) -> dt.date:
    if repeat == "daily":
        return d + dt.timedelta(days=1)
    if repeat == "weekdays":
        d += dt.timedelta(days=1)
        while d.weekday() >= 5:
            d += dt.timedelta(days=1)
        return d
    if repeat == "weekly":
        return d + dt.timedelta(days=7)
    day = day or d.day
    if repeat == "yearly":
        return _on_day(d.year + 1, d.month, day)
    y, m = (d.year + 1, 1) if d.month == 12 else (d.year, d.month + 1)
    return _on_day(y, m, day)  # 31 号遇到小月就落在月底，下个月回到 31 号


def next_due(due: dt.date, repeat: str, today: dt.date, day: int | None = None) -> dt.date:
    """下一次的日期：从这一次往后推，推到今天之后（逾期很久才勾掉，也不会补出一串过去的）。
    day 是按几号重复的原来那天（repeat_day），不给就用这一次的日子。"""
    d = _advance(due, repeat, day)
    while d <= today:
        d = _advance(d, repeat, day)
    return d


def clean(item: dict, project_ids: set[str], *, partial: bool = False, current: dict | None = None) -> dict:
    """校验输入字段，以及修改后的登记是否完整。"""
    if not isinstance(item, dict):
        raise BaseError(tr("agent.563"))
    allowed = EDITABLE if partial else EDITABLE[:-1]
    unknown = set(item) - set(allowed)
    if unknown:
        raise BaseError(tr("agent.564", v0='、'.join(sorted(unknown)), v1='、'.join(allowed)))
    out = {}
    if not partial or "title" in item:
        out["title"] = _check_text(tr("agent.565"), item.get("title"), limit=80, single_line=True)
    if "due" in item:
        out["due"] = _date(item["due"])
    if "time" in item:
        out["time"] = _time(item["time"])
    if "project" in item:
        pid = (item.get("project") or "").strip() or None
        if pid and pid not in project_ids:
            raise BaseError(tr("agent.566", v0=pid))
        out["project"] = pid
    if "notes" in item:
        out["notes"] = soften_headings(_check_text(tr("agent.567"), item.get("notes"), required=False, limit=2000))
    if "status" in item:
        if item["status"] not in STATUSES:
            raise BaseError(tr("agent.568"))
        out["status"] = item["status"]
    if "repeat" in item:
        out["repeat"] = _repeat(item["repeat"])
    if "ask" in item:
        if item["ask"] not in ASKS:
            raise BaseError(tr("ask.invalid_kind"))
        out["ask"] = item["ask"]
    for field, limit in ASK_FIELDS.items():
        if field in item:
            if not isinstance(item[field], str):
                raise BaseError(tr("ask.text_required", field=field))
            out[field] = _check_text(field, item[field], limit=limit)
    merged = (current or {}) | out
    if merged.get("ask"):
        for field in ASK_FIELDS:
            if not merged.get(field):
                raise BaseError(tr("ask.text_required", field=field))
        if merged.get("repeat"):
            raise BaseError(tr("ask.no_repeat"))
    elif any(field in merged for field in ASK_FIELDS):
        raise BaseError(tr("ask.needs_kind"))
    if "notified" in item:
        if not isinstance(item["notified"], bool):
            raise BaseError(tr("ask.notified_boolean"))
        if not merged.get("ask"):
            raise BaseError(tr("ask.notified_only"))
        out["notified"] = item["notified"]
    return out


def file_content(meta: dict, notes: str) -> str:
    order = ["id", "type", "owner", "title", "status", "due", "time", "repeat", "repeat_day", "project",
             "ask", "notified_by", "notified_at", "sensitivity", "created", *ASK_FIELDS,
             "created_by", "updated", "updated_by", "done_at", "repeat_from"]
    meta = {k: meta[k] for k in order if meta.get(k) not in (None, "")} | {
        k: v for k, v in meta.items() if k not in order and v not in (None, "")}
    body = f"\n# {meta['title']}\n" + (f"\n{notes.strip()}\n" if notes and notes.strip() else "")
    return _dump_front(meta) + body


def time_of(meta: dict) -> str | None:
    """手改文件时写成不带引号的 14:30，YAML 会读成 870（分钟），这里转回来。"""
    t = meta.get("time")
    if isinstance(t, int) and 0 <= t < 24 * 60:
        return f"{t // 60:02d}:{t % 60:02d}"
    return str(t) if t not in (None, "") else None


def notes_of(doc: Doc) -> str:
    return _strip_title(doc.body).strip()


def bucket(doc: Doc, today: dt.date) -> str:
    """分到哪一组：逾期、今天、接下来 7 天、以后、没日期、做完了、不做了。"""
    status = doc.meta.get("status", "open")
    if status != "open":
        return status
    due = _as_date(doc.meta.get("due"))
    if due is None:
        return "nodate"
    if due < today:
        return "overdue"
    if due == today:
        return "today"
    if due <= today + dt.timedelta(days=7):
        return "week"
    return "later"


BUCKET_NAMES = {"overdue": "逾期", "today": "今天", "week": "接下来 7 天", "later": "以后", "nodate": "没定日期",
                "done": "做完了", "dropped": "不做了"}
BUCKET_ORDER = ["overdue", "today", "week", "later", "nodate", "done", "dropped"]


def sort_key(doc: Doc):
    due = _as_date(doc.meta.get("due"))
    return (due or dt.date.max, time_of(doc.meta) or "99:99", str(doc.meta.get("created") or ""), doc.id)


def notice(meta: dict) -> str | None:
    if not meta.get("ask"):
        return None
    if meta.get("status", "open") in ("done", "dropped"):
        return "closed"
    return "notified" if meta.get("notified_at") else "unnotified"


def ask_sort_key(doc: Doc):
    return (notice(doc.meta) != "unnotified", str(doc.meta.get("created") or ""), doc.id)


def line(doc: Doc, today: dt.date, owner: str = "") -> str:
    m = doc.meta
    when = ""
    due = _as_date(m.get("due"))
    if due:
        when = due.isoformat() + (f" {time_of(m)}" if time_of(m) else "")
    bits = [f"`{doc.id}`", _cell(doc.title)]
    if when:
        bits.append(when)
    if m.get("repeat") in REPEATS:
        bits.append(tr("agent.569", v0=system_label(REPEATS[m['repeat']])))
    if m.get("project"):
        bits.append(tr("agent.570", v0=m['project']))
    if m.get("created_by") and m.get("created_by") != config.owner_key():
        bits.append(tr("agent.571", v0=m['created_by']))
    if m.get("ask"):
        bits.append(tr("ask.kind." + m["ask"], owner=owner))
        state = notice(m)
        bits.append(system_label(STATUS_NAMES[m["status"]]) if state == "closed" else
                    tr("ask.notified", who=m.get("notified_by"), when=m.get("notified_at"))
                    if state == "notified" else tr("ask.unnotified"))
    return "- " + " · ".join(bits)


class TodoMixin:
    """CompanyBase 的待办部分。"""

    def _todos(self, visible: list[Doc]) -> list[Doc]:
        return [d for d in visible if d.kind == "todo"]

    def list_todos(self, scope: str = "open", project_id: str | None = None) -> str:
        _, _, visible = self._ctx()
        today = self._today()
        groups = self._todo_groups(visible, scope, project_id)
        owner = self.owner_name
        if scope == "asks":
            if not groups:
                return tr("ask.empty", owner=owner) + "\n"
            out = []
            for state, docs in groups.items():
                out += [tr("ask.group." + state, count=len(docs)), ""]
                for d in docs:
                    m = d.meta
                    out.append(tr("ask.entry", id=d.id, title=_cell(d.title),
                                  kind=tr("ask.kind." + m["ask"], owner=owner),
                                  who=_cell(m.get("created_by")), date=str(m.get("created") or "")))
                    for field in ASK_FIELDS:
                        out.append("  - " + tr("ask.detail_line", label=tr("ask.detail." + field, owner=owner),
                                             value=str(m[field]).replace("\n", "\n    ")))
                    if state == "notified":
                        out.append("  - " + tr("ask.notified", who=_cell(m.get("notified_by")), when=m["notified_at"]))
                out.append("")
            return "\n".join(out + [tr("ask.hint", owner=owner)]) + "\n"
        total = sum(len(v) for v in groups.values())
        out = [tr("agent.573", v0=today.isoformat(), v1=(tr("agent.582")[today.weekday()] if config.lang() == "zh" else today.strftime("%A"))), ""]
        if not total:
            out.append(tr("agent.578"))
        for b, docs in groups.items():
            out += [f"## {system_label(BUCKET_NAMES[b])}（{len(docs)}）", ""] + [line(d, today, owner) for d in docs] + [""]
        out += ["", tr("agent.574")]
        return "\n".join(out).strip() + "\n"

    def _todo_groups(self, visible: list[Doc], scope: str = "open", project_id: str | None = None) -> dict[str, list[Doc]]:
        today = self._today()
        todos = self._todos(visible)
        if project_id:
            todos = [d for d in todos if d.meta.get("project") == project_id.strip()]
        if scope == "asks":
            groups = {}
            for d in sorted((d for d in todos if notice(d.meta) in ("unnotified", "notified")), key=ask_sort_key):
                groups.setdefault(notice(d.meta), []).append(d)
            return groups
        scopes = {"open": {"overdue", "today", "week", "later", "nodate"}, "today": {"overdue", "today"},
                  "week": {"overdue", "today", "week"}, "all": set(BUCKET_ORDER)}
        if scope not in scopes:
            raise BaseError(tr("agent.572"))
        groups: dict[str, list[Doc]] = {}
        for d in sorted(todos, key=sort_key):
            b = bucket(d, today)
            if b in scopes[scope]:
                groups.setdefault(b, []).append(d)
        return {b: groups[b] for b in BUCKET_ORDER if b in groups}

    def _new_id(self, now: dt.datetime, taken: set[str]) -> str:
        stamp = f"{now:%Y-%m-%d-%H%M%S}"
        n = 1
        while f"{stamp}-{n}" in taken or (self.root / FOLDER / f"{stamp}-{n}.md").exists():
            n += 1
        taken.add(f"{stamp}-{n}")
        return f"{stamp}-{n}"

    def add_todos(self, items: list, note: str | None = None, signed_as: str | None = None) -> str:
        ids, commit = self._add_todos_commit(items, note, signed_as)
        return tr("agent.557", v0=len(ids), v1=commit, v2='、'.join((f'`{i}`' for i in ids)))

    def _add_todos_commit(self, items: list, note: str | None = None, signed_as: str | None = None) -> tuple[list[str], str]:
        signed_as = self._sign(signed_as)
        note = _check_text(tr("agent.008"), note, required=False, limit=200).replace("\n", " ") or None
        if not isinstance(items, list) or not items:
            raise BaseError(tr("agent.575"))
        if len(items) > MAX_BATCH:
            raise BaseError(tr("agent.576", v0=MAX_BATCH))
        with self._write():
            viewer, _, visible = self._writer(operation="personal")
            project_ids = {d.id for d in visible if d.kind == "project"}
            cleaned = [clean(it, project_ids) for it in items]
            for c in cleaned:
                if c.get("repeat") and not c.get("due"):
                    raise BaseError(tr("agent.581", v0=c['title']))
            now = self._now()
            author = gitops.author_name(viewer.key, signed_as)
            files, ids, taken = {}, [], set()
            for c in cleaned:
                tid = self._new_id(now, taken)
                meta = {"id": tid, "type": "todo", "owner": viewer.workspace_owner,
                        "title": c["title"], "status": c.get("status", "open"),
                        "due": c.get("due"), "time": c.get("time"), "repeat": c.get("repeat"),
                        "repeat_day": c["due"].day if c.get("repeat") in BY_DAY else None,
                        "project": c.get("project"),
                        "sensitivity": "internal", "created": now.date(), "created_by": author}
                meta.update({k: c[k] for k in ("ask", *ASK_FIELDS) if k in c})
                if meta["status"] == "done":
                    meta["done_at"] = now.date()
                files[f"{FOLDER}/{tid}.md"] = file_content(meta, c.get("notes", ""))
                ids.append(tid)
            if len(cleaned) == 1:
                c = cleaned[0]
                subject = (tr("ask.commit_registered", title=c["title"],
                              kind=tr("ask.commit_kind." + c["ask"], lang="zh"), lang="zh")
                           if c.get("ask") else f"待办：加了「{c['title']}」")
            else:
                subject = f"待办：整理了 {len(cleaned)} 条" + (f"（{note}）" if note else "")
            commit = gitops.write_and_commit(self.root, files, subject, "add_todos", viewer.key, signed_as)
        return ids, commit

    def update_todo(self, todo_id: str, changes: dict, signed_as: str | None = None) -> str:
        tid, commit, nxt = self._update_todo_commit(todo_id, changes, signed_as)
        if commit is None:
            return tr("agent.031")
        return tr("agent.558", v0=tid, v1=commit) + (tr("agent.559", v0=nxt.isoformat()) if nxt else "。\n")

    def _update_todo_commit(self, todo_id: str, changes: dict, signed_as: str | None = None) -> tuple[str, str | None, dt.date | None]:
        signed_as = self._sign(signed_as)
        if not isinstance(changes, dict) or not changes:
            raise BaseError(tr("agent.577"))
        with self._write():
            viewer, docs, visible = self._writer(operation="personal")
            doc = next((d for d in self._todos(visible) if d.id == (todo_id or "").strip()), None)
            if not doc:
                raise NotFound(tr("agent.579", v0=todo_id))
            doc = next(d for d in docs if d.rel == doc.rel)
            project_ids = {d.id for d in visible if d.kind == "project"}
            c = clean(changes, project_ids, partial=True, current=doc.meta)
            meta = dict(doc.meta)
            meta["owner"] = personal_owner(doc)
            notes = c.pop("notes", None)
            notified = c.pop("notified", None)
            old_status = meta.get("status", "open")
            old_repeat = meta.get("repeat")
            for k, v in c.items():
                meta[k] = v
            if meta.get("repeat") and not _as_date(meta.get("due")):
                raise BaseError(tr("agent.580"))
            if "due" in c or "repeat" in c:  # 改了日期或重复方式，就按新的日子记“几号”
                meta["repeat_day"] = _as_date(meta["due"]).day if meta.get("repeat") in BY_DAY else None
            today = self._today()
            author = gitops.author_name(viewer.key, signed_as)
            if notified is True and not meta.get("notified_at"):
                meta["notified_by"] = author
                meta["notified_at"] = self._now().astimezone(TAIPEI).strftime("%Y-%m-%d %H:%M")
            elif notified is False:
                meta.pop("notified_by", None)
                meta.pop("notified_at", None)
            text = notes_of(doc) if notes is None else notes
            if notified is not None and file_content(meta, text) == doc.raw:
                return doc.id, None, None
            meta["updated"] = today
            meta["updated_by"] = author
            new_status = meta.get("status", "open")
            if new_status == "done" and old_status != "done":
                meta["done_at"] = today
            elif new_status != "done":
                meta.pop("done_at", None)
            content = file_content(meta, text)
            if content == doc.raw:
                return doc.id, None, None
            verb = {("open", "done"): "做完了", ("open", "dropped"): "不做了"}.get(
                (old_status, new_status), "重新打开了" if new_status == "open" and old_status != "open" else "改了")
            if verb == "改了" and old_repeat and not meta.get("repeat"):
                verb = "停了重复"
            if verb == "改了" and notified is True and not doc.meta.get("notified_at"):
                verb = tr("ask.commit_notified", lang="zh")
            files = {doc.rel: content}
            nxt = None
            closing = old_status == "open" and new_status in ("done", "dropped")
            already = any(d.meta.get("repeat_from") == doc.id and personal_owner(d) == meta["owner"]
                          for d in self._todos(visible))
            if closing and meta.get("repeat") and not already:  # 重新打开再勾掉，不会再加一次
                nxt = next_due(_as_date(meta["due"]), meta["repeat"], today, meta.get("repeat_day"))
                tid = self._new_id(self._now(), set())
                files[f"{FOLDER}/{tid}.md"] = file_content(
                    {"id": tid, "type": "todo", "owner": meta["owner"],
                     "title": meta["title"], "status": "open", "due": nxt,
                     "time": meta.get("time"), "repeat": meta["repeat"], "repeat_day": meta.get("repeat_day"),
                     "project": meta.get("project"),
                     "sensitivity": meta.get("sensitivity", "internal"), "created": today, "created_by": author,
                     "repeat_from": doc.id}, text)
            subject = f"待办：{verb}「{meta['title']}」" + (f"（下一次 {nxt.month}-{nxt.day:02d}）" if nxt else "")
            commit = gitops.write_and_commit(self.root, files, subject, "update_todo", viewer.key, signed_as)
        return doc.id, commit, nxt
