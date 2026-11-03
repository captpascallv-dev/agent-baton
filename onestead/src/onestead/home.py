"""个人首页：agent 给所属的人提草稿，本人在网页上采用或丢弃。"""

from __future__ import annotations

from .i18n import label, text as tr

from . import config

import json
from dataclasses import replace

from . import gitops
from .store import BaseError, Conflict, Doc, Viewer, personal_owner, valid_person_key
from .textutil import _check_text

def paths(owner: str) -> tuple[str, str]:
    """调用方只传核心已核实的 workspace_owner，不接受文件路径。"""
    if not valid_person_key(owner):
        raise BaseError(tr("agent.259"))
    return f"home/{owner}.md", f"home/{owner}-draft.md"

# 模块目录：kind → 名称、做什么、可调的选项（类型、默认值、范围）
MODULES = {
    "today_todos": {"name": "今天的待办", "desc": "今天要做的和逾期没做的待办，可以直接勾掉", "options": {}},
    "apps": {"name": "常用应用", "desc": "应用广场里常用的几个，能直接打开、看在不在线",
             "options": {"ids": {"type": "list", "default": [], "help": "放哪几个应用（应用 id，按顺序）；空表示自动选前 8 个网页和本机程序"}}},
    "skills": {"name": "Skill 提醒", "desc": "坏了的 Skill（缺 SKILL.md、文件损坏、联接断了），没有坏的就不显示", "options": {}},
    "shared_skills": {"name": "共享 Skill", "desc": "已授权共享且可用的 Skill", "options": {}},
    "attention": {"name": "要你处理的事", "desc": "待批准的交接、摘要过时的项目、标记有问题的文件", "options": {}},
    "projects": {"name": "项目健康", "desc": "每个项目的状态、负责人、最近进展、摘要新旧",
                 "options": {"states": {"type": "list", "default": [], "help": "只看这些状态，空表示全部，例如 [进行中, 维护中]"}}},
    "activity_chart": {"name": "伙伴写入量", "desc": "最近每天伙伴们往底座写了多少次，以及这 7 天按伙伴分",
                       "options": {"days": {"type": "int", "default": 14, "min": 7, "max": 60, "help": "看最近几天"}}},
    "recent": {"name": "最近动态", "desc": "最近几条改动", "options": {
        "limit": {"type": "int", "default": 6, "min": 3, "max": 20, "help": "显示几条"}}},
}
# 最近动态和动态页重复，默认不放（主人 10-11）；模块还在，想要时可以提草稿加回来
DEFAULT_LAYOUT = [{"kind": "today_todos"}, {"kind": "apps"}, {"kind": "attention"}, {"kind": "projects"},
                  {"kind": "skills"}, {"kind": "activity_chart"}]
COLLEAGUE_LAYOUT = [{"kind": k} for k in ("today_todos", "projects", "apps", "shared_skills", "recent")]


def catalog(viewer: Viewer | None = None) -> dict:
    if viewer is None or (viewer.workspace_owner == config.owner_key() and not viewer.remote):
        return dict(MODULES)
    return {k: m for k, m in MODULES.items() if k not in ("attention", "skills")}


def shared_skill_items(viewer: Viewer, docs: list[Doc], data: dict, rows: list[dict]) -> list[dict]:
    """共享模块只走共享包资格；第四批渲染时复用，不带本机安装扫描信息。"""
    from . import skills

    # 本机 full 也用共享包投影，内容范围仍由原来的 access_mode 决定。
    packages = skills.shared_packages(replace(viewer, remote=True), docs, data, rows)
    items = []
    for row in rows:
        if row["id"] in packages:
            info = skills.shared_info(row, docs, packages[row["id"]])
            items.append({k: info[k] for k in ("id", "name", "one_line", "version")})
    return items


