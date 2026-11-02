"""给本机程序用的 JSON 接口：一次一个请求，权限仍走 CompanyBase。"""

from __future__ import annotations

import json
import sys
import traceback
from contextlib import redirect_stdout

from . import config, todos, usage
from .base import CompanyBase
from .i18n import text as tr
from .store import AuthRejected, BaseError, Busy, Conflict, Forbidden, NotFound, StorageError, owner_name, project_path_list
from .textutil import _as_date


SKILL_FIELDS = {key: (str, type(None)) for key in (
    "title", "one_line", "maintainer", "upstream", "upstream_version", "home", "project", "notes")}
SKILL_FIELDS["origin"] = str
TODO_FIELDS = {key: (str, type(None)) for key in (
    "due", "time", "repeat", "project", "notes")}
TODO_FIELDS.update({key: str for key in ("title", "ask", *todos.ASK_FIELDS)})
FIELDS = {
    "owner": {}, "list_projects": {}, "list_members": {},
    "create_project": {"id": str, "title": str, "goal": str, "state": str, "paths": list, "signed_as": str},
    "append_status": {"project_id": str, "text": str, "signed_as": str},
    "list_skills": {"include_unlisted": bool},
    "register_skill": {"id": str, **SKILL_FIELDS, "one_line": str, "signed_as": str},
    "update_skill": {"skill_id": str, "changes": dict, "signed_as": str},
    "list_todos": {"scope": str, "project_id": (str, type(None))},
    "add_todo": {**TODO_FIELDS, "signed_as": str},
    "update_todo": {"todo_id": str, "changes": dict, "signed_as": str},
}
REQUIRED = {
    "create_project": {"id", "title", "goal", "signed_as"},
    "append_status": {"project_id", "text", "signed_as"},
    "register_skill": {"id", "one_line", "signed_as"},
    "update_skill": {"skill_id", "changes", "signed_as"},
    "add_todo": {"title", "signed_as"},
    "update_todo": {"todo_id", "changes", "signed_as"},
}


def _fields(values: dict, fields: dict, required=()) -> None:
    if set(values) - fields.keys() or set(required) - values.keys():
        raise BaseError(tr("api.fields"))
    for key, value in values.items():
        if not isinstance(value, fields[key]):
            raise BaseError(tr("api.type", field=key))


def _request(request) -> tuple[str, dict]:
    if not isinstance(request, dict):
        raise BaseError(tr("api.request"))
    _fields(request, {"method": str, "params": dict}, {"method"})
    method = request["method"]
    if method not in FIELDS:
        raise BaseError(tr("api.method"))
    params = request.get("params", {})
    _fields(params, FIELDS[method], REQUIRED.get(method, ()))
    if method in REQUIRED and not params["signed_as"].strip():
        raise BaseError(tr("api.signature"))
    if "paths" in params and not all(isinstance(path, str) for path in params["paths"]):
        raise BaseError(tr("api.type", field="paths"))
    if method == "update_skill":
        _fields(params["changes"], {**SKILL_FIELDS, "listed": bool})
    if method == "update_todo":
        _fields(params["changes"], {**TODO_FIELDS, "status": str, "notified": bool})
    return method, params


def _invalid_json():
    raise ValueError


def _date(value) -> str | None:
    date = _as_date(value)
    return date.isoformat() if date else None


