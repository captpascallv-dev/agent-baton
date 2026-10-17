# claude-agent-dispatch

让 Claude Code 当 Lead，把活派给其他 agent CLI，执行者交卷时由后台守候命令退出，通知 Claude 验收。守候脚本不调用模型，不需要中转服务；Claude 处理通知、验收或续挂守候时仍会产生正常会话用量。

> Dispatch task cards from Claude Code to detached agent CLIs. A local watcher exits on delivery so Claude can resume. Watcher scripts make no model calls; Claude's notification handling, review, and watcher renewal still use the normal session.

## 原理

本方法使用 Claude Code 后台命令（`run_in_background`）退出后通知原会话的行为。贡献者报告在其环境中已运行；实际通知行为和后台命令期限取决于你安装的 Claude Code 版本及环境。

```
Claude(Lead) ──写任务卡──> Dispatch.ps1 ──后台隐藏进程──> agent CLI（干活，写交卷目录）
     ▲                                                          │
     │                                                  最后写 READY.json
     └──── 唤醒 <── Wait-Delivery.ps1（纯本地文件检查，后台运行）<──┘
```

- 派单后 Claude 结束本轮，由本地守候等待交卷，不持续调用模型轮询。
- 守候脚本只做本地文件与进程检查（默认每 20 秒一次），不调用任何模型。
- 执行者以独立进程启动；重新打开 Claude 会话后，可用 `Pending.ps1` 列出未交卷任务，再执行其 WATCH 命令重挂守候。完整重启恢复需在你自己的环境确认。

## 目录

```
bin/Dispatch.ps1        派单：按 executors.json 组装命令，后台启动，输出 WATCH 命令
bin/Run-Agent.ps1       后台进程里实际执行 agent CLI，写 agent.log / exit_code.txt
bin/Wait-Delivery.ps1   守候：READY.json→0；原生结束标记但无READY→3；进程消失但无READY→4；超时→2
bin/Wait-Many.ps1       多任务守候：任一交卷或执行者结束就退出
bin/Pending.ps1         列出未交卷任务并给出重挂守候命令（跳过带 CLOSED.txt 的任务）
executors.example.json  执行者配置样例（复制为 executors.json 后按需修改）
templates/TASK_CARD.md  任务卡模板（含交卷约定）
CLAUDE.snippet.md       放进 CLAUDE.md 的一段说明，让 Claude 知道怎么用
tests/Smoke.ps1         无模型、无凭据、无网络的本地交付链检查
jobs/<id>/              每次派单的记录：job.json、spec.json、任务卡副本、agent.log、exit_code.txt、terminal.json
```

## 安装

1. 需要 PowerShell 7（`pwsh`）。本次离线 smoke 在 Windows / PowerShell 7.6.4 上执行；Linux/macOS 尚未在本次检查中执行。
2. 装好并登录你要用的 agent CLI（各自官方方式登录，本工具不碰凭据）。
3. `cp executors.example.json executors.json`，删掉你没有的执行者，按所装 CLI 版本核对参数并改成你要的模型。样例中的 `--force` / `--always-approve` 仅代表用户可显式选择的自动批准权限；保留这些参数需要你明确同意。
4. 把 `CLAUDE.snippet.md` 的内容放进你的 `CLAUDE.md`，路径改成实际位置。

然后对 Claude 说"调用外部执行，让 codex 做 XXX"即可。

## 执行者配置

`executors.json` 里每个执行者是一条命令模板：

| 字段 | 含义 |
| --- | --- |
| `command` | PATH 上的命令名（`codex`、`cursor-agent`、`grok`、`pi`、`dsh`） |
| `args` | 参数列表，可用占位符 |
| `stdin_card` | `true` 时把任务卡全文从标准输入喂给它（如 `codex exec -`） |
| `cwd` | 工作目录，默认 `{workspace}` |
| `env` | 额外环境变量（空值会被忽略）。**不要把 API key 写进这里**，用 CLI 自己的登录或系统环境变量 |
| `unset_env` | 启动前从执行进程里删掉的环境变量名，例如 Cursor 在 Git Bash 下要去掉 `SHELL` 和 `MSYSTEM` |
| `args_on_resume` | 带了 `-ResumeSessionId` 时改用的参数列表。里面要有 `{resume}` 或 `{resume_path}`。没有这一项时，派单会拒绝续接，不会悄悄新开一会话 |

