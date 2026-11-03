"""远程 Skill 共享：授权、包过滤、MCP、工作台和旧地址跳转共用规则。"""

from .support import OWNER, OWNER_NAME

import io
import json
import re
import zipfile

import pytest
from starlette.testclient import TestClient

from onestead import skillscan, skillshare, tokens
from onestead.base import CompanyBase
from onestead.skills import visible_skill_ids
from onestead.store import BaseError, Store, check_meta, make_viewer
from onestead.web.app import build_app

from .conftest import git
from .support import (
    SID,
    WEB_POST_HEADERS as POST_HEADERS,
    bearer,
    grant,
    junction,
    mark,
    result,
    rpc,
    skill,
)


def admin(repo):
    return CompanyBase(repo, OWNER, allow_owner=True)


@pytest.fixture(autouse=True)
def token_store(tmp_path, monkeypatch):
    monkeypatch.setenv("COMPANY_BASE_TOKENS", str(tmp_path / "tokens.json"))


def login(c, key="ext-zhang"):
    token, record, headers = bearer(key)
    challenge = re.search(r'name="challenge" value="([^"]+)"', c.get("/login").text)[1]
    assert c.post("/auth/login", data={"token": token, "challenge": challenge}).status_code == 303
    return token, record, headers


def zip_names(response):
    assert response.status_code == 200
    with zipfile.ZipFile(io.BytesIO(response.content)) as archive:
        assert all(i.compress_type == zipfile.ZIP_DEFLATED for i in archive.infolist())
        return archive.namelist()


def test_unshared_and_local_output(shared, ext):  # noqa: F811
    local = ext("codex").list_skills()
    assert "装在哪几家" in local and "共用目录" in local and "插件自带" in local
    assert "私有备注不外传" in ext("codex").get_skill(SID)
    assert ext("ext-zhang").list_skills() == "还没有共享给你的 Skill。\n"
    messages = []
    for sid in (SID, "unknown-tool"):
        with pytest.raises(BaseError) as error:
            ext("ext-zhang").get_skill(sid)
        messages.append(str(error.value).replace(sid, "x"))
    assert messages == ["找不到 Skill `x`，或者它没共享给你。"] * 2


def test_granted_mcp_has_content_without_local_metadata(remote, shared, ext, repo):  # noqa: F811
    grant(repo)
    token, _, _ = bearer()
    for name, args in (("list_skills", {}), ("get_skill", {"skill_id": SID})):
        error, text = result(rpc(remote, token, name, args))
        assert not error and "共享工具" in text and "处理示例任务" in text and "1.2" in text
        for private in (str(shared), str(shared.parent.parent), "私有备注不外传", "home", "path",
                        "Claude", "Codex", "Grok", "Cursor", "Sample Agent", "共用目录", "插件", "装的话"):
            assert private not in text
        assert not re.search(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]", text)
    text = ext("ext-zhang").get_skill(SID)
    assert "完整操作步骤" in text and "scripts/run.py" in text and "大小（字节）" in text
    assert "https://example.test/tool" in text and "谁维护：claude" in text and "Authorization: Bearer" in text
    assert ext("ext-zhang").search("私有备注不外传").startswith("没有找到")
    assert all(d.kind != "skill" for d in make_viewer(Store(repo).load(), "ext-zhang").visible(Store(repo).load()))
    with pytest.raises(BaseError):
        ext("ext-zhang").update_skill(SID, {"one_line": "改动"})


def test_public_and_owner_inheritance(shared, ext, repo):  # noqa: F811
    grant(repo)
    assert SID in ext("ext-zhang-claude").list_skills()
    grant(repo, "ext-zhang-claude", ["unknown-tool"])
    assert ext("ext-zhang-claude").list_skills().startswith("还没有")
    grant(repo, "ext-zhang-claude", [SID, "unknown-tool"])
    viewer = make_viewer(Store(repo).load(), "ext-zhang-claude")
    assert viewer.skills == frozenset({SID})
    mark(repo, f"skills/{SID}.md", sensitivity="public")
    for key in ("ext-zhang", "ext-li", "ext-li-bot", "ext-zhang-claude"):
        assert SID in ext(key).list_skills()
    assert ext("ext-orphan").list_skills().startswith("还没有")
    mark(repo, "people/ext-zhang.md", kind="agent")
    assert ext("ext-zhang-claude").list_skills().startswith("还没有")
    assert make_viewer(Store(repo).load(), "ext-zhang-claude").skills == frozenset()


