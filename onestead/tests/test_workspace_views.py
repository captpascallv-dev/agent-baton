"""同事业务接口的真实会话测试：白名单、个人归属和主机动作隔离。"""

from .support import OWNER, OWNER_NAME

import io
import json
import os
import re
import zipfile
from pathlib import Path

import pytest

from onestead import skillscan, skillshare, tokens, usage
from onestead.base import CompanyBase
from onestead.redact import mask_local, safe_url
from onestead.store import BaseError, Store, make_viewer
from onestead.web import history, present, probe, views
from onestead.web.app import ROUTE_POLICY, STATIC, build_workspace_app
from onestead.web.auth import RemoteAuth

from .conftest import git
from .support import SID, WORKSPACE_ORIGIN as ORIGIN, grant, login, mark, result, rpc, signed

# 拼接只避开仓库个人值扫描；运行时仍用完整的虚构路径验证脱敏。
PATHS = ["C:" + r"\Users\PrivateMark\file.md", "C:/Users/PrivateMark/file.md", r"\\PrivateMark\share\file.md",
         "file:///C:/PrivateMark/file.md", "file:///home/PrivateMark/file.md",
         r"%LOCALAPPDATA%\PrivateMark\file.md", "%PRIVATE_MARK%/PrivateMark/file.md",
         "/Users/PrivateMark/file.md", "/home/PrivateMark/file.md", "~/PrivateMark/file.md"]
SECRET = "cbx_" + "SeCrEtMaRk" * 4


@pytest.mark.parametrize("path", PATHS)
@pytest.mark.parametrize("template", ["前 {p} 后", "[链接]({p})", "```text\n{p}\n```", '<a href="{p}">链接</a>'])
def test_mask_local_every_path_form(path, template):
    masked = mask_local(template.format(p=path))
    assert "PrivateMark" not in masked and "（本机路径）" in masked
    assert "链接" in masked if "链接" in template else "后" in masked if "后" in template else "```" in masked


def test_mask_local_keeps_portable_text_and_does_not_drop_paragraphs():
    text = "开头 C:/PrivateMark/file.md 结尾\n相对路径 scripts/run.py\nhttps://example.test/page"
    assert mask_local(text) == "开头 （本机路径） 结尾\n相对路径 scripts/run.py\nhttps://example.test/page"
    assert mask_local('/api/home/adopt 和 https://example.test/home/page') == '/api/home/adopt 和 https://example.test/home/page'


@pytest.mark.parametrize("path", [r'C:\Program Files\PrivateMark\file name.md', 'C:/Program Files/PrivateMark/file name.md'])
@pytest.mark.parametrize("template", ['前 "{p}" 后', '[文件]({p})', '```\n{p}\n```'])
def test_mask_local_paths_with_spaces(path, template):
    assert "PrivateMark" not in mask_local(template.format(p=path))


@pytest.mark.parametrize("url", ["ftp://example.test/a", "file:///C:/x", "https://u:p@example.test/a",
    "https://localhost/a", "https://x.local/a", "https://computer/a", "http://127.0.0.1/a",
    "http://10.2.3.4/a", "http://192.168.1.2/a", "http://169.254.2.3/a", "http://[::1]/a",
    "http://[fc00::1]/a", "http://[fe80::1]/a", "http://[::ffff:127.0.0.1]/a",
    "http://2130706433/a", "http://127.1/a", "http://0x7f000001/a", "http://x.local./a",
    "http://localhost./a", "http://public.test:bad/a", "https://example.test\\@127.0.0.1/a",
    "http://%31%32%37.0.0.1/a", "http://127。0.0.1/a"])
def test_remote_url_rejects_local_or_unsafe_targets(url):
    assert safe_url(url) is None


@pytest.mark.parametrize("url", ["https://example.test/a", "http://www.example.test:8080/a", "https://8.8.8.8/a", "https://public.example.test/a"])
def test_remote_url_allows_public_http_without_probe(url):
    assert safe_url(url) == url


