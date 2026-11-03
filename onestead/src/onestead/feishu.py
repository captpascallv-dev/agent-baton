"""飞书登录：读本机配置、向飞书换令牌、记下还没登记的人。

配置目录默认 %LOCALAPPDATA%\\Onestead（已有安装继续用旧状态目录），测试用环境变量 ONESTEAD_FEISHU_DIR 指到临时目录。
App Secret 只在换令牌时交给飞书，不进日志、页面、异常消息、调用记录和数据仓库。
"""

from __future__ import annotations

from .i18n import text as tr

import json
import logging
import os
import re
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from contextlib import contextmanager
from pathlib import Path

from .gitops import file_lock
from . import config
from .store import StorageError, now_taipei

log = logging.getLogger(__name__)

AUTHORIZE = "https://accounts.feishu.cn/open-apis/authen/v1/authorize"
TOKEN_URL = "https://accounts.feishu.cn/oauth/v3/token"
USER_INFO_URL = "https://open.feishu.cn/open-apis/authen/v1/user_info"
USER_FAIL = "飞书登录没成功，请再试一次"
PENDING_KEEP = 50
_CODE = re.compile(r"^[A-Za-z0-9_.-]{1,64}$")
_PENDING_FIELDS = ("open_id", "union_id", "name", "tenant_key", "first_seen", "last_seen", "count")


class FeishuError(Exception):
    """换令牌或取用户信息失败。消息里没有密钥、授权码、令牌。"""

    def __init__(self, code: object):
        self.code = code
        super().__init__(USER_FAIL)


def config_dir() -> Path:
    custom = config.env("FEISHU_DIR")
    if custom:
        return Path(custom)
    return config.state_dir()


def _read(path: Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeError):
        return None


def _app_id(text: str) -> str:
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("app_id="):
            return line.split("=", 1)[1].strip()
    return ""


def load_app() -> dict | None:
    """配好了返回 {app_id, secret}，否则 None。调用方不得把 secret 写出去。"""
    folder = config_dir()
    login = _read(folder / "feishu-login.txt")
    secret_text = _read(folder / "feishu-secret.txt")
    if login is None or secret_text is None:
        return None
    app_id = _app_id(login)
    secret = secret_text.replace("\ufeff", "").strip()
    if not app_id or not secret:
        return None
    return {"app_id": app_id, "secret": secret}


def enabled() -> bool:
    """可选登录入口只以现有配置读取结果为准，不另存开关。"""
    return load_app() is not None


def ready() -> bool:
    return enabled()


def authorize_url(app_id: str, redirect_uri: str, state: str) -> str:
    query = urllib.parse.urlencode((
        ("client_id", app_id),
        ("response_type", "code"),
        ("redirect_uri", redirect_uri),
        ("state", state),
    ))
    return f"{AUTHORIZE}?{query}"


def log_failure(step: str, code: object) -> None:
    """只记错误码。不像密钥、授权码、令牌、说明文字那样整段写进日志。"""
    if isinstance(code, bool) or code is None:
        text = "unknown"
    elif isinstance(code, int):
        text = str(code)
    elif isinstance(code, str) and _CODE.fullmatch(code):
        text = code
    else:
        text = "unknown"
    log.warning("飞书登录失败：%s code=%s", step, text)


def _request_json(req: urllib.request.Request) -> dict:
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            raw = resp.read()
    except urllib.error.HTTPError as e:
        try:
            raw = e.read()
        except Exception:
            raw = b""
    except Exception:
        log_failure("请求", "network")
        raise FeishuError("network") from None
    try:
        data = json.loads(raw.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError, TypeError):
        log_failure("请求", "bad_json")
        raise FeishuError("bad_json") from None
    if not isinstance(data, dict):
        log_failure("请求", "bad_json")
        raise FeishuError("bad_json")
    return data


def _failure_code(data: dict) -> object:
    if "code" in data and data.get("code") != 0:
        return data.get("code")
    if "error" in data:
        return data.get("error")
    return "unknown"


