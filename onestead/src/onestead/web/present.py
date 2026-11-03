"""远程工作台的白名单投影：先处理原文，再渲染；不输出管理对象。"""

from __future__ import annotations

from ..i18n import text as tr

import datetime as dt
from dataclasses import replace

from .. import apps, config, sites, todos
from ..redact import documents, html, safe_text, safe_url, text_fields
from ..store import Doc, Viewer, make_viewer, member_cards, member_keys
from ..textutil import _as_date, _strip_title
from . import views
from .names import actor, status_head  # 人名规则在 names.py，投影沿用原来的名字
from .policy import project_actions
from .refs import colleague_author as author
from .refs import project_ref as _project_ref


def project_ref(pid, by_id) -> dict | None:
    return _project_ref(pid, by_id, safe_text, keep_missing=False)


def project(d: Doc, viewer: Viewer, docs: list[Doc], commits: list[dict], today: dt.date) -> dict:
    allowed = {c["key"] for c in member_cards(viewer, docs)}
    all_members = member_keys(docs)
    hidden_names = {name for k, m in all_members.items() if k not in allowed for name in (k, m.title, views.short(m.title))}
    roles = [{k: r.get(k) for k in ("role", "who", "key", "since")}
             for r in d.meta.get("roles") or [] if r.get("who") not in hidden_names]
    authority = d.meta.get("authority")
    meta = {k: d.meta.get(k) for k in ("id", "title", "state", "goal", "summary_as_of", "sensitivity", "access_note")}
    meta["roles"] = roles
    if isinstance(authority, dict):
        meta["authority"] = {"label": authority.get("label")}
    projected = replace(d, meta=text_fields(meta), body="", raw="")
    activity = views.project_activity(commits).get(d.id)
    # 只拿同事看得到的进展来数条数，不把进展正文放进这一行。
    rel = d.rel.rsplit("/", 1)[0] + "/status.md"
    status = next((x for x in docs if x.kind == "project-status" and x.rel == rel
                   and viewer.can_see(x) and not x.problem), None)
    row = views.project_row(projected, today, activity, {}, status.body if status else None)
    row.pop("problem", None)
    if row["authority"]:
        row["authority"].pop("path", None)
    row["last_by"] = author(activity["last_by"], viewer, docs) if activity else None
    row["allowed_actions"] = project_actions(d, viewer)
    return row


def project_list(visible, viewer, docs, commits, today, states=()) -> list[dict]:
    rows = [project(d, viewer, docs, commits, today) for d in visible if d.kind == "project"
            and (not states or d.meta.get("state") in states)]
    return sorted(rows, key=lambda r: (views.STATE_ORDER.get(r["state"], 9), r["name"]))


def app(d: Doc, viewer: Viewer, docs, by_id) -> dict:
    m = d.meta
    url = safe_url((m.get("open") or {}).get("url"))
    links = [{"label": safe_text(l.get("label")), "target": target}
             for l in m.get("links") or [] if isinstance(l, dict) and (target := safe_url(l.get("target")))]
    row = text_fields({"id": d.id, "rel": d.rel, "title": d.title,
                       **{k: m.get(k) for k in ("one_line", "kind", "icon", "maintainer", "status_note")},
                       "listed": True, "project": project_ref(m.get("project"), by_id),
                       "updated": str(m.get("updated") or ""),
                       "registered_by": author(m.get("registered_by"), viewer, docs),
                       "open": {"url": url} if url else {}, "links": links,
                       "entry_note": "" if url else "暂未提供在线入口"})
    site = sites.site_of(d)
    if config.site_domain() and sites.can_open(site, viewer.key, docs):
        row["site_url"] = sites.url(site["slug"])
    row["notes_html"] = html(apps.notes_of(d))
    row["allowed_actions"] = {"open_url": bool(url), "open_local": False, "probe": False, "listed": False}
    return row


def todo(d: Doc, viewer: Viewer, docs, today, by_id) -> dict:
    m = d.meta
    due = _as_date(m.get("due"))
    notes = safe_text(todos.notes_of(d))
    row = text_fields({"id": d.id, "rel": d.rel, "title": d.title, "status": m.get("status", "open"),
                       "bucket": todos.bucket(d, today), "due": due.isoformat() if due else None,
                       "time": todos.time_of(m), "project": project_ref(m.get("project"), by_id),
                       "notes": notes, "created": str(m.get("created") or ""),
                       "done_at": str(m.get("done_at") or "") or None,
                       "repeat": m.get("repeat") if m.get("repeat") in todos.REPEATS else None,
                       "created_by": author(m.get("created_by"), viewer, docs)})
    if m.get("ask"):
        row.update(text_fields({key: m.get(key) for key in ("ask", *todos.ASK_FIELDS, "notified_at")}))
        row["notice"] = todos.notice(m)
        row["notified_by"] = author(m.get("notified_by"), viewer, docs) if m.get("notified_by") else None
    row["notes_html"] = html(notes)
    row["allowed_actions"] = {"edit": True}
    return row