READS = ["/api/home", "/api/home?draft=1", "/api/activity", "/api/project/alpha", "/api/doc?rel=handbook/company.md",
         "/api/search?q=公司", "/api/todos", "/api/apps", "/api/agents", "/api/projects", "/api/docs",
         "/api/decisions", "/api/members", "/api/skills", f"/api/skills/{SID}"]


@pytest.mark.parametrize("path", READS)
def test_open_remote_reads_use_current_identity(business, path):
    response = business["client"].get(path)
    assert response.status_code == 200, (path, response.text)
    for hidden in ("乙私密待办", "暗号公开待办", "暗号紫藤萝", "家里的事", "暗号贝塔湖", "贝塔看板",
                   "私有备注不外传", "C:\\x\\STATUS.md", "feishu_open_id", "visible_to", "probe_process", "code_path"):
        assert hidden not in response.text, (path, hidden)


def test_me_navigation_capabilities_and_object_actions(business):
    c = business["client"]
    me = c.get("/api/me").json()
    assert me["workspace_owner"] == "ext-zhang" and me["access_context"] == "remote"
    assert me["navigation"] == ["home", "activity", "todos", "projects", "apps", "agents", "skills", "docs", "decisions", "members"]
    assert me["capabilities"]["write_own_todos"] and me["capabilities"]["manage_own_agent_tokens"]
    assert not me["capabilities"]["open_local"] and not me["capabilities"]["manage_company"]
    assert not {"projects", "assets", "skills", "members", "tokens"} & me.keys()
    assert c.get("/api/project/alpha").json()["allowed_actions"]["append_status"]
    assert c.get("/api/todos").json()["items"][0]["allowed_actions"]["edit"]
    assert not c.get("/api/apps").json()["apps"][0]["allowed_actions"]["open_local"]
    agent = c.get("/api/agents").json()["agents"][0]
    assert agent["key"] == "ext-zhang-claude" and "connect" not in agent
    assert agent["allowed_actions"]["issue_token"] and "names" not in agent
    assert c.get("/api/home").json()["draft"]["allowed_actions"]["adopt"]


def test_disabled_agent_action_is_computed_from_current_record(business, repo):
    mark(repo, "people/ext-zhang-claude.md", remote_enabled=False)
    c = business["client"]
    assert not c.get("/api/agents").json()["agents"][0]["allowed_actions"]["issue_token"]
    response = c.post("/api/tokens/issue", json={"key": "ext-zhang-claude", "label": "不能发"}, headers=signed(c))
    assert response.status_code == 400


def test_colleague_member_cards_never_call_management(business, repo, monkeypatch):
    mark(repo, "people/ext-li.md", connect="接法私密", feishu_open_id="ou_FeishuMark", assets=["未授权清单标记"])
    for name in ("token_list", "feishu_pending"):
        monkeypatch.setattr(CompanyBase, name, lambda *args: pytest.fail("同事名录不能调用管理服务"))
    members = business["client"].get("/api/members").json()["items"]
    assert any(m["key"] == "ext-li" for m in members)
    assert all(set(m) == {"key", "name", "title", "kind", "role"} for m in members)
    assert "接法私密" not in json.dumps(members, ensure_ascii=False)


