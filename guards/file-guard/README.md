# 删除护栏

这个目录里的程序只检查命令文本里直接写出来的删除和移动。钩子适配器拿到拒绝结果后，按各家客户端的格式把这条命令退回去。它不监视别的程序在内部又删了什么，也拦不住没有装上钩子的进程。所以它是一道可见命令护栏，不是一道不可绕过的沙箱。

> This is a visible-command guard. A hook adapter turns its deny result into the client’s own refusal. It is not a sandbox, and it does not see what another program deletes internally.

## 它守住的行为

- 只接受已经解析好的字面绝对路径。路径里出现变量、环境占位（`$`、`%`）、反引号、通配符（`*`、`?`、`[]`），或者还是相对路径，一律拒绝。解析失败同样拒绝，不会当成“看不懂就放行”。
- `recycle` 先把一整批路径都检查完。任何一项不合格，就在调用回收器之前返回，已经合格的那些也不会被送走。
- 回收器抛错时停在失败上，不改叫永久删除。没装 `send2trash` 时同样一项都不删。
- 磁盘根、保护根，以及保护根的上级目录，不能删，也不能移。保护根里面的普通文件不在这条里：护栏挡住的是根目录本身和它的祖先，避免把整个用户目录或盘符移走、删掉。
- 在 Windows 上，目录联接和符号链接会先解析。你交出的路径必须已经是最终目标；解析后如果和原字符串不是同一条路径，就拒绝，护栏不会替你改写删除对象。

检查本身不执行删除。一条写明了绝对路径的 `del` 仍会被拒绝，并给出改用 `recycle` 的命令。真正搬走文件的只有 `recycle`。

## 安装到 Claude Code

需要 Python 3.10 或更新版本，以及回收器：

```powershell
pip install send2trash
```

