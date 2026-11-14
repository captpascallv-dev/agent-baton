import argparse
import json
from pathlib import Path
import subprocess
import sys

import pytest

from onestead import config, server
from onestead.web import launcher


def test_path_priority_and_restore(tmp_path, monkeypatch):
    monkeypatch.setenv("ONESTEAD_DATA", "environment-data")
    monkeypatch.setenv("ONESTEAD_CONFIG", str(tmp_path / "environment.toml"))
    monkeypatch.setenv("ONESTEAD_STATE_DIR", "environment-state")
    given = argparse.Namespace(data="argument-data", config=str(tmp_path / "argument.toml"), state="argument-state")
    with config.use_paths(given):
        assert config.data_dir() == "argument-data"
        assert config.config_path() == Path(given.config)
        assert config.state_dir() == Path(given.state)
    assert config.data_dir() == "environment-data"
    assert config.state_dir() == Path("environment-state")


@pytest.mark.parametrize("entry", ["mcp", "web"])
def test_service_paths_apply_before_configuration(tmp_path, monkeypatch, entry):
    monkeypatch.delenv("COMPANY_BASE_OWNER")
    selected = tmp_path / "selected.toml"
    selected.write_text('owner_key="example-owner"\nlang="en"\n', encoding="utf-8")
    monkeypatch.setenv("ONESTEAD_CONFIG", str(tmp_path / "broken.toml"))
    (tmp_path / "broken.toml").write_text("[broken", encoding="utf-8")
    seen = []
    def check():
        seen.append((config.data_dir(), config.config_path(), config.state_dir(), config.owner_key()))
    options = ["--config", str(selected), "--data", str(tmp_path / "data"), "--state", str(tmp_path / "state")]
    if entry == "mcp":
        class Service:
            def run(self, transport):
                assert transport == "stdio"
                check()
        def build(root, agent):
            assert root == str(tmp_path / "data") and agent == "helper"
            return Service()
        monkeypatch.setattr(server, "build_server", build)
        server.main([*options, "--agent", "helper"])
    else:
        monkeypatch.setattr(launcher, "_main", lambda args: check())
        launcher.main(options)
    assert seen == [(str(tmp_path / "data"), selected, tmp_path / "state", "example-owner")]
    assert not (tmp_path / "state").exists()


@pytest.mark.parametrize("before", [True, False])
def test_cli_api_uses_private_paths(tmp_path, before):
    boot = [sys.executable, "-m", "onestead.cli", "bootstrap", "--data", str(tmp_path / "data"),
            "--config", str(tmp_path / "config.toml"), "--state", str(tmp_path / "state"), "--agent", "assistant", "--json"]
    created = json.loads(subprocess.run(boot, capture_output=True, check=True).stdout)
    options = ["--config", created["config_path"], "--data", created["data_dir"], "--state", created["state_dir"]]
    args = [*options, "api"] if before else ["api", *options]
    proc = subprocess.run([sys.executable, "-m", "onestead.cli", *args, "--as", "assistant"],
                          input=json.dumps({"method": "list_projects"}), capture_output=True,
                          text=True, encoding="utf-8")
    assert proc.returncode == 0, proc.stderr + proc.stdout
    assert json.loads(proc.stdout)["ok"]
