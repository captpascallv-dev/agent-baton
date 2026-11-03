"""onestead connect：只在临时主目录里预览和写入三家假配置。"""

import json
import sys
import tomllib
from pathlib import Path

from onestead.cli import run


def _home(tmp_path, monkeypatch) -> Path:
    home = tmp_path / "userhome"
    home.mkdir()
    monkeypatch.setenv("HOME", str(home))
    monkeypatch.setenv("USERPROFILE", str(home))
    monkeypatch.setenv("ONESTEAD_LANG", "zh")
    monkeypatch.delenv("CODEX_HOME", raising=False)
    monkeypatch.delenv("ONESTEAD_DATA", raising=False)
    monkeypatch.delenv("COMPANY_BASE_DATA", raising=False)
    return home


def _mcp(monkeypatch, home: Path, *, present: bool) -> Path:
    path = home / ("onestead-mcp.exe" if present else "missing-onestead-mcp.exe")
    if present:
        path.write_bytes(b"")
    monkeypatch.setattr("onestead.connect.tool_path", lambda name: path)
    return path


def _files(home: Path) -> dict[str, Path]:
    return {
        "claude-code": home / ".claude.json",
        "codex": home / ".codex" / "config.toml",
        "cursor": home / ".cursor" / "mcp.json",
    }