在 `~/.claude/settings.json` 里加上 PreToolUse 钩子。把 `<安装目录>` 换成这份仓库在你机器上的位置：

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash|PowerShell",
        "hooks": [
          {
            "type": "command",
            "command": "python -X utf8 <安装目录>/guards/file-guard/hook_adapter.py --client claude"
          }
        ]
      }
    ]
  }
}
```

适配器始终以 0 退出：拒绝是协议回答，不是钩子进程崩溃。`--client` 还可以是 `codex`、`cursor`、`grok`，输出格式不同，判断都在 `file_guard.py`。各家钩子配置的字段以你所装版本的官方文档为准。

`powershell_ast.ps1` 和 `powershell_stop.ps1` 必须跟 `file_guard.py`、`hook_adapter.py` 放在同一目录。前者用 PowerShell 的语法树把命令拆开，后者在允许执行的 PowerShell 里插入 `$ErrorActionPreference = 'Stop'`，避免前面的语句失败后后面的删除接着跑。插不进去就拒绝这条命令。

Cursor 的 `sessionStart` 只有在配置了 `session_rules_path` 时才把那段文字注入会话。不配置就什么都不加。

## 配置

读取顺序：

1. 环境变量 `FILE_GUARD_CONFIG` 指向的 JSON 文件。变量有值但文件缺失或不是 JSON 对象时，删除和移动会被拒绝，不会退回更宽的默认值。
2. 否则，若 `file_guard.py` 旁边有 `file_guard.config.json`，读这一份。
3. 都没有，就用下面的默认值。

空字符串和 `null` 表示这一项仍用默认。`protected_roots` 一旦写成数组，就整份替换默认保护根，不与默认根合并。可以先复制 [`file_guard.config.example.json`](file_guard.config.example.json)，它的空值和 `null` 保持默认，不会把保护根清掉。

| 键 | 作用 | 默认 |
| --- | --- | --- |
| `python` | 拒绝删除时，建议命令里使用的 Python | 当前解释器 `sys.executable`。配置了但文件不存在时，仍退回当前解释器 |
| `pwsh` | 解析 PowerShell 的可执行文件 | PATH 上的 `pwsh`；没有再试 PATH 上的 `powershell` |
| `protected_roots` | 不能删、不能移的根，以及这些根的上级目录 | 见下方 |
| `preimage_root` | `preimage` 保存旧文件副本的目录 | 护栏目录下的 `preimages` |
| `session_rules_path` | Cursor `sessionStart` 要注入的规则文件 | 不设置，不注入 |

默认保护根从当前用户的家目录推出，不要求这些目录已经存在：

- 家目录本身
- 家目录的上一级（用户目录）
- `~/.claude`、`~/.codex`、`~/.cursor`
- `~/Documents`、`~/Projects`
- 护栏自己所在的目录

另外，环境变量 `FILE_GUARD_PWSH` 如果指向一个存在的可执行文件，优先于配置里的 `pwsh`。

配置里的路径必须是字面绝对路径，不能带变量或通配符。

## 命令

```powershell
python guards/file-guard/file_guard.py inspect
python guards/file-guard/file_guard.py recycle --path <已经解析的绝对路径>
python guards/file-guard/file_guard.py preimage --path <已经解析的绝对文件路径>
```

`inspect` 从标准输入读一个 JSON 对象：`command`、`cwd`、`shell`。`shell` 是 `powershell`、`cmd`、`bash` 或 `python`。`recycle` 和 `preimage` 可以重复 `--path`。`preimage` 可用 `--preimage-root` 临时指定这一次的副本目录。

## 平台

这套路径规则按 Windows 写的：盘符绝对路径、`\\?\` 前缀、Git Bash 的 `/c/...` 写法、`ntpath`，以及 Windows 上的目录联接。

在 Windows 上，上面列出的行为都可以用。PowerShell 检查优先用 PATH 上的 `pwsh`（PowerShell 7）；没有时用 PATH 上的 `powershell.exe`（Windows PowerShell 5.1 也有同一套语法树）。回收通过 `send2trash` 进回收站。

在 Mac / Linux 上：

- 能跑的是不碰 Windows 路径的那一部分：`cmd` / `bash` 的分词、Python 源码的语法树检查。装了 `pwsh` 时，`powershell_ast.ps1` 和 `powershell_stop.ps1` 也能执行，因为它们只用 PowerShell 的语法树。
- 不能当作本机文件的删除护栏。POSIX 绝对路径（例如 `/home/...`）没有盘符，会被判成“还不是绝对路径”而拒绝。`send2trash` 在这些系统上能把文件放进废纸篓，但路径过不了前面的 Windows 规则，正常回收走不到那一步。
- 联接和符号链接的核对只在 Windows 上做（`os.name == "nt"` 时才 `Path.resolve`）。其他系统不解析符号链接，也不会发出“请改交最终路径”的那次拒绝。
- 钩子适配器在非 Windows 上启动 PowerShell 时不使用 `CREATE_NO_WINDOW`（该标志不存在时按 0 处理），进程调用本身可以执行。

## 测试

```powershell
python -m pytest guards/file-guard/tests -q
```

测试在临时目录里自建保护根和目标，不需要一份本机配置。没有 pytest 时，在同一目录执行 `python -m unittest discover -s guards/file-guard/tests -q`。

## License

MIT，见仓库根目录 LICENSE。

## English

`file_guard.py` inspects deletion and move text that is visible in a command. `hook_adapter.py` is what actually refuses the tool call. Nothing here confines an arbitrary process, so an agent that bypasses the hook is outside this guard.

A target is accepted only after it is a resolved literal absolute path. Variables, environment placeholders, expressions, wildcards, and relative paths are refused. If parsing fails, the command is refused as well. `recycle` checks the whole batch first and touches nothing when any item fails. A recycle error, or a missing `send2trash` package, never falls through to permanent deletion. Disk roots, configured protected roots, and ancestors of those roots cannot be deleted or moved. On Windows, a junction or symlink must already be written as its final target; the guard does not retarget the deletion for you.

Install `send2trash`, then point Claude Code’s `PreToolUse` hook at `hook_adapter.py --client claude`, as in the JSON above. Keep `powershell_ast.ps1` and `powershell_stop.ps1` beside the Python files. Allowed PowerShell is rewritten to set `ErrorActionPreference` to `Stop`; if that rewrite fails, the command is denied.

Configuration is the JSON file named by `FILE_GUARD_CONFIG`, otherwise `file_guard.config.json` next to `file_guard.py`, otherwise defaults. Empty strings and `null` keep the default for that key. A `protected_roots` array replaces the derived roots. Defaults are the home directory, its parent, `~/.claude`, `~/.codex`, `~/.cursor`, `~/Documents`, `~/Projects`, and this guard’s own directory. Python defaults to `sys.executable`. PowerShell defaults to `pwsh` on `PATH`, then `powershell` on `PATH`. Old-file copies default to `preimages` under this directory. `FILE_GUARD_PWSH` still overrides `pwsh` when it names an existing executable. Copy `file_guard.config.example.json`; as shipped, it leaves the defaults in place.

The path policy is Windows-shaped. On macOS and Linux the text scanners and, when `pwsh` is installed, the PowerShell AST scripts can run, but a native POSIX path is rejected as non-absolute, symlink retargeting is not checked, and recycle therefore does not become a local trash workflow.
