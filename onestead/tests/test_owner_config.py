"""主人键缺失拒绝构造；换主人后仍保留本机专属、个人隔离和迁移边界。"""

import pytest

from onestead import config, migrate_stage4, online, server
from onestead.base import CompanyBase
from onestead.store import BaseError, Store, Viewer, make_viewer
from onestead.web import launcher
from onestead.web.auth import LocalAuth, Principal, RemoteAuth

from .support import OWNER, PUBLIC_HOST


@pytest.mark.parametrize("entry", [
    lambda root: CompanyBase(root, "codex"),
    lambda root: Viewer("codex", None),
    lambda root: make_viewer([], "codex"),
    lambda root: LocalAuth(48123, "test"),
    lambda root: RemoteAuth(root, PUBLIC_HOST),
    lambda root: Principal("codex", False, "shortcut", None),
    lambda root: server.build_server(str(root), "codex"),
    lambda root: server.TokenGate(str(root)),
    lambda root: server.remote_app(str(root), 8765),
    lambda root: migrate_stage4.migration_plan(root),
    lambda root: online.main(),
    lambda root: launcher.main(),
])
def test_missing_owner_fails_before_start(entry, configured_data, monkeypatch):
    monkeypatch.delenv("COMPANY_BASE_OWNER")
    with pytest.raises(BaseError, match="owner_key.*COMPANY_BASE_OWNER"):
        entry(configured_data)


@pytest.mark.parametrize("key", ["", " ", "../boss", "Boss", "boss-draft"])
def test_invalid_owner_fails_closed(key, monkeypatch):
    monkeypatch.setenv("COMPANY_BASE_OWNER", key)
    with pytest.raises(BaseError, match="owner_key"):
        config.owner_key()


def test_configured_owner_keeps_identity_boundaries(repo):
    docs = Store(repo).load()
    with pytest.raises(BaseError, match="身份留给网页界面"):
        make_viewer(docs, OWNER)
    owner = make_viewer(docs, OWNER, allow_owner=True)
    assert owner.is_owner and owner.can_write and not owner.can_remote
    assert owner.workspace_owner == OWNER
    agent = make_viewer(docs, "codex")
    assert agent.workspace_owner == OWNER and not agent.is_owner
    assert not RemoteAuth(repo, PUBLIC_HOST).viewer(OWNER)


def test_migration_uses_configured_owner(repo):
    path = repo / "people" / "codex.md"
    path.write_text(path.read_text(encoding="utf-8").replace(f"owner: {OWNER}\n", ""), encoding="utf-8")
    row = next(row for row in migrate_stage4.migration_plan(repo) if row["rel"] == "people/codex.md")
    assert row["changes"]["owner"] == {"before": None, "after": OWNER}


def test_config_file_owner_used_when_env_absent(isolated_config, monkeypatch, repo):
    isolated_config.write_text(f'owner_key = "{OWNER}"', encoding="utf-8")
    monkeypatch.delenv("COMPANY_BASE_OWNER")
    assert CompanyBase(repo, OWNER, allow_owner=True)._ctx()[0].is_owner
