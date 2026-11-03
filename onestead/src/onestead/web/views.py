"""把底座的文件和 git 历史整理成页面要的样子：一句人话、项目健康、要你处理的事、写入量。"""

from __future__ import annotations

from .. import config

import datetime as dt
import re
from collections import Counter

from ..base import STALE_DAYS, _as_date, _strip_title, newer_status_count
from ..handover import pending_handovers, short
from ..store import TAIPEI, Doc, make_viewer, member_keys
from .. import apps, feishu, skills, skillscan, skillshare, todos
from .labels import file_label
from .refs import local_who, project_ref, who_view
from .render import to_html

KIND_LABEL = {"handbook": "手册", "project": "项目", "project-status": "项目进展", "project-links": "项目链接",
              "decision": "决定", "asset": "资料", "person": "成员", "home": "首页", "todo": "待办", "app": "应用", "skill": "Skill"}


def taipei(iso: str) -> dt.datetime:
    return dt.datetime.fromisoformat(iso).astimezone(TAIPEI)


# ---------- 一次改动 → 一句人话 ----------

SUBJECT_RE = re.compile(r"^(进展|交接|接任申请|接任不批)：([a-z0-9-]+)(.*)$")


def classify(subject: str, files: list[str], by_id: dict[str, Doc], by_rel: dict[str, Doc]) -> dict:
    """返回 type（进展/决定/交接/申请/首页/撤回/其他）、project（id 或 None）、what（人话里“做了什么”那半句）。"""
    if subject.startswith("撤回："):
        inner = classify(subject[3:], files, by_id, by_rel)
        return {"type": "撤回", "project": inner["project"], "what": f"撤回了一次改动：{inner['what']}",
                "excerpt": inner.get("excerpt")}
    m = SUBJECT_RE.match(subject)
    if m:
        kind, pid, rest = m.groups()
        pname = short(by_id[pid].title) if pid in by_id else pid
        rest = rest.lstrip(" —").strip()
        if kind == "进展":
            return {"type": "进展", "project": pid, "what": f"在「{pname}」记了一条进展", "excerpt": rest}
        role_m = re.match(r"^的(.+?) → (.+)$", rest)
        role, who = (role_m.group(1), role_m.group(2)) if role_m else (rest, "")
        if kind == "交接":
            return {"type": "交接", "project": pid, "what": f"把「{pname}」的{role}交接给了 {who}"}
        if kind == "接任不批":
            return {"type": "申请", "project": pid, "what": f"没批「{pname}」的{role}换成 {who}"}
        return {"type": "申请", "project": pid, "what": f"申请把「{pname}」的{role}换成 {who}，等你批准"}
    if subject.startswith("决定："):
        title = subject[3:]
        pid = next((by_rel[f].meta.get("project") for f in files if f in by_rel and by_rel[f].kind == "decision"), None)
        return {"type": "决定", "project": pid, "what": f"记了一条决定：{title}"}
    if subject.startswith("待办："):
        rest = subject[3:]
        pids = {by_rel[f].meta.get("project") for f in files if f in by_rel and by_rel[f].kind == "todo"}
        pid = pids.pop() if len(pids) == 1 else None  # 一次整理了好几个项目的待办，就不挂项目
        m = re.match(r"^整理了 (\d+) 条(.*)$", rest)
        what = f"整理了 {m.group(1)} 条待办{m.group(2)}" if m else rest.replace("「", "待办「", 1)
        return {"type": "待办", "project": pid, "what": what}
    if subject.startswith(("修改：", "新建：")):
        verb = "改了" if subject.startswith("修改：") else "新建了"
        pid = None
        for f in files:
            if f.startswith("projects/"):
                pid = f.split("/")[1]
            elif f in by_rel and by_rel[f].kind == "decision":
                pid = by_rel[f].meta.get("project")
        return {"type": "编辑", "project": pid, "what": verb + subject[3:]}
    if subject.startswith("Skill："):
        rest = subject[6:]
        m = re.match(r"^登记了 (\d+) 个(.*)$", rest)
        what = f"登记了 {m.group(1)} 个 Skill 的说明{m.group(2)}" if m else rest.replace("「", "Skill「", 1) + ("的说明" if "「" in rest else "")
        return {"type": "Skill", "project": None, "what": what}
    if subject.startswith("应用："):
        rest = subject[3:]
        pids = {by_rel[f].meta.get("project") for f in files if f in by_rel and by_rel[f].kind == "app"}
        pid = pids.pop() if len(pids) == 1 else None
        m = re.match(r"^登记了 (\d+) 个(.*)$", rest)
        if m:
            what = f"登记了 {m.group(1)} 个应用{m.group(2)}"
        elif "改名为" in rest:
            what = "把应用" + rest
        else:
            what = rest.replace("「", "应用「", 1)
        return {"type": "应用", "project": pid, "what": what}
    if subject.startswith("首页草稿："):
        return {"type": "首页", "project": None, "what": f"给你的首页提了一份草稿：{subject[5:]}"}
    if subject.startswith("首页："):
        return {"type": "首页", "project": None, "what": subject[3:]}
    created = re.match(r"^项目：新建「(.+)」$", subject)
    updated = re.match(r"^项目：更新「(.+)」的(.+)$", subject)
    if created or updated:
        pid = next((f.split("/")[1] for f in files if f.startswith("projects/") and f.count("/") >= 2), None)
        if created:
            what = f"新建了项目「{created.group(1)}」"
        else:
            what = f"更新了「{updated.group(1)}」的{updated.group(2)}"
        return {"type": "编辑", "project": pid, "what": what}
    stopped = re.match(r"^停用成员「(.+)」$", subject)
    if stopped:
        return {"type": "成员", "project": None, "what": f"停用了成员「{stopped.group(1)}」"}
    restored = re.match(r"^恢复成员「(.+)」$", subject)
    if restored:
        return {"type": "成员", "project": None, "what": f"恢复了成员「{restored.group(1)}」"}
    pids = {f.split("/")[1] for f in files if f.startswith("projects/") and f.count("/") >= 2}
    pid = pids.pop() if len(pids) == 1 else None
    if pid in by_id:  # “示例项目：定位改为……”→“改了「示例项目」：定位改为……”
        pname = short(by_id[pid].title)
        if subject.startswith(pname + "："):
            return {"type": "其他", "project": pid, "what": f"改了「{pname}」：{subject[len(pname) + 1:]}"}
    return {"type": "其他", "project": pid, "what": subject}


