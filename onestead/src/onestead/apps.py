"""应用广场：agent 做出来、主人 会打开用的东西（网页、本机程序、bot、插件、命令行工具、后台服务）。

每个应用一份文件 `apps/<id>.md`：开头标记放名称、类型、怎么打开、代码在哪、谁维护、属于哪个项目，
正文放说明。agent 做完一个应用用 `register_app` 登记上架；改名、换图标、下架用 `update_app`。
"""

from __future__ import annotations

from .i18n import text as tr

import re

from . import gitops
from .store import BaseError, Doc
from .redact import safe_url
from .textutil import _cell, _check_text, _dump_front, _strip_title

FOLDER = "apps"
KINDS = ("网页", "本机程序", "bot", "浏览器插件", "命令行工具", "后台服务")
ID_RE = re.compile(r"^[a-z0-9][a-z0-9-]{1,40}$")
OPEN_KEYS = ("url", "path", "command", "shortcut")
TEXT_FIELDS = {"title": 40, "one_line": 120, "maintainer": 120, "icon": 8, "code_path": 300,
               "probe_process": 80, "status_note": 200}
EDITABLE = ("title", "one_line", "kind", "project", "maintainer", "icon", "open", "code_path",
            "probe_process", "status_note", "links", "listed", "notes")


def _url_ok(url: str) -> bool:
    return bool(re.match(r"^https?://[^\s]+$", url))


def clean(item: dict, project_ids: set[str], *, partial: bool = False) -> dict:
    if not isinstance(item, dict):
        raise BaseError(tr("agent.006"))
    unknown = set(item) - set(EDITABLE) - ({"id"} if not partial else set())
    if unknown:
        raise BaseError(tr("agent.007", v0='、'.join(sorted(unknown)), v1='、'.join(EDITABLE)))
    out: dict = {}
    for key, limit in TEXT_FIELDS.items():
        required = key in ("title", "one_line") and not partial
        if key in item or required:
            value = _check_text({"title": "名称", "one_line": "一句话"}.get(key, key), item.get(key),
                                required=required, limit=limit,
                                single_line=key in ("title", "one_line")).replace("\n", " ")
            out[key] = value or None
    if "kind" in item or not partial:
        if item.get("kind") not in KINDS:
            raise BaseError(tr("agent.010", v0='、'.join(KINDS)))
        out["kind"] = item["kind"]
    if "project" in item:
        pid = (item.get("project") or "").strip() or None
        if pid and pid not in project_ids:
            raise BaseError(tr("agent.011", v0=pid))
        out["project"] = pid
    if "open" in item:
        given = item.get("open") or {}
        if not isinstance(given, dict) or set(given) - set(OPEN_KEYS):
            raise BaseError(tr("agent.012", v0='、'.join(OPEN_KEYS)))
        opened = {}
        for k in OPEN_KEYS:
            v = _check_text(f"open.{k}", given.get(k), required=False, limit=500).replace("\n", " ") or None
            if k == "url" and v and not _url_ok(v):
                raise BaseError(tr("agent.032"))
            opened[k] = v
        out["open"] = {k: v for k, v in opened.items() if v}
    if "links" in item:
        links = item.get("links") or []
        if not isinstance(links, list) or not all(isinstance(x, dict) and x.get("label") and x.get("target") for x in links):
            raise BaseError(tr("agent.013"))
        out["links"] = [{"label": _check_text(tr("agent.033"), x["label"], limit=60), "target": _check_text(tr("agent.034"), x["target"], limit=500),
                         **({"note": _check_text(tr("agent.046"), x["note"], limit=200)} if x.get("note") else {})} for x in links]
    if "listed" in item:
        if not isinstance(item["listed"], bool):
            raise BaseError(tr("agent.014"))
        out["listed"] = item["listed"]
    if "notes" in item:
        out["notes"] = _check_text(tr("agent.008"), item.get("notes"), required=False, limit=3000)
    return out


