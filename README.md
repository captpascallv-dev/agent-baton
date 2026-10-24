# agent-baton

**让一个 Lead agent 指挥一群 agent CLI：派活、等交卷、Lead 之间商量，全在本地完成，等待期间不花模型额度。**

> agent-baton is a local-first toolkit for running a team of coding agents. A "lead" agent (Claude Code or Codex App) plans, dispatches task cards to cheaper agent CLIs, and reviews the results. Local programs do the waiting and wake the lead's original conversation when work is delivered; leads can also talk to each other through file mailboxes. No relay model, no polling by the model.

名字取自两种"棒"：指挥手里的指挥棒（Lead 负责指挥），接力赛里的接力棒（任务在 agent 之间交出、交回）。本仓库原名 `agent-lead-wake`，旧链接会自动跳转。

---

## 它解决什么问题

一个顺手的分工是：**判断力强的模型当 Lead**，只负责理解需求、写任务卡、验收；**便宜、额度多的执行者**（各家 agent CLI）负责埋头干活。真这么用起来，会卡在三件事上：

1. **派活**：每家 CLI 的启动方式、参数和会话续接都不一样。
2. **等**：执行者一跑就是几分钟到几小时。让模型自己轮询，每查一次都要花一轮额度；另开一个便宜模型定时中转，交卷后最多还要多等一个周期。
3. **商量**：多个 Lead 各管一摊时，需要一条可靠的线来交接、提问、回执，而且不能把 Lead 之间的信混进执行者的任务里。

agent-baton 的做法是：**派活和等待都交给本地程序**。守候只看文件在不在、进程活不活，不读日志正文，也不调用模型。只有执行者真正干活、Lead 被叫醒处理交卷的时候，才消耗模型额度。

---

## 五个部分

