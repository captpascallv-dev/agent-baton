"""同事接远程接口用的口令。

- 口令只在发出时显示一次，本机只存它的 SHA-256（口令本身是 32 字节随机数，不需要慢哈希），
  放在 %LOCALAPPDATA%\\Onestead\\tokens.json（已有安装继续用旧状态目录），不进 git、不进数据仓库。
- 只发给允许远程接入的成员（remote_enabled: true），与内容权限无关。
- 发和收回由本人或 主人 在网页上做（manage 核归属），MCP 和命令行没有发口令的入口。
- 用口令接进来时，还要现场核对那个身份仍允许远程接入；成员记录改回只在本机用、删掉或写错，口令立刻失效。
- kind=token 是 主人 发的口令（不过期，远程接口和网页都能用）。kind=feishu 是飞书登录的旧会话，
  只为旧记录保留。新的网页登录用 kind=web，30 天绝对到期，只给网页 cookie 接口。
  旧记录没有 kind 的按口令兼容；未知种类完全拒绝。
"""

from __future__ import annotations

from .i18n import text as tr

import datetime as dt
import hashlib
import hmac
import json
import os
import secrets
import tempfile
from contextlib import contextmanager
from pathlib import Path

from .gitops import file_lock
from . import config
from .store import TAIPEI, BaseError, StorageError, now_taipei

PREFIX = "cbx_"


def store_path() -> Path:
    custom = config.env("TOKENS")
    if custom:
        return Path(custom)
    return config.state_dir() / "tokens.json"