@pytest.mark.parametrize("value", ["tool", 1, [1], {"tool": True}])
def test_member_skills_validation(value):
    meta = {"id": "test", "agent_key": "test", "type": "person", "sensitivity": "internal", "kind": "human", "skills": value}
    assert check_meta(meta, "person") == "skills 要是 id 列表"


def test_public_requires_valid_metadata_and_scan(shared, ext, repo, tmp_path):  # noqa: F811
    mark(repo, f"skills/{SID}.md", sensitivity="public", type="wrong")
    assert ext("ext-li").list_skills().startswith("还没有")
    mark(repo, f"skills/{SID}.md", type="skill")
    standalone = skill(tmp_path / "standalone", "只有正本")
    ext("claude").register_skill({"id": "not-scanned", "one_line": "没有安装", "home": str(standalone)})
    mark(repo, "skills/not-scanned.md", sensitivity="public")
    listing = ext("ext-li").list_skills(fresh=True)
    assert SID in listing and "not-scanned" not in listing
    docs = Store(repo).load()
    data = skillscan.cached_scan()
    rows = skillscan.summarize(data, {d.id: d.meta for d in docs if d.kind == "skill"})
    assert visible_skill_ids(make_viewer(docs, "codex"), docs, data, rows) == frozenset()


@pytest.mark.parametrize("name", [".env", ".ENV.prod", "config.local.json", "my-secret.txt", "TOKEN.txt",
    "credentials.json", "password.txt", "cookies.bin", "cert.pem", "cert.KEY", "cert.p12", "cert.pfx",
    "cert.keystore", "cert.jks", "id_rsa.pub", "id_ed25519.backup"])
def test_credential_filenames_are_withheld(tmp_path, name):
    root = skill(tmp_path / "tool", "工具")
    (root / name).write_text("测试占位内容", encoding="utf-8")
    package = skillshare.package_manifest(root, capture=True)
    assert package.withheld == [{"path": name, "reason": "文件名像凭据"}]
    assert [f["path"] for f in package.files] == ["SKILL.md"]


@pytest.mark.parametrize("content", ["-----BEGIN RSA PRIVATE KEY-----", "sk-" + "A" * 24,
    "ghp_" + "A" * 32, "github_pat_" + "A" * 24, "xoxb-" + "A" * 12,
    "xoxa-" + "A" * 12, "xoxp-" + "A" * 12, "xoxr-" + "A" * 12,
    "AKIA" + "A" * 16, "AIza" + "A" * 32, "cbx_" + "A" * 24])
def test_secret_content_is_withheld(tmp_path, content):
    root = skill(tmp_path / "tool", "工具")
    (root / "config.txt").write_text(content, encoding="utf-8")
    package = skillshare.package_manifest(root)
    assert package.withheld == [{"path": "config.txt", "reason": "内容里像有密钥"}]


def test_package_filters_and_zip_use_same_manifest(shared, repo, tmp_path, remote):
    grant(repo)
    (shared / ".env").write_text("测试占位内容", encoding="utf-8")
    (shared / "my-secret.txt").write_text("测试占位内容", encoding="utf-8")
    (shared / "private.txt").write_text("-----BEGIN PRIVATE KEY-----", encoding="utf-8")
    (shared / "api.txt").write_text("sk-" + "A" * 24, encoding="utf-8")
    (shared / "large.bin").write_bytes(b"A" * (skillshare.MAX_FILE + 1))
    (shared / "zero.bin").write_bytes(b"\0" * 100)
    (shared / "empty.txt").write_bytes(b"")
    outside = tmp_path / "outside"
    outside.mkdir()
    (outside / "outside.txt").write_text("目录外的内容", encoding="utf-8")
    junction(shared / "outside-link", outside)
    for folder in skillshare.SKIP_DIRS:
        (shared / folder).mkdir()
        (shared / folder / "skip.txt").write_text("跳过", encoding="utf-8")
    package = skillshare.package_manifest(shared, capture=True)
    by = {f["path"]: f["reason"] for f in package.withheld}
    assert by == {".env": "文件名像凭据", "my-secret.txt": "文件名像凭据", "private.txt": "内容里像有密钥",
        "api.txt": "内容里像有密钥", "large.bin": "太大", "zero.bin": "文件损坏",
        "outside-link": "联接指向技能目录外", **{f: "跳过目录" for f in skillshare.SKIP_DIRS}}
    expected = ["SKILL.md", "empty.txt", "scripts/run.py"]
    assert [f["path"] for f in package.files] == expected
    _, _, headers = bearer()
    response = remote.get(f"/skills/{SID}.zip", headers=headers)
    assert zip_names(response) == [f"{SID}/{path}" for path in expected]
    assert response.headers["content-type"] == "application/zip"
    assert response.headers["content-disposition"] == f'attachment; filename="{SID}.zip"'
    assert response.headers["cache-control"] == "no-store"
    with zipfile.ZipFile(io.BytesIO(response.content)) as archive:
        for item in package.files:
            assert archive.read(f"{SID}/{item['path']}") == package.contents[item["path"]]
            assert archive.getinfo(f"{SID}/{item['path']}").file_size == item["size"]


