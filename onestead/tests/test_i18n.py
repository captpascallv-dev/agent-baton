"""中英界面、语言优先级与固定数据值；只用临时数据和离线 Node/ASGI。"""

import json
import re
from pathlib import Path

import pytest
from starlette.testclient import TestClient

from onestead import config, sites
from onestead.base import CompanyBase
from onestead.cli import run
from onestead.server import remote_app
from onestead.store import BaseError, Store
from onestead.web.auth import RemoteAuth
from onestead.web.i18n import TEXT
from .support import APP, OWNER, PUBLIC_HOST, WORKSPACE_ORIGIN

STATIC = Path(__file__).parents[1] / "src/onestead/web/static"


def catalog(lang):
    source = (STATIC / f"i18n-{lang}.js").read_text(encoding="utf-8")
    return json.loads(source.split(" = ", 1)[1].strip().removesuffix(";"))


def test_catalogs_have_identical_keys_and_parameters():
    zh, en = catalog("zh"), catalog("en")
    assert zh.keys() == en.keys()
    assert all(isinstance(v, str) and v for v in [*zh.values(), *en.values()])
    for key in zh:
        assert set(re.findall(r"\{\d+\}", zh[key])) == set(re.findall(r"\{\d+\}", en[key])), key
    assert all(len(pair) == 2 and all(pair) for pair in TEXT.values())
    for name in ("app.js", "apps.js", "manage.js", "skills.js", "todos.js", "i18n.js"):
        source = (STATIC / name).read_text(encoding="utf-8")
        assert set(re.findall(r'uiText\("([\w.-]+)"', source)) <= zh.keys(), name
    html = (STATIC / "index.html").read_text(encoding="utf-8")
    assert set(re.findall(r'data-i18n(?:-aria)?="([\w.-]+)"', html)) <= zh.keys()


def test_business_scripts_have_no_chinese_outside_comments():
    for name in ("app.js", "apps.js", "manage.js", "skills.js", "todos.js", "index.html"):
        source = (STATIC / name).read_text(encoding="utf-8")
        # 注释里的中文保留；协议值也不能靠 Unicode 转义藏在业务脚本里。
        source = re.sub(r"/\*.*?\*/|//[^\n]*|<!--.*?-->", "", source, flags=re.S)
        source = re.sub(r"\\u([0-9a-fA-F]{4})", lambda m: chr(int(m[1], 16)), source)
        assert not re.search(r"[\u3400-\u9fff]", source), name


def test_lang_default(monkeypatch):
    monkeypatch.delenv("COMPANY_BASE_LANG")
    assert config.lang() == "en"


@pytest.mark.parametrize("value", ['"en"', '"zh"'])
def test_lang_file(isolated_config, monkeypatch, value):
    monkeypatch.delenv("COMPANY_BASE_LANG")
    isolated_config.write_text("lang = " + value, encoding="utf-8")
    assert config.lang() == json.loads(value)


@pytest.mark.parametrize("value", ['"fr"', '"EN"', '""', '" en "', "1", "true", "[]"])
def test_lang_rejects_other_values(isolated_config, monkeypatch, value):
    monkeypatch.delenv("COMPANY_BASE_LANG")
    isolated_config.write_text("lang = " + value, encoding="utf-8")
    with pytest.raises(BaseError, match="lang 只能是 en 或 zh"):
        config.lang()


def test_lang_new_and_old_env(isolated_config, monkeypatch):
    isolated_config.write_text('lang = "en"', encoding="utf-8")
    monkeypatch.setenv("COMPANY_BASE_LANG", "zh")
    assert config.lang() == "zh"
    monkeypatch.setenv("ONESTEAD_LANG", "en")
    assert config.lang() == "en"
    monkeypatch.setenv("ONESTEAD_LANG", "")
    with pytest.raises(BaseError, match="lang"):
        config.lang()
    monkeypatch.delenv("ONESTEAD_LANG")
    monkeypatch.setenv("COMPANY_BASE_LANG", "fr")
    with pytest.raises(BaseError, match="lang"):
        config.lang()


@pytest.mark.parametrize("lang", ["en", "zh", None])
def test_init_writes_lang(tmp_path, isolated_config, monkeypatch, lang):
    monkeypatch.delenv("COMPANY_BASE_LANG")
    args = ["init", str(tmp_path / "new-company"), "--owner", "ada", "--name", "Ada"]
    assert run(args + (["--lang", lang] if lang else [])) == 0
    assert config.load()["lang"] == (lang or "en")
    assert config.lang() == (lang or "en")