def test_all_remote_writes_record_authenticated_actor(business, repo):
    c = business["client"]
    headers = signed(c)
    response = c.post("/api/project/alpha/status", json={"text": "网页新增进展"}, headers=headers)
    assert response.status_code == 200
    assert git(repo, "log", "-1", "--format=%an") == "ext-zhang"
    response = c.post("/api/decision/new", json={"title": "网页决定", "decision": "按计划做", "reason": "已商量",
        "project": "alpha", "decided_by": OWNER_NAME}, headers=headers)
    assert response.status_code == 200, response.text
    d = next(d for d in Store(repo).load() if d.kind == "decision" and d.title == "网页决定")
    assert d.meta["decided_by"] == d.meta["recorded_by"] == "ext-zhang"
    assert c.post("/api/todos/add", json={"title": "网页待办"}, headers=headers).status_code == 200
    assert c.post(f"/api/todos/{business['tid']}", json={"notes": "本人备注"}, headers=headers).status_code == 200
    assert c.post(f"/api/todos/{business['other_tid']}", json={"status": "done"}, headers=headers).status_code == 404
    version = c.get("/api/home?draft=1").json()["draft"]["version"]
    assert c.post("/api/home/adopt", json={"version": version}, headers=headers).status_code == 200
    assert git(repo, "log", "-1", "--format=%an") == "ext-zhang"
    business["a"].propose_home([{"kind": "recent"}], "再提案")
    version = c.get("/api/home?draft=1").json()["draft"]["version"]
    assert c.post("/api/home/discard", json={"version": version}, headers=headers).status_code == 200
    response = c.post("/api/tokens/issue", json={"key": "ext-zhang-claude", "label": "网页测试"}, headers=headers)
    assert response.status_code == 200 and response.json()["record"]["issued_by"] == "ext-zhang"
    raw = response.json()["token"]
    assert tokens.lookup_record(raw)["key"] == "ext-zhang-claude"
    assert raw not in c.get("/api/agents").text
    tokens.issue("ext-zhang-claude", f"{OWNER_NAME} 发的", issued_by=OWNER)
    issuers = {t["label"]: t["issued_by_name"] for t in c.get("/api/agents").json()["agents"][0]["tokens"]}
    assert issuers["网页测试"] == "你" and issuers[f"{OWNER_NAME} 发的"] not in (OWNER, "成员")
    assert c.post("/api/tokens/revoke", json={"id": response.json()["record"]["id"]}, headers=headers).status_code == 200
    assert tokens.lookup_record(raw) is None
    assert c.post("/api/render", json={"text": "**预览**"}, headers=headers).json()["html"].startswith("<p><strong>")


@pytest.mark.parametrize("path,data", [("/api/todos/add", {"title": "伪造", "owner": OWNER}),
    ("/api/decision/new", {"title": "伪造", "decision": "决定", "reason": "理由", "project": "alpha", "visible_to": []}),
    ("/api/project/alpha/status", {"text": "伪造", "signed_as": OWNER}),
    ("/api/tokens/issue", {"key": "ext-zhang-claude", "label": "伪造", "owner": OWNER})])
def test_remote_mass_assignment_is_refused_without_commit(business, repo, path, data):
    before = git(repo, "rev-parse", "HEAD")
    assert business["client"].post(path, json=data, headers=signed(business["client"])).status_code == 400
    assert git(repo, "rev-parse", "HEAD") == before


def test_readonly_company_member_can_use_own_workspace_but_not_company(business):
    c = business["client"]
    assert login(c, "ext-li")[0].status_code == 303
    assert c.get("/api/decisions").json()["projects"] == []
    assert not c.get("/api/project/alpha").json()["allowed_actions"]["append_status"]
    assert c.post("/api/project/alpha/status", json={"text": "不许写"}, headers=signed(c)).status_code == 400
    assert c.post("/api/todos/add", json={"title": "乙自己的新待办"}, headers=signed(c)).status_code == 200
    assert "甲本人待办" not in c.get("/api/todos").text