def normalize(modules, viewer: Viewer | None = None) -> list[dict]:
    """检查 agent 给的模块列表，补上默认值。不认识的模块或选项直接拒绝，免得首页悄悄坏掉。"""
    if not isinstance(modules, list) or not modules:
        raise BaseError(tr("agent.260"))
    out, seen = [], set()
    allowed = catalog(viewer)
    for m in modules:
        if not isinstance(m, dict) or m.get("kind") not in MODULES:
            raise BaseError(tr("agent.263", v0=m, v1='、'.join(MODULES)))
        kind = m["kind"]
        if kind not in allowed:
            raise BaseError(tr("agent.264", v0=kind, v1='、'.join(allowed)))
        if kind in seen:
            raise BaseError(tr("agent.265", v0=kind))
        seen.add(kind)
        spec = MODULES[kind]["options"]
        given = m.get("options") or {}
        if not isinstance(given, dict):
            raise BaseError(tr("agent.266", v0=kind))
        unknown = set(given) - set(spec)
        if unknown:
            raise BaseError(tr("agent.267", v0=kind, v1='、'.join(sorted(unknown))))
        opts = {}
        for key, rule in spec.items():
            value = given.get(key, rule["default"])
            if rule["type"] == "int":
                if not isinstance(value, int) or not rule["min"] <= value <= rule["max"]:
                    raise BaseError(tr("agent.278", v0=kind, v1=key, v2=rule['min'], v3=rule['max']))
            elif rule["type"] == "list":
                if not isinstance(value, list) or not all(isinstance(x, str) for x in value):
                    raise BaseError(tr("agent.279", v0=kind, v1=key))
            opts[key] = value
        out.append({"kind": kind, "options": opts})
    return out


def current(docs: list[Doc], viewer: Viewer | None = None) -> tuple[list[dict], dict | None]:
    """返回（现在的首页模块，草稿或 None）。文件坏了就回到默认首页，不让首页打不开。"""
    owner = viewer.workspace_owner if viewer else config.owner_key()
    layout_rel, draft_rel = paths(owner)
    by_rel = {d.rel: d for d in docs if personal_owner(d) == owner}
    allowed = catalog(viewer)

    def readable(modules):
        # 旧布局保留在盘上；权限变化时只跳过失去能力的模块。
        normalized = normalize(modules)
        return [m for m in normalized if m["kind"] in allowed]

    layout = readable(DEFAULT_LAYOUT if owner == config.owner_key() else COLLEAGUE_LAYOUT)
    doc = by_rel.get(layout_rel)
    if doc and not doc.problem:
        try:
            layout = readable(doc.meta.get("modules"))
        except BaseError:
            pass
    draft = None
    ddoc = by_rel.get(draft_rel)
    if ddoc and not ddoc.problem:
        from .manage import version_of
        version = version_of(ddoc.raw)
        try:
            draft = {"modules": readable(ddoc.meta.get("modules")), "note": ddoc.meta.get("note"),
                     "by": ddoc.meta.get("proposed_by"), "date": str(ddoc.meta.get("updated") or ""),
                     "version": version}
        except BaseError as e:
            draft = {"modules": None, "note": ddoc.meta.get("note"), "by": ddoc.meta.get("proposed_by"),
                     "date": str(ddoc.meta.get("updated") or ""), "version": version, "error": str(e)}
    return layout, draft


def file_content(modules: list[dict], title: str, extra: dict, body: str, *, owner: str | None = None,
                 state: str | None = None) -> str:
    from .base import _dump_front  # 避免循环引用

    owner = config.owner_key() if owner is None else owner
    paths(owner)
    state = state or ("draft" if "proposed_by" in extra else "active")
    if state not in ("active", "draft"):
        raise BaseError(tr("agent.261"))
    meta = {**extra, "id": owner + ("-draft" if state == "draft" else ""), "type": "home", "title": title,
            "owner": owner, "state": state, "sensitivity": "internal", "modules": modules}
    return _dump_front(meta) + "\n" + body


def describe(viewer: Viewer | None = None) -> str:
    lines = [tr("agent.262"), ""]
    for kind, m in catalog(viewer).items():
        lines.append(f"- `{kind}`：{label(m['name'])}。{label(m['desc'])}。")
        for key, rule in m["options"].items():
            rng = f"，{rule['min']}–{rule['max']}" if rule["type"] == "int" else ""
            lines.append(tr("agent.272", v0=key, v1=rule['default'], v2=rng, v3=label(rule['help'])))
    return "\n".join(lines)