def dispatch(base: CompanyBase, method: str, params: dict):
    """把校验过的请求交给现有的权限和写入路径，结果组装成结构化数据。"""
    viewer, _, visible = base._ctx()
    if not viewer.known or not viewer.valid:
        raise Forbidden(tr("api.identity"))
    if method == "owner":
        return {"key": config.owner_key(), "name": owner_name(visible)}
    if method == "list_projects":
        return [{"id": d.id, "title": d.title, "state": d.meta.get("state"), "goal": d.meta.get("goal"),
                 "roles": [{"role": r.get("role"), "who": r.get("who"), "since": _date(r.get("since")),
                            "key": r.get("key") is True}
                           for r in (d.meta.get("roles") if isinstance(d.meta.get("roles"), list) else [])
                           if isinstance(r, dict)],
                 "paths": project_path_list(d.meta.get("paths")) or [], "summary_as_of": _date(d.meta.get("summary_as_of")),
                 "sensitivity": d.sensitivity, "problem": d.problem}
                for d in visible if d.kind == "project"]
    if method == "list_members":
        return [{"agent_key": d.meta.get("agent_key"), "title": d.title, "kind": d.meta.get("kind"),
                 "role": d.meta.get("role"), "owner": d.meta.get("owner"),
                 "archived": d.meta.get("archived") is True, "can_write": d.meta.get("can_write") is True}
                for d in visible if d.kind == "person"]
    if method == "create_project":
        values = dict(params)
        project_id = values.pop("id").strip()
        return {"id": project_id, "commit": base._create_project_commit(project_id, **values)}
    if method == "append_status":
        _, commit = base._append_status_commit(params["project_id"], params["text"], params["signed_as"])
        return {"commit": commit}
    if method == "list_skills":
        _, _, visible = base._internal_only(tr("agent.473"))
        _, rows = base._skill_rows(visible)
        records = {d.id: d.meta for d in visible if d.kind == "skill"}
        result = []
        for row in rows:
            if not row["listed"] and not params.get("include_unlisted", False):
                continue
            meta = records.get(row["id"], {})
            result.append({"id": row["id"], "title": meta.get("title") or row["skill_name"] or row["id"],
                           "one_line": meta.get("one_line") or row["description"],
                           "origin": meta.get("origin"), "maintainer": meta.get("maintainer"),
                           "home": meta.get("home"), "updated": _date(meta.get("updated")),
                           "installed_in": list(row["installs"]), "listed": row["listed"]})
        return result
    if method == "register_skill":
        skill = dict(params)
        signed_as = skill.pop("signed_as")
        _, commit = base._register_skills_commit([skill], None, signed_as)
        return {"id": skill["id"].strip(), "commit": commit}
    if method == "update_skill":
        return {"commit": base._update_skill_commit(**params)}
    if method == "list_todos":
        groups = base._todo_groups(visible, **params)
        return [{"id": d.id, "title": d.title, "status": d.meta.get("status", "open"),
                 "due": _date(d.meta.get("due")), "time": todos.time_of(d.meta),
                 "repeat": d.meta.get("repeat"), "project": d.meta.get("project"),
                 "notes": todos.notes_of(d), "created": _date(d.meta.get("created")),
                 "created_by": d.meta.get("created_by"), "done_at": _date(d.meta.get("done_at")),
                 **{key: d.meta.get(key) for key in ("ask", *todos.ASK_FIELDS, "notified_by", "notified_at")},
                 "notice": todos.notice(d.meta)} for docs in groups.values() for d in docs]
    if method == "add_todo":
        item = dict(params)
        signed_as = item.pop("signed_as")
        ids, commit = base._add_todos_commit([item], signed_as=signed_as)
        return {"id": ids[0], "commit": commit}
    if method == "update_todo":
        _, commit, _ = base._update_todo_commit(**params)
        return {"commit": commit}


def run(args) -> int:
    method = "unknown"
    agent = getattr(args, "agent", None) or config.env("AGENT")
    try:
        # 库和以后加的诊断输出不能混进标准输出的响应。
        with redirect_stdout(sys.stderr):
            try:
                try:
                    envelope = json.loads(sys.stdin.buffer.read().decode("utf-8-sig"),
                                          parse_constant=lambda _: _invalid_json())
                except (ValueError, UnicodeError):
                    raise BaseError(tr("api.json")) from None
                # 参数校验失败也按请求的方法名记用量。
                if isinstance(envelope, dict) and isinstance(envelope.get("method"), str):
                    method = envelope["method"]
                method, params = _request(envelope)
                if not agent or not agent.strip():
                    raise Forbidden(tr("api.identity"))
                root = getattr(args, "data", None) or config.data_dir()
                result = dispatch(CompanyBase(root, agent), method, params)
                response = {"ok": True, "result": result}
            finally:
                usage.record(agent, "api:" + method, "api")
    except (Forbidden, AuthRejected) as error:
        response = {"ok": False, "error": {"code": "forbidden", "message": str(error)}}
    except NotFound as error:
        response = {"ok": False, "error": {"code": "not_found", "message": str(error)}}
    except (Conflict, Busy) as error:
        response = {"ok": False, "error": {"code": "conflict", "message": str(error)}}
    except StorageError:
        traceback.print_exc(file=sys.stderr)
        response = {"ok": False, "error": {"code": "internal", "message": tr("api.internal")}}
    except BaseError as error:
        response = {"ok": False, "error": {"code": "invalid", "message": str(error)}}
    except Exception:
        traceback.print_exc(file=sys.stderr)
        response = {"ok": False, "error": {"code": "internal", "message": tr("api.internal")}}
    try:
        output = (json.dumps(response, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")
    except Exception:
        # 手改的数据里可能有 JSON 表示不了的 YAML 值。
        traceback.print_exc(file=sys.stderr)
        response = {"ok": False, "error": {"code": "internal", "message": tr("api.internal")}}
        output = (json.dumps(response, ensure_ascii=False) + "\n").encode("utf-8")
    sys.stdout.buffer.write(output)
    sys.stdout.buffer.flush()
    return 0 if response["ok"] else 1
