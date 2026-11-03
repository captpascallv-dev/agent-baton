"""Deep Research 的文档接口；复用远程搜索的投影，外加主人点名开放的共享文件夹，不读取任意给定路径。"""

from .i18n import text as tr

from urllib.parse import quote

from pydantic import BaseModel

from . import folders
from .redact import safe_text
from .store import BaseError


class SearchResult(BaseModel):
    id: str
    title: str
    url: str


class SearchOutput(BaseModel):
    results: list[SearchResult]


class FetchOutput(BaseModel):
    id: str
    title: str
    text: str
    url: str
    metadata: dict[str, str]


def projected_items(base, terms, origin):
    viewer, docs, _ = base._ctx()
    by_rel = {d.rel: d for d in docs}
    by_member = {d.meta.get("agent_key"): d for d in docs if d.kind == "person" and not d.problem}
    by_skill = {d.id: d for d in docs if d.kind == "skill" and not d.problem}
    items = []
    for kind, title, ref, _, text in base._projected_hits(viewer, docs, terms):
        doc = by_rel.get(ref)
        if kind == "成员":
            doc = by_member.get(ref)
        elif kind == "Skill":
            doc = by_skill.get(ref)
        rel = doc.rel if doc else f"skills/{ref}.md"
        # 同一网页已有这些深链接；Skill 只打开共享说明，成员只给简卡。
        route = ("skills/" + quote(ref, safe="") if kind == "Skill" else
                 "members" if kind == "成员" else "docs/" + quote(rel, safe="/"))
        items.append({"id": rel, "title": safe_text(title), "text": safe_text(text),
                      "url": origin + "/#/" + route,
                      "metadata": {"type": kind, "source": "Onestead"}})
    return items


def _reader(base):
    viewer, _, _ = base._ctx()
    return viewer.key if viewer.known and viewer.valid else None


def _folder_url(origin, document_id):
    # 共享文件夹没有网页，给一个带文档 ID 的地址供引用
    return origin + "/#/folder/" + quote(document_id, safe="/")


def search(base, query, origin):
    terms = [term.lower() for term in (query or "").split()]
    if not terms:
        return SearchOutput(results=[])
    rows = [{k: row[k] for k in ("id", "title", "url")} for row in projected_items(base, terms, origin)[:50]]
    rows += [{"id": row["id"], "title": safe_text(row["title"]), "url": _folder_url(origin, row["id"])}
             for row in folders.search(_reader(base), terms, limit=50)]
    return SearchOutput(results=[SearchResult(**row) for row in rows])


def fetch(base, document_id, origin):
    if folders.parse_id(document_id or ""):
        # 共享文件夹：只在这个身份能读的文件夹里、按白名单规则解析，隐藏和不存在同一个错误。
        row = folders.fetch(_reader(base), document_id)
        if row is None:
            raise BaseError(tr("agent.353"))
        return FetchOutput(id=row["id"], title=safe_text(row["title"]), text=safe_text(row["text"]),
                           url=_folder_url(origin, row["id"]), metadata=row["metadata"])
    # 只按可见投影里的精确 ID 找，不做文件系统路径解析，隐藏和不存在同一个错误。
    row = next((row for row in projected_items(base, [], origin) if row["id"] == document_id), None)
    if row is None:
        raise BaseError(tr("agent.353"))
    return FetchOutput(**row)