def test_internal_junction_and_cycle(shared):
    junction(shared / "inside-link", shared / "scripts")
    junction(shared / "cycle", shared)
    package = skillshare.package_manifest(shared)
    assert {f["path"] for f in package.files} == {"SKILL.md", "scripts/run.py", "inside-link/run.py"}
    assert {f["path"]: f["reason"] for f in package.withheld} == {"cycle": "循环联接"}


@pytest.mark.parametrize("mode", ["secret", "zero", "large", "outside", "missing"])
def test_withheld_skill_md_cannot_be_shared(shared, ext, repo, tmp_path, mode):  # noqa: F811
    grant(repo)
    path = shared / "SKILL.md"
    if mode == "secret":
        path.write_text("sk-" + "A" * 24, encoding="utf-8")
    elif mode == "zero":
        path.write_bytes(b"\0" * 100)
    elif mode == "large":
        path.write_bytes(b"A" * (skillshare.MAX_FILE + 1))
    else:
        path.unlink()
        if mode == "outside":
            outside = tmp_path / "external-md"
            outside.mkdir()
            (outside / "SKILL.md").write_text("外面的技能", encoding="utf-8")
            # 目录联接制造 SKILL.md 不再是正常文件，同样不能共享。
            junction(path, outside)
    assert ext("ext-zhang").list_skills(fresh=True).startswith("还没有")
    with pytest.raises(BaseError, match="没共享"):
        ext("ext-zhang").get_skill(SID)


def test_size_limits_and_truncation(shared, repo, ext, remote):  # noqa: F811
    grant(repo)
    md = shared / "SKILL.md"
    md.write_text(md.read_text(encoding="utf-8") + "中" * 23000, encoding="utf-8")
    info = ext("ext-zhang").shared_skill(SID)
    assert info["truncated"] and len(info["skill_md"].encode("utf-8")) <= 60 * 1024
    assert "超过 60 KB" in ext("ext-zhang").get_skill(SID)
    login(remote)
    assert remote.get(f"/portal/skills/{SID}").headers["location"] == f"/#/skills/{SID}"
    page = remote.get(f"/api/skills/{SID}")
    assert "中" * 23000 in page.text and "只显示前 60 KB" not in page.text
    for i in range(11):
        (shared / f"large-{i}.bin").write_bytes(b"A" * skillshare.MAX_FILE)
    package = skillshare.package_manifest(shared)
    assert package.too_large and len(package.files) == 13
    assert skillshare.TOO_LARGE.format(owner_name=OWNER_NAME) in ext("ext-zhang").get_skill(SID)
    _, _, headers = bearer()
    response = remote.get(f"/skills/{SID}.zip", headers=headers)
    assert response.status_code == 400 and response.text == skillshare.TOO_LARGE.format(owner_name=OWNER_NAME)
    assert remote.get(f"/api/skills/{SID}").json()["too_large"]
    assert remote.get(f"/portal/skills/{SID}.zip", follow_redirects=True).status_code == 400


def test_remote_download_authentication_and_revocation(remote, repo):
    url = f"/skills/{SID}.zip"
    assert remote.get(url).status_code == 401
    feishu, _, headers = bearer(kind="feishu")
    assert remote.get(url, headers=headers).status_code == 401
    token, record, headers = bearer()
    for sid in (SID, "unknown-tool"):
        response = remote.get(f"/skills/{sid}.zip", headers=headers)
        assert response.status_code == 404 and "或者它没共享给你" in response.text
    grant(repo)
    assert remote.get(url, headers=headers).status_code == 200
    assert remote.get(url, headers={**headers, "host": "evil.example"}).status_code == 404
    tokens.revoke(record["id"])
    assert remote.get(url, headers=headers).status_code == 401
    _, _, headers = bearer(days=-1)
    assert remote.get(url, headers=headers).status_code == 401
    _, _, headers = bearer("codex")
    assert remote.get(url, headers=headers).status_code == 401


