"""读取数据仓库：解析每个文件开头的标记，判断谁能看。

规则见数据仓库里的 FORMAT.md。这里只做两件事：
1. 把文件读成 Doc（标记 + 正文），标记有问题的记下原因；
2. 按内容授权判断能不能看，远程接入资格另行检查。
"""

from __future__ import annotations

from .i18n import text as tr

from . import config

import datetime as dt
import io
import re
from dataclasses import dataclass, field, replace
from pathlib import Path

import yaml

TAIPEI = dt.timezone(dt.timedelta(hours=8))  # 台北没有夏令时，固定 +8
LEVELS = ("public", "internal", "restricted")
LEVEL_NAMES = {"public": "公开", "internal": "内部", "restricted": "限定"}

# 主人身份留给网页界面，不能从 MCP 或命令行自报
PERSON_KEY_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,40}$")


def valid_person_key(key) -> bool:
    """身份键也用于个人首页路径；保留草稿后缀，禁止路径字符。"""
    return isinstance(key, str) and bool(PERSON_KEY_RE.fullmatch(key)) and not key.endswith("-draft")


class BaseError(Exception):
    """可预期的失败，消息直接给调用的 agent 看。"""


class NotFound(BaseError):
    """内容不存在或当前身份看不见。"""


class Forbidden(BaseError):
    """身份或权限不允许这次操作。"""


class Busy(BaseError):
    """写锁等不到：别的写入还没结束，稍后重试。"""


class AuthRejected(BaseError):
    """这次请求的会话或口令在落笔前已经失效。"""


class Conflict(BaseError):
    """版本对不上：不覆盖，请调用方重新打开。"""


class StorageError(BaseError):
    """存储层的失败（git、锁、口令文件）。消息里可能带本机路径，远程侧只给一句通用的。"""


def now_taipei() -> dt.datetime:
    return dt.datetime.now(TAIPEI)


@dataclass
class Doc:
    rel: str  # 相对数据仓库的路径，用 /
    kind: str  # handbook / project / project-status / project-links / asset / decision / person / home / todo / app
    meta: dict
    body: str  # 去掉开头标记后的正文
    raw: str  # 整个文件（统一成 \n 换行）
    problem: str | None = None  # 标记有问题时的原因

    @property
    def id(self) -> str:
        return str(self.meta.get("id") or "")

    @property
    def title(self) -> str:
        return str(self.meta.get("title") or self.id or self.rel)

    @property
    def sensitivity(self) -> str:
        return self.meta.get("sensitivity", "restricted")


def split_front(text: str) -> tuple[dict | None, str, str | None]:
    """拆出开头的 YAML 标记。返回（标记, 正文, 问题）。"""
    text = text.replace("\r\n", "\n").lstrip("﻿")
    if not text.startswith("---\n"):
        return None, text, tr("agent.530")
    end = text.find("\n---\n", 3)
    if end < 0:
        return None, text, tr("agent.531")
    try:
        meta = yaml.safe_load(text[4:end])
    except yaml.YAMLError:
        return None, text[end + 5 :], tr("agent.546")
    if not isinstance(meta, dict):
        return None, text[end + 5 :], tr("agent.532")
    return meta, text[end + 5 :], None


