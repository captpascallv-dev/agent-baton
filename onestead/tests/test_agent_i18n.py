"""第二单：公司语言下的 agent／CLI 文案及两处网页改动；只用隔离样本。"""

import asyncio
import io
import json
import re
from pathlib import Path
from string import Formatter
from types import SimpleNamespace
from unittest.mock import patch
from urllib.parse import urlencode

import pytest
import segno

from onestead import config, gitops, sites
from onestead.base import CompanyBase, remote_message
from onestead.cli import build_parser, run
from onestead.connect import connect_prompt
from onestead.i18n import TEXT, text
from onestead.server import INSTRUCTIONS, build_server
from onestead.store import BaseError, StorageError, Store, check_meta
from onestead.textutil import _check_text
from .support import APP, FIXED_NOW, OWNER, OWNER_NAME, WEB_POST_HEADERS, WORKSPACE_ORIGIN, git

CJK = re.compile(r"[\u3400-\u9fff]")


def fields(value):
    return {name for _, name, _, _ in Formatter().parse(value) if name is not None}


def test_server_catalog_keys_and_parameters_match():
    zh = {key: pair[0] for key, pair in TEXT.items()}
    en = {key: pair[1] for key, pair in TEXT.items()}
    assert zh.keys() == en.keys()
    assert all(len(pair) == 2 and all(isinstance(v, str) and v for v in pair) for pair in TEXT.values())
    for key in TEXT:
        assert fields(zh[key]) == fields(en[key]), key
        assert not CJK.search(en[key]), key