class HomeMixin:
    """CompanyBase 的首页部分。"""

    def home_layout(self) -> str:
        viewer, _, visible = self._ctx()
        if not viewer.known or not viewer.valid or not viewer.workspace_owner:
            raise BaseError(tr("agent.268"))
        layout, draft = current(visible, viewer)
        out = [tr("agent.269"), "", tr("agent.273") + " → ".join(
            f"`{m['kind']}`（{label(MODULES[m['kind']]['name'])}）" for m in layout), ""]
        out.append("```json\n" + json.dumps(layout, ensure_ascii=False) + "\n```")
        if draft:
            out += ["", tr("agent.274", v0=draft.get('by') or tr("agent.280"), v1=draft.get('note') or tr("agent.281"))]
        out += ["", describe(viewer), "",
                tr("agent.270")]
        return "\n".join(out) + "\n"

    def propose_home(self, modules: list, note: str, signed_as: str | None = None) -> str:
        signed_as = self._sign(signed_as)
        note = _check_text(tr("agent.008"), note, limit=200).replace("\n", " ")
        with self._write():
            viewer, _, visible = self._writer(operation="personal")
            mods = normalize(modules, viewer)
            app_ids = {d.id for d in visible if d.kind == "app"}
            if any(set(m["options"].get("ids", [])) - app_ids for m in mods if m["kind"] == "apps"):
                raise BaseError(tr("agent.275"))
            extra = {"proposed_by": gitops.author_name(viewer.key, signed_as), "note": note,
                     "updated": self._today()}
            content = file_content(mods, "首页草稿", extra,
                                   "本人在网页首页上预览，点“采用”替换现在的首页，点“丢弃”删掉。\n",
                                   owner=viewer.workspace_owner, state="draft")
            _, draft_rel = paths(viewer.workspace_owner)
            commit = gitops.write_and_commit(self.root, {draft_rel: content}, f"首页草稿：{note}",
                                             "propose_home", viewer.key, signed_as)
        return (tr("agent.258", v0=commit))

    def adopt_home(self, version: str | None = None) -> dict:
        with self._write():
            viewer, _, visible = self._human_workspace()
            _, draft = current(visible, viewer)
            if not draft or not draft.get("modules"):
                raise BaseError(tr("agent.276"))
            self._same_home_draft(viewer, version)
            layout_rel, draft_rel = paths(viewer.workspace_owner)
            content = file_content(draft["modules"], "本人的首页", {"updated": self._today()},
                                   "网页首页放哪些模块、什么顺序；本人采用草稿后写到这里。\n",
                                   owner=viewer.workspace_owner, state="active")
            commit = gitops.write_and_commit(self.root, {layout_rel: content, draft_rel: None},
                                             f"首页：采用了新首页（{draft.get('note') or '没写说明'}）",
                                             "网页界面", viewer.key, None)
        return {"ok": True, "commit": commit}

    def discard_home(self, version: str | None = None) -> dict:
        with self._write():
            viewer, _, visible = self._human_workspace()
            _, draft = current(visible, viewer)
            if not draft:
                raise BaseError(tr("agent.277"))
            self._same_home_draft(viewer, version)
            _, draft_rel = paths(viewer.workspace_owner)
            commit = gitops.write_and_commit(self.root, {draft_rel: None},
                                             f"首页：丢弃了一份草稿（{draft.get('note') or '没写说明'}）",
                                             "网页界面", viewer.key, None)
        return {"ok": True, "commit": commit}

    def _same_home_draft(self, viewer: Viewer, version: str | None) -> None:
        """采用和丢弃都要比对预览时看到的那一版；对不上就留着草稿。"""
        from .manage import version_of

        _, draft_rel = paths(viewer.workspace_owner)
        path = self.root / draft_rel
        if not path.is_file():
            raise Conflict(tr("agent.271"))
        current_version = version_of(path.read_text(encoding="utf-8").replace("\r\n", "\n"))
        if not isinstance(version, str) or version != current_version:
            raise Conflict(tr("agent.271"))