def check_meta(meta: dict, expected_type: str) -> str | None:
    """检查标记是否合规，返回问题描述；合规返回 None。"""
    if meta.get("type") != expected_type:
        return tr("agent.523", v0=expected_type)
    if not isinstance(meta.get("id"), str) or not meta["id"].strip():
        return tr("agent.524")
    level = meta.get("sensitivity")
    if level not in LEVELS:
        return tr("agent.525")
    if level == "restricted":
        names = meta.get("visible_to")
        if not isinstance(names, list) or not all(isinstance(x, str) for x in names):
            return tr("agent.533")
    if expected_type == "project":
        roles = meta.get("roles", [])
        if not isinstance(roles, list) or not all(
            isinstance(r, dict) and isinstance(r.get("role"), str) and isinstance(r.get("who"), str)
            for r in roles
        ):
            return tr("agent.534")
    if expected_type == "person":
        if not valid_person_key(meta.get("agent_key")):
            return tr("agent.535")
        # 旧 scope 完全忽略；新字段严格校验，不能把字符串 false 当作真。
        if not isinstance(meta.get("remote_enabled", False), bool):
            return tr("agent.536")
        if "archived" in meta and not isinstance(meta.get("archived"), bool):
            return tr("agent.537")
        if meta.get("access_mode", "assigned") not in ("assigned", "full"):
            return tr("agent.538")
        if meta.get("kind") not in ("human", "agent"):
            return tr("agent.539")
        if "can_write" in meta and not isinstance(meta["can_write"], bool):
            return tr("agent.540")
        for field in ("projects", "assets", "skills"):
            v = meta.get(field)
            if field in meta and not (isinstance(v, list) and all(isinstance(x, str) for x in v)):
                return tr("agent.547", v0=field)
        if meta.get("kind") == "human" and "owner" in meta:
            return tr("agent.541")
        if meta.get("kind") == "agent" and not (isinstance(meta.get("owner"), str) and meta["owner"].strip()):
            return tr("agent.542")
        if "feishu_open_id" in meta and not (
            isinstance(meta.get("feishu_open_id"), str) and meta["feishu_open_id"].strip()
        ):
            return tr("agent.543")
    if expected_type == "todo" and meta.get("status", "open") not in ("open", "done", "dropped"):
        return tr("agent.526")
    if expected_type == "project" and "paths" in meta and project_path_list(meta.get("paths")) is None:
        return tr("here.paths_invalid")
    return None


# 盘符、UNC、POSIX 根都算绝对路径。相对路径会跟着进程目录变，不能拿来认项目。
_ABSOLUTE_DIR = re.compile(r"^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+\\[^\\/]+|//[^/]+/[^/]+|/)")


def _is_absolute_dir(text: str) -> bool:
    return bool(text) and not any(ch in text for ch in "\r\n\x00") and _ABSOLUTE_DIR.match(text) is not None


def project_path_list(value) -> list[str] | None:
    """合规时返回去掉首尾空白的绝对路径；缺省、空列表或不合法都返回 None。"""
    if not isinstance(value, list) or not value:
        return None
    items = []
    for item in value:
        if not isinstance(item, str):
            return None
        text = item.strip()
        if not _is_absolute_dir(text):
            return None
        items.append(text)
    return items


def clean_project_paths(value) -> list[str] | None:
    """写入用。空白表示不记；给出了却不合规则拒绝。"""
    if value is None:
        return None
    if isinstance(value, str):
        value = [line.strip() for line in value.replace("\r\n", "\n").split("\n") if line.strip()]
        if not value:
            return None
    items = project_path_list(value)
    if not items:
        raise BaseError(tr("here.paths_invalid"))
    return items


def read_doc(root: Path, path: Path, expected_type: str) -> Doc:
    rel = path.relative_to(root).as_posix()
    raw = io.open(path, encoding="utf-8").read().replace("\r\n", "\n")
    meta, body, problem = split_front(raw)
    if meta is None:
        meta = {}
    elif problem is None:
        problem = check_meta(meta, expected_type)
    return Doc(rel=rel, kind=expected_type, meta=meta, body=body, raw=raw, problem=problem)