def test_portal_detail_download_and_sessions(remote, repo, usage_log):
    url = f"/portal/skills/{SID}"
    target = f"/api/skills/{SID}"
    assert remote.get(url).headers["location"] == f"/#/skills/{SID}"
    assert remote.get(url + ".zip").headers["location"] == target + "/download"
    assert remote.get(target + "/download").headers["location"] == "/login"
    token, _, headers = login(remote)
    assert remote.get(target).status_code == 404
    assert remote.get(url + ".zip", follow_redirects=True).status_code == 404
    grant(repo)
    listing = remote.get("/api/skills").json()
    assert listing["mode"] == "shared" and listing["skills"][0]["id"] == SID
    page = remote.get(target)
    assert "完整操作步骤" in page.text and "scripts/run.py" in page.text
    assert "<script" not in page.json()["html"]
    assert page.json()["download_url"] == target + "/download" and "unsafe-inline" not in page.headers["content-security-policy"]
    assert zip_names(remote.get(url + ".zip", follow_redirects=True)) == [f"{SID}/SKILL.md", f"{SID}/scripts/run.py"]
    assert zip_names(remote.get(f"/skills/{SID}.zip", headers=headers))
    calls = [json.loads(line) for line in usage_log.read_text(encoding="utf-8").splitlines()]
    downloads = [row for row in calls if row["tool"] == "skill_download"]
    assert [row["via"] for row in downloads] == ["web", "remote"]
    assert token not in usage_log.read_text(encoding="utf-8")
    feishu, record, headers = bearer(kind="feishu")
    remote.cookies.clear()
    remote.cookies.set("cb_portal", feishu, path="/portal")
    assert remote.get(url).status_code == 303
    assert remote.get(url + ".zip", follow_redirects=True).url.path == "/login"
    assert remote.get(target).status_code == 401
    assert remote.get(f"/skills/{SID}.zip", headers=headers).status_code == 401
    tokens.revoke(record["id"])
    assert remote.get(url + ".zip").status_code == 303


def test_web_share_commit_and_package_warning(shared, repo, client, ext):  # noqa: F811
    (shared / ".env").write_text("测试占位内容", encoding="utf-8")
    before = git(repo, "rev-list", "--count", "HEAD")
    assert client.post("/api/skills/share", json={"id": SID, "public": True}).status_code == 403
    response = client.post("/api/skills/share", json={"id": SID, "public": True}, headers=POST_HEADERS)
    assert response.status_code == 200 and response.json()["commit"]
    assert git(repo, "log", "-1", "--format=%an") == OWNER
    assert int(git(repo, "rev-list", "--count", "HEAD")) == int(before) + 1
    assert SID in ext("ext-li").list_skills()
    row = next(r for r in client.get("/api/skills").json()["skills"] if r["id"] == SID)
    assert row["sharing"]["public"] and row["sharing"]["withheld"] == [{"path": ".env", "reason": "文件名像凭据"}]
    assert client.post("/api/skills/share", json={"id": SID, "public": False}, headers=POST_HEADERS).status_code == 200
    assert ext("ext-li").list_skills().startswith("还没有")
    grant(repo)
    row = next(r for r in client.get("/api/skills").json()["skills"] if r["id"] == SID)
    assert not row["sharing"]["public"] and "张三（合作方）" in row["sharing"]["members"]
    assert row["sharing"]["withheld"]
    with pytest.raises(BaseError, match="agent 不能"):
        ext("claude").share_skill(SID, True)
    with pytest.raises(BaseError, match="没有这些字段"):
        ext("claude").update_skill(SID, {"sensitivity": "public"})


