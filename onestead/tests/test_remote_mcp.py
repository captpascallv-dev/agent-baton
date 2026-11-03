"""远程 MCP 接口测试。真起一个 HTTP 服务，用真的 MCP 客户端带口令连；身份由口令决定，不靠自报。"""

import asyncio
import json
import socket
import threading
import time

import httpx2
import pytest
import uvicorn
from mcp.client.session import ClientSession
from mcp.client.streamable_http import streamable_http_client
from mcp.shared._httpx_utils import create_mcp_http_client

from onestead import tokens
from onestead.server import remote_app
from onestead.store import BaseError

from .conftest import git


@pytest.fixture(autouse=True)
def token_store(tmp_path, monkeypatch):
    path = tmp_path / "tokens.json"
    monkeypatch.setenv("COMPANY_BASE_TOKENS", str(path))
    return path


@pytest.fixture
def remote(ext, repo):  # noqa: F811
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    server = uvicorn.Server(uvicorn.Config(remote_app(str(repo), port), log_level="warning"))
    thread = threading.Thread(target=server.run, kwargs={"sockets": [sock]}, daemon=True)
    thread.start()
    deadline = time.monotonic() + 10
    while not server.started and time.monotonic() < deadline:
        time.sleep(0.05)
    yield f"http://127.0.0.1:{port}/mcp"
    server.should_exit = True
    thread.join(5)


async def _call(url, token, tool, args=None):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    async with create_mcp_http_client(headers=headers) as client:
        async with streamable_http_client(url, http_client=client) as (read, write):
            async with ClientSession(read, write) as session:
                await session.initialize()
                result = await session.call_tool(tool, args or {})
                return result.is_error, "".join(getattr(c, "text", "") for c in result.content)


def call(url, token, tool, args=None):
    return asyncio.run(_call(url, token, tool, args))


def status_code(url, token):
    headers = {"Accept": "application/json, text/event-stream", "Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    body = {"jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "1"}}}
    return httpx2.post(url, headers=headers, content=json.dumps(body)).status_code


def test_without_valid_token_is_refused(remote):
    assert status_code(remote, None) == 401
    assert status_code(remote, "cbx_not-a-real-token") == 401


def test_identity_comes_from_token(remote):
    token, _ = tokens.issue("ext-zhang", "张三的电脑")
    is_error, text = call(remote, token, "whoami")
    assert not is_error and "ext-zhang" in text and "按授权访问的同事" in text
    _, text = call(remote, token, "list_projects")
    assert "阿尔法" in text and "贝塔" not in text
    is_error, text = call(remote, token, "list_todos")
    assert not is_error and "暗号公开待办" not in text


def test_remote_write_is_authored_by_token_identity(remote, repo):
    token, _ = tokens.issue("ext-zhang-claude", "张三的 Claude")
    is_error, text = call(remote, token, "update_project_status",
                          {"project_id": "alpha", "text": "远程记的进展", "signed_as": "张三的 Claude"})
    assert not is_error and "已记录到" in text
    assert git(repo, "log", "-1", "--format=%an") == "ext-zhang-claude · 张三的 Claude"


def test_token_for_local_partner_is_never_accepted(remote):
    # 发口令的地方（网页）只给允许远程接入的同事发；就算有一条给了本机伙伴，远程接口也不认，免得借口令拿到全部内容
    token, _ = tokens.issue("codex", "不该有的")
    assert status_code(remote, token) == 401


def test_revoked_token_stops_working(remote):
    token, record = tokens.issue("ext-zhang", "临时")
    assert status_code(remote, token) == 200
    tokens.revoke(record["id"])
    assert status_code(remote, token) == 401


def test_token_stops_when_member_no_longer_external(remote, repo):
    token, _ = tokens.issue("ext-li", "李四")
    assert status_code(remote, token) == 200
    path = repo / "people" / "ext-li.md"
    path.write_text(path.read_text(encoding="utf-8").replace("remote_enabled: true\naccess_mode: assigned\n", ""), encoding="utf-8")
    assert status_code(remote, token) == 401


def test_tokens_are_stored_hashed(token_store):
    token, record = tokens.issue("ext-zhang", "看存储")
    raw = token_store.read_text(encoding="utf-8")
    assert token not in raw and record["tail"] == token[-4:]
    assert tokens.lookup(token) == "ext-zhang"
    assert tokens.lookup(token + "x") is None


def test_remote_only_listens_on_loopback(repo):
    with pytest.raises(BaseError, match="只监听本机"):
        remote_app(str(repo), 8765, host="0.0.0.0")