def commit_row(c: dict, by_id, by_rel, members, viewer=None) -> dict:
    author = local_who(c["author"], members, viewer)
    info = classify(c["subject"], c["files"], by_id, by_rel)
    pid = info["project"]
    return {
        "sha": c["sha"], "short": c["sha"][:7], "when": c["when"], "who": author,
        "type": info["type"], "what": info["what"], "excerpt": info.get("excerpt"),
        "project": project_ref(pid, by_id, short, keep_missing=True),
        "files": [{"path": f, "label": file_label(f, by_rel)} for f in c["files"]],
        "reverted_by": c["reverted_by"][:7] if c["reverted_by"] else None,
        "reverts": c["reverts"][:7] if c["reverts"] else None, "is_root": c["is_root"],
        "subject": c["subject"],
    }


def added_text(diff: list[dict]) -> str | None:
    """进展这类只往后追加的改动：把新加的那一段拿出来（去掉自动加的标题行）。"""
    files = [f for f in diff if f["path"].endswith("/status.md")]
    if len(files) != 1 or any(l["t"] == "del" for l in files[0]["lines"]):
        return None
    lines = [l["s"] for l in files[0]["lines"] if l["t"] == "add"]
    while lines and (not lines[0].strip() or lines[0].startswith("# ") or lines[0].startswith("只往后追加")):
        lines.pop(0)
    if lines and lines[0].startswith("## "):
        lines.pop(0)
    text = "\n".join(lines).strip()
    return text or None


# ---------- 项目健康 ----------

