"""Skill 广场：各家装了哪些 Skill、内容一不一样，以及伙伴登记的补充说明。

装在哪、一不一样每次现场扫（见 skillscan.py），不存进底座，免得过时。
底座里只存补充说明：`skills/<id>.md`，id 就是技能目录名。开头标记放中文名、一句话、
来源（自建 / 第三方 / 软件自带）、上游、谁维护、正本在哪（home）、属于哪个项目，正文放说明。
登记了 home 的，各家拿来和这份比；没登记的，以最新的一份为准。
"""

from __future__ import annotations

from .i18n import label as system_label, text as tr

import re

from . import gitops, skillscan, skillshare
from .store import BaseError, Doc, NotFound, Viewer
from .textutil import _cell, _check_text, _dump_front, _strip_title
from .redact import safe_text, safe_url

FOLDER = "skills"
ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{1,60}$")  # 技能目录名，有的带大写（如 Humanizer-zh）
ORIGINS = ("自建", "第三方", "软件自带")
TEXT_FIELDS = {"title": 40, "one_line": 160, "maintainer": 120, "upstream": 300, "upstream_version": 60, "home": 300}
EDITABLE = ("title", "one_line", "origin", "maintainer", "upstream", "upstream_version", "home", "project", "notes")
STATE_NAMES = {"same": "一致", "behind": "落后", "differs": "和正本不同", "broken": "有问题", "snapshot": "云端（只有快照）"}


def clean(item: dict, project_ids: set[str], *, partial: bool = False) -> dict:
    if not isinstance(item, dict):
        raise BaseError(tr("agent.469"))
    editable = (*EDITABLE, "listed") if partial else EDITABLE
    unknown = set(item) - set(editable) - ({"id"} if not partial else set())
    if unknown:
        raise BaseError(tr("agent.470", v0='、'.join(sorted(unknown)), v1='、'.join(editable)))
    out: dict = {}
    for key, limit in TEXT_FIELDS.items():
        required = key == "one_line" and not partial
        if key in item or required:
            out[key] = _check_text({"one_line": "一句话"}.get(key, key), item.get(key), required=required,
                                   limit=limit).replace("\n", " ") or None
    if "origin" in item:
        if item["origin"] not in ORIGINS:
            raise BaseError(tr("agent.471", v0='、'.join(ORIGINS)))
        out["origin"] = item["origin"]
    if "project" in item:
        pid = (item.get("project") or "").strip() or None
        if pid and pid not in project_ids:
            raise BaseError(tr("agent.011", v0=pid))
        out["project"] = pid
    if "notes" in item:
        out["notes"] = _check_text(tr("agent.008"), item.get("notes"), required=False, limit=3000)
    if "listed" in item:
        if not isinstance(item["listed"], bool):
            raise BaseError(tr("api.listed_boolean"))
        out["listed"] = item["listed"]
    return out


def file_content(meta: dict, notes: str) -> str:
    order = ["id", "type", "title", "one_line", "origin", "upstream", "upstream_version", "maintainer", "home",
             "project", "sensitivity", "registered_by", "updated", "updated_by"]
    keep = {k: meta[k] for k in order if meta.get(k) not in (None, "", {}, [])}
    keep |= {k: v for k, v in meta.items() if k not in order and v not in (None, "", {}, [])}
    body = f"\n# {keep.get('title') or keep['id']}\n" + (f"\n{notes.strip()}\n" if notes and notes.strip() else "")
    return _dump_front(keep) + body


def notes_of(doc: Doc) -> str:
    return _strip_title(doc.body).strip()


def records_of(visible: list[Doc]) -> dict[str, dict]:
    return {d.id: d.meta for d in visible if d.kind == "skill"}


def shared_packages(viewer: Viewer, docs: list[Doc], data: dict, rows: list[dict],
                    *, skill_id: str | None = None, capture: bool = False) -> dict[str, skillshare.Package]:
    """有效读者的共享范围；assigned 按授权，full 可读全部安全共享包；公开说明从全部文档找，不经 visible。"""
    if not (viewer.known and viewer.valid and (viewer.assigned or viewer.remote or viewer.is_owner)):
        return {}
    public = {d.id for d in docs if d.kind == "skill" and not d.problem and d.sensitivity == "public"}
    unlisted = {d.id for d in docs if d.kind == "skill" and d.meta.get("listed") is False}
    scanned = {i["dir"] for h in data["harnesses"] for i in h["items"]}
    scanned |= {i["dir"] for i in data["shared"]["items"]}
    granted = (viewer.skills | public) & scanned if viewer.assigned else scanned
    result = {}
    for row in rows:
        sid = row["id"]
        if (not row["listed"] or sid in unlisted or sid not in granted or not ID_RE.fullmatch(sid) or not row["ref"] or row["ref"].get("broken")
                or row["alert"] == "broken"
                or (skill_id is not None and skill_id != sid)):
            continue
        package = skillshare.package_manifest(row["ref"]["path"], capture=capture)
        if package.skill_md is not None:
            result[sid] = package
    return result