class Store:
    """每次调用都重新读盘：数据很小，这样 主人 手改的内容马上生效。"""

    def __init__(self, root: str | Path):
        self.root = Path(root)

    def load(self) -> list[Doc]:
        root = self.root
        if not (root / ".git").exists():
            raise StorageError(tr("agent.544", v0=root))
        docs: list[Doc] = []
        fmt = root / "FORMAT.md"
        if fmt.exists():
            docs.append(read_doc(root, fmt, "handbook"))
        for folder, kind in (("handbook", "handbook"), ("assets", "asset"),
                             ("decisions", "decision"), ("people", "person"), ("home", "home"),
                             ("todos", "todo"), ("apps", "app"), ("skills", "skill")):
            for path in sorted((root / folder).glob("*.md")):
                docs.append(read_doc(root, path, kind))
        for pdir in sorted(p for p in (root / "projects").glob("*") if p.is_dir()):
            docs.extend(self._load_project(pdir))
        _mark_duplicate_keys(docs)
        _mark_invalid_owners(docs)
        members = member_keys(docs)
        for d in docs:
            if d.kind not in ("todo", "home") or d.problem:
                continue
            owner = personal_owner(d)
            person = members.get(owner)
            if person is None or person.meta.get("kind") != "human":
                d.problem = tr("personal_owner_problem")
        return docs

    def _load_project(self, pdir: Path) -> list[Doc]:
        root = self.root
        main_path = pdir / "project.md"
        if main_path.exists():
            project = read_doc(root, main_path, "project")
            if project.problem is None and project.id != pdir.name:
                project.problem = tr("project_id_problem", name=pdir.name)
        else:
            project = Doc(rel=f"projects/{pdir.name}/project.md", kind="project",
                          meta={}, body="", raw="", problem=tr("project_missing"))
        out = [project] if main_path.exists() else []
        # status.md、links.md 跟随 project.md 的标记
        for name, kind in (("status.md", "project-status"), ("links.md", "project-links")):
            path = pdir / name
            if path.exists():
                raw = io.open(path, encoding="utf-8").read().replace("\r\n", "\n")
                out.append(Doc(rel=path.relative_to(root).as_posix(), kind=kind,
                               meta=dict(project.meta), body=raw, raw=raw, problem=project.problem))
        return out


def is_archived(meta: dict) -> bool:
    """停用是严格的布尔值；缺字段或 false 都还在用。"""
    return isinstance(meta, dict) and meta.get("archived") is True


def personal_owner(doc: Doc) -> str | None:
    """个人内容先认主人；仅缺字段的旧 主人 文件适用兼容。"""
    if doc.problem:
        return None
    if doc.kind == "todo":
        owner = doc.meta.get("owner", config.owner_key())
        return owner if valid_person_key(owner) else None
    if doc.kind == "home":
        legacy = doc.rel in (f"home/{config.owner_key()}.md", f"home/{config.owner_key()}-draft.md")
        owner = doc.meta.get("owner", config.owner_key() if legacy else None)
        if not valid_person_key(owner):
            return None
        draft = doc.rel == f"home/{owner}-draft.md"
        if not draft and doc.rel != f"home/{owner}.md":
            return None
        state = "draft" if draft else "active"
        if doc.meta.get("state", state if legacy else None) != state:
            return None
        if (not legacy or "owner" in doc.meta or "state" in doc.meta) and doc.id != owner + ("-draft" if draft else ""):
            return None
        return owner
    return None