@pytest.mark.parametrize("remote,oauth", [(False, False), (True, False), (True, True)])
@pytest.mark.parametrize("lang", ["zh", "en"])
def test_mcp_metadata_uses_company_language(repo, monkeypatch, remote, oauth, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    mcp = build_server(str(repo), "claude", remote=remote,
                       oauth_provider=SimpleNamespace() if oauth else None)
    assert mcp.name == "company-base"  # MCP 对外名字不改
    assert "OS" in mcp.instructions and "Onestead" in mcp.instructions
    assert ("第一次用" if lang == "zh" else "On first use") in mcp.instructions
    assert text(INSTRUCTIONS, title=text("mcp.title_default"), owner_name=OWNER_NAME) == mcp.instructions
    tools = asyncio.run(mcp.list_tools())
    assert tools and all(tool.description for tool in tools)
    assert all(tool.input_schema for tool in tools)
    for tool in tools:
        if lang == "en":
            assert not CJK.search(tool.description), tool.name
        # 包括 signed_as、OAuth search/fetch 和 publish_site 的每个参数。
        for name, spec in tool.input_schema.get("properties", {}).items():
            assert spec.get("description"), (tool.name, name)
            if lang == "en":
                assert not CJK.search(spec["description"]), (tool.name, name)
    if lang == "en":
        assert not CJK.search(mcp.instructions + mcp.title)
    project = next(t for t in tools if t.name == "create_project")
    assert project.input_schema["properties"]["state"]["default"] == "构想中"


@pytest.mark.parametrize("lang", ["en", "zh"])
def test_common_errors_follow_language(as_agent, repo, monkeypatch, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    base = as_agent("claude")
    operations = [lambda: base.get_project("missing"), lambda: base.search(""),
                  lambda: _check_text("标题", ""), lambda: sites.enabled(),
                  lambda: CompanyBase(repo, OWNER).company()]
    expected = (["Not found", "Provide search terms", "Title cannot be empty", "not enabled", "reserved"]
                if lang == "en" else ["找不到", "请给出关键词", "标题不能为空", "没开应用发布", "留给网页界面"])
    for operation, phrase in zip(operations, expected):
        with pytest.raises(BaseError) as error:
            operation()
        assert phrase in str(error.value)
        if lang == "en":
            assert not CJK.search(str(error.value))
    assert ("type must be" if lang == "en" else "type 应为") in check_meta({}, "project")
    hidden = remote_message(StorageError("不应暴露 C:\\private\\data"), repo)
    assert ("storage problem" if lang == "en" else "存储出了问题") in hidden
    assert "private" not in hidden and str(repo) not in hidden


@pytest.mark.parametrize("lang", ["zh", "en"])
def test_config_errors_use_language_without_recursion(monkeypatch, isolated_config, capsys, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    monkeypatch.delenv("COMPANY_BASE_OWNER")
    assert run(["company", "--as", "claude"]) == 2
    assert ("not configured" if lang == "en" else "还没配置") in capsys.readouterr().err
    isolated_config.write_text("bad = [", encoding="utf-8")
    assert run(["company", "--as", "claude"]) == 2
    assert "TOML" in capsys.readouterr().err
    monkeypatch.delenv("ONESTEAD_LANG")
    monkeypatch.delenv("COMPANY_BASE_LANG")
    isolated_config.write_text('lang = "zh"\nowner_key = false', encoding="utf-8")
    with pytest.raises(BaseError, match="配置 owner_key 要是文字"):
        config.owner_key()


@pytest.mark.parametrize("lang", ["zh", "en"])
def test_cli_all_help_and_common_parse_prompts(monkeypatch, capsys, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    parser = build_parser()
    helpers = [parser] + list(next(a for a in parser._actions if hasattr(a, "choices") and isinstance(a.choices, dict)).choices.values())
    for helper in helpers:
        help_text = helper.format_help()
        assert ("用法：" if lang == "zh" else "usage:") in help_text
        assert ("显示帮助并退出" if lang == "zh" else "show this help message and exit") in help_text
        if lang == "en":
            assert not CJK.search(help_text)
    with pytest.raises(SystemExit) as exit:
        run(["init"])
    assert exit.value.code == 2
    assert ("缺少必需参数" if lang == "zh" else "arguments are required") in capsys.readouterr().err
    with pytest.raises(SystemExit):
        run(["todos", "--scope", "invalid"])
    assert ("选项无效" if lang == "zh" else "invalid choice") in capsys.readouterr().err
    prompt = connect_prompt("Ada")
    assert "OS" in prompt and "ONESTEAD_AGENT" in prompt and "company()" in prompt
    if lang == "en":
        assert not CJK.search(prompt)


@pytest.mark.parametrize("lang", ["en", "zh"])
def test_generated_onboarding_keeps_user_body(as_agent, monkeypatch, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    base = as_agent("claude")
    value = base.company()
    assert ("Onboarding" if lang == "en" else "入职说明") in value
    assert ("## Members" if lang == "en" else "## 成员") in value
    assert "我们是一家测试公司。" in value and "规则在别处。" in value and "阿尔法" in value
    assert ("Available home modules" if lang == "en" else "可用的首页模块") in base.home_layout()
    assert ("Your to-dos" if lang == "en" else "你的待办") in base.list_todos()


def test_language_switch_does_not_change_data_or_commit_messages(repo, tmp_path, monkeypatch):
    # 同一份输入、相同台北时间分别在两份隔离仓库执行，比较完整提交说明和所有追踪文件。
    import shutil
    second = tmp_path / "other-data"
    shutil.copytree(repo, second)
    results = []
    for root, lang in ((repo, "zh"), (second, "en")):
        monkeypatch.setenv("ONESTEAD_LANG", lang)
        base = CompanyBase(root, "claude", now=lambda: FIXED_NOW)
        base.create_project("same-project", "用户名称", "用户目标")
        base.update_project_status("same-project", "用户进展")
        base.update_project_summary("same-project", {"现状": "用户现状", "下一步": "用户下一步"})
        base.record_decision("用户决定", "决定正文", "用户理由", "Ada", project_id="same-project")
        base.handover_role("same-project", "ordinary", "Ada", "用户原因")
        base.register_app(APP)
        base.update_app(APP["id"], {"listed": False})
        base.add_todos([{"title": "用户待办", "repeat": "daily", "due": "2026-10-10"}])
        todo = next(d for d in Store(root).load() if d.kind == "todo")
        base.update_todo(todo.id, {"status": "done"})
        base.register_skill({"id": "same-skill", "one_line": "用户说明", "origin": "自建"})
        base.update_skill("same-skill", {"one_line": "用户新说明"})
        base.propose_home([{"kind": "projects"}], "用户首页")
        files = {rel: (root / rel).read_bytes() for rel in git(root, "ls-files").splitlines()}
        results.append((git(root, "log", "--format=%B", "-12"), files))
    assert results[0] == results[1]
    assert "通过Onestead写入" in results[0][0] and "进展：" in results[0][0]


@pytest.mark.parametrize("lang", ["zh", "en"])
def test_login_owner_hint_only_for_valid_web_return(workspace, monkeypatch, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    marker = "如果你是" if lang == "zh" else "If you are"
    for target in (None, "/", "/projects", "https://evil.test/sites/enter", "//evil.test/sites/enter", "/sites/enter-evil"):
        url = "/login" + ("?" + urlencode({"return_to": target}) if target is not None else "")
        assert marker not in workspace.get(url).text
    target = "/sites/enter?slug=demo&next=%2Freport"
    response = workspace.get("/login?" + urlencode({"return_to": target}))
    assert response.status_code == 200 and marker in response.text and OWNER_NAME in response.text
    assert ("在手机上打开" if lang == "zh" else "Open on phone") in response.text
    assert "name=\"token\"" in response.text  # 同事仍然用同一表单
    failed = workspace.post("/auth/login", headers={"Origin": WORKSPACE_ORIGIN},
                            data={"challenge": "invalid", "token": "fake", "return_to": target})
    assert failed.status_code == 403 and marker in failed.text


def test_login_owner_hint_escapes_name(repo, monkeypatch):
    from .support import change
    from onestead.web.auth import RemoteAuth
    change(repo, f"people/{OWNER}.md", title="Ada <script>bad()</script>")
    monkeypatch.setenv("ONESTEAD_LANG", "en")
    page = RemoteAuth(repo, "workspace.example.test").login_page(return_to="/sites/enter?slug=demo")
    assert b"&lt;script&gt;" in page.body and b"<script>bad()" not in page.body


@pytest.mark.parametrize("lang", ["en", "zh"])
def test_phone_svg_encodes_exact_returned_url_and_is_not_persisted(ext, repo, tmp_path, client, monkeypatch, caplog, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    monkeypatch.setenv("COMPANY_BASE_SITE_DOMAIN", "example.com")
    build = tmp_path / "build"
    build.mkdir()
    (build / "index.html").write_text("<h1>Sample</h1>", encoding="utf-8")
    agent = ext("claude")
    agent.register_app(APP)
    agent.publish_site(APP["id"], str(build), "demo")
    owner = CompanyBase(repo, OWNER, allow_owner=True)
    owner.manage_site(APP["id"], {"action": "publish", "visibility": "所有远程同事"})
    with patch("segno.make_qr", wraps=segno.make_qr) as make:
        response = client.post(f"/api/apps/{APP['id']}/site", headers=WEB_POST_HEADERS, json={"action": "phone"})
    assert response.status_code == 200 and response.headers["cache-control"] == "no-store"
    result = response.json()
    assert result["qr_svg"].startswith("<svg") and "http://www.w3.org/2000/svg" in result["qr_svg"]
    make.assert_called_once_with(result["url"], error="m")
    expected = io.BytesIO()
    segno.make_qr(result["url"], error="m").save(expected, kind="svg", scale=5, border=4, xmldecl=False)
    assert result["qr_svg"] == expected.getvalue().decode("utf-8")
    assert ("10 minutes" if lang == "en" else "10 分钟") in result["message"]
    for doc in Store(repo).load():
        assert result["url"] not in doc.raw and result["qr_svg"] not in doc.raw
    assert result["url"] not in caplog.text and result["qr_svg"] not in caplog.text
    # 重新打开应用详情不会返回二维码或一次性链接。
    payload = json.dumps(client.get("/api/apps").json(), ensure_ascii=False)
    assert result["url"] not in payload and "qr_svg" not in payload