def _put(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def _snapshot(root: Path) -> dict[str, bytes]:
    return {str(path): path.read_bytes() for path in root.rglob("*") if path.is_file()}


def _backups(root: Path) -> list[Path]:
    return [path for path in root.rglob("*") if ".onestead-backup-" in path.name]


def _section(out: str, client: str) -> str:
    marker = f"客户端：{client}"
    start = out.index(marker)
    rest = out[start:]
    nxt = rest.find("\n\n客户端：")
    return rest if nxt < 0 else rest[:nxt]


def test_preview_writes_nothing(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=True)
    paths = _files(home)
    _put(paths["claude-code"], b'{"mcpServers": {"company-base": {"command": "old", "env": {"COMPANY_BASE_AGENT": "from-old"}}}}\n')
    _put(paths["codex"], b"# keep\n[other]\nname = \"stable\"\n")
    _put(paths["cursor"], b'{"mcpServers": {}}\n')
    before = _snapshot(home)
    assert run(["connect"]) == 0
    out = capsys.readouterr().out
    assert out.startswith("预览，未写入任何文件。")
    assert "旧项 company-base 会被替换。" in _section(out, "claude-code")
    assert "将改成：" in out
    assert _snapshot(home) == before
    assert _backups(home) == []


def test_apply_backs_up_and_replaces_company_base(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    mcp = _mcp(monkeypatch, home, present=True)
    paths = _files(home)
    original = {
        "userID": "keep",
        "mcpServers": {
            "other": {"command": "stay"},
            "company-base": {"command": "company-base-mcp", "env": {"COMPANY_BASE_AGENT": "from-old"}},
        },
    }
    raw = (json.dumps(original, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    _put(paths["claude-code"], raw)
    _put(paths["cursor"], b'{"theme": "keep", "mcpServers": {"company-base": {"env": {"ONESTEAD_AGENT": "cursor-old"}}}}\n')
    _put(paths["codex"], b"# codex\n")
    assert run(["connect", "--apply"]) == 0
    out = capsys.readouterr().out
    assert "已写入。" in out
    claude = json.loads(paths["claude-code"].read_text(encoding="utf-8"))
    assert claude["userID"] == "keep"
    assert claude["mcpServers"]["other"] == {"command": "stay"}
    assert "company-base" not in claude["mcpServers"]
    assert claude["mcpServers"]["onestead"] == {
        "command": str(mcp),
        "env": {"ONESTEAD_AGENT": "from-old"},
    }
    cursor = json.loads(paths["cursor"].read_text(encoding="utf-8"))
    assert cursor["theme"] == "keep"
    assert cursor["mcpServers"]["onestead"]["env"]["ONESTEAD_AGENT"] == "cursor-old"
    assert "company-base" not in cursor["mcpServers"]
    saved = _backups(home)
    assert len(saved) == 3
    by_name = {path.name.split(".onestead-backup-")[0]: path for path in saved}
    assert by_name[".claude.json"].read_bytes() == raw
    assert "旧项 company-base 会被替换。" in _section(out, "claude-code")
    assert "身份：from-old" in _section(out, "claude-code")


def test_codex_toml_keeps_other_tables_and_comments(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    mcp = _mcp(monkeypatch, home, present=True)
    kept = (
        "# top comment\r\n"
        "[other]\r\n"
        "name = \"stable\"  # inline\r\n"
        "\r\n"
        "# tail comment\r\n"
        "[notes]\r\n"
        "ok = true\r\n"
        "\r\n"
    )
    legacy = (
        "[mcp_servers.company-base]\r\n"
        "command = \"company-base-mcp\"\r\n"
        "\r\n"
        "[mcp_servers.company-base.env]\r\n"
        "COMPANY_BASE_AGENT = \"from-old\"\r\n"
    )
    path = home / ".codex" / "config.toml"
    _put(path, (kept + legacy).encode("utf-8"))
    _put(home / ".claude.json", b"{}\n")
    _put(home / ".cursor" / "mcp.json", b"{}\n")
    assert run(["connect", "--apply", "--client", "codex"]) == 0
    result = path.read_bytes().decode("utf-8")
    assert result.startswith(kept)
    assert "[mcp_servers.company-base]" not in result
    assert "company-base-mcp" not in result
    assert 'name = "stable"  # inline' in result
    assert "# top comment" in result and "# tail comment" in result
    parsed = tomllib.loads(result)
    assert parsed["other"]["name"] == "stable"
    assert parsed["notes"]["ok"] is True
    assert parsed["mcp_servers"]["onestead"]["command"] == str(mcp)
    assert "args" not in parsed["mcp_servers"]["onestead"]
    assert parsed["mcp_servers"]["onestead"]["env"]["ONESTEAD_AGENT"] == "from-old"
    assert "company-base" not in parsed["mcp_servers"]


def test_multiline_string_is_not_treated_as_a_table(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=True)
    body = (
        '# keep\n'
        '[other]\n'
        'note = """\n'
        'not a header\n'
        '[mcp_servers.onestead]\n'
        '"""\n'
        'value = 1\n'
        '\n'
        '[mcp_servers.company-base]\n'
        'command = "old"\n'
        '\n'
        '[mcp_servers.company-base.env]\n'
        'COMPANY_BASE_AGENT = "from-old"\n'
    )
    path = home / ".codex" / "config.toml"
    _put(path, body.encode("utf-8"))
    assert run(["connect", "--apply", "--client", "codex"]) == 0
    result = path.read_text(encoding="utf-8")
    assert 'note = """\nnot a header\n[mcp_servers.onestead]\n"""\nvalue = 1\n' in result
    assert "[mcp_servers.company-base]" not in result
    assert tomllib.loads(result)["mcp_servers"]["onestead"]["env"]["ONESTEAD_AGENT"] == "from-old"


def test_inline_toml_server_is_skipped(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=True)
    path = home / ".codex" / "config.toml"
    original = '[mcp_servers]\nonestead = { command = "old" }\nother = { command = "keep" }\n'
    _put(path, original.encode("utf-8"))
    _put(home / ".claude.json", b"{}\n")
    assert run(["connect", "--apply"]) == 0
    out = capsys.readouterr().out
    assert path.read_text(encoding="utf-8") == original
    assert "已跳过：" in _section(out, "codex")
    assert "已写入。" in _section(out, "claude-code")
    assert _backups(path.parent) == []


def test_bad_json_skips_that_client_only(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=True)
    paths = _files(home)
    _put(paths["claude-code"], b"{")
    _put(paths["codex"], b"# ok\n")
    _put(paths["cursor"], b'{"mcpServers": {}}\n')
    assert run(["connect", "--apply"]) == 0
    out = capsys.readouterr().out
    assert paths["claude-code"].read_bytes() == b"{"
    assert "JSON 解析失败，原文件未改。" in _section(out, "claude-code")
    assert "已写入。" in _section(out, "codex")
    assert "已写入。" in _section(out, "cursor")
    names = [path.name for path in _backups(home)]
    assert not any(name.startswith(".claude.json.onestead-backup-") for name in names)
    assert any(name.startswith("config.toml.onestead-backup-") for name in names)
    assert any(name.startswith("mcp.json.onestead-backup-") for name in names)


def test_second_apply_is_idempotent(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=True)
    paths = _files(home)
    _put(paths["claude-code"], b'{"other": 1}\n')
    _put(paths["codex"], b"# note\n")
    _put(paths["cursor"], b'{"mcpServers": {"other": {"command": "stay"}}}\n')
    assert run(["connect", "--apply"]) == 0
    capsys.readouterr()
    first = _snapshot(home)
    backups = _backups(home)
    assert len(backups) == 3
    assert run(["connect", "--apply"]) == 0
    out = capsys.readouterr().out
    assert out.count("已是最新。") == 3
    assert "已写入。" not in out
    assert "备份：" not in out
    assert _snapshot(home) == first
    assert _backups(home) == backups


def test_client_flag_limits_one_client(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=True)
    paths = _files(home)
    for path, data in (
        (paths["claude-code"], b"{}\n"),
        (paths["codex"], b"# same\n"),
        (paths["cursor"], b"{}\n"),
    ):
        _put(path, data)
    before = {key: path.read_bytes() for key, path in paths.items()}
    assert run(["connect", "--apply", "--client", "cursor"]) == 0
    out = capsys.readouterr().out
    assert "客户端：cursor" in out
    assert "客户端：claude-code" not in out
    assert "客户端：codex" not in out
    assert paths["claude-code"].read_bytes() == before["claude-code"]
    assert paths["codex"].read_bytes() == before["codex"]
    assert paths["cursor"].read_bytes() != before["cursor"]
    assert [path.parent.name for path in _backups(home)] == [".cursor"]


def test_missing_client_is_omitted(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=True)
    _put(home / ".claude.json", b"{}\n")
    _put(home / ".codex" / "config.toml", b"# only\n")
    assert run(["connect"]) == 0
    out = capsys.readouterr().out
    assert "客户端：claude-code" in out
    assert "客户端：codex" in out
    assert "客户端：cursor" not in out
    assert not (home / ".cursor").exists()


def test_directory_without_config_file_counts_as_installed(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    mcp = _mcp(monkeypatch, home, present=True)
    (home / ".cursor").mkdir()
    assert run(["connect", "--client", "cursor"]) == 0
    preview = capsys.readouterr().out
    target = home / ".cursor" / "mcp.json"
    assert "客户端：cursor" in preview
    assert "文件还不存在，将会新建。" in preview
    assert not target.exists()
    assert run(["connect", "--client", "cursor", "--apply"]) == 0
    written = json.loads(target.read_text(encoding="utf-8"))
    assert written["mcpServers"]["onestead"]["command"] == str(mcp)
    assert written["mcpServers"]["onestead"]["env"]["ONESTEAD_AGENT"] == "cursor"
    assert _backups(home) == []


def test_codex_home_overrides_default_directory(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=True)
    custom = tmp_path / "codex-home"
    decoy = home / ".codex" / "config.toml"
    _put(custom / "config.toml", b"# custom\n")
    _put(decoy, b"# decoy\n")
    monkeypatch.setenv("CODEX_HOME", str(custom))
    assert run(["connect", "--apply", "--client", "codex"]) == 0
    assert "onestead" in (custom / "config.toml").read_text(encoding="utf-8")
    assert decoy.read_bytes() == b"# decoy\n"


def test_missing_mcp_uses_python_module(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=False)
    paths = _files(home)
    _put(paths["claude-code"], b"{}\n")
    _put(paths["codex"], b"# empty\n")
    _put(paths["cursor"], b"{}\n")
    assert run(["connect", "--apply"]) == 0
    for client in ("claude-code", "cursor"):
        entry = json.loads(paths[client].read_text(encoding="utf-8"))["mcpServers"]["onestead"]
        assert entry["command"] == sys.executable
        assert entry["args"] == ["-m", "onestead.server"]
    codex = tomllib.loads(paths["codex"].read_text(encoding="utf-8"))["mcp_servers"]["onestead"]
    assert codex["command"] == sys.executable
    assert codex["args"] == ["-m", "onestead.server"]


def test_agent_key_requires_exactly_one_client(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    mcp = _mcp(monkeypatch, home, present=True)
    path = home / ".codex" / "config.toml"
    _put(path, b"# base\n")
    _put(home / ".cursor" / "mcp.json", b"{}\n")
    before = _snapshot(home)
    assert run(["connect", "--apply", "--agent-key", "chosen"]) == 2
    err = capsys.readouterr().err
    assert "--agent-key 只能和单独一个 --client 一起用。" in err
    assert _snapshot(home) == before
    assert run(["connect", "--apply", "--client", "codex", "--client", "cursor", "--agent-key", "chosen"]) == 2
    assert _snapshot(home) == before
    capsys.readouterr()
    assert run(["connect", "--apply", "--client", "codex", "--agent-key", "chosen"]) == 0
    parsed = tomllib.loads(path.read_text(encoding="utf-8"))
    assert parsed["mcp_servers"]["onestead"]["env"]["ONESTEAD_AGENT"] == "chosen"
    assert parsed["mcp_servers"]["onestead"]["command"] == str(mcp)


def test_missing_member_is_reported_and_does_not_block(tmp_path, monkeypatch, repo, capsys):
    home = _home(tmp_path, monkeypatch)
    _mcp(monkeypatch, home, present=True)
    monkeypatch.setenv("ONESTEAD_DATA", str(repo))
    paths = _files(home)
    for path in paths.values():
        _put(path, b"{}\n" if path.suffix == ".json" else b"# x\n")
    assert run(["connect", "--apply"]) == 0
    out = capsys.readouterr().out
    assert "没能读取数据仓库" not in out
    assert "数据仓库里没有成员 claude" not in _section(out, "claude-code")
    assert "数据仓库里没有成员 codex" not in _section(out, "codex")
    assert "数据仓库里没有成员 cursor。" in _section(out, "cursor")
    assert "已写入。" in _section(out, "cursor")
    assert json.loads(paths["cursor"].read_text(encoding="utf-8"))["mcpServers"]["onestead"]["env"]["ONESTEAD_AGENT"] == "cursor"


def test_init_prints_connect_hint_at_the_prompt_and_the_end(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    monkeypatch.delenv("ONESTEAD_CONFIG", raising=False)
    monkeypatch.delenv("COMPANY_BASE_CONFIG", raising=False)
    assert run(["init", str(tmp_path / "company"), "--owner", "ada", "--name", "Ada Lovelace"]) == 0
    out = capsys.readouterr().out
    assert out.count("接本机 agent：`onestead connect`") >= 2
    assert out.rstrip().splitlines()[-1].startswith("下一步：")
    assert (home / ".onestead" / "config.toml").is_file()


def test_workbuddy_entry_keeps_other_servers_and_type(tmp_path, monkeypatch, capsys):
    home = _home(tmp_path, monkeypatch)
    mcp = _mcp(monkeypatch, home, present=True)
    path = home / ".workbuddy" / "mcp.json"
    other = {"type": "stdio", "command": "other.cmd", "disabled": True, "env": {"X": "keep"}}
    _put(path, (json.dumps({"mcpServers": {"other": other}}, indent=2) + "\n").encode("utf-8"))
    assert run(["connect", "--client", "workbuddy", "--apply"]) == 0
    out = capsys.readouterr().out
    assert "身份：workbuddy" in _section(out, "workbuddy")
    servers = json.loads(path.read_text(encoding="utf-8"))["mcpServers"]
    assert servers["other"] == other
    assert servers["onestead"] == {"type": "stdio", "command": str(mcp), "env": {"ONESTEAD_AGENT": "workbuddy"}}
    assert len(_backups(home)) == 1
    assert run(["connect", "--client", "workbuddy", "--apply"]) == 0
    assert "已是最新。" in capsys.readouterr().out
    assert len(_backups(home)) == 1