@dataclass
class Viewer:
    """内容权限和请求环境分开；remote 只表示本次请求来自远程入口。"""

    key: str | None
    member: Doc | None
    is_owner: bool = False
    valid: bool = True
    access_mode: str = "assigned"
    remote: bool = False
    remote_enabled: bool = False
    projects: frozenset[str] = frozenset()
    assets: frozenset[str] = frozenset()
    skills: frozenset[str] = frozenset()
    names: frozenset[str] = frozenset()
    acl: str | None = None
    workspace_owner: str | None = None
    owner_can_write: bool = True
    owner_remote_enabled: bool = True
    project_docs: dict[str, Doc] = field(default_factory=dict, repr=False)

    def __post_init__(self):
        config.owner_key()

    @property
    def known(self) -> bool:
        return self.member is not None or self.is_owner

    @property
    def assigned(self) -> bool:
        return not self.is_owner and self.access_mode == "assigned"

    @property
    def can_remote(self) -> bool:
        return self.known and self.valid and not self.is_owner and self.remote_enabled and self.owner_remote_enabled

    @property
    def can_write(self) -> bool:
        if self.is_owner:
            return True
        return (self.known and self.valid and self.member.meta.get("can_write") is True
                and self.owner_can_write)

    def can_see(self, doc: Doc) -> bool:
        if not self.valid:
            return False
        # 个人隔离在 主人/full 全读之前；public 和项目授权都不能绕过。
        if doc.kind in ("todo", "home"):
            return self.known and self.workspace_owner is not None and personal_owner(doc) == self.workspace_owner
        # 停用成员只留在本机 主人 的成员页；同事、搜索和入职说明都不算“看得到”。
        if doc.kind == "person" and is_archived(doc.meta):
            return self.is_owner and not self.remote
        if self.known and (self.is_owner or not self.assigned):
            return True
        if doc.problem:
            return False
        if doc.kind in ("decision", "app") and doc.meta.get("project"):
            parent = self.project_docs.get(doc.meta["project"])
            if parent is None or not self.can_see(parent):
                return False
        if not self.known:
            return doc.sensitivity == "public"
        if doc.kind == "skill":
            return False  # 原始说明不开放；共享包走专用通道。
        if doc.kind == "person":
            return doc.meta.get("agent_key") in self.names
        if doc.sensitivity == "public":
            return True
        if doc.kind == "handbook":
            granted = True
        elif doc.kind in ("project", "project-status", "project-links"):
            granted = doc.id in self.projects
        elif doc.kind in ("decision", "app"):
            granted = doc.meta.get("project") in self.projects
        elif doc.kind == "asset":
            granted = doc.id in self.assets
        else:
            granted = False
        return granted and (doc.sensitivity != "restricted" or self.acl in (doc.meta.get("visible_to") or []))

    def visible(self, docs: list[Doc]) -> list[Doc]:
        visible = [d for d in docs if self.can_see(d)]
        projects = {d.id for d in visible if d.kind == "project"}
        out = []
        for d in visible:
            if d.kind == "todo" and d.meta.get("project") and d.meta["project"] not in projects:
                # 只遮读取投影；写入口从原始 docs 取回内容，避免改备注时丢掉旧关联。
                meta = dict(d.meta)
                meta.pop("project")
                raw = "---\n" + yaml.safe_dump(meta, allow_unicode=True, sort_keys=False) + "---\n" + d.body
                d = replace(d, meta=meta, raw=raw)
            out.append(d)
        return out


def member_cards(viewer: Viewer, docs: list[Doc]) -> list[dict]:
    """同事名录只投影四个字段；限定简卡仍按所属人的名单过滤。"""
    if not (viewer.known and viewer.valid):
        return []
    return [{"key": key, "title": d.title, "kind": d.meta["kind"], "role": d.meta.get("role") or ""}
            for key, d in member_keys(docs).items()
            if not is_archived(d.meta) and (not viewer.assigned or d.sensitivity != "restricted"
                                            or key in viewer.names or viewer.acl in (d.meta.get("visible_to") or []))]


def _id_list(value) -> frozenset[str]:
    if isinstance(value, list):
        return frozenset(x.strip() for x in value if isinstance(x, str) and x.strip())
    return frozenset()


def _mark_duplicate_keys(docs: list[Doc]) -> None:
    """两份成员记录写了同一个 agent_key 或同一个 feishu_open_id：都记成标记有问题。
    agent_key 重了谁也用不了这个身份，不能“后一份覆盖前一份”，否则手工加的同事会接走同名本机伙伴的限定名单和署名。
    飞书账号重了，两个身份都不能用这个账号登录网页。"""
    _mark_same(docs, "agent_key", tr("agent.527"))
    _mark_same(docs, "feishu_open_id", tr("agent.528"))


def _mark_same(docs: list[Doc], field: str, template: str) -> None:
    seen: dict[str, list[Doc]] = {}
    for d in docs:
        value = d.meta.get(field) if d.kind == "person" and not d.problem else None
        if isinstance(value, str) and value:
            seen.setdefault(value, []).append(d)
    for value, same in seen.items():
        if len(same) < 2:
            continue
        # 停用成员继续占着这个身份名（或飞书号）：只把后来的标成有问题，停用的那份还要能对上历史里的名字。
        occupied = [d for d in same if is_archived(d.meta)]
        fresh = [d for d in same if not is_archived(d.meta)]
        targets = fresh if occupied and fresh else same
        for d in targets:
            others = "、".join(x.rel for x in same if x is not d)
            d.problem = template.format(value=value, others=others)