| 目录 | 干什么 | 适用 |
| --- | --- | --- |
| [`claude/`](claude/) | **派活 + 叫醒（Claude 侧）**：Claude Code 当 Lead，把任务卡派给任意 agent CLI；执行者最后写 `READY.json`，后台守候退出，原会话被叫醒 | Claude Code（CLI / 桌面版） |
| [`codex/`](codex/) | **叫醒（Codex 侧）**：`codex-mail-wake` 插件，执行者交卷时把通知投回 Codex App 的原 Lead 会话 | Codex App（桌面版） |
| [`mailbox/`](mailbox/) | **Lead 之间的信箱**：一封信就是一个 Markdown 文件，支持线程、回复和"需要回复"标记；本地守着来信，有信就叫醒 | 两个或多个 Lead 会话 |
| [`acp/`](acp/) | **统一插头**：通用 ACP（Agent Client Protocol）客户端，驱动任何支持 ACP 的执行者；模型和档位只在对方支持时设置并回读，权限请求默认拒绝并逐条留痕 | 提供 ACP stdio 的 agent |
| [`watch/`](watch/) | **执行观察窗**：只读查看本机 CLI 进程、派单记录和交卷状态；未知如实显示，结束任务留在历史 | Windows x64（[成品下载](https://github.com/captpascallv-dev/agent-baton/releases/tag/watch-v0.1.0)） |

### 怎么组合

```
              ┌──────────── mailbox/ ────────────┐
              │   Lead A ⇄ Lead B：商量、交接、回执   │
              └──────────────────────────────────┘
 Lead（Claude Code / Codex App）
   │  写任务卡
   ▼
 claude/ Dispatch.ps1 ──启动──▶ 执行者（Codex CLI / Cursor / Grok Build / PI / 任何 ACP agent …）
   │                                   │
   └────────── watch/ 只读观察 ◀─────────┘
   ▲                                   │ 干活，最后写 READY.json
   │                                   ▼
   └── 叫醒原会话 ◀── claude/ 守候 或 codex/ 插件（只看文件，不调模型）
```

- **派活**用 `claude/`：Lead 写任务卡，`Dispatch.ps1` 启动执行者，`Wait-Delivery.ps1` 在交卷时退出，Claude Code 回到原会话。Codex App 里的 Lead 不走这套脚本，而是用 `codex/` 插件接收交卷通知。
- **执行者只会 ACP** 时，把 `acp/acp_client.py` 当成启动命令，写进 `claude/executors.json`。任务卡仍然要求执行者把产物和 `READY.json` 写进交卷目录：ACP 客户端只负责跑协议，不代替交卷。
- **不止一个 Lead** 时，加上 `mailbox/`。它不启动执行者，也不看 `READY.json`，只负责 Lead 之间的信。
- **想随时看执行情况** 时，加上 [`watch/`](watch/)。它读取登记和进程身份，不派单，也不把进程结束或交卷当成业务验收。
- **想让一群 Grok Bot 当执行者**：见 [docs/grokbot.md](docs/grokbot.md)。本仓库不带调用代码，只讲清怎么用官方 BDK 扩展接入、要加哪层约定、踩过哪些坑。

---

## 快速开始

### 派活并等交卷（Claude Code）

```powershell
# 1. 配置执行者（已有 Codex / Cursor / Grok Build / PI / DeepSeek DSH 样例）
Copy-Item claude/executors.example.json claude/executors.json

# 2. 写任务卡（模板：claude/templates/TASK_CARD.md），然后派单
pwsh -NoProfile -File claude/bin/Dispatch.ps1 -Executor codex `
  -Workspace D:\work\demo -Outbox D:\work\demo\out -PromptFile D:\work\demo\CARD.md -Task "demo"

# 3. 把它输出的 WATCH: 那一行交给 Claude，用后台模式运行，然后结束本轮
```

原理：Claude Code 的后台命令退出时，会往原会话插一条通知并开始新的一轮。守候脚本看到 `READY.json` 就退出，Lead 就被叫醒了。执行者如果支持续接原会话，可以在 `executors.json` 里配置 `args_on_resume`，再用 `-ResumeSessionId` 派单；没配置就明确拒绝，不会悄悄新开一个会话。细节见 [claude/README.md](claude/README.md)。

### Codex App 当 Lead

`codex-mail-wake`（公共版 0.5.0）通过 Codex 插件 marketplace 安装；配置后用 `start_app_mailbox_watch` 持续接收登记交付，并经用户已安装的官方 App Tools 通知原 Lead。0.5.0 新增原 owner 已处理通知回执、同 owner 积压合批与逐件收据恢复，并修正 UTF-8 跨块解码；原发送锁、超时隔离和身份委托继续保留。安装、配置、恢复及验证边界见 [Codex 说明](codex/README.md)。

### 打开 Windows 执行窗

下载 **[CLIWatch-0.1.0-win-x64.zip](https://github.com/captpascallv-dev/agent-baton/releases/download/watch-v0.1.0/CLIWatch-0.1.0-win-x64.zip)**，完整解压，双击 **`Start-CLI-Watch.cmd`**。包内自带运行时，无需编译或安装 .NET SDK / PowerShell 7。首次可查看本机 CLI；关联派单任务时，在 `config/default.json` 填自己的任务目录即可，见 [配置和启动说明](watch/README.md#下载成品包windows)。

### 两个 Lead 互相写信

```powershell
$env:AGENT_MAILBOX_ROOT = 'D:\work\mailbox'   # 信箱根目录；也可以每次传 -Root
# 参与方写在根目录的 parties.json 里（样例：mailbox/parties.example.json）

# 发信：写完再原子改名，对方不会读到半截。只有两方时可以省略 -To
pwsh -NoProfile -File mailbox/bin/Send-Mail.ps1 -From alpha -Subject "接口改好了" -BodyFile note.md -Thread api-v2 -NeedsReply

# 守候：有信退出码 0（叫醒），到点退出码 2（再挂一轮）
pwsh -NoProfile -File mailbox/bin/Wait-Mail.ps1 -Me beta -TimeoutMinutes 29

# 读信并归档到 read\
pwsh -NoProfile -File mailbox/bin/Read-Mail.ps1 -Me beta
```

每封信开头依次是 from、to、subject、created_at_utc、thread、reply_to、needs_reply。详见 [mailbox/README.md](mailbox/README.md)。

### 用 ACP 驱动执行者

```powershell
python acp/acp_client.py --agent <支持 ACP 的命令> --agent-arg <参数> `
  --cwd D:\work\demo --prompt-file D:\work\demo\CARD.md --log-dir D:\work\demo\out\acp
```

权限请求默认拒绝。`--permission-mode approve` 会自动批准执行者的每一次权限请求，等于把工作目录里的读写、命令执行都交给它。只在这些前提都满足时才打开：工作目录里只有这个任务需要的东西，任务来源可信，最好执行者自身还带沙箱。每一次权限请求都会写进日志，事后可以查。详见 [acp/README.md](acp/README.md)。

---

## 实测数据

**本地派单测量**（Windows 11，PowerShell 7）：

| 执行者 | 测试模型 | 派单 → 交卷 |
| --- | --- | --- |
| Codex CLI | gpt-6-luna / max | 20 秒 |
| Cursor CLI | grok-4.7-xhigh | 64 秒 |
| Grok Build | grok-4.7 / xhigh | 16 秒 |
| PI | gpt-6-luna / max | 19 秒 |
| DeepSeek DSH | 默认 | 4 秒 |

测试任务只是"读卡、写一行报告、写 READY.json"，五个执行者并行派出，全部自动叫醒，只证明链路是通的。真实使用中，这套做法跑过数小时的多执行者并行开发，Lead 全程不轮询。

---

## 我们学到的几条

1. **执行者自报 ≠ 交付。** 验收要看实际产物、回原文抽查，不能只看 REPORT 写了什么。
2. **`READY.json` 必须最后写**，做不完也要写（blocked / failed）。静默退出是最难排查的失败。
3. **宁可不发，也不重发。** 不确定有没有送达时，先标记出来交给人或协调者，不要自动再发一遍。
4. **卡住的东西必须有人看得见。** "不重发"之后一定要有通知或看门，否则就变成静默丢件。
5. **守候不读日志正文。** 只看文件在不在、进程活不活，等待才能真正做到零成本。
6. **信箱和派单分开。** Lead 之间的信用来商量；交给执行者的任务卡要自成一体，需要的内容复制进任务目录，不要让执行者去读 Lead 的信箱。
7. **校验要相称。** 只在跨信任边界或长期存档时加哈希校验；自己刚写、马上又读的文件不用校验，更不要放进轮询的热路径。

---

## 不包含的

- 调用 Grok Bot 的代码（接入方法见 [docs/grokbot.md](docs/grokbot.md)）。
- 各家 CLI 的私有启动器、作者本机的授权和额度规则、账号配置和运行记录。
- 任何凭据。本仓库不管理任何 CLI 的登录。

## 安全提醒

- `--force`、`--always-approve` 这类参数，以及 `acp/` 的 `approve` 模式，都等于允许执行者在你的机器上跑任意命令。优先用带沙箱的执行者，工作目录里只放这个任务需要的东西。
- 配置、任务卡、信件和公开日志里都不要写入凭据。
- 执行者可能会把任务卡和它读到的文件发送给对应的服务。敏感数据派给谁，先想清楚。
- `codex/` 插件只在你授权的范围内给你自己的会话发消息，不要改成往任意会话发。

## 本地离线检查

```powershell
npm --prefix codex/plugin test
node codex/tools/check-public.mjs
pwsh -NoProfile -File claude/tests/Smoke.ps1
pwsh -NoProfile -File mailbox/tests/Smoke.ps1
python acp/tests/smoke.py
```

`watch/` 另需 Windows 和 .NET 8 SDK，构建后运行自己的测试与公开目录检查：

```powershell
dotnet test watch/src/CliWatch.Tests/CliWatch.Tests.csproj -c Release
pwsh -NoProfile -File watch/tools/Check-Public.ps1
pwsh -NoProfile -File watch/Build-CliWatch.ps1
pwsh -NoProfile -File watch/tests/Smoke.ps1
```

首次还原 .NET 开发依赖需要访问 NuGet；这些测试使用本地夹具，不调用模型。

全部使用本地模拟执行者、假 ACP agent 和假 MCP 宿主，不需要模型账号，也不碰生产数据。[CI 示例](docs/offline-ci.example.yml)可以复制到 `.github/workflows/ci.yml` 启用；当前仓库没有自动工作流。离线检查和真实模型任务、App 生命周期验收不是同一个范围。

本项目不由 OpenAI 或 Anthropic 发布，也不包含它们的私有工具源码。

## License

- `claude/`、`codex/`、`mailbox/`、`acp/` 及根文档：[MIT](LICENSE)。
- `watch/`：[MPL-2.0](watch/LICENSE)，来源与第三方声明见 [watch/NOTICE.txt](watch/NOTICE.txt)。