def project_activity(commits: list[dict]) -> dict[str, dict]:
    """每个项目最近一次被改动的时间、谁改的，以及最近 30 天改了几次。"""
    out: dict[str, dict] = {}
    now = dt.datetime.now(TAIPEI)
    for c in commits:  # 新的在前
        pids = {f.split("/")[1] for f in c["files"] if f.startswith("projects/") and f.count("/") >= 2}
        for pid in pids:
            rec = out.setdefault(pid, {"last": None, "last_by": None, "count30": 0})
            if rec["last"] is None:
                rec["last"], rec["last_by"] = c["when"], c["author"]
            if (now - taipei(c["when"])).days < 30:
                rec["count30"] += 1
    return out


def project_row(d: Doc, today: dt.date, act: dict | None, members, status_body: str | None = None) -> dict:
    meta = d.meta
    as_of = _as_date(meta.get("summary_as_of"))
    days = (today - as_of).days if as_of else None
    roles = meta.get("roles") or []
    lead = next((r for r in roles if r.get("key")), roles[0] if roles else None)
    auth = meta.get("authority") if isinstance(meta.get("authority"), dict) else None
    return {
        "id": d.id, "name": short(d.title), "title": d.title, "state": meta.get("state"), "goal": meta.get("goal"),
        "lead": {"role": lead.get("role"), "who": short(lead.get("who")), "who_full": lead.get("who")} if lead else None,
        "roles": [{"role": r.get("role"), "who": r.get("who"), "key": bool(r.get("key")),
                   "since": _as_date(r.get("since")).isoformat() if _as_date(r.get("since")) else None} for r in roles],
        "summary_as_of": as_of.isoformat() if as_of else None, "summary_days": days,
        "newer_status": newer_status_count(meta.get("summary_as_of"), status_body),
        "stale": days is None or days > STALE_DAYS,
        "authority": {"label": auth.get("label"), "path": auth.get("path")} if auth else None,
        "sensitivity": d.sensitivity, "access_note": meta.get("access_note"), "problem": d.problem,
        "last": act["last"] if act else None,
        "last_by": local_who(act["last_by"], members)["name"] if act and act["last_by"] else None,
        "count30": act["count30"] if act else 0,
    }


STATE_ORDER = {"进行中": 0, "维护中": 1, "构想中": 2, "暂停": 3, "已结束": 4, "历史": 5}


def projects(visible: list[Doc], commits: list[dict], members, today: dt.date, states=()) -> list[dict]:
    act = project_activity(commits)
    status_body = {d.rel.split("/")[1]: d.body for d in visible if d.kind == "project-status"}
    rows = [project_row(d, today, act.get(d.id), members, status_body.get(d.id))
            for d in visible if d.kind == "project"]
    if states:
        rows = [r for r in rows if r["state"] in states]
    rows.sort(key=lambda r: (STATE_ORDER.get(r["state"], 9), -(taipei(r["last"]).timestamp() if r["last"] else 0)))
    return rows


# ---------- 要你处理的事 ----------

def attention(visible: list[Doc], by_id, project_rows: list[dict]) -> list[dict]:
    items = []
    for p in pending_handovers(visible, by_id):
        items.append({"kind": "批准", "title": f"「{p['project_name']}」的{p['role']}要换人",
                      "detail": f"从 {short(p['from'])} 换成 {short(p['to'])}（{p['head']} 申请）。"
                                "在“待批准”里点批准或不批。",
                      "project": p["project"]})
    for r in project_rows:
        if r["state"] in ("历史", "已结束"):
            continue
        if r["stale"]:
            when = f"{r['summary_days']} 天前" if r["summary_days"] is not None else "没写日期"
            items.append({"kind": "过时", "title": f"「{r['name']}」的现状摘要可能过时了",
                          "detail": f"摘要是 {when}写的。" + (f"负责的是 {r['lead']['who']}。" if r["lead"] else ""),
                          "project": r["id"]})
    for d in visible:
        if d.problem and d.kind not in ("project-status", "project-links"):
            items.append({"kind": "标记", "title": f"{d.rel} 的开头标记有问题", "detail": d.problem, "rel": d.rel})
    return items


# ---------- 写入量 ----------