def _exchange_code(app: dict, code: str, redirect_uri: str) -> str:
    """用授权码换 access_token。测试用 monkeypatch 换掉，不真的联网。"""
    body = urllib.parse.urlencode({
        "grant_type": "authorization_code",
        "client_id": app["app_id"],
        "client_secret": app["secret"],
        "code": code,
        "redirect_uri": redirect_uri,
    }).encode("utf-8")
    req = urllib.request.Request(
        TOKEN_URL, data=body, method="POST",
        headers={"Content-Type": "application/x-www-form-urlencoded"},
    )
    data = _request_json(req)
    token = data.get("access_token")
    if data.get("code") == 0 and isinstance(token, str) and token:
        return token
    err = _failure_code(data)
    log_failure("换令牌", err)
    raise FeishuError(err)


def _user_info(access_token: str) -> dict:
    """用访问令牌取用户信息。测试用 monkeypatch 换掉，不真的联网。"""
    req = urllib.request.Request(
        USER_INFO_URL, method="GET",
        headers={"Authorization": f"Bearer {access_token}"},
    )
    data = _request_json(req)
    info = data.get("data")
    if data.get("code") == 0 and isinstance(info, dict):
        open_id = info.get("open_id")
        if isinstance(open_id, str) and open_id.strip():
            return info
        log_failure("用户信息", "no_open_id")
        raise FeishuError("no_open_id")
    err = _failure_code(data)
    log_failure("用户信息", err)
    raise FeishuError(err)


def _pending_path() -> Path:
    return config_dir() / "feishu-pending.json"


def _load_pending() -> list[dict]:
    path = _pending_path()
    try:
        items = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return []
    except (OSError, ValueError) as e:
        raise StorageError(tr("agent.255", v0=path, v1=type(e).__name__)) from None
    return items if isinstance(items, list) else []


@contextmanager
def _editing():
    path = _pending_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    with file_lock(path.with_name(path.name + ".lock")):
        yield


def _save_pending(items: list[dict]) -> None:
    path = _pending_path()
    clean = [{k: item.get(k) for k in _PENDING_FIELDS} for item in items]
    fd, tmp = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(json.dumps(clean, ensure_ascii=False, indent=1))
        os.replace(tmp, path)
    except BaseException:
        Path(tmp).unlink(missing_ok=True)
        raise


def _text(value) -> str:
    return value.strip() if isinstance(value, str) else ""


def list_pending() -> list[dict]:
    """最新的在前。"""
    return [dict(item) for item in reversed(_load_pending())]


def contains(open_id: str) -> bool:
    return any(item.get("open_id") == open_id for item in _load_pending())


def remember(info: dict) -> None:
    """同一个 open_id 只留一条：更新最后一次和次数，最多留最新 50 条。"""
    open_id = info.get("open_id")
    if not isinstance(open_id, str) or not open_id.strip():
        raise FeishuError("no_open_id")
    now = now_taipei().isoformat(timespec="seconds")
    with _editing():
        loaded = _load_pending()
        previous = next((i for i in loaded if i.get("open_id") == open_id), None)
        items = [i for i in loaded if i.get("open_id") != open_id]
        try:
            count = int((previous or {}).get("count") or 0)
        except (TypeError, ValueError):
            count = 0
        current = {
            "open_id": open_id,
            "union_id": _text(info.get("union_id")) or _text((previous or {}).get("union_id")),
            "name": _text(info.get("name")) or _text((previous or {}).get("name")),
            "tenant_key": _text(info.get("tenant_key")) or _text((previous or {}).get("tenant_key")),
            "first_seen": (previous or {}).get("first_seen") or now,
            "last_seen": now,
            "count": count + 1,
        }
        items.append(current)
        if len(items) > PENDING_KEEP:
            items = items[-PENDING_KEEP:]
        _save_pending(items)


def dismiss(open_id: str) -> bool:
    with _editing():
        items = _load_pending()
        kept = [i for i in items if i.get("open_id") != open_id]
        if len(kept) == len(items):
            return False
        _save_pending(kept)
        return True