def file_content(meta: dict, notes: str) -> str:
    order = ["id", "type", "title", "one_line", "kind", "listed", "icon", "project", "maintainer", "open",
             "code_path", "probe_process", "status_note", "links", "sensitivity", "registered_by", "updated", "updated_by"]
    keep = {k: meta[k] for k in order if meta.get(k) not in (None, "", {}, [])}
    keep |= {k: v for k, v in meta.items() if k not in order and v not in (None, "", {}, [])}
    body = f"\n# {keep['title']}\n" + (f"\n{notes.strip()}\n" if notes and notes.strip() else "")
    return _dump_front(keep) + body


def notes_of(doc: Doc) -> str:
    return _strip_title(doc.body).strip()


def how_to_open(meta: dict, remote: bool = False) -> str:
    o = meta.get("open") or {}
    parts = []
    if o.get("url") and (not remote or safe_url(o["url"])):
        parts.append(tr("agent.015", v0=o['url']))
    if remote:  # 快捷方式、路径、命令都在这台电脑上，远程接入时不告诉同事
        return parts[0] if parts else tr("agent.001")
    if o.get("shortcut"):
        parts.append(tr("agent.016", v0=o['shortcut']))
    if o.get("path"):
        parts.append(tr("agent.017", v0=o['path']))
    if o.get("command"):
        parts.append(tr("agent.018", v0=o['command']))
    return "；".join(parts) or tr("agent.000")