def test_share_without_record_and_member_editor(shared, repo, skill_home, client):  # noqa: F811
    skill(skill_home / ".codex" / "skills" / "new-tool", "新工具")
    skillscan.cached_scan(True)
    response = client.post("/api/skills/share", json={"id": "new-tool", "public": True}, headers=POST_HEADERS)
    assert response.status_code == 200
    doc = next(d for d in Store(repo).load() if d.kind == "skill" and d.id == "new-tool")
    assert doc.meta["title"] == "新工具" and doc.meta["one_line"] == "新工具 的说明" and doc.sensitivity == "public"
    source = admin(repo).doc_source("people/ext-li.md")
    field = next(f for f in source["fields"] if f["key"] == "skills")
    assert field["label"] == "能用的 Skill" and field["type"] == "multi" and field["show_if"] == "access_mode" and field["show_value"] == "assigned"
    assert field["labels"]["new-tool"] == "新工具"
    response = admin(repo).save_doc(source["rel"], source["version"],
        {"remote_enabled": True, "skills": [SID, "new-tool", SID], "projects": ["alpha"], "assets": []}, None)
    assert response["commit"]
    member = next(d for d in Store(repo).load() if d.id == "ext-li")
    assert member.meta["skills"] == [SID, "new-tool"]
    card = next(i for i in client.get("/api/members").json()["items"] if i["key"] == "ext-li")
    assert card["skills"] == ["共享工具", "新工具"]
    source = admin(repo).doc_source("people/ext-li.md")
    with pytest.raises(BaseError, match="没有 id"):
        admin(repo).save_doc(source["rel"], source["version"], {"remote_enabled": True, "skills": ["not-scanned"]}, None)
    admin(repo).save_doc(source["rel"], source["version"], {"remote_enabled": False}, None)
    member = next(d for d in Store(repo).load() if d.id == "ext-li")
    assert member.meta["skills"] == [SID, "new-tool"] and member.meta["access_mode"] == "assigned"


def test_web_share_requires_session(shared, repo):
    from .support import WEB_BASE, WEB_PORT, WEB_TOKEN as TOKEN
    with TestClient(build_app(repo, WEB_PORT, TOKEN), base_url=WEB_BASE) as anonymous:
        assert anonymous.post("/api/skills/share", json={"id": SID, "public": True}, headers=POST_HEADERS).status_code == 401


def test_local_paths_are_redacted_and_non_web_upstream_hidden(shared, ext, repo):  # noqa: F811
    grant(repo)
    # 拼接只避开仓库个人值扫描；运行时仍用完整的虚构路径验证脱敏。
    mark(repo, f"skills/{SID}.md", upstream="C:" + r"\Users\Someone\private")
    md = shared / "SKILL.md"
    md.write_text(md.read_text(encoding="utf-8") + "\n路径 C:" "\\Users\\Someone\\private.md 和 /home/user/tool.md\n",
                  encoding="utf-8")
    text = ext("ext-zhang").get_skill(SID)
    assert "Someone" not in text and "/home/user" not in text and "上游：" not in text


def test_permission_rechecked_during_download(shared, repo, remote, monkeypatch):
    grant(repo)
    original = skillshare.zip_package

    def revoke_while_packing(sid, package):
        grant(repo, ids=[])
        return original(sid, package)

    monkeypatch.setattr(skillshare, "zip_package", revoke_while_packing)
    _, _, headers = bearer()
    assert remote.get(f"/skills/{SID}.zip", headers=headers).status_code == 404


def test_broken_reference_is_not_shared(shared, repo, ext, skill_home, tmp_path):  # noqa: F811
    grant(repo)
    skill(skill_home / ".codex" / "skills" / SID, "健康副本")
    broken = skill(tmp_path / "broken-reference", "损坏正本")
    (broken / "notes.md").write_bytes(b"\0" * 64)
    mark(repo, f"skills/{SID}.md", home=str(broken))
    assert ext("ext-zhang").list_skills(fresh=True).startswith("还没有")


def test_packaging_keeps_filtered_snapshot(shared, repo, ext, monkeypatch):  # noqa: F811
    grant(repo)
    original = skillshare.zip_package
    expected = (shared / "scripts" / "run.py").read_bytes()

    def replace_before_zip(sid, package):
        (shared / "scripts" / "run.py").write_text("sk-" + "A" * 24, encoding="utf-8")
        return original(sid, package)

    monkeypatch.setattr(skillshare, "zip_package", replace_before_zip)
    with zipfile.ZipFile(io.BytesIO(ext("ext-zhang").download_skill(SID))) as archive:
        assert archive.read(f"{SID}/scripts/run.py") == expected
