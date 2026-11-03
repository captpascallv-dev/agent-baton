"""本机网页的后台：端口和钥匙跨重启沿用，刷新就能接上；钥匙满期才换。"""

import json
import os
import socket
import time

from onestead import config
from onestead.web import launcher

from .support import WEB_TOKEN


def _state():
    folder = config.state_dir()
    folder.mkdir(parents=True, exist_ok=True)
    return folder / "ui-session.json"


def test_session_token_survives_restarts_until_it_expires():
    path = _state()
    first = launcher._session_token()
    assert launcher._session_token() == first
    saved = json.loads(path.read_text(encoding="utf-8"))
    path.write_text(json.dumps({"token": first, "created": saved["created"] - launcher.TOKEN_DAYS * 86400 - 1}),
                    encoding="utf-8")
    renewed = launcher._session_token()
    assert renewed != first and len(renewed) >= 32
    path.write_text("坏了", encoding="utf-8")
    assert launcher._session_token() not in (first, renewed)
    path.write_text(json.dumps({"token": "short", "created": time.time()}), encoding="utf-8")
    assert launcher._session_token() != "short"


def test_key_files_are_private_and_rewritten_whole():
    path = _state()
    launcher._write_private(path, "很长很长的旧内容" * 10)
    launcher._write_private(path, "新")
    assert path.read_text(encoding="utf-8") == "新"
    if os.name != "nt":
        assert path.stat().st_mode & 0o777 == 0o600


def test_bind_uses_the_fixed_port_and_falls_back_when_taken(monkeypatch):
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    free = probe.getsockname()[1]
    probe.close()
    monkeypatch.setattr(launcher, "UI_PORT", free)
    sock = launcher._bind()
    try:
        assert sock.getsockname() == ("127.0.0.1", free)
        other = launcher._bind()
        try:
            assert other.getsockname()[1] not in (free, 0)
        finally:
            other.close()
    finally:
        sock.close()


def test_open_cookie_outlives_the_browser_session(client):
    from starlette.testclient import TestClient

    from .support import WEB_BASE

    r = TestClient(client.app, base_url=WEB_BASE, follow_redirects=False).get(f"/open?t={WEB_TOKEN}")
    assert r.status_code == 303
    cookie = r.headers["set-cookie"]
    assert f"Max-Age={30 * 86400}" in cookie and "HttpOnly" in cookie and "SameSite=strict" in cookie