def visible_skill_ids(viewer: Viewer, docs: list[Doc], data: dict, rows: list[dict]) -> frozenset[str]:
    return frozenset(shared_packages(viewer, docs, data, rows))


def shared_info(row: dict, docs: list[Doc], package: skillshare.Package, *, full_text: bool = False) -> dict:
    meta = next((d.meta for d in docs if d.kind == "skill" and d.id == row["id"] and not d.problem), {})
    raw = (package.skill_md or "").encode("utf-8")
    truncated = not full_text and len(raw) > 60 * 1024
    body = raw[:60 * 1024].decode("utf-8", errors="ignore") if truncated else package.skill_md
    upstream = str(meta.get("upstream") or "")
    return {"id": row["id"], "name": safe_text(meta.get("title") or row["skill_name"] or row["id"]),
            "one_line": safe_text(meta.get("one_line") or row["description"]),
            "description": safe_text(row["description"]), "version": safe_text(row["version"]),
            "upstream": safe_url(upstream) or "",
            "maintainer": safe_text(meta.get("maintainer")), "skill_md": body or "",
            "truncated": truncated, "files": package.files, "too_large": package.too_large}


def shared_not_found(skill_id: str) -> BaseError:
    return BaseError(tr("agent.463", v0=safe_text(skill_id)))


def install_dir(key: str) -> str | None:
    """装到某一家时按惯例放哪（装之前要先确认这家实际从哪读）。快照不作为安装位置。"""
    roots = {h["key"]: h["user"] for h in skillscan.harness_table() if not h.get("snapshot")}
    paths = roots.get(key) or []
    return str(paths[0]) if paths else None


def install_hint(key: str) -> str:
    where = install_dir(key)
    if any(h["key"] == key and h.get("snapshot") for h in skillscan.harness_table()):
        return tr("agent.466")
    return tr("agent.464", v0=where) if where else tr("agent.465")