def activity_chart(commits: list[dict], days: int, name_of, *, skip_owner: bool) -> dict:
    """按天计数。跳不跳过 主人、作者叫什么，由两边投影各自决定。"""
    today = dt.datetime.now(TAIPEI).date()
    start = today - dt.timedelta(days=days - 1)
    per_day = Counter()
    per_who = Counter()
    week_start = today - dt.timedelta(days=6)
    for c in commits:
        if skip_owner and c["author"].split("·")[0].strip() == config.owner_key():
            continue
        day = taipei(c["when"]).date()
        if day >= start:
            per_day[day] += 1
        if day >= week_start:
            per_who[name_of(c["author"])] += 1
    series = [{"date": (start + dt.timedelta(days=i)).isoformat(), "count": per_day[start + dt.timedelta(days=i)]}
              for i in range(days)]
    return {"days": days, "series": series, "total": sum(per_day.values()),
            "by_who": [{"name": n, "count": k} for n, k in per_who.most_common()]}


# ---------- 搜索和单份文件 ----------

def _plain(md: str) -> str:
    """搜索摘要只要字：去掉标题井号、列表符号、加粗星号、表格竖线、代码反引号、链接网址。"""
    md = re.sub(r"(?m)^\s{0,3}(#{1,6}|>|[-*+]|\d+\.)\s+", "", md)
    md = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", md)
    md = re.sub(r"(?m)^\s*\|?\s*:?-{3,}.*$", "", md)
    return re.sub(r"[*`|]+", " ", md)


def search(visible: list[Doc], query: str, limit: int = 30) -> list[dict]:
    terms = [t.lower() for t in query.split() if t.strip()]
    if not terms:
        return []
    hits = []
    for d in visible:
        if d.kind == "home":
            continue
        full = f"{d.title}\n{d.body}"
        text = full.lower()
        if not all(t in text for t in terms):
            continue
        flat = re.sub(r"\s+", " ", _plain(_strip_title(d.body)))
        pos = flat.lower().find(terms[0])
        snippet = flat[max(0, pos - 40): pos + 80] if pos >= 0 else flat[:120]
        in_title = all(t in d.title.lower() for t in terms)
        pid = d.rel.split("/")[1] if d.rel.startswith("projects/") else None
        if d.kind == "person" and d.meta.get("archived") is True:
            continue
        hits.append({"kind": KIND_LABEL.get(d.kind, d.kind), "title": d.title if d.kind != "project-status" else f"{d.title} · 进展",
                     "rel": d.rel, "project": pid, "snippet": snippet, "score": (0 if in_title else 1, d.rel)})
    hits.sort(key=lambda h: h["score"])
    for h in hits:
        h.pop("score")
    return hits[:limit]


def doc_view(d: Doc) -> dict:
    meta = d.meta
    facts = []
    if d.kind == "decision":
        facts = [("日期", meta.get("date")), ("谁定的", meta.get("decided_by")), ("谁记的", meta.get("recorded_by"))]
    elif d.kind == "person":
        facts = [("身份", meta.get("agent_key")), ("角色", meta.get("role")), ("能写入", "是" if meta.get("can_write") else "否")]
    return {"rel": d.rel, "kind": KIND_LABEL.get(d.kind, d.kind), "title": d.title,
            "facts": [{"k": k, "v": str(v)} for k, v in facts if v],
            "sensitivity": d.sensitivity, "access_note": meta.get("access_note"), "problem": d.problem,
            "html": to_html(_strip_title(d.body))}


# ---------- 待办 ----------

def todo_view(d: Doc, today: dt.date, by_id: dict[str, Doc], members) -> dict:
    m = d.meta
    pid = m.get("project")
    due = _as_date(m.get("due"))
    by = m.get("created_by")
    row = {
        "id": d.id, "rel": d.rel, "title": d.title, "status": m.get("status", "open"),
        "bucket": todos.bucket(d, today), "due": due.isoformat() if due else None, "time": todos.time_of(m),
        "project": project_ref(pid, by_id, short, keep_missing=True),
        "notes": todos.notes_of(d), "notes_html": to_html(todos.notes_of(d)) if todos.notes_of(d) else None,
        "created_by": local_who(by, members)["name"] if by else None,
        "created": str(m.get("created") or ""), "done_at": str(m.get("done_at") or "") or None,
        "repeat": m.get("repeat") if m.get("repeat") in todos.REPEATS else None,
        "problem": d.problem,
    }
    if m.get("ask"):
        row.update({key: m.get(key) for key in ("ask", *todos.ASK_FIELDS, "notified_at")})
        row["notice"] = todos.notice(m)
        row["notified_by"] = local_who(m["notified_by"], members)["name"] if m.get("notified_by") else None
    return row


