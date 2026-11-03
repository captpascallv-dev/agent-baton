"""真实走一遍 MCP：拉起服务进程，列工具，调 company 和一次没登记身份的过滤。"""

from .support import OWNER_NAME

import asyncio
import os
import sys

from mcp.client.session import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client


async def _call(repo, agent, tool, args=None):
    env = dict(os.environ, COMPANY_BASE_DATA=str(repo), COMPANY_BASE_AGENT=agent)
    params = StdioServerParameters(command=sys.executable, args=["-m", "onestead.server"], env=env)
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            listed = await session.list_tools()
            tools = {t.name: t.description or "" for t in listed.tools}
            result = await session.call_tool(tool, args or {})
            text = "".join(getattr(c, "text", "") for c in result.content)
            return tools, result.is_error, text


def test_mcp_tools_and_filtering(repo):
    tools, is_error, text = asyncio.run(_call(repo, "codex", "company"))
    assert {"company", "get_project", "search", "update_project_status", "update_project_summary",
            "record_decision", "handover_role", "get_home_layout", "propose_home",
            "create_project"} <= tools.keys()
    assert "现状、下一步、分工、入口变了时用这个改正文；进展照常用 update_project_status 记。" in tools["update_project_summary"]
    assert f"{OWNER_NAME} 或项目负责人让你开新项目时用；建完用 handover_role 把担任者登记上。" in tools["create_project"]
    assert not is_error and "入职说明" in text and "阿尔法" in text

    _, is_error, text = asyncio.run(_call(repo, "stranger", "get_asset", {"asset_id": "sample"}))
    assert is_error and "找不到" in text
