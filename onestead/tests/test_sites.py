"""静态发布：只用临时数据、构建与状态目录。"""

import asyncio
from pathlib import Path

import pytest

from onestead import config, sites
from onestead.base import CompanyBase
from onestead.server import build_server
from onestead.store import BaseError, Store
from .support import APP, change, git, junction


@pytest.fixture
def build(tmp_path):
    folder = tmp_path / "build"
    folder.mkdir()
    (folder / "index.html").write_text("<!doctype html><h1>第一版</h1>", encoding="utf-8")
    return folder


@pytest.fixture
def publisher(as_agent, monkeypatch):
    monkeypatch.setenv("COMPANY_BASE_SITE_DOMAIN", "example.com")
    agent = as_agent("claude")
    agent.register_app(APP)
    return agent


def site_doc(repo, app_id="demo-web"):
    return next(d for d in Store(repo).load() if d.kind == "app" and d.id == app_id)


def publish(publisher, build, slug="demo"):
    return publisher.publish_site("demo-web", str(build), slug)


def test_config(isolated_config, monkeypatch):
    isolated_config.write_text('site_domain = "example.com"\nreserved_slugs = ["team"]', encoding="utf-8")
    assert config.site_domain() == "example.com"
    assert {"www", "sites", "workspace", "team"} <= config.reserved_slugs()
    monkeypatch.setenv("COMPANY_BASE_SITE_DOMAIN", "other.test")
    assert config.site_domain() == "other.test"
    monkeypatch.setenv("COMPANY_BASE_SITE_DOMAIN", "")
    assert config.site_domain() is None
    monkeypatch.setenv("COMPANY_BASE_SITE_DOMAIN", "https://example.com")
    with pytest.raises(BaseError):
        config.site_domain()


def test_publish_atomic_versions_and_signer(publisher, build, repo):
    change(repo, "people/claude.md", signers=["claude"])
    with pytest.raises(BaseError, match="共用"):
        publish(publisher, build)
    before = git(repo, "rev-list", "--count", "HEAD")
    result = publisher.publish_site("demo-web", str(build), "demo", "claude")
    assert "https://demo.example.com" in result and "谁能看由主人" in result
    assert int(git(repo, "rev-list", "--count", "HEAD")) == int(before) + 1
    assert git(repo, "log", "-1", "--format=%an") == "claude · claude"
    site = site_doc(repo).meta["site"]
    assert site["status"] == "待发布" and site["pending"] and site["visibility"] == "指定的人"
    assert (sites.version_dir("demo", site["pending"]) / "index.html").exists()
    assert "发布状态：待发布" in publisher.get_app("demo-web")
    assert "sites/" not in git(repo, "ls-files")
    site.update(status="已发布", current=site["pending"], pending=None)
    change(repo, "apps/demo-web.md", site=site)
    for number in range(6):
        (build / "index.html").write_text(f"版本 {number}", encoding="utf-8")
        publisher.publish_site("demo-web", str(build), signed_as="claude")
    site = site_doc(repo).meta["site"]
    assert site["current"] == site["versions"][0]["id"] and site["pending"] is None
    assert len(site["versions"]) == len(list(sites.slug_dir("demo").iterdir())) == 5
    site["status"] = "已停用"
    change(repo, "apps/demo-web.md", site=site)
    publisher.publish_site("demo-web", str(build), signed_as="claude")
    site = site_doc(repo).meta["site"]
    assert site["status"] == "已停用" and site["pending"] != site["current"]


@pytest.mark.parametrize("slug", [None, "ab", "-abc", "abc-", "Aaa", "a" * 31, "../bad", "www", "workspace"])
def test_bad_slugs(publisher, build, slug):
    with pytest.raises(BaseError):
        publish(publisher, build, slug)


def test_slug_occupied_and_changed(publisher, build):
    publish(publisher, build)
    publisher.register_app(APP | {"id": "another"})
    with pytest.raises(BaseError, match="占用"):
        publisher.publish_site("another", str(build), "demo")
    with pytest.raises(BaseError, match="网页上"):
        publish(publisher, build, "new-name")
    for operation in (lambda: publisher.update_app("demo-web", {"site": {}}),
                      lambda: publisher.register_app(APP | {"id": "third", "site": {}})):
        with pytest.raises(BaseError, match="字段"):
            operation()


def test_permissions_kind_disabled(publisher, build, repo, as_agent, monkeypatch):
    with pytest.raises(BaseError):
        as_agent("reader").publish_site("demo-web", str(build), "demo")
    change(repo, "people/claude.md", access_mode="assigned", projects=["alpha"])
    with pytest.raises(BaseError, match="按授权"):
        publish(publisher, build)
    change(repo, "people/claude.md", access_mode="full")
    publisher.update_app("demo-web", {"kind": "bot"})
    with pytest.raises(BaseError, match="类型"):
        publish(publisher, build)
    monkeypatch.delenv("COMPANY_BASE_SITE_DOMAIN")
    with pytest.raises(BaseError, match="这台底座没开应用发布"):
        publish(publisher, build)
    with pytest.raises(BaseError, match="只能在这台电脑上的 agent 发布"):
        CompanyBase(repo, "claude", require_remote=True).publish_site("demo-web", str(build), "demo")