class AppMixin:
    """CompanyBase 的应用广场部分。"""

    def _apps(self, visible: list[Doc]) -> list[Doc]:
        return sorted((d for d in visible if d.kind == "app"), key=lambda d: (d.meta.get("listed") is False, d.title))

    def list_apps(self, kind: str | None = None, project_id: str | None = None, include_unlisted: bool = False) -> str:
        viewer, _, visible = self._ctx()
        apps = self._apps(visible)
        # 下架应用只给本机全读身份看；远程和按授权读即使要求 include_unlisted 也不列。
        if viewer.remote or viewer.assigned or not include_unlisted:
            apps = [d for d in apps if d.meta.get("listed", True)]
        if kind:
            apps = [d for d in apps if d.meta.get("kind") == kind]
        if project_id:
            apps = [d for d in apps if d.meta.get("project") == project_id.strip()]
        if not apps:
            return tr("agent.009")
        rows = [tr("agent.019"), "", tr("agent.020"), "|---|---|---|---|---|---|---|"]
        for d in apps:
            m = d.meta
            name = d.title + (tr("agent.043") if m.get("listed") is False else "")
            rows.append(f"| `{d.id}` | {_cell(name)} | {_cell(m.get('kind'))} | {_cell(m.get('one_line'))} | "
                        f"{_cell(m.get('project') or '')} | {_cell(m.get('maintainer') or '')} | {_cell(how_to_open(m, viewer.remote))} |")
        return "\n".join(rows) + tr("agent.002")

    def get_app(self, app_id: str) -> str:
        viewer, _, visible = self._ctx()
        d = next((x for x in self._apps(visible) if x.id == (app_id or "").strip()), None)
        if d is not None and (viewer.remote or viewer.assigned) and d.meta.get("listed") is False:
            d = None
        if not d:
            raise BaseError(tr("agent.021", v0=app_id))
        m = d.meta
        out = [f"# {d.title}", "", tr("agent.022", v0=m.get('one_line', '')), tr("agent.023", v0=m.get('kind')),
               tr("agent.035", v0=tr("agent.049") if m.get('listed') is False else tr("agent.050")) + (f"；{m['status_note']}" if m.get("status_note") else ""),
               tr("agent.024", v0=m.get('project') or tr("agent.047")), tr("agent.025", v0=m.get('maintainer') or tr("agent.048")),
               tr("agent.026", v0=how_to_open(m, viewer.remote))]
        if not viewer.remote:  # 代码目录和本机文件链接不告诉同事，网址照给
            out.append(tr("agent.036", v0=m.get('code_path') or tr("agent.048")))
        for link in m.get("links") or []:
            if viewer.remote:
                if not safe_url(link["target"]):
                    continue
            out.append(tr("agent.044", v0=link['label'], v1=link['target']) + (f"（{link['note']}）" if link.get("note") else ""))
        from .sites import summary
        site = summary(d)
        if site:
            out += [tr("agent.037", v0=site['url']), tr("agent.038", v0=site['status']),
                    tr("agent.039", v0=site['visibility']),
                    tr("agent.040", v0=site.get('current') or tr("agent.051"), v1=site.get('pending') or tr("agent.051"))]
        notes = notes_of(d)
        if notes:
            out += ["", notes]
        return "\n".join(out) + "\n"

    def register_app(self, app: dict, signed_as: str | None = None) -> str:
        signed_as = self._sign(signed_as)
        if not isinstance(app, dict):
            raise BaseError(tr("agent.027"))
        app_id = str(app.get("id") or "").strip()
        if not ID_RE.match(app_id):
            raise BaseError(tr("agent.028"))
        with self._write():
            viewer, _, visible = self._writer()
            if (self.root / FOLDER / f"{app_id}.md").exists():
                raise BaseError(tr("agent.041", v0=app_id))
            c = clean(app, {d.id for d in visible if d.kind == "project"})
            notes = c.pop("notes", "")
            meta = {"id": app_id, "type": "app", "listed": True, **c, "sensitivity": "internal",
                    "registered_by": gitops.author_name(viewer.key, signed_as), "updated": self._today()}
            commit = gitops.write_and_commit(self.root, {f"{FOLDER}/{app_id}.md": file_content(meta, notes)},
                                             f"应用：登记了「{meta['title']}」", "register_app", viewer.key, signed_as)
        return tr("agent.003", v0=app_id, v1=commit)

    def register_apps(self, apps: list, note: str, signed_as: str | None = None) -> str:
        """一次登记多个（首批入库用），一次提交。"""
        signed_as = self._sign(signed_as)
        note = _check_text(tr("agent.008"), note, limit=200).replace("\n", " ")
        if not isinstance(apps, list) or not apps:
            raise BaseError(tr("agent.029"))
        with self._write():
            viewer, _, visible = self._writer()
            pids = {d.id for d in visible if d.kind == "project"}
            files = {}
            for app in apps:
                app_id = str(app.get("id") or "").strip()
                if not ID_RE.match(app_id) or (self.root / FOLDER / f"{app_id}.md").exists() or f"{FOLDER}/{app_id}.md" in files:
                    raise BaseError(tr("agent.045", v0=app_id))
                c = clean(app, pids)
                notes = c.pop("notes", "")
                meta = {"id": app_id, "type": "app", "listed": True, **c, "sensitivity": "internal",
                        "registered_by": gitops.author_name(viewer.key, signed_as), "updated": self._today()}
                files[f"{FOLDER}/{app_id}.md"] = file_content(meta, notes)
            commit = gitops.write_and_commit(self.root, files, f"应用：登记了 {len(files)} 个（{note}）",
                                             "register_apps", viewer.key, signed_as)
        return tr("agent.004", v0=len(files), v1=commit)

    def update_app(self, app_id: str, changes: dict, signed_as: str | None = None) -> str:
        signed_as = self._sign(signed_as)
        if not isinstance(changes, dict) or not changes:
            raise BaseError(tr("agent.030"))
        with self._write():
            viewer, _, visible = self._writer()
            d = next((x for x in self._apps(visible) if x.id == (app_id or "").strip()), None)
            if not d:
                raise BaseError(tr("agent.042", v0=app_id))
            c = clean(changes, {x.id for x in visible if x.kind == "project"}, partial=True)
            notes = c.pop("notes", None)
            meta = dict(d.meta)
            old_title, old_listed = meta.get("title"), meta.get("listed", True)
            meta.update(c)
            meta["updated"] = self._today()
            meta["updated_by"] = gitops.author_name(viewer.key, signed_as)
            content = file_content(meta, notes_of(d) if notes is None else notes)
            if content == d.raw:
                return tr("agent.031")
            if old_listed and meta.get("listed") is False:
                subject = f"应用：下架了「{meta['title']}」"
            elif not old_listed and meta.get("listed"):
                subject = f"应用：重新上架了「{meta['title']}」"
            elif old_title != meta["title"]:
                subject = f"应用：「{old_title}」改名为「{meta['title']}」"
            else:
                subject = f"应用：改了「{meta['title']}」"
            commit = gitops.write_and_commit(self.root, {d.rel: content}, subject, "update_app", viewer.key, signed_as)
        return tr("agent.005", v0=d.id, v1=commit)