占位符：`{workspace}` `{outbox}` `{card}`（任务卡绝对路径）`{carddir}` `{jobdir}` `{pointer}`。续接时还可以用 `{resume}`（会话 id）和 `{resume_path}`（会话文件绝对路径）。

续接原会话：

```powershell
pwsh -NoProfile -File ./bin/Dispatch.ps1 -Executor pi -Workspace <dir> -Outbox <dir> `
  -PromptFile <card.md> -Task "name" -ResumeSessionId <uuid> -ResumeSessionPath <session-file>
```

`-ResumeSessionPath` 只在 `args_on_resume` 含 `{resume_path}` 时必填。样例里的 `--resume` 只说明占位符放在哪；以你安装的 CLI 帮助为准。沙箱、服务档、快速模式也写在该执行者的 `args` 里，不另设开关。样例保持工作区沙箱，不会默认打开全权限。

可选 `-JobId` 只接受 1–128 位 ASCII 字母、数字、`_` 和 `-`，首位必须是字母或数字；省略时生成 UUID。任务记录固定写入本安装目录的 `jobs/<id>`。

每个任务使用独立 Outbox。派发会拒绝已含 `READY.json` 的目录，保留其中旧交付；新的 `JobId` 也不能复用旧 READY。

`{pointer}` 是一句"请读取任务卡文件 {card} 并严格执行"的短提示（可在配置顶层 `pointer` 改写）。任务卡不直接塞进命令行，避免 Windows 命令行长度限制和转义问题，执行者自己去读卡文件。

### 各 CLI 的非交互配置样例（使用前核对所装版本）

| 执行者 | 关键参数 | 说明 |
| --- | --- | --- |
| Codex CLI | `codex exec -m <model> -c model_reasoning_effort=<档> -s workspace-write -C <ws> -` | 任务卡走 stdin；有沙箱，写权限限于工作区和 `--add-dir`。想用另一个账号，设 `env.CODEX_HOME` 指向单独的 Codex 目录 |
| Cursor CLI | `cursor-agent -p --model <model> --force --trust --workspace <ws>` | `--force` 是用户显式选择的自动批准权限。样例会在执行进程里去掉 `SHELL` 和 `MSYSTEM`，避免 Git Bash 把命令行改成 bash 后和 PowerShell 钩子冲突 |
| Grok Build | `grok --prompt-file <card> -m <model> --reasoning-effort <档> --always-approve --cwd <ws>` | 从文件读 prompt；`--always-approve` 是用户显式选择的自动批准权限 |
| PI | `pi -p --provider <p> --model <m> --thinking <档> --no-session` | 续接时用 `args_on_resume`，样例把 `--no-session` 换成 `--resume {resume}`。标志是否如此，先核对所装版本 |
| DeepSeek DSH | `dsh --profile headless "<task>"` | 内置 headless profile；模型取 DSH 自己的设置 |

**接入其他 CLI**：可为支持非交互任务及本地文件交付的 CLI 添加配置，并单独验证它的参数、权限及交卷行为。现有配置样例不保证所有 CLI 版本可直接使用。

## 交卷约定

任务卡里必须写明：产物写进交卷目录，写 `REPORT.md`，**最后一步**写 `READY.json`：`{"status":"completed|blocked|failed","deliverables":[...],"notes":"一句话"}`。做不完也要写（blocked/failed），不能静默退出。守候脚本只认 `READY.json`；执行者进程结束了却没写它，守候会以退出码 4 叫醒 Lead，由 Lead 查 `jobs/<id>/agent.log`。

## 守候期限与续挂

贡献者报告其 Claude Code 环境的后台命令约 30 分钟会被停止，所以默认 WATCH 命令每轮守 29 分钟（`min(29, TimeoutMinutes + 10)`；默认 `-TimeoutMinutes 90` 仍是 29）。这个期限不是所有版本的保证。退出码 2 而执行者进程还活着，可再挂一轮；续挂需要 Claude 处理通知并调用工具，会使用正常会话用量。

在 Windows 上，派单用外壳启动执行进程，不继承派单命令的输出管道。否则宿主会一直等到执行者退出，才把派单结果还给你。

执行进程结束时会原子写入 `jobs/<id>/terminal.json`（先写临时文件再改名）。默认 WATCH 不把它当终态标记，进程消失仍是退出码 4。需要把它当成执行者自己的结束标记时，把该路径传给 `-TerminalFiles`。

`Pending.ps1` 重挂时使用该任务记录里的 `watch_minutes`，旧记录没有这个字段时用 29。不再跟进的任务，在 `jobs/<id>/CLOSED.txt` 放任意内容，Pending 会跳过。

同时派了多单时，用 `bin/Wait-Many.ps1` 一个守候盯全部，任意一单交卷就叫醒你，避免一到点跳出一串超时通知。

Claude 守候脚本每次遇到终态就退出，不持久记录跨重挂的已处理列表。Lead 处理 READY 后，必须从下一轮 `Wait-Many` 的 Outbox/PID 数组移除该任务；直接重挂同一列表会再次看到旧 READY。

在 PowerShell 会话中传入真正的数组，例如：

```powershell
& './bin/Wait-Many.ps1' -Outbox @('./example/outbox-a', './example/outbox-b') -ProcessId @(111, 222) -TimeoutMinutes 29
```

替换示例目录和 PID；需要放进后台工具命令时，用 `pwsh -Command` 执行以上 PowerShell 表达式。`pwsh -File` 的参数传递不能把逗号分隔的命令行字符串绑定成数组。

## 安全提醒

- `--force` / `--always-approve` 是高权限选择；请仅在你明确授权自动批准执行者命令时保留，并按该 CLI 的实际权限语义确认影响。工作目录只放该任务需要的东西。
- 本工具不管理凭据；各 CLI 用自己的登录状态。不要在公开配置或任务卡中写凭据。
- 验收时核对实际产物，不要只信执行者的自报。

## 本次离线 smoke

从本目录运行：

```powershell
pwsh -NoProfile -File ./tests/Smoke.ps1
```

只用 `pwsh` 作为模拟执行者，检查语法/JSON/模板路径、带空格与中文路径、stdin 卡和指针卡、UTF-8 REPORT/READY/log、超时及无 READY 退出、多任务守候和 Pending 的可执行恢复命令，以及拒绝复用旧 READY 并保留旧交付。配置及 `jobs` 都建在独立临时目录，测试结束前检查所属目录再清理；`-KeepTemp` 可保留诊断文件，`-OutboxReuseOnly` 可单独运行旧 READY 拒绝用例。

该检查验证本地交付链，不会登录或调用真实 agent CLI，也不验证 Claude Code 原生通知、会话重启或复杂任务质量。

## 贡献者报告的历史实测

以下由贡献者提供（2026-09-30，Windows 11，PowerShell 7.6），本次发布检查未重新运行这些真实 CLI/模型任务。

| 执行者 | 版本 | 测试模型 | 结果 | 派单→交卷 |
| --- | --- | --- | --- | --- |
| Codex CLI | 0.159.2 | gpt-6-luna / max | 通过 | 20 秒 |
| Cursor CLI | 2026.09.28 | grok-4.7-xhigh | 通过 | 64 秒 |
| Grok Build | — | grok-4.7 / xhigh | 通过 | 16 秒 |
| PI | — | gpt-6-luna / max（openai-codex） | 通过 | 19 秒 |
| DeepSeek DSH | — | DSH 默认设置 | 通过 | 4 秒 |

贡献者报告的测试任务：读任务卡 → 写一行中文 REPORT.md → 写 READY.json；五个并行派出，守候脚本全部以退出码 0 通知原会话。这是其环境中的简单交付结果，不保证当前 CLI 版本、其他平台或复杂任务的质量。

## License

MIT，见仓库根目录 LICENSE。