def todo_list(visible: list[Doc], today: dt.date, by_id, members) -> list[dict]:
    items = sorted((d for d in visible if d.kind == "todo"), key=todos.sort_key)
    return [todo_view(d, today, by_id, members) for d in items]


# ---------- 应用广场 ----------

def app_view(d: Doc, by_id: dict[str, Doc], members) -> dict:
    m = d.meta
    pid = m.get("project")
    by = m.get("registered_by")
    notes = apps.notes_of(d)
    return {
        "id": d.id, "rel": d.rel, "title": d.title, "one_line": m.get("one_line"), "kind": m.get("kind"),
        "icon": m.get("icon"), "listed": m.get("listed", True) is not False,
        "project": project_ref(pid, by_id, short, keep_missing=True),
        "maintainer": m.get("maintainer"), "open": m.get("open") or {}, "code_path": m.get("code_path"),
        "probe_process": m.get("probe_process"), "status_note": m.get("status_note"), "links": m.get("links") or [],
        "notes_html": to_html(notes) if notes else None, "updated": str(m.get("updated") or ""),
        "registered_by": local_who(by, members)["name"] if by else None,
        "problem": d.problem,
    }


def project_app_cards(visible: list[Doc], project_id: str) -> list[dict]:
    """项目详情里的应用：只要这个身份已经看得到的，不含路径和启动命令。"""
    rows = []
    for d in visible:
        if d.kind != "app" or d.problem or d.meta.get("project") != project_id:
            continue
        m = d.meta
        rows.append({"id": d.id, "title": d.title, "one_line": m.get("one_line") or "",
                     "kind": m.get("kind") or "", "icon": m.get("icon") if isinstance(m.get("icon"), str) else "",
                     "listed": m.get("listed", True) is not False})
    rows.sort(key=lambda a: (not a["listed"], a["title"]))
    return rows


def app_list(visible: list[Doc], by_id, members) -> list[dict]:
    items = [app_view(d, by_id, members) for d in visible if d.kind == "app"]
    return sorted(items, key=lambda a: (not a["listed"], apps.KINDS.index(a["kind"]) if a["kind"] in apps.KINDS else 9, a["title"]))


# ---------- 管理页：资料、决定、成员 ----------

def doc_list(visible: list[Doc]) -> list[dict]:
    items = [{"rel": d.rel, "kind": KIND_LABEL[d.kind], "kind_key": d.kind, "id": d.id, "title": d.title,
              "sensitivity": d.sensitivity, "visible_to": d.meta.get("visible_to") or [],
              "access_note": d.meta.get("access_note"), "updated": str(d.meta.get("updated") or ""), "problem": d.problem}
             for d in visible if d.kind in ("handbook", "asset")]
    return sorted(items, key=lambda x: (x["kind_key"] != "handbook", x["title"]))


def decision_list(visible: list[Doc], by_id, members) -> list[dict]:
    items = []
    for d in visible:
        if d.kind != "decision":
            continue
        m = d.meta
        pid = m.get("project")
        by = m.get("recorded_by")
        decider = str(m.get("decided_by") or "")
        decider = short(members[decider.lower()].title) if decider.lower() in members else decider
        items.append({"rel": d.rel, "title": d.title, "date": str(m.get("date") or ""), "decided_by": decider or None,
                      "project": project_ref(pid, by_id, short, keep_missing=True),
                      "recorded_by": local_who(by, members)["name"] if by else None,
                      "sensitivity": d.sensitivity, "access_note": m.get("access_note"), "problem": d.problem})
    return sorted(items, key=lambda x: (x["date"], x["rel"]), reverse=True)