def member_keys(docs: list[Doc]) -> dict[str, Doc]:
    """agent_key → 成员文件（只认标记合规的；重名的在读盘时已标成有问题）。"""
    out = {}
    for d in docs:
        if d.kind == "person" and not d.problem:
            key = d.meta.get("agent_key")
            if isinstance(key, str) and key:
                out[key] = d
    return out


def owner_name(docs: list[Doc]) -> str:
    """主人的显示名来自有效成员记录的 title；缺标题或成员时用配置的身份键。"""
    key = config.owner_key()
    member = member_keys(docs).get(key)
    return str(member.meta.get("title") or key) if member else key


def owner_name_at(root: str | Path) -> str:
    """登录页、工具说明和存储报错也能取名；仓库不可读时仍能用身份键报错。"""
    try:
        docs = Store(root).load()
    except (StorageError, OSError):
        return config.owner_key()
    return owner_name(docs)


def _mark_invalid_owners(docs: list[Doc]) -> None:
    members = member_keys(docs)
    for d in docs:
        if d.kind != "person" or d.problem or d.meta.get("kind") != "agent":
            continue
        owner = members.get(d.meta.get("owner"))
        if owner is None or owner.meta.get("kind") != "human" or "owner" in owner.meta:
            d.problem = tr("agent.545")


def make_viewer(docs: list[Doc], key: str | None, allow_owner: bool = False, *, remote: bool = False) -> Viewer:
    company_owner = config.owner_key()
    key = (key or "").strip() or None
    members = member_keys(docs)
    projects = {d.id: d for d in docs if d.kind == "project"}
    if key == company_owner and allow_owner:
        return Viewer(key=company_owner, member=members.get(company_owner), is_owner=True,
                      access_mode="full", workspace_owner=company_owner, acl=company_owner, remote=remote,
                      project_docs=projects)
    if key == company_owner:
        raise Forbidden(tr("agent.529", v0=company_owner))
    member = members.get(key) if key else None
    if member is None:
        # 已登记但无效的身份不给任何内容；从未登记的仍可看公开。
        invalid = any(d.kind == "person" and d.meta.get("agent_key") == key for d in docs) if key else False
        return Viewer(key=key, member=None, valid=not invalid, remote=remote, project_docs=projects)
    meta = member.meta
    if is_archived(meta):
        # 停用后这个身份什么都读不到、写不了，也不能远程进来。记录还在，历史署名对得上。
        return Viewer(key=key, member=member, valid=False, remote=remote, remote_enabled=False,
                      project_docs=projects)
    grants = {f: _id_list(meta.get(f)) for f in ("projects", "assets", "skills")}
    mode = meta.get("access_mode", "assigned")
    owner_key = key
    owner_write = owner_remote = True
    if meta["kind"] == "agent":
        owner_key = meta.get("owner")
        owner = members.get(owner_key)
        if owner is None or owner.meta.get("kind") != "human" or "owner" in owner.meta or is_archived(owner.meta):
            return Viewer(key=key, member=member, valid=False, remote=remote, owner_can_write=False,
                          project_docs=projects)
        if owner_key != company_owner and owner.meta.get("access_mode", "assigned") == "assigned":
            mode = "assigned"
            for f in grants:
                inherited = _id_list(owner.meta.get(f))
                grants[f] = inherited & grants[f] if f in meta else inherited
        owner_write = owner.meta.get("can_write") is True
        # 主人 本人不开远程；他名下的 agent 由各自的开关决定。
        owner_remote = owner_key == company_owner or owner.meta.get("remote_enabled", False)
    return Viewer(key=key, member=member, access_mode=mode, remote=remote,
                  remote_enabled=meta.get("remote_enabled", False), owner_remote_enabled=owner_remote,
                  names=frozenset({key, owner_key}), acl=owner_key, workspace_owner=owner_key,
                  owner_can_write=owner_write, project_docs=projects, **grants)