class SkillMixin:
    """CompanyBase 的 Skill 广场部分。"""

    def _skill_rows(self, visible: list[Doc], fresh: bool = False) -> tuple[dict, list[dict]]:
        data = skillscan.cached_scan(fresh)
        return data, skillscan.summarize(data, records_of(visible))

    def list_skills(self, only_alerts: bool = False, fresh: bool = False, include_unlisted: bool = False) -> str:
        viewer, docs, visible = self._ctx()
        if viewer.assigned or viewer.remote:
            data, rows = self._skill_rows(docs, False)
            packages = shared_packages(viewer, docs, data, rows)
            if not packages:
                return tr("agent.472")
            out = ["# Skill", "", tr("agent.485"), "|---|---|---|---|"]
            for row in rows:
                if row["id"] in packages:
                    info = shared_info(row, docs, packages[row["id"]])
                    out.append(f"| `{info['id']}` | {_cell(info['name'])} | {_cell(info['one_line'])} | {_cell(info['version'])} |")
            return "\n".join(out) + "\n"
        self._internal_only(tr("agent.473"))
        data, rows = self._skill_rows(visible, fresh)
        listed = [r for r in rows if r["listed"]]
        names = {h["key"]: h["name"] for h in data["harnesses"]}
        behind = [r for r in rows if r["alert"] == "behind"]
        broken = [r for r in rows if r["alert"] == "broken"]
        out = [tr("agent.474", v0=data['scanned_at'][:16].replace('T', ' ')), "",
               tr("agent.475", v0=len(listed), v1=len(behind), v2=len(broken)),
               tr("agent.476", v0=data['shared']['path']), ""]
        shown = [r for r in rows if r["alert"]] if only_alerts else (rows if include_unlisted else listed)
        out += [tr("agent.477"), "|---|---|---|---|"]
        for r in shown:
            where = "、".join(f"{names[k]}" + (tr("agent.514") if v["in_shared_dir"] else
                                               tr("agent.521", v0='、'.join((names[x] for x in v['same_copy_as']))) if v["same_copy_as"] else "") +
                             ("" if v["state"] == "same" else f"：{system_label(STATE_NAMES[v['state']])}")
                             for k, v in r["installs"].items()) or (tr("agent.500") if r["in_shared"] else tr("agent.501"))
            alert = {"behind": tr("agent.512"), "broken": tr("agent.513")}.get(r["alert"], "")
            if not r["listed"]:
                alert = tr("api.unlisted")
            out.append(f"| `{r['id']}` | {_cell(r['name'])} | {_cell(where)} | {alert} |")
        out += ["", tr("agent.478"), ""]
        for h in data["harnesses"]:
            bits = []
            if h["plugins"]:
                bits.append(tr("agent.502", v0=len(h['plugins']), v1=sum((len(p['skills']) for p in h['plugins']))))
            if h["builtin"]:
                bits.append(tr("agent.503", v0=len(h['builtin'])))
            if bits:
                out.append(f"- {h['name']}：" + "，".join(bits))
        out += ["", tr("agent.479")]
        return "\n".join(out) + "\n"

    def get_skill(self, skill_id: str, fresh: bool = False) -> str:
        viewer, _, _ = self._ctx()
        if viewer.assigned or viewer.remote:
            info = self.shared_skill(skill_id, fresh=fresh)
            out = [f"# {info['name']}（`{info['id']}`）", ""]
            for key, label in (("one_line", tr("agent.332")), ("description", tr("agent.504")),
                               ("version", tr("agent.505")), ("upstream", tr("agent.490")), ("maintainer", tr("agent.492"))):
                if info[key]:
                    out.append(f"- {label}：{info[key]}")
            out += ["", "## SKILL.md", "", info["skill_md"]]
            if info["truncated"]:
                out += ["", tr("agent.506")]
            out += ["", tr("agent.486"), "", tr("agent.487"), "|---|---|"]
            out += [f"| {_cell(f['path'])} | {f['size']} |" for f in info["files"]]
            out += ["", tr("agent.522").format(owner_name=self.owner_name) if info["too_large"] else
                    tr("agent.507")]
            return "\n".join(out) + "\n"
        _, _, visible = self._internal_only(tr("agent.473"))
        data, rows = self._skill_rows(visible, fresh)
        sid = (skill_id or "").strip()
        r = next((x for x in rows if x["id"] == sid), None)
        if not r:
            raise NotFound(tr("agent.480", v0=skill_id))
        names = {h["key"]: h["name"] for h in data["harnesses"]}
        rec = next((d for d in visible if d.kind == "skill" and d.id == sid), None)
        m = rec.meta if rec else {}
        out = [f"# {r['name']}（`{sid}`）", ""]
        if not r["listed"]:
            out.append(tr("api.unlisted"))
        if m.get("one_line"):
            out.append(tr("agent.022", v0=m['one_line']))
        if r["description"]:
            out.append(tr("agent.488", v0=r['description']))
        for key, label in (("origin", tr("agent.489")), ("upstream", tr("agent.490")), ("upstream_version", tr("agent.491")),
                           ("maintainer", tr("agent.492")), ("home", tr("agent.493")), ("project", tr("agent.494"))):
            if m.get(key):
                out.append(f"- {label}：{m[key]}")
        if r["version"]:
            out.append(tr("agent.495", v0=r['version']))
        ref = r["ref"]
        out.append(tr("agent.496", v0=ref['path'], v1=tr("agent.516") if m.get('home') else tr("agent.517"), v2=ref['newest'] or tr("agent.518"))
                   if ref else tr("agent.497"))
        out += ["", tr("agent.481"), "|---|---|---|---|"]
        for h in data["harnesses"]:
            v = r["installs"].get(h["key"])
            if v:
                state = system_label(STATE_NAMES[v["state"]]) + (tr("agent.514") if v["in_shared_dir"] else
                                                   tr("agent.519", v0='、'.join((names[x] for x in v['same_copy_as']))) if v["same_copy_as"] else "")
                if v["dangling"]:
                    state += tr("agent.508")
                elif v["no_skill_md"]:
                    state += tr("agent.515")
                elif v.get("damaged_count"):
                    state += tr("agent.520", v0=v['damaged_count'], v1=v['damaged'][0])
                out.append(f"| {h['name']} | {state} | `{v['path']}` | {v['newest'] or ''} |")
            else:
                out.append(tr("agent.509", v0=h['name'], v1=install_hint(h['key'])))
        if r["shared_copy"]:
            sc = r["shared_copy"]
            out.append(tr("agent.498", v0=system_label(STATE_NAMES[sc['state']]), v1=sc['path'], v2=sc['newest'] or ''))
        out += ["", tr("agent.482", v0=self.owner_name)]
        if rec and notes_of(rec):
            out += ["", notes_of(rec)]
        return "\n".join(out) + "\n"

    def shared_skill_list(self) -> list[dict]:
        viewer, docs, _ = self._ctx()
        data, rows = self._skill_rows(docs)
        packages = shared_packages(viewer, docs, data, rows)
        return [shared_info(r, docs, packages[r["id"]]) for r in rows if r["id"] in packages]

    def shared_skill(self, skill_id: str, *, fresh: bool = False, full_text: bool = False) -> dict:
        viewer, docs, _ = self._ctx()
        data, rows = self._skill_rows(docs, fresh if viewer.is_owner and not viewer.remote else False)
        packages = shared_packages(viewer, docs, data, rows, skill_id=skill_id)
        if skill_id not in packages:
            raise shared_not_found(skill_id)
        row = next(r for r in rows if r["id"] == skill_id)
        return shared_info(row, docs, packages[skill_id], full_text=full_text)

    def download_skill(self, skill_id: str) -> bytes:
        viewer, docs, _ = self._ctx()
        data, rows = self._skill_rows(docs)
        packages = shared_packages(viewer, docs, data, rows, skill_id=skill_id, capture=True)
        if skill_id not in packages:
            raise shared_not_found(skill_id)
        try:
            content = skillshare.zip_package(skill_id, packages[skill_id])
        except skillshare.PackageTooLarge:
            raise skillshare.PackageTooLarge(tr("agent.522").format(owner_name=self.owner_name)) from None
        # 打包不持写锁。返回字节前再核一次凭据，再核授权，不能把已经失效的口令换成压缩包。
        self._recheck_auth()
        viewer, docs, _ = self._ctx()
        if skill_id not in visible_skill_ids(viewer, docs, data, rows):
            raise shared_not_found(skill_id)
        return content

    def register_skill(self, skill: dict, signed_as: str | None = None) -> str:
        return self.register_skills([skill], None, signed_as)

    def register_skills(self, skills: list, note: str | None, signed_as: str | None = None) -> str:
        count, commit = self._register_skills_commit(skills, note, signed_as)
        return tr("agent.467", v0=count, v1=commit)

    def _register_skills_commit(self, skills: list, note: str | None,
                                signed_as: str | None = None) -> tuple[int, str]:
        signed_as = self._sign(signed_as)
        if not isinstance(skills, list) or not skills:
            raise BaseError(tr("agent.483"))
        with self._write():
            viewer, _, visible = self._writer()
            pids = {d.id for d in visible if d.kind == "project"}
            files = {}
            for item in skills:
                sid = str((item or {}).get("id") or "").strip() if isinstance(item, dict) else ""
                rel = f"{FOLDER}/{sid}.md"
                if not ID_RE.match(sid):
                    raise BaseError(tr("agent.510"))
                if (self.root / rel).exists() or rel in files:
                    raise BaseError(tr("agent.511", v0=sid))
                c = clean(item, pids)
                notes = c.pop("notes", "")
                meta = {"id": sid, "type": "skill", **c, "sensitivity": "internal",
                        "registered_by": gitops.author_name(viewer.key, signed_as), "updated": self._today()}
                files[rel] = file_content(meta, notes)
            if len(files) == 1:
                meta_title = skills[0].get("title") or skills[0]["id"]
                subject = f"Skill：登记了「{meta_title}」"
            else:
                note = _check_text(tr("agent.008"), note, limit=200).replace("\n", " ")
                subject = f"Skill：登记了 {len(files)} 个（{note}）"
            commit = gitops.write_and_commit(self.root, files, subject, "register_skill", viewer.key, signed_as)
        return len(files), commit

    def update_skill(self, skill_id: str, changes: dict, signed_as: str | None = None) -> str:
        commit = self._update_skill_commit(skill_id, changes, signed_as)
        return tr("agent.468", v0=(skill_id or "").strip(), v1=commit) if commit else tr("agent.031")

    def _update_skill_commit(self, skill_id: str, changes: dict, signed_as: str | None = None) -> str | None:
        signed_as = self._sign(signed_as)
        if not isinstance(changes, dict) or not changes:
            raise BaseError(tr("agent.484"))
        with self._write():
            viewer, _, visible = self._writer()
            d = next((x for x in visible if x.kind == "skill" and x.id == (skill_id or "").strip()), None)
            if not d:
                raise NotFound(tr("agent.499", v0=skill_id))
            c = clean(changes, {x.id for x in visible if x.kind == "project"}, partial=True)
            notes = c.pop("notes", None)
            meta = dict(d.meta)
            meta.update(c)
            meta["updated"] = self._today()
            meta["updated_by"] = gitops.author_name(viewer.key, signed_as)
            content = file_content(meta, notes_of(d) if notes is None else notes)
            if content == d.raw:
                return None
            title = meta.get('title') or d.id
            subject = (tr("api.skill_relisted" if c["listed"] else "api.skill_unlisted", title=title)
                       if "listed" in c else f"Skill：改了「{title}」")
            commit = gitops.write_and_commit(self.root, {d.rel: content}, subject,
                                             "update_skill", viewer.key, signed_as)
        return commit