def test_remote_secrets_masked_before_json_html_search_and_errors(business, repo, monkeypatch):
    c = business["client"]
    paths = "\n".join(PATHS)
    path = repo / "assets/deal.md"
    mark(repo, "assets/deal.md", title="路径 " + PATHS[0], visible_to=["ext-zhang", "名单秘密标记"],
         feishu_open_id="飞书秘密标记", token=SECRET)
    path.write_text(path.read_text(encoding="utf-8") + "\n查找正文\n" + paths + "\n" + SECRET, encoding="utf-8")
    for target in ("/api/docs", "/api/doc?rel=assets/deal.md", "/api/search?q=查找正文"):
        response = c.get(target)
        assert response.status_code == 200
        for secret in ("PrivateMark", SECRET, "名单秘密标记", "飞书秘密标记", "visible_to"):
            assert secret not in response.text
    response = c.post("/api/render", json={"text": "[路径](" + PATHS[0] + ")\n```\n" + paths + "\n```\n" + SECRET}, headers=signed(c))
    assert response.status_code == 200 and "PrivateMark" not in response.text and SECRET not in response.text
    assert "（本机路径）" in response.text
    def fail(*args, **kwargs):
        raise BaseError("标题太长：" + paths + " " + SECRET)
    monkeypatch.setattr(CompanyBase, "update_project_status", fail)
    response = c.post("/api/project/alpha/status", json={"text": "测试"}, headers=signed(c))
    assert response.status_code == 400 and "标题太长" in response.text
    assert "PrivateMark" not in response.text and SECRET not in response.text
    def unknown(*args, **kwargs):
        raise RuntimeError("内部秘密 " + PATHS[0])
    monkeypatch.setattr(CompanyBase, "update_project_status", unknown)
    response = c.post("/api/project/alpha/status", json={"text": "测试"}, headers=signed(c))
    assert response.status_code == 500 and response.json() == {"error": "底座内部出错了，请稍后再试。"}


def test_remote_real_validation_error_and_invisible_objects(business, repo):
    c = business["client"]
    response = c.post("/api/todos/add", json={"title": "长" * 200}, headers=signed(c))
    assert response.status_code == 400 and "标题太长" in response.text
    for target in ("/api/project/beta", "/api/doc?rel=people/ext-zhang.md", "/api/doc?rel=people/ext-li.md",
                   f"/api/doc?rel=skills/{SID}.md", f"/api/doc?rel=todos/{business['other_tid']}.md",
                   "/api/doc?rel=../outside.md", "/api/skills/missing-tool", "/api/skills/missing-tool/download"):
        assert c.get(target).status_code == 404, target
    for tid in ("missing-token", tokens.issue("ext-li-bot", "别人口令")[1]["id"]):
        assert c.post("/api/tokens/revoke", json={"id": tid}, headers=signed(c)).status_code == 404
    for key in ("ext-li-bot", "ext-zhang", OWNER):
        assert c.post("/api/tokens/issue", json={"key": key, "label": "非法"}, headers=signed(c)).status_code == 400


def test_public_project_is_readable_but_not_a_writable_option(business, repo):
    mark(repo, "projects/beta/project.md", sensitivity="public")
    c = business["client"]
    row = c.get("/api/project/beta").json()
    assert row["allowed_actions"]["append_status"] is False
    assert "beta" not in [p["id"] for p in c.get("/api/decisions").json()["projects"]]
    response = c.post("/api/project/beta/status", json={"text": "不能越权写"}, headers=signed(c))
    assert response.status_code == 400 and "不在你的授权里" in response.text


def test_app_uses_only_public_url_and_safe_explanation(business, repo):
    path = repo / "apps/alpha-app.md"
    mark(repo, "apps/alpha-app.md", open={"url": "https://example.test/app", "path": PATHS[0],
         "shortcut": PATHS[1], "command": SECRET}, code_path=PATHS[2], probe_process="私有进程标记",
         links=[{"label": "公网入口", "target": "https://example.test/info"},
                {"label": "内部入口标记", "target": "http://192.168.1.1/private"}],
         unknown=SECRET, one_line="说明 " + PATHS[0])
    path.write_text(path.read_text(encoding="utf-8") + "\n正常说明 " + PATHS[1] + "\n" + SECRET +
                    "\n[私网](http://127.0.0.1/private)", encoding="utf-8")
    response = business["client"].get("/api/apps")
    row = response.json()["apps"][0]
    assert row["open"] == {"url": "https://example.test/app"} and len(row["links"]) == 1
    assert all(s not in response.text for s in ("PrivateMark", SECRET, "私有进程标记", "内部入口标记", "127.0.0.1"))