def _digest(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def _load(path: Path | None = None) -> list[dict]:
    path = path or store_path()
    try:
        items = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return []
    except (OSError, ValueError) as e:
        raise StorageError(tr("agent.588", v0=store_path(), v1=type(e).__name__)) from None
    return items if isinstance(items, list) else []


@contextmanager
def _editing():
    """发、收回都是“读—改—存”，整段排队：不然一边收回、一边发新的，后存的会把收回冲掉。"""
    path = store_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    with file_lock(path.with_name(path.name + ".lock")):
        yield


def _save(items: list[dict], path: Path | None = None) -> None:
    path = path or store_path()
    fd, tmp = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(json.dumps(items, ensure_ascii=False, indent=1))
        os.replace(tmp, path)
    except BaseException:
        Path(tmp).unlink(missing_ok=True)
        raise


def _kind(item: dict) -> str | None:
    # 只有真正缺字段的旧记录兼容为口令，错误值不能获得机器接入能力。
    kind = item.get("kind", "token")
    return kind if isinstance(kind, str) and kind in ("token", "feishu", "web", "oauth") else None


def _expired(item: dict) -> bool:
    raw = item.get("expires")
    if raw in (None, ""):
        return False
    if not isinstance(raw, str):
        return True
    try:
        when = dt.datetime.fromisoformat(raw)
    except ValueError:
        return True
    if when.tzinfo is None:
        when = when.replace(tzinfo=TAIPEI)
    return now_taipei() >= when


def _public(item: dict) -> dict:
    out = {k: item.get(k) for k in ("id", "key", "label", "created", "revoked", "tail", "issued_by")}
    out["kind"] = _kind(item)
    out["expires"] = item.get("expires")
    return out


def issue(key: str, label: str, kind: str = "token", days: int | None = None,
          *, issued_by: str | None = None) -> tuple[str, dict]:
    """发一个新口令或飞书会话，返回（原文, 记录）。原文只此一次。
    days 给出时，expires 是到期时间（台北时间）；口令不传 days，expires 为 None。"""
    if kind not in ("token", "feishu"):
        raise BaseError(tr("agent.585"))
    now = now_taipei()
    expires = (now + dt.timedelta(days=days)).isoformat(timespec="minutes") if days is not None else None
    token = PREFIX + secrets.token_urlsafe(32)
    record = {"id": secrets.token_hex(4), "key": key, "label": label.strip()[:60], "kind": kind,
              "created": now.isoformat(timespec="minutes"), "expires": expires, "revoked": None,
              "tail": token[-4:], "sha256": _digest(token), "issued_by": issued_by}
    with _editing():
        items = _load()
        items.append(record)
        _save(items)
    return token, _public(record)


def revoke(token_id: str, *, authorize=None) -> dict:
    """锁内按公开记录核授权，再收回；未知 ID 和无权 ID 使用相同错误。"""
    with _editing():
        items = _load()
        for item in items:
            if item.get("id") == token_id:
                if authorize is not None and not authorize(_public(item)):
                    break
                if not item.get("revoked"):
                    item["revoked"] = now_taipei().isoformat(timespec="minutes")
                    _save(items)
                return _public(item)
    raise BaseError(tr("agent.584"))


def revoke_key(key: str, kind: str | None = None) -> int:
    """收回这个身份还有效的口令。kind 给出时只收回那一类（例如只收回飞书会话）。返回收回了几个。"""
    with _editing():
        items = _load()
        live = [i for i in items if i.get("key") == key and not i.get("revoked")
                and (kind is None or _kind(i) == kind)]
        if live:
            stamp = now_taipei().isoformat(timespec="minutes")
            for item in live:
                item["revoked"] = stamp
            _save(items)
    return len(live)


def list_tokens(key: str | None = None) -> list[dict]:
    items = _load()
    # OAuth 的登记、挑战、码和刷新令牌是内部记录，不出现在成员页。
    internal = ("oauth_client", "oauth_pending", "oauth_code", "oauth_refresh")
    return [_listed(i, items) for i in items if i.get("kind") not in internal and (key is None or i.get("key") == key)]


def _row_live(item: dict, items: list[dict]) -> bool:
    """成员页上这一行现在还能不能用。网页会话的源口令死了，会话行本身没收回也算失效。"""
    if _kind(item) is None or item.get("revoked") or _expired(item):
        return False
    if _kind(item) == "web" and item.get("auth_method") == "token":
        return _source_live(items, item.get("source_token_id"), item.get("key"))
    if _kind(item) == "oauth":
        return _source_live(items, item.get("source_token_id"), item.get("key"))
    return True


def _listed(item: dict, items: list[dict]) -> dict:
    out = _public(item)
    out["live"] = _row_live(item, items)
    if out["kind"] == "web":
        out["auth_method"] = item.get("auth_method")
    return out


def _live(token: str) -> dict | None:
    """对上的那一条；不认识、已收回或已过期视为没有。逐条用定长比较，不提前退出。"""
    if not isinstance(token, str) or not token.startswith(PREFIX):
        return None
    want = _digest(token)
    found = None
    items = _load()
    for item in items:
        if (_kind(item) is not None and hmac.compare_digest(str(item.get("sha256", "")), want)
                and _row_live(item, items)):
            found = item
    return found


def lookup(token: str) -> str | None:
    """口令 → 身份；不认识、已收回或已过期返回 None。"""
    item = _live(token)
    if item is None:
        return None
    key = item.get("key")
    return key if isinstance(key, str) else None


def lookup_record(token: str) -> dict | None:
    """按随机值取无哈希的记录，含认证依赖的 ID；已收回、到期或未知种类返回 None。"""
    item = _live(token)
    if item is None:
        return None
    return {**_public(item), **{k: item.get(k) for k in ("auth_method", "source_token_id", "binding_id")}}


def _source_live(items: list[dict], source_id: str, key: str) -> bool:
    return any(i.get("id") == source_id and i.get("key") == key and _kind(i) == "token"
               and not i.get("revoked") and not _expired(i) for i in items)


def issue_web(key: str, auth_method: str, *, source_token_id: str | None = None,
              binding_id: str | None = None) -> tuple[str, dict]:
    """独立网页会话；撤旧、核源口令和发新在同一凭据锁内完成。调用方先持有数据写锁。"""
    if auth_method not in ("token", "feishu"):
        raise BaseError(tr("agent.586"))
    if (auth_method == "token" and not source_token_id) or (auth_method == "feishu" and not binding_id):
        raise BaseError(tr("agent.587"))
    now = now_taipei()
    stamp = now.isoformat(timespec="seconds")
    value = PREFIX + secrets.token_urlsafe(32)
    record = {"id": secrets.token_hex(8), "key": key, "kind": "web", "label": "网页登录",
              "sha256": _digest(value), "tail": value[-4:], "created": stamp,
              "expires": (now + dt.timedelta(days=30)).isoformat(timespec="seconds"), "revoked": None,
              "auth_method": auth_method, "source_token_id": source_token_id, "binding_id": binding_id}
    with _editing():
        items = _load()
        if auth_method == "token" and not _source_live(items, source_token_id, key):
            raise BaseError(tr("agent.589"))
        for item in items:
            if item.get("key") == key and _kind(item) == "web" and not item.get("revoked"):
                item["revoked"] = stamp
        items.append(record)
        _save(items)
    return value, _public(record)


def lookup_web(value: str) -> dict | None:
    """网页只认 web，且口令登录依赖的源口令当前仍有效；绑定和成员由适配器现场核对。"""
    record = lookup_record(value)
    if not record or record["kind"] != "web" or not record["expires"]:
        return None
    method = record["auth_method"]
    if method == "token":
        if not _source_live(_load(), record["source_token_id"], record["key"]):
            return None
    elif method != "feishu" or not record["binding_id"]:
        return None
    return record


def web_session(session_id: str, key: str) -> dict | None:
    """子域名只按来源 ID 核对会话，不读取或接受底座的 cookie。"""
    items = _load()
    item = next((i for i in items if i.get("id") == session_id and i.get("key") == key
                 and _kind(i) == "web" and i.get("expires") and _row_live(i, items)), None)
    if not item or item.get("auth_method") not in ("token", "feishu"):
        return None
    if item["auth_method"] == "feishu" and not item.get("binding_id"):
        return None
    return {**_public(item), "auth_method": item["auth_method"], "binding_id": item.get("binding_id")}


def revoke_web(key: str, auth_method: str | None = None) -> int:
    """按认证来源收回网页会话，飞书解绑不能误收回口令登录。"""
    with _editing():
        items = _load()
        live = [i for i in items if i.get("key") == key and _kind(i) == "web" and not i.get("revoked")
                and (auth_method is None or i.get("auth_method") == auth_method)]
        for item in live:
            item["revoked"] = now_taipei().isoformat(timespec="seconds")
        if live:
            _save(items)
    return len(live)


def _challenge_path() -> Path:
    return store_path().with_name(store_path().name + ".challenges.json")


def issue_challenge(purpose: str) -> str:
    """登录挑战独立于凭据白名单，只存哈希；与凭据复用原子写和文件锁。"""
    value = secrets.token_urlsafe(32)
    record = {"sha256": _digest(value), "purpose": purpose,
              "expires": (now_taipei() + dt.timedelta(minutes=10)).isoformat(timespec="seconds")}
    with _editing():
        items = [i for i in _load(_challenge_path()) if not _expired(i)]
        items.append(record)
        _save(items, _challenge_path())
    return value


def consume_challenge(value: str, purpose: str) -> bool:
    """先消费再做换码或登录；即使后续认证失败也不能重放。"""
    if not isinstance(value, str) or not value:
        return False
    want = _digest(value)
    with _editing():
        items = _load(_challenge_path())
        hit = [i for i in items if i.get("purpose") == purpose
               and hmac.compare_digest(str(i.get("sha256", "")), want)]
        valid = any(not _expired(i) for i in hit)
        if hit:
            _save([i for i in items if i not in hit], _challenge_path())
    return valid


# OAuth 与口令共用凭据文件和锁；随机值仅存摘要，不进入数据仓库。
OAUTH_CODE_SECONDS = 300
OAUTH_ACCESS_SECONDS = 3600
OAUTH_REFRESH_SECONDS = 30 * 86400
OAUTH_SOURCE_USED = "这个口令已经授权过一次。要重新连接，请在桌面版成员页给这个 agent 发一个新口令。"


def _oauth_new(kind: str, fields: dict, seconds: int) -> tuple[str, dict]:
    now = now_taipei()
    value = PREFIX + secrets.token_urlsafe(32)
    record = {**fields, "id": secrets.token_hex(16), "kind": kind,
              "sha256": _digest(value), "created": now.isoformat(timespec="seconds"),
              "expires": (now + dt.timedelta(seconds=seconds)).isoformat(timespec="seconds"),
              "revoked": None, "used": None}
    return value, record


def oauth_create(kind: str, fields: dict, seconds: int) -> tuple[str, dict]:
    value, record = _oauth_new(kind, fields, seconds)
    with _editing():
        items = _load()
        items.append(record)
        _save(items)
    return value, record


def _oauth_find(items: list[dict], value: str, kind: str) -> dict | None:
    if not isinstance(value, str) or not value.startswith(PREFIX):
        return None
    digest = _digest(value)
    return next((i for i in items if i.get("kind") == kind
                 and hmac.compare_digest(str(i.get("sha256", "")), digest)), None)


def oauth_get(value: str, kind: str) -> dict | None:
    with _editing():
        items = _load()
        item = _oauth_find(items, value, kind)
        if not item:
            return None
        # 用过的码、旧刷新令牌重放：撤销同一授权产生的整条令牌链。
        if item.get("used") and kind in ("oauth_code", "oauth_refresh"):
            _oauth_revoke_grant(items, item.get("grant_id", item["id"]))
            _save(items)
            return None
        if item.get("revoked") or item.get("used") or _expired(item):
            return None
        if kind in ("oauth", "oauth_code", "oauth_refresh"):
            if not _source_live(items, item.get("source_token_id"), item.get("key")):
                return None
        return dict(item)


def oauth_client(client_id: str) -> dict | None:
    return next((dict(i) for i in _load() if i.get("kind") == "oauth_client"
                 and i.get("client_id") == client_id), None)


def oauth_register(fields: dict) -> None:
    with _editing():
        items = _load()
        items.append({**fields, "kind": "oauth_client"})
        _save(items)


def oauth_authorize(pending_value: str, source: dict) -> str:
    """消费授权表单并发码。调用方在数据写锁内核对 agent；这里在凭据锁内核源口令。"""
    with _editing():
        items = _load()
        pending = _oauth_find(items, pending_value, "oauth_pending")
        if (not pending or pending.get("used") or _expired(pending)
                or not _source_live(items, source["id"], source["key"])):
            raise BaseError(tr("agent.590"))
        # 一个口令只授权一次：别人发来的授权页就算骗到一次，也没法再拿同一个口令开第二条授权；
        # 要重连就在桌面版发个新口令。发了码但没被换走、已经过期的那次没连上，不算。
        def counts(i):
            if i.get("source_token_id") != source["id"]:
                return False
            if i.get("kind") in ("oauth", "oauth_refresh"):
                return True
            return i.get("kind") == "oauth_code" and (bool(i.get("used")) or not (i.get("revoked") or _expired(i)))
        if any(counts(i) for i in items):
            raise BaseError(tr("agent.583"))
        fields = {k: pending[k] for k in ("client_id", "redirect_uri", "code_challenge", "resource", "scopes")}
        value, code = _oauth_new("oauth_code", {**fields, "key": source["key"],
                                               "source_token_id": source["id"]}, OAUTH_CODE_SECONDS)
        code["grant_id"] = code["id"]
        code["grant_expires"] = (now_taipei() + dt.timedelta(seconds=OAUTH_REFRESH_SECONDS)).isoformat(timespec="seconds")
        pending["used"] = now_taipei().isoformat(timespec="seconds")
        items.append(code)
        _save(items)
        return value


def _oauth_revoke_grant(items: list[dict], grant_id: str) -> None:
    for item in items:
        if item.get("grant_id") == grant_id:
            item["revoked"] = now_taipei().isoformat(timespec="seconds")


def oauth_revoke(value: str, kind: str) -> None:
    with _editing():
        items = _load()
        item = _oauth_find(items, value, kind)
        if item:
            _oauth_revoke_grant(items, item["grant_id"])
            _save(items)


def oauth_exchange(value: str, kind: str, client_id: str, resource: str, issuer: str,
                   scopes: list[str]) -> tuple[str, str]:
    """码和刷新令牌只消费一次，消费与发新令牌在同一锁中完成。"""
    with _editing():
        items = _load()
        item = _oauth_find(items, value, kind)
        if not item or item.get("client_id") != client_id or item.get("resource") != resource:
            raise BaseError(tr("agent.591"))
        if item.get("used"):
            _oauth_revoke_grant(items, item["grant_id"])
            _save(items)
            raise BaseError(tr("agent.591"))
        if (item.get("revoked") or _expired(item)
                or not _source_live(items, item.get("source_token_id"), item.get("key"))):
            raise BaseError(tr("agent.591"))
        item["used"] = now_taipei().isoformat(timespec="seconds")
        # 刷新同时轮换访问令牌；码重放时也能撤回刷新后的令牌。
        for old in items:
            if old.get("kind") == "oauth" and old.get("grant_id") == item["grant_id"]:
                old["revoked"] = item["used"]
        fields = {k: item[k] for k in ("key", "source_token_id", "client_id", "resource", "grant_id", "grant_expires")}
        fields |= {"issuer": issuer, "audience": resource, "scopes": scopes, "label": "ChatGPT 授权"}
        access, access_row = _oauth_new("oauth", fields, OAUTH_ACCESS_SECONDS)
        refresh, refresh_row = _oauth_new("oauth_refresh", fields, OAUTH_REFRESH_SECONDS)
        refresh_row["expires"] = fields["grant_expires"]
        items.extend([access_row, refresh_row])
        _save(items)
        return access, refresh
