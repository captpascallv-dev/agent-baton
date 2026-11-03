"""只给本机 主人 的网页操作：成员、飞书、全文、待批准、撤回、交接，以及本机打开和探活。

同事也能用的口令发放不在这里。
"""

from __future__ import annotations

from ..i18n import text as tr

import os
import re

from starlette.concurrency import run_in_threadpool
from starlette.requests import Request
from starlette.responses import JSONResponse

from .. import gitops, home, skillscan
from ..store import BaseError
from . import history, probe, views
from .refs import local_who


class AdminApi:
    async def app_probe(self, request: Request):
        self.require_local_owner(request)
        visible, by_rel, by_id, members = self.ctx(request)
        targets = []
        for d in visible:
            if d.kind == "app" and d.meta.get("listed", True) is not False:
                if d.id == "company-base":  # 就是正在看的这个页面
                    continue
                url = (d.meta.get("open") or {}).get("url")
                targets.append({"id": d.id, "url": url if url and url.startswith(("http://", "https://")) else None,
                                "process": d.meta.get("probe_process")})
        result = await run_in_threadpool(probe.probe, targets)
        if any(d.kind == "app" and d.id == "company-base" for d in visible):
            result["company-base"] = {"state": "online", "how": "本页面"}
        return JSONResponse(result)

    async def skill_share(self, request: Request):
        self.require_local_owner(request)
        data = await request.json()
        return JSONResponse(self.as_actor(request).share_skill(data.get("id"), data.get("public")))

    async def skill_open(self, request: Request):
        """打开扫到的那个技能目录（只开这次扫描里出现过的路径）。"""
        self.require_local_owner(request)
        data = await request.json()
        visible, *_ = self.ctx(request)
        rows = skillscan.summarize(skillscan.cached_scan(), {d.id: d.meta for d in visible if d.kind == "skill"})
        row = next((r for r in rows if r["id"] == data.get("id")), None)
        known = {c["path"] for c in row["copies"]} | {v["path"] for v in row["installs"].values()} if row else set()
        target = data.get("path")
        if target not in known:
            return JSONResponse({"error": tr("agent.597")}, status_code=400)
        if not os.path.isdir(target):
            return JSONResponse({"error": tr("agent.598", v0=target)}, status_code=400)
        if not hasattr(os, "startfile"):
            return JSONResponse({"error": tr("agent.599")}, status_code=501)
        os.startfile(target)  # noqa: S606 —— 和在资源管理器里打开文件夹一样
        return JSONResponse({"ok": True})

    async def member_archive(self, request: Request):
        self.require_local_owner(request)
        return JSONResponse(self.as_actor(request).archive_member(request.path_params["key"]))

    async def member_restore(self, request: Request):
        self.require_local_owner(request)
        return JSONResponse(self.as_actor(request).restore_member(request.path_params["key"]))

    async def project_new(self, request: Request):
        self.require_local_owner(request)
        data = await request.json()
        if set(data) - {"id", "title", "goal", "state", "body", "sensitivity"}:
            raise BaseError(tr("agent.595"))
        body = data.get("body")
        project_id = str(data.get("id") or "").strip()
        message = self.as_actor(request).create_project(
            project_id, str(data.get("title") or ""), str(data.get("goal") or ""),
            str(data.get("state") or "构想中"), body if isinstance(body, str) else None,
            str(data.get("sensitivity") or "internal"))
        return JSONResponse({"ok": True, "id": project_id, "message": message.strip()})

    async def feishu_bind(self, request: Request):
        self.require_local_owner(request)
        data = await request.json()
        return JSONResponse(self.as_actor(request).feishu_bind(str(data.get("open_id", "")), str(data.get("key", ""))))

    async def feishu_unbind(self, request: Request):
        self.require_local_owner(request)
        data = await request.json()
        return JSONResponse(self.as_actor(request).feishu_unbind(str(data.get("key", ""))))

    async def feishu_dismiss(self, request: Request):
        self.require_local_owner(request)
        data = await request.json()
        return JSONResponse(self.as_actor(request).feishu_dismiss(str(data.get("open_id", ""))))

    async def approvals_page(self, request: Request):
        self.require_local_owner(request)
        visible, by_rel, by_id, members = self.ctx(request)
        viewer, _, _ = self.as_actor(request)._ctx()
        _, draft = home.current(visible, viewer)
        if draft and draft.get("by"):
            draft = {**draft, "by_name": local_who(draft["by"], members)["name"]}
        return JSONResponse({"handovers": views.pending_handovers(visible, by_id), "draft": draft})

    async def source(self, request: Request):
        self.require_local_owner(request)
        return JSONResponse(self.as_actor(request).doc_source(request.query_params.get("rel", "")))

    async def source_save(self, request: Request):
        self.require_local_owner(request)
        data = await request.json()
        body = data.get("body")
        return JSONResponse(self.as_actor(request).save_doc(
            str(data.get("rel", "")), str(data.get("version", "")),
            data.get("changes") or {}, body if isinstance(body, str) else None))

    async def source_new(self, request: Request):
        self.require_local_owner(request)
        data = await request.json()
        return JSONResponse(self.as_actor(request).create_doc(
            str(data.get("kind", "")), data.get("fields") or {}, str(data.get("body") or "")))

    async def handover_decide(self, request: Request):
        self.require_local_owner(request)
        data = await request.json()
        msg = self.as_actor(request).decide_handover(
            str(data.get("project", "")), str(data.get("role", "")), str(data.get("to", "")),
            bool(data.get("approve")), data.get("note") or None)
        return JSONResponse({"ok": True, "message": msg.strip()})

    async def do_revert(self, request: Request):
        self.require_local_owner(request)
        data = await request.json()
        try:
            with gitops.write_lock(self.root):
                viewer, docs, *_ = self.snapshot(request)
                entry = history.find_workspace_commit(
                    self.activity_set(request, viewer, docs), str(data.get("sha", "")))
                if not entry:
                    return JSONResponse({"error": tr("agent.604")}, status_code=404)
                if not entry["raw"]:
                    return JSONResponse({"error": tr("agent.605")}, status_code=403)
                subject = entry["commit"]["subject"]
                while subject.startswith("撤回："):
                    subject = subject[3:]
                if subject.startswith(("停用成员", "恢复成员")):
                    return JSONResponse({"error": tr("agent.606")}, status_code=403)
                result = history.revert(self.root, entry["row"]["sha"], data.get("reason"))
        except BaseError as e:
            return JSONResponse({"ok": False, "error": str(e)}, status_code=400)
        if not result["ok"]:
            viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
            entries = self.activity_set(request, viewer, docs)
            blockers = []
            for blocker in result["blockers"]:
                entry = history.find_workspace_commit(entries, blocker["sha"])
                if not entry:
                    continue
                if not entry["raw"]:
                    blockers.append(entry["row"])
                    continue
                info = views.classify(blocker["subject"], [], by_id, by_rel)
                blocker["who"] = local_who(blocker["author"], members)["name"]
                blocker["what"] = info["what"]
                blocker["short"] = blocker["sha"][:7]
                blockers.append(blocker)
            result["blockers"] = blockers
            return JSONResponse(result, status_code=409)
        return JSONResponse(result)

    async def app_open(self, request: Request):
        """在本机打开应用登记的快捷方式、路径或代码目录。只开底座里登记过的那个路径。"""
        self.require_local_owner(request)
        visible, *_ = self.ctx(request)
        d = next((x for x in visible if x.kind == "app" and x.id == request.path_params["aid"]), None)
        if not d:
            return JSONResponse({"error": tr("agent.600")}, status_code=404)
        data = await request.json()
        what = data.get("what")
        path = (d.meta.get("open") or {}).get("path")
        target = {"shortcut": (d.meta.get("open") or {}).get("shortcut"), "path": path,
                  "folder": os.path.dirname(path) if path else None, "code": d.meta.get("code_path")}.get(what)
        if what == "link":  # 相关链接里登记的本机路径
            links = d.meta.get("links") or []
            index = data.get("index")
            link = links[index] if isinstance(index, int) and 0 <= index < len(links) else {}
            target = link.get("target") if not re.match(r"^https?://", str(link.get("target", ""))) else None
        if target:
            target = os.path.expandvars(target)
        if not target:
            return JSONResponse({"error": tr("agent.601")}, status_code=400)
        if not os.path.exists(target):
            return JSONResponse({"error": tr("agent.602", v0=target)}, status_code=400)
        if not hasattr(os, "startfile"):
            return JSONResponse({"error": tr("agent.603")}, status_code=501)
        os.startfile(target)  # noqa: S606 —— 只开登记过的路径，和在资源管理器里双击一样
        return JSONResponse({"ok": True})

    async def app_listed(self, request: Request):
        self.require_local_owner(request)
        listed = (await request.json()).get("listed")
        if not isinstance(listed, bool):
            raise BaseError(tr("agent.596"))
        msg = self.as_actor(request).update_app(request.path_params["aid"], {"listed": listed})
        return JSONResponse({"ok": True, "message": msg.strip()})