def test_full_remote_still_uses_safe_views_and_personal_boundary(business, repo):
    mark(repo, "people/ext-zhang.md", access_mode="full")
    c = business["client"]
    for path in ("/api/skills", "/api/members", "/api/agents", "/api/todos", "/api/home"):
        response = c.get(path)
        assert response.status_code == 200
        assert "installs" not in response.text and "connect_prompt" not in response.text and "乙私密待办" not in response.text
    assert c.get("/api/doc?rel=people/ext-zhang.md").status_code == 404


def test_skill_mcp_web_and_zip_share_same_visibility_and_bytes(business, repo, usage_log, monkeypatch):
    c, directory = business["client"], business["shared"]
    md = directory / "SKILL.md"
    md.write_text(md.read_text(encoding="utf-8") + "\n" + "\n".join(PATHS), encoding="utf-8")
    original_scan = skillscan.cached_scan
    def cached(fresh=False):
        assert fresh is False, "远程不能强制扫描"
        return original_scan(False)
    monkeypatch.setattr(skillscan, "cached_scan", cached)
    page = c.get(f"/api/skills/{SID}").json()
    assert SID in business["a"].list_skills() and page["name"] == "共享工具"
    assert "PrivateMark" not in page["skill_md"]
    assert c.get("/api/skills?fresh=1").status_code == 200
    response = c.get(page["download_url"])
    assert response.status_code == 200 and response.headers["content-disposition"].startswith("attachment;")
    assert response.headers["cache-control"] == "no-store"
    with zipfile.ZipFile(io.BytesIO(response.content)) as archive:
        assert archive.read(f"{SID}/SKILL.md").decode("utf-8") == page["skill_md"]
        assert all(b"PrivateMark" not in archive.read(n) for n in archive.namelist())
    assert '"via": "web"' in usage_log.read_text(encoding="utf-8")
    grant(repo, ids=[])
    assert SID not in business["a"].list_skills()
    assert c.get(f"/api/skills/{SID}").status_code == c.get(f"/api/skills/{SID}/download").status_code == 404


def test_skill_secret_in_skill_md_closes_mcp_web_and_zip(business):
    md = business["shared"] / "SKILL.md"
    md.write_text(md.read_text(encoding="utf-8") + "\n" + SECRET, encoding="utf-8")
    c = business["client"]
    assert SID not in business["a"].list_skills()
    assert c.get(f"/api/skills/{SID}").status_code == 404
    assert c.get(f"/api/skills/{SID}/download").status_code == 404


def test_web_download_rechecks_permission_after_packaging(business, repo, monkeypatch):
    original = skillshare.zip_package
    def revoke(sid, package):
        content = original(sid, package)
        grant(repo, ids=[])
        return content
    monkeypatch.setattr(skillshare, "zip_package", revoke)
    response = business["client"].get(f"/api/skills/{SID}/download")
    assert response.status_code == 404 and "attachment" not in response.headers.get("content-disposition", "")


def test_remote_read_rechecks_permissions_before_output(business, repo, monkeypatch):
    original = present.doc
    def revoke(doc, viewer, docs):
        result = original(doc, viewer, docs)
        mark(repo, "people/ext-zhang.md", assets=[])
        return result
    monkeypatch.setattr(present, "doc", revoke)
    response = business["client"].get("/api/doc?rel=assets/deal.md")
    assert response.status_code == 403 and "合作条款" not in response.text


CLOSED = [(method, template) for (method, template), (_, allowed) in ROUTE_POLICY.items() if not allowed]


