# Getting started

Onestead (package and command: `onestead`) reads company facts from a git repository you own. One command creates that repository, writes the local config, and prints connection instructions for Claude Code, Codex, Cursor, WorkBuddy, and ChatGPT.

```text
onestead init <data-dir> --owner <key> --name <display-name> [--lang en|zh] [--force-config]
```

`<data-dir>` is the repository. The command creates it when the path is missing. An empty directory is fine. A path that already has files is refused, and the message says why. Nothing in that directory is changed.

`--owner` is the owner's member key: lowercase letters, digits, and hyphens, and it must not end in `-draft`. This key is reserved for the local web app. MCP and the CLI reject it. Agents need their own member files later.

`--name` is the display name on the owner member file. It is also the author of the first commit. The existing git author email format is retained. This flag does not set the workspace's name; set `name` in the local config for that.

`--lang` chooses the handbook prose and the `getting-started` project. `en` is the default. `zh` is the other copy. Both live in the package and are read with `importlib.resources`.

## What the repository contains

```text
FORMAT.md
handbook/company.md
handbook/collaboration.md
people/<owner>.md
projects/getting-started/project.md
projects/getting-started/status.md
decisions/.gitkeep
assets/.gitkeep
apps/.gitkeep
todos/.gitkeep
home/.gitkeep
skills/.gitkeep
```

The owner member is a human, reads everything (`access_mode: full`), and can write. `onestead init` makes one commit on `main`. Later writes from the tools commit as the member who did the write.

`getting-started` says what to do next: open the local web app, add an agent member, and paste a connection snippet.

## Config

The command writes `name = "Onestead"`, `data_dir`, `owner_key` and `lang` (from `--lang`, default `en`) to `~/.onestead/config.toml`, or to the file named by `ONESTEAD_CONFIG`.

If that file already exists, it is left as it is. The command prints: `已有配置，要改请手动编辑或加 --force-config`. With `--force-config`, the old file is renamed to `config.toml.bak-<YYYYMMDD-HHMMSS>` beside it, and then the new file is written. The time is UTC+8.

For an existing installation, configuration fallback and command aliases are described in [Compatibility](../README.md#已有安装仍然兼容). Environment variables still win over the file. See [Configuration](deploy.md#configuration). `data_dir` is stored as an absolute path.

## Connect an agent

The command prints four blocks. The same text is what the local web page shows under "接一个新的伙伴". Each block can be copied on its own.

The MCP program is `onestead-mcp` in the scripts directory of the Python environment that ran the command (`Scriptsonestead-mcp.exe` on Windows). Use the absolute path that `init` prints. Set `ONESTEAD_AGENT` to the agent's member key, not the owner key.

- **Claude Code.** A `claude mcp add` command, or a `.mcp.json` snippet.
- **Codex.** A `[mcp_servers.onestead]` block for `~/.codex/config.toml`.
- **Cursor.** An `mcp.json` snippet, usually `~/.cursor/mcp.json`.
- **WorkBuddy.** An `mcp.json` snippet for `~/.workbuddy/mcp.json`.
- **ChatGPT.** Deploy the remote server first, as described in [Deploy](deploy.md), then add a custom MCP connector with OAuth. The command does not print a URL. You use your own public hostname.

An agent that cannot use MCP calls `onestead <command> --as <key>` instead.

## Next

The last line of the command is the next step: open the local web app with `python -m onestead.web`, or ask an agent to call `company()` first.

On the web members page, add the agent (`kind: agent`, `owner` set to your key) before that identity can read internal pages.

## Languages

`--lang` changes the prose. It does not change the words the program matches.

Project section headings stay `目标`, `现状`, `下一步`, `分工`, `入口`, and `边界与注意`. The getting-started project state stays `进行中`. App kinds and skill origins in later files are Chinese too. This is the current rule in [Data format](data-format.md). A later release is planned to make those words translatable. Do not translate them in files the tools read.
