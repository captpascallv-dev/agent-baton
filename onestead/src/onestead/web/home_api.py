"""首页：按布局取每块的数据，再交给当前观众的投影。"""

from __future__ import annotations

from starlette.requests import Request
from starlette.responses import JSONResponse

from .. import home, skills, skillscan
from ..store import now_taipei
from . import views
from .projection import projection_for


class HomeApi:
    def as_skill_rows(self, docs):
        data = skillscan.cached_scan()
        return data, skillscan.summarize(data, skills.records_of(docs))

    def module_data(self, module, visible, by_rel, by_id, members, commits, rows_cache, viewer, docs, entries):
        kind, opts = module["kind"], module["options"]
        today = now_taipei().date()
        show = projection_for(viewer)
        if kind == "today_todos":
            all_todos = show.todo_list(visible, viewer, docs, today, by_id, members)
            return {"items": [t for t in all_todos if t["bucket"] in ("overdue", "today")],
                    "week": sum(1 for t in all_todos if t["bucket"] == "week")}
        if kind == "apps":
            items = show.apps(visible, viewer, docs, by_id, members, listed_only=True, with_actions=False)
            if opts["ids"]:
                order = {aid: i for i, aid in enumerate(opts["ids"])}
                items = sorted((a for a in items if a["id"] in order), key=lambda a: order[a["id"]])
            else:
                items = [a for a in items if a["kind"] in ("网页", "本机程序")][:8]
            return {"items": items}
        if kind == "shared_skills":
            data, skill_rows = self.as_skill_rows(docs)
            return {"items": home.shared_skill_items(viewer, docs, data, skill_rows)}
        if kind == "skills":
            page = views.skill_page(visible, by_id)
            # 首页只提醒坏了的（主人 10-11 定）；版本差异留给伙伴在 Skill 广场看
            broken = [s for s in page["skills"] if any(i["state"] == "broken" for i in s["installs"].values())]
            return {"broken": len(broken), "total": len(page["skills"]), "items": broken[:4],
                    "harnesses": page["harnesses"]}
        if kind == "attention":
            rows = rows_cache.setdefault("all", views.projects(visible, commits, members, today))
            return {"items": views.attention(visible, by_id, rows)}
        if kind == "projects":
            return {"rows": show.project_list(visible, viewer, docs, commits, today, members, opts["states"])}
        if kind == "activity_chart":
            # 输入已经是安全集合；远程作者名只用允许的简卡。
            return show.activity_chart(commits, members, opts["days"], viewer, docs)
        if kind == "recent":
            return {"rows": [e["row"] for e in entries[:opts["limit"]]], "scope": "最近 500 次改动里你能看到的"}
        return {}

    async def home_page(self, request: Request):
        viewer, docs, visible, by_rel, by_id, members = self.snapshot(request)
        layout, draft = home.current(visible, viewer)
        preview = request.query_params.get("draft") == "1" and draft and draft.get("modules")
        modules = draft["modules"] if preview else layout
        entries = self.activity_set(request, viewer, docs)
        commits = [e["commit"] for e in entries]
        cache: dict = {}
        out = []
        for module in modules:
            out.append({**module, "name": home.MODULES[module["kind"]]["name"],
                        "data": self.module_data(module, visible, by_rel, by_id, members, commits, cache,
                                                 viewer, docs, entries)})
        # 飞书登录待登记不跟着首页布局走：有就放在最上面，在首页就能对上（主人 10-12 定）
        return JSONResponse(projection_for(viewer).home_payload(
            now_taipei().isoformat(), out, bool(preview), draft, viewer, docs, by_id, members, visible,
            lambda: self.as_actor(request).feishu_pending()))