def member_list(visible: list[Doc], token_rows: list[dict] | None = None) -> list[dict]:
    use_feishu = feishu.enabled()
    # 只收窄网页投影，不改盘上的绑定和会话，也不改变认证与收回规则。
    token_rows = [t for t in token_rows or []
                  if use_feishu or (t.get("kind") != "feishu" and t.get("auth_method") != "feishu")]
    titles = {x.id: x.title for x in visible if x.kind in ("project", "asset")}
    if any(d.kind == "person" and d.meta.get("access_mode", "assigned") == "assigned" for d in visible):
        titles |= {r["id"]: r["name"] if r["registered"] else r["skill_name"] or r["id"]
                   for r in skillscan.summarize(skillscan.cached_scan(), skills.records_of(visible))}
    owners = {x.meta.get("agent_key"): short(x.title) for x in visible if x.kind == "person"}
    items = []
    for d in visible:
        if d.kind != "person":
            continue
        m = d.meta
        viewer = make_viewer(visible, m.get("agent_key"), allow_owner=True)
        item = {"rel": d.rel, "key": m.get("agent_key"), "name": short(d.title), "title": d.title,
                "kind": m.get("kind"), "role": m.get("role"), "can_write": bool(m.get("can_write")),
                "connect": m.get("connect"), "sensitivity": d.sensitivity, "problem": d.problem,
                "remote_enabled": m.get("remote_enabled", False), "access_mode": viewer.access_mode,
                "archived": m.get("archived") is True,
                "owner_key": m.get("owner") if isinstance(m.get("owner"), str) else None}
        item |= {"projects": [titles.get(x, x) for x in m.get("projects") or []],
                 "assets": [titles.get(x, x) for x in m.get("assets") or []],
                 "skills": [titles.get(x, x) for x in m.get("skills") or []],
                 "owner": owners.get(m.get("owner"), m.get("owner")) if m.get("owner") else None,
                 "tokens": [t for t in token_rows or [] if t["key"] == item["key"] and t.get("kind") == "token"],
                 "sessions": [t for t in token_rows or [] if t["key"] == item["key"] and t.get("kind") != "token"]}
        if use_feishu:
            bound = m.get("feishu_open_id")
            item["feishu_bound"] = isinstance(bound, str) and bool(bound.strip())
        items.append(item)
    return sorted(items, key=lambda x: (x["kind"] == "human", x["name"]))


# ---------- Skill 广场 ----------

def skill_page(visible: list[Doc], by_id, fresh: bool = False, include_unlisted: bool = False) -> dict:
    data = skillscan.cached_scan(fresh)
    recs = {d.id: d for d in visible if d.kind == "skill"}
    rows = skillscan.summarize(data, {k: d.meta for k, d in recs.items()})
    if not include_unlisted:
        rows = [r for r in rows if r["listed"]]
    members = member_keys(visible)
    viewers = [(d.title, make_viewer(visible, key, allow_owner=True)) for key, d in members.items()]
    for r in rows:
        d = recs.get(r["id"])
        m = d.meta if d else {}
        pid = m.get("project")
        notes = skills.notes_of(d) if d else ""
        r["record"] = {
            "one_line": m.get("one_line"), "origin": m.get("origin"), "maintainer": m.get("maintainer"),
            "upstream": m.get("upstream"), "upstream_version": m.get("upstream_version"), "home": m.get("home"),
            "project": project_ref(pid, by_id, short, keep_missing=True),
            "notes_html": to_html(notes) if notes else None, "rel": d.rel,
        } if d else None
        authorized = [title for title, viewer in viewers
                      if viewer.assigned
                      and viewer.valid and r["id"] in viewer.skills]
        public = bool(d and not d.problem and d.sensitivity == "public")
        r["sharing"] = {"public": public, "members": authorized}
        if public or authorized:
            package = skillshare.package_manifest(r["ref"]["path"]) if r["ref"] else skillshare.Package()
            r["sharing"] |= {"withheld": package.withheld, "too_large": package.too_large,
                              "available": r["listed"] and package.skill_md is not None and r["alert"] != "broken"}
    harnesses = [{"key": h["key"], "name": h["name"], "snapshot": h["snapshot"], "roots": h["roots"],
                  "builtin": h["builtin"], "plugins": h["plugins"], "install_dir": skills.install_dir(h["key"])}
                 for h in data["harnesses"]]
    return {"scanned_at": data["scanned_at"], "shared_path": data["shared"]["path"], "harnesses": harnesses, "skills": rows}