@pytest.mark.parametrize("lang,expected,other", [("en", "Sign in to Onestead", "登录Onestead"), ("zh", "登录Onestead", "Sign in to Onestead")])
def test_login_uses_company_not_browser_lang(workspace, monkeypatch, lang, expected, other):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    response = workspace.get("/login", headers={"Accept-Language": "zh-CN" if lang == "en" else "en-US"})
    assert response.status_code == 200
    assert expected in response.text and other not in response.text
    assert f'lang="{"en" if lang == "en" else "zh-CN"}"' in response.text
    assert 'name="token"' in response.text and 'action="/auth/login"' in response.text
    failed = workspace.post("/auth/login", data={"challenge": "expired", "token": "fake"}, headers={"Origin": WORKSPACE_ORIGIN})
    assert failed.status_code == 403
    assert ("This sign-in form expired" if lang == "en" else "这个登录页已经过期") in failed.text


def test_me_exposes_company_language_and_api_errors_follow_it(client, monkeypatch):
    monkeypatch.setenv("ONESTEAD_LANG", "en")
    assert client.get("/api/me").json()["lang"] == "en"
    assert 'data-company-lang="en"' in client.get("/").text
    assert client.get("/api/project/missing").json()["error"] == "This project was not found."


@pytest.mark.parametrize("lang", ["en", "zh"])
def test_oauth_page_language(repo, monkeypatch, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    with TestClient(remote_app(str(repo), 48722), base_url=WORKSPACE_ORIGIN, follow_redirects=False) as c:
        info = c.post("/register", json={"redirect_uris": ["https://chatgpt.com/connector_platform_oauth_redirect"], "token_endpoint_auth_method": "none"}).json()
        auth = c.get("/authorize", params={"client_id": info["client_id"], "redirect_uri": info["redirect_uris"][0], "response_type": "code", "code_challenge": "x" * 43, "code_challenge_method": "S256", "resource": WORKSPACE_ORIGIN + "/chatgpt/mcp"})
        assert auth.status_code == 302
        page = c.get(auth.headers["location"])
        assert page.status_code == 200
        assert ("Connect to Onestead" if lang == "en" else "接入Onestead") in page.text
        assert 'action="/chatgpt/consent"' in page.text
        invalid = c.get("/chatgpt/consent?request=invalid")
        assert invalid.status_code == 400
        assert ("authorization page expired" if lang == "en" else "这个授权页已过期") in invalid.text


@pytest.mark.parametrize("lang", ["en", "zh"])
def test_site_prompts_and_values_are_not_translated(ext, repo, tmp_path, monkeypatch, lang):
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    monkeypatch.setenv("COMPANY_BASE_SITE_DOMAIN", "example.com")
    build = tmp_path / "build"
    build.mkdir()
    (build / "index.html").write_text("用户内容不翻译", encoding="utf-8")
    agent = ext("claude")
    agent.register_app(APP)
    agent.publish_site(APP["id"], str(build), "demo")
    owner = CompanyBase(repo, OWNER, allow_owner=True)
    owner.manage_site(APP["id"], {"action": "publish", "visibility": "指定的人", "members": ["ext-zhang"]})
    doc = next(d for d in Store(repo).load() if d.kind == "app" and d.id == APP["id"])
    assert doc.meta["kind"] == "网页"
    assert sites.site_of(doc)["status"] == "已发布"
    assert sites.site_of(doc)["visibility"] == "指定的人"
    assert next(d for d in Store(repo).load() if d.id == "alpha" and d.kind == "project").meta["state"] == "进行中"
    with TestClient(remote_app(str(repo), 48723, public_host=PUBLIC_HOST), base_url=WORKSPACE_ORIGIN, follow_redirects=False) as c:
        missing = c.get("https://unknown.example.com/")
        assert missing.status_code == 404 and missing.text == ("Not Found" if lang == "en" else "找不到")
        expired = c.get("https://demo.example.com/_base/callback?code=invalid")
        assert expired.status_code == 403
        assert ("This link expired" if lang == "en" else "链接已失效") in expired.text
        form = c.get("/login?return_to=%2Fsites%2Fenter%3Fslug%3Ddemo")
        assert ("Sign in to" if lang == "en" else "登录Onestead") in form.text


@pytest.mark.parametrize("lang,prefix", [("zh", "\u7528\u6cd5\uff1a"), ("en", "usage: ")])
@pytest.mark.parametrize("cmd", ["status", "whoami", "projects"])
def test_subcommand_help_has_single_usage_prefix(monkeypatch, capsys, lang, prefix, cmd):
    from onestead.cli import build_parser
    monkeypatch.setenv("ONESTEAD_LANG", lang)
    with pytest.raises(SystemExit):
        build_parser().parse_args([cmd, "-h"])
    first = capsys.readouterr().out.splitlines()[0]
    assert first.startswith(prefix + "onestead " + cmd), first
    assert first.count(prefix.strip()) == 1, first