def todo_list(visible, viewer, docs, today, by_id) -> list[dict]:
    return [todo(d, viewer, docs, today, by_id) for d in sorted((d for d in visible if d.kind == "todo"), key=todos.sort_key)]


def doc(d: Doc, viewer: Viewer, docs) -> dict:
    facts = []
    if d.kind == "decision":
        facts = [{"k": "日期", "v": safe_text(d.meta.get("date"))},
                 {"k": "谁定的", "v": author(d.meta.get("decided_by"), viewer, docs)},
                 {"k": "谁记的", "v": author(d.meta.get("recorded_by"), viewer, docs)}]
    row = text_fields({"rel": d.rel, "kind": views.KIND_LABEL.get(d.kind, d.kind), "title": d.title,
                       "facts": facts, "sensitivity": d.sensitivity, "access_note": d.meta.get("access_note")})
    row["html"] = html(_strip_title(d.body))
    return row


def doc_list(visible) -> list[dict]:
    return [text_fields({"id": d.id, "rel": d.rel, "kind_key": d.kind, "kind": views.KIND_LABEL[d.kind],
                         "title": d.title, "sensitivity": d.sensitivity,
                         "access_note": d.meta.get("access_note"), "updated": str(d.meta.get("updated") or "")})
            for d in visible if d.kind in ("handbook", "asset")]


def decisions(visible, viewer, docs, by_id) -> list[dict]:
    return sorted([text_fields({"rel": d.rel, "title": d.title, "date": str(d.meta.get("date") or ""),
                                "decided_by": author(d.meta.get("decided_by"), viewer, docs),
                                "recorded_by": author(d.meta.get("recorded_by"), viewer, docs),
                                "project": project_ref(d.meta.get("project"), by_id),
                                "sensitivity": d.sensitivity, "access_note": d.meta.get("access_note")})
                   for d in visible if d.kind == "decision"], key=lambda r: r["date"], reverse=True)


def search(visible, query) -> list[dict]:
    # 原文先处理，避免搜索摘要截断路径或密钥后识别不出来。
    safe = [replace(d, meta={"id": d.id, "title": safe_text(d.title)}, body=safe_text(d.body), raw="")
            for d in visible]
    return text_fields(views.search(safe, safe_text(query)))


def members(viewer, docs) -> list[dict]:
    return [text_fields({"key": c["key"], "name": c["title"], "title": c["title"],
                         "kind": c["kind"], "role": c["role"]}) for c in member_cards(viewer, docs)]


def agents(rows, viewer, docs) -> list[dict]:
    fields = ("key", "name", "title", "kind", "role", "can_write", "last_write", "writes_7d",
              "last_use", "last_tool", "last_via", "calls_7d")
    # 发证人给名字，按名录的可见规则取（自己是“你”，看不到的写“成员”）。
    return [text_fields({**{k: r.get(k) for k in fields},
                         "tokens": [{**t, "issued_by_name": actor(t.get("issued_by"), viewer, docs)["name"]}
                                    for t in r.get("tokens") or []],
                         "allowed_actions": {"issue_token": viewer.can_remote and make_viewer(docs, r["key"]).can_remote,
                                             "revoke_token": True, "open_token_drawer": True}}) for r in rows]


def draft(value, viewer, docs, by_id) -> dict | None:
    if not value:
        return None
    modules = []
    for m in value.get("modules") or []:
        options = dict(m["options"])
        if m["kind"] == "apps":
            visible_ids = {d.id for d in documents(viewer, docs) if d.kind == "app"}
            options["ids"] = [sid for sid in options["ids"] if sid in visible_ids]
        modules.append({"kind": m["kind"], "options": text_fields(options)})
    return text_fields({"modules": modules if value.get("modules") is not None else None,
                        "note": value.get("note"), "date": value.get("date"),
                        "version": value.get("version"),
                        "by_name": author(value.get("by"), viewer, docs),
                        "error": tr("agent.634") if value.get("error") else None,
                        "allowed_actions": {"adopt": bool(value.get("modules")), "discard": True}})
