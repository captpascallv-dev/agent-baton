"""网页专用的一次性码和会话，与底座口令白名单分开，只存随机值的哈希。"""

from __future__ import annotations

import datetime as dt
import hmac
import secrets
from contextlib import contextmanager

from . import config, gitops, tokens
from .store import make_viewer, now_taipei


def path():
    return config.state_dir() / "site-sessions.json"


@contextmanager
def editing():
    file = path()
    file.parent.mkdir(parents=True, exist_ok=True)
    with gitops.file_lock(file.with_suffix(".lock")):
        yield


def new_record(kind: str, fields: dict, seconds: int):
    value = secrets.token_urlsafe(32)
    now = now_taipei()
    record = {**fields, "kind": kind, "sha256": tokens._digest(value),
              "expires": (now + dt.timedelta(seconds=seconds)).isoformat(timespec="seconds")}
    return value, record


def issue_code(slug: str, key: str, source: str, *, preview: str | None = None) -> str:
    value, record = new_record("code", {"slug": slug, "key": key, "source": source, "preview": preview},
                               600 if preview else 60)
    with editing():
        items = [i for i in tokens._load(path()) if not tokens._expired(i)]
        tokens._save([*items, record], path())
    return value


def authorized(record: dict, site: dict, docs) -> bool:
    from .sites import can_open
    if record.get("slug") != site.get("slug") or site.get("status") != "已发布":
        return False
    key = record.get("key")
    if record.get("source") == "主人预览":
        viewer = make_viewer(docs, key, allow_owner=True)
        return (key == config.owner_key() and viewer.valid and viewer.member is not None
                and viewer.member.meta.get("kind") == "human" and not viewer.member.meta.get("archived")
                and bool(record.get("preview")) and any(grant.get("id") == record["preview"]
                    and not tokens._expired(grant) for grant in site.get("previews", [])))
    if not can_open(site, key, docs):
        return False
    source = tokens.web_session(record.get("source"), key)
    if not source:
        return False
    viewer = make_viewer(docs, key, remote=True)
    return source["auth_method"] != "feishu" or viewer.member.meta.get("feishu_open_id") == source["binding_id"]


def find(items: list[dict], value: str, kind: str) -> dict | None:
    if not value:
        return None
    digest = tokens._digest(value)
    return next((item for item in items if item.get("kind") == kind
                 and hmac.compare_digest(str(item.get("sha256", "")), digest)), None)


def exchange(value: str, site: dict, docs) -> str | None:
    """消费与发会话在同一跨进程锁内；错短名、失效来源也会烧掉这个码。"""
    with editing():
        items = tokens._load(path())
        record = find(items, value, "code")
        if not record:
            return None
        items.remove(record)
        result = None
        if not tokens._expired(record) and authorized(record, site, docs):
            result, session = new_record("session", {k: record[k] for k in ("slug", "key", "source", "preview")}, 7 * 86400)
            items.append(session)
        tokens._save(items, path())
        return result


def session(value: str, site: dict, docs) -> dict | None:
    # 只读热路径不拿锁；无效时锁内重新读再删除，不能覆盖同时签发的新会话。
    record = find(tokens._load(path()), value, "session")
    if record and not tokens._expired(record) and authorized(record, site, docs):
        return record
    if record:
        revoke(value)
    return None


def revoke(value: str) -> None:
    if not value:
        return
    with editing():
        items = tokens._load(path())
        record = find(items, value, "session")
        if record:
            items.remove(record)
            tokens._save(items, path())