@pytest.mark.parametrize("method,template", CLOSED)
@pytest.mark.parametrize("bypass_policy", [False, True])
def test_local_owner_routes_refuse_in_policy_and_handler(business, repo, monkeypatch, method, template, bypass_policy, feishu_cfg):
    from .support import write_config
    write_config(feishu_cfg)
    c = business["client"]
    if bypass_policy:
        monkeypatch.setitem(ROUTE_POLICY, (method, template), (True, True))
    path = re.sub(r"\{[^}]+\}", "alpha-app", template)
    before = git(repo, "rev-parse", "HEAD"), git(repo, "status", "--porcelain")
    monkeypatch.setattr(os, "startfile", lambda *a: pytest.fail("远程不能打开主机文件"), raising=False)
    monkeypatch.setattr(probe, "probe", lambda *a: pytest.fail("远程不能探活"))
    response = c.request(method, path, json={"path": "C:/PrivateMark/x", "public": True, "listed": True}, headers=signed(c))
    assert response.status_code == 403, (path, response.text)
    assert before == (git(repo, "rev-parse", "HEAD"), git(repo, "status", "--porcelain"))


def test_remote_reads_do_not_probe_or_check_registered_local_paths(business, repo, monkeypatch):
    mark(repo, "apps/alpha-app.md", open={"path": PATHS[0]}, code_path=PATHS[1])
    monkeypatch.setattr(probe, "probe", lambda *a: pytest.fail("远程不能探活"))
    monkeypatch.setattr(os, "startfile", lambda *a: pytest.fail("远程不能打开"), raising=False)
    exists, isdir = os.path.exists, os.path.isdir
    def checked(original):
        def check(path):
            assert "PrivateMark" not in str(path), "远程不能检查登记的本机路径"
            return original(path)
        return check
    monkeypatch.setattr(os.path, "exists", checked(exists))
    monkeypatch.setattr(os.path, "isdir", checked(isdir))
    assert business["client"].get("/api/apps").status_code == 200
    assert business["client"].get("/api/home").status_code == 200


def test_static_files_contain_no_local_path_and_prompt_only_local(business, client):  # noqa: F811
    for path in STATIC.iterdir():
        if path.is_file():
            text = path.read_text(encoding="utf-8")
            assert mask_local(text) == text, path.name
    assert "connect_prompt" not in business["client"].get("/api/agents").json()
    import sysconfig

    assert sysconfig.get_path("scripts") in client.get("/api/agents").json()["connect_prompt"]


def test_author_only_calls_current_viewer_you(business, repo):
    docs = Store(repo).load()
    viewer = make_viewer(docs, "ext-zhang", remote=True)
    members = {d.meta["agent_key"]: d for d in docs if d.kind == "person" and not d.problem}
    assert views.who_view(history.author_view(OWNER, members, viewer), viewer)["name"] == OWNER_NAME
    assert views.who_view(history.author_view("ext-zhang", members, viewer), viewer)["name"] == "你"


def test_mcp_and_portal_redirect_target_mask_before_render(business, repo):
    path = repo / "projects/alpha/project.md"
    path.write_text(path.read_text(encoding="utf-8") + "\n可查说明 " + PATHS[0] + " " + SECRET, encoding="utf-8")
    c = business["client"]
    raw, _ = tokens.issue("ext-zhang", "过渡入口测试")
    for name, args in (("get_project", {"project_id": "alpha"}), ("search", {"query": "可查说明"})):
        error, text = result(rpc(c, raw, name, args))
        assert not error and "PrivateMark" not in text and SECRET not in text
    assert c.post("/portal/login", data={"token": raw}, headers={"Origin": ORIGIN}).status_code == 303
    response = c.get("/portal/p/alpha")
    assert response.status_code == 303 and response.headers["location"] == "/#/projects/alpha"
    response = c.get("/api/project/alpha")
    assert response.status_code == 200 and "PrivateMark" not in response.text and SECRET not in response.text