# ---------- 我的 Agent ----------

# MCP 工具名和命令行子命令 → 人话
TOOL_NAMES = {
    "company": "看入职说明", "whoami": "看自己的身份", "projects": "看项目一览", "list_projects": "看项目一览",
    "project": "看项目", "get_project": "看项目", "search": "搜索", "handbook": "看手册", "get_handbook": "看手册",
    "assets": "看资料一览", "list_assets": "看资料一览", "asset": "看资料", "get_asset": "看资料",
    "decisions": "看决定", "list_decisions": "看决定", "status": "记进展", "update_project_status": "记进展",
    "decide": "记决定", "record_decision": "记决定", "handover": "交接角色", "handover_role": "交接角色",
    "home": "看首页布局", "get_home_layout": "看首页布局", "propose-home": "提首页草稿", "propose_home": "提首页草稿",
    "todos": "看待办", "list_todos": "看待办", "todo-add": "加待办", "add_todos": "加待办",
    "todo-update": "改待办", "update_todo": "改待办", "apps": "看应用", "list_apps": "看应用", "app": "看应用",
    "get_app": "看应用", "app-register": "登记应用", "register_app": "登记应用", "app-update": "改应用",
    "project-create": "新建项目", "create_project": "新建项目",
    "update_app": "改应用", "skills": "看 Skill", "list_skills": "看 Skill", "skill": "看 Skill", "get_skill": "看 Skill",
    "skill-register": "登记 Skill", "register_skill": "登记 Skill", "skill-update": "改 Skill", "update_skill": "改 Skill",
    "check": "检查数据",
}

def agent_rows(visible: list[Doc], commits: list[dict], members, used: dict[str, dict], viewer,
               token_rows: list[dict] | None = None) -> list[dict]:
    if not (viewer.known and viewer.valid):
        return []
    agents = [d for d in members.values() if not d.problem and d.meta.get("archived") is not True
              and d.meta.get("kind") == "agent" and d.meta.get("owner") == viewer.workspace_owner]
    keys = {d.meta["agent_key"] for d in agents}
    used = {k: u for k, u in used.items() if k in keys}
    now = dt.datetime.now(TAIPEI)
    writes: dict[str, dict] = {}
    for c in commits:  # 新的在前
        key, _, signed = (s.strip() for s in c["author"].partition("·"))
        if key not in keys:
            continue
        rec = writes.setdefault(key, {"last": None, "recent": 0, "names": {}})
        if rec["last"] is None:
            rec["last"] = c["when"]
        if (now - taipei(c["when"])).days < 7:
            rec["recent"] += 1
        if signed:
            rec["names"].setdefault(signed, c["when"])
    rows = []
    for d in agents:
        m = d.meta
        key = m.get("agent_key")
        w, u = writes.get(key, {}), used.get(key, {})
        rows.append({
            "key": key, "name": short(d.title), "title": d.title, "kind": m.get("kind"), "role": m.get("role"),
            "can_write": make_viewer(list(members.values()), key).can_write,
            "connect": m.get("connect"), "rel": d.rel,
            "tokens": [{field: t.get(field) for field in
                        ("id", "label", "created", "expires", "revoked", "tail", "issued_by")}
                       for t in token_rows or [] if t.get("key") == key and t.get("kind", "token") == "token"],
            "last_write": w.get("last"), "writes_7d": w.get("recent", 0),
            "last_use": u.get("last"), "last_tool": TOOL_NAMES.get(u.get("last_tool"), u.get("last_tool")),
            "last_via": u.get("last_via"),
            "calls_7d": u.get("recent", 0),
            "names": [n for n, _ in sorted(w.get("names", {}).items(), key=lambda x: x[1], reverse=True)][:8],
        })
    rows.sort(key=lambda r: (r["kind"] == "human", -max(taipei(t).timestamp() for t in (r["last_use"], r["last_write"]) if t)
                             if (r["last_use"] or r["last_write"]) else 0))
    return rows
