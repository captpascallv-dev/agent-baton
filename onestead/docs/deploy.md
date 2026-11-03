# Deploy

`Onestead` serves this computer. Remote clients reach it through a tunnel or a reverse proxy that you run. The application listens on loopback. HTTPS ends at that tunnel or proxy.

The local web app, local MCP, CLI, remote server, and bundled tunnel supervisor have platform-aware code for Windows, macOS, and Linux. Runtime testing has only been done on Windows; macOS and Linux installation and operation are **not verified**. Their platform branches are covered by simulated tests. See [Other operating systems](#other-operating-systems).

The computer has to be on, awake, and running the process. Remote clients have nowhere else to connect.

## What listens where

Two programs, both on `127.0.0.1`.

The local web app:

```powershell
python -m onestead.web
```

It prefers port `8764` on `127.0.0.1`, falls back to a free port, and opens a session-bootstrap URL in the default browser. The session token is reused for up to 30 days. It exits after 24 hours without a heartbeat. When `backup_dir` is configured, it backs up 5 minutes after start, every 6 hours while running, and on exit; unchanged commits are skipped. That port is for a browser on this computer. The cookie is `cb_session_<port>`. The `Host` header must be exactly `127.0.0.1:<port>`.

The remote server:

```powershell
onestead-remote 8765 --public-host your.example.com
```

`onestead-remote` is the console script. `python -m onestead.server --remote 8765 --public-host your.example.com` is the same process. The port defaults to `8765`. The process binds `127.0.0.1` only. Passing any other bind address is refused. There is no switch for `0.0.0.0`.

On that port:

| Path | Who |
| --- | --- |
| `/` | The web app for colleagues. |
| `/mcp` | Remote MCP. Header `Authorization: Bearer <token>`. |
| `/chatgpt/mcp` | ChatGPT custom connector. OAuth. |
| `/skills/<id>.zip` | A shared skill package. Same bearer token as `/mcp`. |
| `/.well-known/oauth-authorization-server` | OAuth discovery. |
| `/.well-known/oauth-protected-resource/chatgpt/mcp` | Protected-resource metadata for the ChatGPT route. |

The data repository is `data_dir` in the config file, or `ONESTEAD_DATA`. The environment variable wins. There is no compiled path. If both are missing, the process errors. The CLI can pass `--data` or `--root` for that command. See [Configuration](#configuration).

## The public hostname

Set the hostname you will publish:

```powershell
onestead-remote 8765 --public-host your.example.com
```

`public_host` in the config file, or `ONESTEAD_PUBLIC_HOST`, sets the same hostname. The environment variable wins over the file. When `--public-host` is also set, that flag is the value that is used. If none of them is set, the remote server errors. There is no hostname compiled into the source. The local web app still runs, and the page shows that no public host is configured.

The hostname is a lowercase DNS name with at least two labels, such as `your.example.com`. No port, no trailing dot, no scheme.

When the hostname is set, the server builds `https://your.example.com` as its public origin. It does this even for a request that arrived at loopback.

## The Host header

The colleague web app accepts a request only when the `Host` header list is exactly one value, `your.example.com`. The match is exact. `your.example.com:443` is a different value. So is `127.0.0.1:8765`.

MCP, skill download, and the OAuth routes accept that public hostname, and they also accept `127.0.0.1:<port>`, `localhost:<port>`, and `[::1]:<port>`. A colleague page still needs the public hostname.

Both startup paths pass `proxy_headers=False`. The application never reads a `Forwarded` or `X-Forwarded-*` header. A proxy that puts the real hostname only in `X-Forwarded-Host` is refused. Send the public hostname in `Host`, unchanged.

A second `Host` header is refused.

## HTTPS and the `__Host-` cookie

The colleague session cookie is `__Host-cb_web`. The login form uses `__Host-cb_login`. The ChatGPT consent form uses `__Host-cb_chatgpt_consent`.

The server sets them with `Secure`, `Path=/`, `HttpOnly`, and `SameSite=Strict`, and it does not set `Domain`. Browsers store a `__Host-` cookie only from an HTTPS origin, only with `Path=/`, and only when `Domain` is absent. On plain HTTP the browser drops the cookie, so the colleague cannot stay logged in.

The session lasts 30 days. The login and consent cookies last 10 minutes.

The login form takes a human member's token. The human needs `remote_enabled: true`. An agent token is refused on that form. Agent tokens are for `/mcp` and for the ChatGPT consent page. Feishu is optional: when configured, the login page also offers Feishu; otherwise its UI is hidden and its routes return 404, while token login remains available. Feishu app setup is outside this note.

Bearer tokens do not use this cookie. A public HTTP proxy would still carry MCP tokens in clear text. Terminate TLS on the tunnel or the proxy. Keep port `8765` on loopback.

## Cloudflare Tunnel

The tunnel process runs on this computer and connects out. The application does not open an inbound port.

Create a tunnel in your own Cloudflare account and point the public hostname at the loopback port. The token stays in the tunnel client's token file. It does not go in the data repository, and it does not go in this file.

Ingress, in Cloudflare's configuration for that tunnel:

```yaml
ingress:
  - hostname: your.example.com
    service: http://127.0.0.1:8765
  - service: http_status:404
```

Leave the hostname header alone, so the origin sees `Host: your.example.com`. This tree does not set an origin hostname override.

Run the app and the tunnel as two processes:

```powershell
onestead-remote 8765 --public-host your.example.com
cloudflared tunnel --no-autoupdate run --token-file C:\path\to\tunnel-token.txt
```

Use a path you choose for the token file. A generic example is above. The tunnel supervisor below uses a fixed filename in the configured state directory.

`cloudflared` is not shipped inside `Onestead`. Install it yourself. `ONESTEAD_CLOUDFLARED` can point at the binary.

### The tunnel supervisor

`python -m onestead.online` starts the remote server on `127.0.0.1:8765` and supervises `cloudflared`. It reads `public_host` from the config file, or `ONESTEAD_PUBLIC_HOST`. The environment variable wins. It does not take `--public-host`. If neither is set, the helper errors and does not start. There is no hostname compiled into `online.py`. The module's comment says a Windows scheduled task can launch it with `pythonw` at login. The code does not create that task. Whether a task is installed on a given machine: not verified.

Platform-specific behavior:

- `ONESTEAD_CLOUDFLARED` takes precedence over `PATH`. On Windows only, it then searches the WinGet packages directory in `%LOCALAPPDATA%\Microsoft\WinGet\Packages`.
- Windows stops leftover `cloudflared.exe` processes whose command lines mention `cloudflare-tunnel-token.txt`, using PowerShell and `Win32_Process`. macOS/Linux use `ps -eo pid,args` and send `SIGTERM` to matching `cloudflared` processes. If process lookup is unavailable, cleanup is skipped with a log message; termination failures are logged too.
- The token file used by cloudflared is `cloudflare-tunnel-token.txt` in the state directory. `state_dir` or `ONESTEAD_STATE_DIR` replaces that directory. Platform defaults are listed under [Configuration](#configuration).
- Logs go to `online.log` and `cloudflared.log` in that same directory.
- It asks `cloudflared` for readiness on `127.0.0.1:20241`. That port is metrics for the tunnel, not the application.
- It tries protocol `quic`, then `http2`. If the tunnel stays down for 60 seconds, it switches and starts `cloudflared` again. If `cloudflared` exits, it waits 15 seconds and starts it again.
- If port `8765` is already taken, the helper exits.
- If the binary or the token file is missing, it still starts the app on loopback and logs an error. Nothing is reachable from outside until a tunnel exists.
- When the app process leaves, the helper terminates the tunnel it started.

The hostname mapping still lives in the Cloudflare tunnel configuration. The helper does not contain an ingress file.

## Caddy

Caddy terminates TLS and proxies to loopback. Set `Host` explicitly.

```caddy
your.example.com {
	reverse_proxy 127.0.0.1:8765 {
		header_up Host your.example.com
	}
}
```

Run `onestead-remote 8765 --public-host your.example.com` next to Caddy. Point DNS at the machine that runs Caddy only if that machine is this computer. `Onestead` does not run anywhere else.

## nginx

nginx's default `Host` for a proxied request is the upstream address, `127.0.0.1:8765`. The colleague web app rejects that. Set `Host` to the public hostname.

```nginx
server {
    listen 443 ssl;
    server_name your.example.com;

    # ssl_certificate and ssl_certificate_key

    location / {
        proxy_pass http://127.0.0.1:8765;
        proxy_http_version 1.1;
        proxy_set_header Host your.example.com;
    }
}
```

`X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host` are not read. Adding them does not change the host check.

## ChatGPT

In ChatGPT, add a custom MCP connector.

| Field | Value |
| --- | --- |
| URL | `https://your.example.com/chatgpt/mcp` |
| Authentication | OAuth |

The issuer is `https://your.example.com`. The resource is `https://your.example.com/chatgpt/mcp`. Both come from the public hostname. Discovery is at `https://your.example.com/.well-known/oauth-authorization-server`.

The connector redirects only to these URLs:

- `https://chatgpt.com/connector_platform_oauth_redirect`
- `https://chatgpt.com/connector/oauth/<id>`

Any other redirect is refused.

On the consent page, paste an agent token from the members page. A person's web login does not authorize the connector. The token's member must be an agent who can connect remotely. One agent token can complete this consent once. Connecting again needs a new agent token. An authorization code that expires unused does not consume the token. Codes last 5 minutes.

The OAuth scope remains `company-base` (a protocol identifier, not the renamed command). Access tokens last 1 hour. The refresh grant lasts 30 days. Refresh rotates the access token.

After consent, ChatGPT calls `https://your.example.com/chatgpt/mcp`. On that route, `search` and `fetch` read the pages that agent may see.

## Tokens and the remote switch

The members page on the local web app issues a token. The token is shown once. The server stores a SHA-256 hash outside the git repository. The file is `tokens.json` in the state directory. `ONESTEAD_TOKENS` replaces that file. `state_dir` or `ONESTEAD_STATE_DIR` replaces the directory. See [Configuration](#configuration).

Issued tokens start with `cbx_`. A bearer token of this kind does not expire. A web session opened with one lasts 30 days and dies when that token is revoked. ChatGPT access issued from that token dies with it too, because each OAuth lookup checks that the source token is still live.

Revoke a token on the members page. Disconnecting the connector inside ChatGPT leaves the token valid.

`remote_enabled: false` rejects remote MCP, the colleague web app, and new tokens for that member. Saving that change in the web app also revokes the member's live credentials. When the member is human, credentials for their agents are revoked in the same save. Editing the file by hand turns access off immediately. The revoke of stored tokens happens on the web save, and on archive.

Archiving a member sets `remote_enabled: false`, revokes credentials, and keeps the file. Restoring the member leaves remote access off. Old tokens stay revoked. Issue a new one after you turn remote access back on.

A colleague who can connect remotely can issue and revoke tokens for their own agents. They cannot issue a token for themselves, or for the reserved owner.

The reserved owner uses the local web app. That id cannot log in on the public site, and it cannot call MCP or the CLI. Set it with `owner_key` or `ONESTEAD_OWNER`. There is no compiled default. If it is missing, or it is not a legal member key, the process errors.

## Other operating systems

macOS/Linux installation and end-to-end operation: **not verified**. With Python 3.12+ and Git available, install from the repository using a virtual environment (suggested commands, **not verified**):

```sh
python3 -m venv .venv
. .venv/bin/activate
python -m pip install .
```

Configure `data_dir` and `owner_key` as described below. The same entry points are available:

```sh
python -m onestead.web             # Local desktop browser
onestead-mcp                       # Local MCP over stdio
onestead company --as <agent-key>  # CLI, use a registered local agent
onestead-remote 8765 --public-host your.example.com
python -m onestead.online         # Remote server plus cloudflared supervisor
```

The web launcher uses Python's cross-platform `webbrowser.open`. It needs a desktop browser session. If no browser opens on POSIX, `ui.log` explains that a desktop session is needed and suggests CLI, MCP, or the remote server for headless hosts. The Windows-only exclusive socket option is kept on Windows; POSIX uses ordinary loopback binding.

Opening registered local applications, shortcuts, code paths, and Skill directories **from the web page is disabled on macOS/Linux**. These buttons return an explicit unsupported-platform message asking you to open the registered path manually; website links still work in the browser. Process probing uses `ps -eo comm=` on POSIX. If `ps` is missing, times out, or fails, the result is `unknown`, not “stopped”; the application details explain “没法探：无法读取本机进程列表”.

For background startup, a macOS launchd user agent or a Linux systemd user service can run the virtual environment's Python with `-m onestead.online`, or run `onestead-remote` with a separately managed proxy. These are recommendations only: **not verified**; this repository installs neither service definition. Supply the same config and environment as an interactive run and a working directory accessible to that user. `pythonw` and Windows scheduled tasks are Windows-only launch options, not POSIX prerequisites. Put Caddy, nginx, or a tunnel in front with TLS and keep the app on `127.0.0.1`.

### Optional local backups

Set `backup_dir` or `ONESTEAD_BACKUP_DIR` to a local, non-cloud-synced backup root. There is no default. Without it, the launcher logs “没配 backup_dir，不备份”, and `onestead-backup` returns a skipped result without opening the data or code repository. An empty environment variable also disables backup, even when the config file has a value.

With a root configured, the existing layout is retained: `Company-Base/01-current-complete-*`, `Company-Base/02-previous-complete-*`, root-level `_待删除/`, and `INDEX.json` (created on the first backup). Each version contains source ZIPs and current-branch history bundles for data and code. The code repository is `source_repo` when set; otherwise it is the git checkout the package runs from. If neither exists (for example after a plain `pip install .`), only the data repository is backed up and the manifest says why. Bundle verification, independent clone checks, and ZIP file-count checks run before rotation. Unchanged commits are skipped; older versions are moved, never permanently deleted. To retain an existing Windows backup layout, explicitly set the root to its previous parent directory; no existing directory is migrated automatically.

Verification clones live in the system temporary directory. Windows retains its PowerShell recycle-bin behavior. macOS/Linux use `send2trash`; if recycling fails or is unavailable, the directory stays in place and the log gives its path. Failed staging directories follow the same non-destructive policy. POSIX trash integration is **not verified** on a real host.

## Connect local agents

`onestead connect` looks for Claude Code, Codex, Cursor, and WorkBuddy when that client's user-level config file or directory is already present. With no extra flag it prints each file and the `onestead` entry it would write. It does not write anything.

`onestead connect --apply` writes those files. Before replacing an existing file it copies `<filename>.onestead-backup-<YYYYMMDD-HHMMSS>` into the same directory. The timestamp is UTC+8. The new contents are written to a temporary file in that directory and then replace the original. A file that already matches is left untouched and is not backed up again. The command reports that file as already up to date.

The server name is `onestead`. The command is `onestead-mcp` from the current Python environment when that program exists; otherwise it is the current Python interpreter with `-m onestead.server`. The identity is the `ONESTEAD_AGENT` environment variable. `--agent-key` sets it when exactly one `--client` is given. Otherwise the command keeps `ONESTEAD_AGENT` or `COMPANY_BASE_AGENT` from an existing `company-base` entry, then from an existing `onestead` entry, and otherwise uses `claude`, `codex`, `cursor`, or `workbuddy` for those clients.

If the same file still has a `company-base` entry, `--apply` removes it and writes `onestead` in its place. The preview says that the old entry will be replaced. The identity already stored on that entry is kept.

`--client claude-code`, `--client codex`, `--client cursor`, and `--client workbuddy` limit which client is handled. Repeat the flag to select more than one. A client with neither a config file nor a config directory is omitted. If one file cannot be parsed, or its TOML cannot be edited safely, that client is skipped and the others are still written.

| Client | User-level file |
| --- | --- |
| Claude Code | `~/.claude.json`. Only the `mcpServers` entry for this server is changed; every other key is kept. |
| Codex | `$CODEX_HOME/config.toml`, or `~/.codex/config.toml` when `CODEX_HOME` is unset. The command writes `[mcp_servers.onestead]` and `[mcp_servers.onestead.env]`. |
| Cursor | `~/.cursor/mcp.json`, under `mcpServers`. |
| WorkBuddy | `~/.workbuddy/mcp.json`, under `mcpServers`. The entry also carries `"type": "stdio"`, as WorkBuddy's own entries do. |

Codex is edited as text. Old `mcp_servers.onestead` and `mcp_servers.company-base` table blocks are removed, including their subtables, and the new blocks are appended. Other text, including comments, stays byte for byte. A JSON file that does not parse, and a TOML file whose structure is not safe to edit this way, are left unchanged.

```powershell
onestead connect
onestead connect --apply
onestead connect --client codex --agent-key codex --apply
```

The blocks printed by `onestead init`, and the same instructions on the local "connect a new agent" page, remain available for anyone who would rather edit a file by hand.

## Configuration

The process reads `~/.onestead/config.toml`, or the file named by `ONESTEAD_CONFIG`. Existing installations have a read-only fallback; see [Compatibility](../README.md#已有安装仍然兼容). A missing file is an empty config. A file that cannot be read, or that is not valid TOML, is an error.

For the keys below, the environment variable wins over the file. An empty string is treated as unset for `data_dir`, `public_host`, `owner_key`, `state_dir`, and `backup_dir` (it does not fall back to the file). On the remote command, `--public-host` wins over both the variable and the file. `python -m onestead.online` does not take that flag.

| Key | Environment variable | If it is missing |
| --- | --- | --- |
| `name` | `ONESTEAD_NAME` | `Onestead`. Used by page titles, login, onboarding and connection instructions. This is separate from the owner's display name. |
| `lang` | `ONESTEAD_LANG` | `en`. Company language, `en` or `zh`. Sets the default for the web app, login pages, MCP tool descriptions, errors and CLI help. People can still switch the web app language in their browser. |
| `data_dir` | `ONESTEAD_DATA` | Error. There is no compiled path. The CLI flags `--data` and `--root` supply the path for that command only. |
| `public_host` | `ONESTEAD_PUBLIC_HOST` | The local web app, local MCP, and the CLI still run. The page shows that no public host is configured. `onestead-remote`, `python -m onestead.server --remote`, and `python -m onestead.online` error. The value is a lowercase DNS name with at least two labels, with no scheme, port, or trailing dot. An illegal value errors when the remote server starts. |
| `owner_key` | `ONESTEAD_OWNER` | Error. There is no compiled id. The value is lowercase letters, digits, and hyphens, and it must not end in `-draft`. |
| `state_dir` | `ONESTEAD_STATE_DIR` | Windows: `%LOCALAPPDATA%\Onestead`, or `~/Onestead` without `LOCALAPPDATA` for new installations. macOS: `~/Library/Application Support/Onestead`. Linux: `$XDG_STATE_HOME/onestead`, or `~/.local/state/onestead` without `XDG_STATE_HOME`. |
| `site_domain` | `ONESTEAD_SITE_DOMAIN` | Web app publishing disabled. When set, apps are published at `<slug>.<site_domain>`; see [Static application publishing](sites.md). |
| `reserved_slugs` | — | Extra app names that cannot be used as subdomains, in addition to the built-in reserved names. |
| `backup_dir` | `ONESTEAD_BACKUP_DIR` | Backups disabled. When set, use a local directory that is not synced to the cloud. |
| `source_repo` | `ONESTEAD_SOURCE_REPO` | The source git checkout the package runs from, if any. Must contain `.git` and `pyproject.toml`; a bad value is an error. Without a checkout, backups cover the data repository only. |
| `skill_harnesses` | `ONESTEAD_SKILL_HARNESSES` | No extra local tools or paths. The variable is a JSON list; legacy `COMPANY_BASE_SKILL_HARNESSES` is also supported, with the new name taking precedence, including explicit empty values. Each item has non-empty `key` and `name`, a `user` path list, and optional `builtin` and `plugins` path lists. Paths must be non-empty strings; `~` expands to the process user's home. A matching key appends paths and keeps the built-in name; a new key adds a local agent with the same scan, comparison, sharing, and installation-hint behavior as built-in agents, not a snapshot. A bad shape is an error. |
| `skill_snapshots` | `ONESTEAD_SKILL_SNAPSHOTS` | No extra snapshots. The built-in scan list is unchanged. The variable is a JSON list. Each item has non-empty `key`, `name`, and `root`. A bad shape is an error. |
| `shared_folders` | `ONESTEAD_SHARED_FOLDERS` | No shared folders. A list of local folders opened read-only to the members named in `readers` (agent keys), currently through the ChatGPT connector's `search` and `fetch`. Each item has a unique lowercase `key`, `name`, `path`, a non-empty `readers` list, and an optional `exclude` list of paths or globs relative to the folder. Only `.md .txt .csv .json .html .htm .pdf .docx .pptx .xlsx .xls .zip` files are read. Inside a `.zip`, members of those same types are read (no nested archives, at most 500 members and 200 MB unpacked per archive), and the text starts with a list of the archive's files; symlinks, junctions, browser profile folders, and names that look like secrets, tokens, cookies, credentials, `.env`, or `*.local.*` are skipped, and requested paths must stay inside the folder. Text goes through the usual redaction. Office and PDF text extraction needs `pip install onestead[docs]`. These files never appear in the web app or colleague views. Extracted text is indexed in a SQLite file in the local state directory. The index is synced in the background every 10 minutes, and a search more than a minute after the last sync first catches up, so new and changed files can be searched right away. Deleting that index rebuilds it automatically. |

`ONESTEAD_TOKENS` replaces `tokens.json`. `ONESTEAD_USAGE_LOG` replaces the usage log, and an empty value turns the log off. `ONESTEAD_FEISHU_DIR` replaces the Feishu directory. `ONESTEAD_SKILL_HOME` overrides the home used by the built-in skill scan. `ONESTEAD_CLOUDFLARED` points at the tunnel binary.

Extra local Skill harnesses and read-only snapshots are described in [Data format](data-format.md). They are not part of the public HTTP service.