def test_stdio_only(repo):
    for remote in (True, False):
        names = {tool.name for tool in asyncio.run(build_server(str(repo), "claude", remote=remote).list_tools())}
        assert ("publish_site" in names) is not remote


@pytest.mark.parametrize("value", ["relative", "missing", "file", "parent", "no-index"])
def test_invalid_source(publisher, build, value):
    path = {"relative": "build", "missing": str(build / "missing"), "file": str(build / "index.html"),
            "parent": str(build / ".." / "build"), "no-index": str(build / "empty")}[value]
    (build / "empty").mkdir()
    with pytest.raises(BaseError):
        publisher.publish_site("demo-web", path, "demo")


def test_dot_files_skip_and_extension_fail(publisher, build):
    (build / ".env").write_text("sk-" + "x" * 30, encoding="utf-8")
    (build / ".git").mkdir()
    (build / ".git" / "secret.exe").write_bytes(b"not published")
    (build / "evil.exe").write_bytes(b"no")
    with pytest.raises(BaseError, match="evil.exe"):
        publish(publisher, build)
    (build / "evil.exe").unlink()
    result = publish(publisher, build)
    assert ".env" in result and ".git/" in result
    version = next(sites.slug_dir("demo").iterdir())
    assert [p.name for p in version.iterdir()] == ["index.html"]


@pytest.mark.parametrize("kind", ["file", "count", "total"])
def test_limits(publisher, build, monkeypatch, kind):
    if kind == "file":
        monkeypatch.setattr(sites, "MAX_FILE", 8)
    elif kind == "count":
        monkeypatch.setattr(sites, "MAX_FILES", 1)
        (build / "a.js").write_text("ok", encoding="utf-8")
    else:
        monkeypatch.setattr(sites, "MAX_TOTAL", 8)
    with pytest.raises(BaseError, match="上限|超过"):
        publish(publisher, build)


@pytest.mark.parametrize("secret", ["sk-" + "x" * 24, "ghp_" + "x" * 32,
    "github_pat_" + "x" * 32, "xoxb-" + "x" * 12, "AKIA" + "A" * 16,
    "-----BEGIN PRIVATE KEY-----", "eyJ" + "x" * 12 + "." + "y" * 12 + "." + "z" * 12])
def test_specific_secret_formats(publisher, build, secret):
    (build / "a.js").write_text("// 第一行\n" + secret, encoding="utf-8")
    with pytest.raises(BaseError) as error:
        publish(publisher, build)
    assert "a.js，行号 2" in str(error.value) and secret not in str(error.value)
    assert not list(sites.slug_dir("demo").iterdir())


def test_minified_js_not_false_positive(publisher, build):
    (build / "a.js").write_text("var o={token:e.access_token,password:form.password};", encoding="utf-8")
    assert "待发布" in publish(publisher, build)


def test_junction_and_cleanup_boundary(publisher, build, tmp_path):
    outside = tmp_path / "outside"
    outside.mkdir()
    (outside / "index.html").write_text("不能碰", encoding="utf-8")
    junction(build / "escape", outside)
    with pytest.raises(BaseError, match="目录联接"):
        publish(publisher, build)
    with pytest.raises(BaseError, match="目录联接"):
        sites.remove_tree(build / "escape", build)
    with pytest.raises(BaseError, match="越界"):
        sites.remove_tree(outside, build)
    assert (outside / "index.html").read_text(encoding="utf-8") == "不能碰"


def test_symbolic_link(publisher, build, tmp_path, monkeypatch):
    target = tmp_path / "elsewhere.txt"
    target.write_text("外面的文件", encoding="utf-8")
    try:
        (build / "link.txt").symlink_to(target)
    except OSError as error:
        assert error.winerror == 1314  # 无创建权限时，仍验证链接类型的拒绝分支。
        import os
        import stat
        original = Path.lstat
        def lstat(path):
            return os.stat_result((stat.S_IFLNK | 0o777, 0, 0, 1, 0, 0, 0, 0, 0, 0)) if path == build / "link.txt" else original(path)
        (build / "link.txt").write_text("占位", encoding="utf-8")
        monkeypatch.setattr(Path, "lstat", lstat)
    with pytest.raises(BaseError, match="符号链接"): 
        publish(publisher, build)


def test_commit_failure_removes_unreferenced_version(publisher, build, repo, monkeypatch):
    before = git(repo, "rev-parse", "HEAD")
    def fail(*args, **kwargs):
        raise BaseError("模拟提交失败")
    monkeypatch.setattr(sites, "write_site", fail)
    with pytest.raises(BaseError, match="提交失败"):
        publish(publisher, build)
    assert git(repo, "rev-parse", "HEAD") == before
    assert not list(sites.slug_dir("demo").iterdir())
    assert "site" not in site_doc(repo).meta
