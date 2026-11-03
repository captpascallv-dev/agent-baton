# agent-baton

**一个人 + 一群 agent 的完整工作流：一个 Lead 写卡、派活、验收，一群 agent CLI 并行干活，本地程序负责等和叫醒，人只定方向、批对外动作。**

> agent-baton is the complete, local-first workflow we use every day to run a team of coding agents: one strong "lead" agent (Claude Code or Codex App) writes task cards, dispatches them to cheaper agent CLIs and reviews the results; local programs do the dispatching, the waiting and the waking, so no model ever polls; a studio, an execution window and a quota HUD make the whole team visible; guards keep a dozen parallel test runs from taking the machine down. The method is written down in [`method/`](method/).

名字取自两种"棒"：指挥手里的指挥棒（Lead 负责指挥），接力赛里的接力棒（任务在 agent 之间交出、交回）。本仓库原名 `agent-lead-wake`，旧链接会自动跳转；原来单独发布的 [ai-moe-studio](https://github.com/captpascallv-dev/ai-moe-studio)（研究室）、[codex-usage-hud](https://github.com/captpascallv-dev/codex-usage-hud)（额度 HUD）和 [onestead](https://github.com/captpascallv-dev/onestead)（公司事实底座）已连同完整历史并进来，分别在 [`studio/`](studio/)、[`hud/`](hud/) 和 [`onestead/`](onestead/)。

## 这是一套什么样的工作流

我们的日常是这样的：人说清想要什么，就去忙别的；一个判断力强的模型当 Lead，读现状、拆成一张张任务卡，派给几个便宜、额度多的 agent CLI 同时干；执行者干完写一个 `READY.json`，本地守候看到就把 Lead 叫醒；Lead 读产物、抽查、跑测试，合格的合并，不合格的写退修卡，然后当场派下一批。人回来时看到的是一份讲清进展、量级和待决事项的汇总，需要他点头的只有推送、发布、花钱、永久删除、改凭据这几类。

这套做法我们连续用了几个月：一个 Claude Code Lead 在五周里派出 1500 多张任务卡，常见同时在跑 3～6 张；一个晚上并行推一批代码卡，Lead 只用掉一周额度的几个百分点。分工、写卡、验收、汇报、换会话的具体做法见 [`method/`](method/)。

---

## 它解决什么问题

一个顺手的分工是：**判断力强的模型当 Lead**，只负责理解需求、写任务卡、验收；**便宜、额度多的执行者**（各家 agent CLI）负责埋头干活。真这么用起来，会卡在三件事上：

1. **派活**：每家 CLI 的启动方式、参数和会话续接都不一样。
2. **等**：执行者一跑就是几分钟到几小时。让模型自己轮询，每查一次都要花一轮额度；另开一个便宜模型定时中转，交卷后最多还要多等一个周期。
3. **商量**：多个 Lead 各管一摊时，需要一条可靠的线来交接、提问、回执，而且不能把 Lead 之间的信混进执行者的任务里。

agent-baton 的做法是：**派活和等待都交给本地程序**。守候只看文件在不在、进程活不活，不读日志正文，也不调用模型。只有执行者真正干活、Lead 被叫醒处理交卷的时候，才消耗模型额度。

---

## 仓库里有什么

**方法**

| 目录 | 干什么 |
| --- | --- |
| [`method/`](method/) | **这套工作流怎么用**：谁做什么、一张卡的六步、验收的坑、怎么向人汇报、额度怎么排、换会话怎么交接、护栏，以及验收模板 |

**核心链路：派活、等待、叫醒、商量**

| 目录 | 干什么 | 适用 |
| --- | --- | --- |
| [`claude/`](claude/) | **派活 + 叫醒（Claude 侧）**：Claude Code 当 Lead，把任务卡派给任意 agent CLI；执行者最后写 `READY.json`，后台守候退出，原会话被叫醒 | Claude Code（CLI / 桌面版） |
| [`codex/`](codex/) | **叫醒（Codex 侧）**：`codex-mail-wake` 插件，执行者交卷时把通知投回 Codex App 的原 Lead 会话 | Codex App（桌面版） |
| [`mailbox/`](mailbox/) | **Lead 之间的信箱**：一封信就是一个 Markdown 文件，支持线程、回复和"需要回复"标记；本地守着来信，有信就叫醒 | 两个或多个 Lead 会话 |

**看得见：谁在干活、额度还剩多少**

| 目录 | 干什么 | 适用 |
| --- | --- | --- |
| [`studio/`](studio/) | **研究室**：把本机的 Claude、Codex、执行者画成一间房里的 AI 娘，谁在干活、谁睡着一眼看见；自带演示模式 | 浏览器（Node.js） |
| [`watch/`](watch/) | **执行观察窗**：只读查看本机 CLI 进程、派单记录和交卷状态；未知如实显示，结束任务留在历史 | Windows x64（[成品下载](https://github.com/captpascallv-dev/agent-baton/releases/tag/watch-v0.1.0)） |
| [`hud/`](hud/) | **额度 HUD**：桌面悬浮面板，显示各家订阅剩余额度和重置时间，并写出一份额度快照给 Lead 派活前读 | Windows 10/11 x64（[成品下载](https://github.com/captpascallv-dev/codex-usage-hud/releases/latest)） |

**护栏和小工具**

| 目录 | 干什么 |
| --- | --- |
| [`guards/`](guards/) | **内存护栏**：测试和构建套 Windows Job 内存上限，再加一个钩子拦住没套上限的测试命令，一群 agent 同时跑测试也不会把整机拖垮 |
| [`tools/`](tools/) | `quota_pace.py` 读额度快照算各家消耗进度；`extract_dialogue.py` 把旧会话提成对话正文，换新会话时交接用 |

**公司事实底座**

| 目录 | 干什么 | 适用 |
| --- | --- | --- |
| [`onestead/`](onestead/) | **Onestead**：一个人或小公司的 AI 后台。项目现状、成员、决定、待办、要人点头的事、应用和 Skill 广场都在这里；任何 agent 经 MCP 接进来就能读到全部项目现状，不用人再复述。Lead 每完成一个阶段在这里记一条进展，需要人批准或动手的事登记成待办，人回来一眼看全 | Python 3.12+ |

**可选接入**

| 目录 | 干什么 |
| --- | --- |
| [`acp/`](acp/) | 通用 ACP（Agent Client Protocol）客户端，驱动任何支持 ACP 的执行者；权限请求默认拒绝并逐条留痕 |
| [`docs/grokbot.md`](docs/grokbot.md) | 怎么用官方 BDK 扩展把一群 Grok Bot 当执行者（不带调用代码） |

### 怎么组合

```
              ┌──────────── mailbox/ ────────────┐
              │   Lead A ⇄ Lead B：商量、交接、回执   │
              └──────────────────────────────────┘
 Lead（Claude Code / Codex App）  ◀── 派活前读额度快照 ── hud/ + tools/quota_pace.py
   │  ▲ 读项目现状，记进展、决定、要人批的事
   │  └──────────────────────────────▶ onestead/（MCP）◀── 人在网页上看、批
   │  写任务卡（method/）
   ▼
 claude/ Dispatch.ps1 ──启动──▶ 执行者（Codex CLI / Cursor / Grok Build / PI / 任何 ACP agent …）
   │                                   │  测试、构建套 guards/ 内存上限
   └──── watch/ 观察窗、studio/ 研究室 ◀──┘
   ▲                                   │ 干活，最后写 READY.json
   │                                   ▼
   └── 叫醒原会话 ◀── claude/ 守候 或 codex/ 插件（只看文件，不调模型）
```

- **派活**用 `claude/`：Lead 写任务卡，`Dispatch.ps1` 启动执行者，`Wait-Delivery.ps1` 在交卷时退出，Claude Code 回到原会话。Codex App 里的 Lead 不走这套脚本，而是用 `codex/` 插件接收交卷通知。
- **执行者只会 ACP** 时，把 `acp/acp_client.py` 当成启动命令，写进 `claude/executors.json`。任务卡仍然要求执行者把产物和 `READY.json` 写进交卷目录：ACP 客户端只负责跑协议，不代替交卷。
- **不止一个 Lead** 时，加上 `mailbox/`。它不启动执行者，也不看 `READY.json`，只负责 Lead 之间的信。
- **想随时看执行情况** 时，加上 [`watch/`](watch/)。它读取登记和进程身份，不派单，也不把进程结束或交卷当成业务验收。
- **想让一群 Grok Bot 当执行者**：见 [docs/grokbot.md](docs/grokbot.md)。本仓库不带调用代码，只讲清怎么用官方 BDK 扩展接入、要加哪层约定、踩过哪些坑。
- **公司事实**放在 [`onestead/`](onestead/)：Lead 开工前从这里读项目现状，阶段做完记进展，要人批准、动手或知道的事登记成待办。人不用翻聊天记录，回来看 Onestead 就知道每个项目到哪了、有哪些事等他。

## 平台支持：Windows 和 macOS

这套东西是在 Windows 11 上天天用出来的，Windows 上全部组件都有实际运行验证。macOS 的情况按组件分三类：

| 组件 | macOS | 说明 |
| --- | --- | --- |
| `method/` 工作流方法 | 适用 | 纯文档 |
| `claude/` 派活和叫醒、`mailbox/` Lead 之间的信箱 | 应该适用，未实测 | PowerShell 7 脚本，先装 `brew install --cask powershell`；没有在 Mac 上跑过 |
| `acp/` ACP 客户端 | 应该适用，未实测 | 结束进程已分平台处理 |
| `tools/extract_dialogue.py` | 适用 | 纯 Python |
| `onestead/` | 应该适用，未实测 | 只有模拟测试覆盖 macOS；网页上“打开本机应用或目录”在 macOS 不可用，见 [onestead/README.zh.md](onestead/README.zh.md) |
| `codex/` Codex App 叫醒插件 | 未验证 | 代码有 POSIX 文件锁和 Unix 配置路径，但没在 Mac 的 Codex App 上真实收过一次唤醒 |
| `studio/` 研究室 | 演示模式适用；真实模式需要重新做 | 网页演示是 Node，跨平台；读本机真实状态和启动器依赖 Windows |
| `watch/` 执行观察窗 | 需要重新做 | WPF，只支持 Windows |
| `hud/` 额度 HUD | 需要重新做 | WPF / net8.0-windows，只支持 Windows |
| `tools/quota_pace.py` | 需要额度快照 | 读 `hud/` 写出的快照；macOS 没有 HUD，可以按同样格式自己写一份 |
| `guards/` 内存护栏和删除护栏 | 需要重新做 | 内存上限用 Windows Job 对象，删除护栏按 Windows 路径、PowerShell 解析和回收站语义写；macOS 没有可靠的硬内存上限，要改成监视内存、超限结束进程树 |

一句话：在 macOS 上，“写卡 → 派活 → 等交卷 → 被叫醒 → Lead 之间写信 → 记进 Onestead”这条主线预计能跑，但还没实测；三个“看得见”的窗口里只有研究室演示模式能开，两道护栏要另做一套。

> **macOS in one line:** the core loop (method, dispatch and wake, lead mailbox, ACP client, Onestead) should work with PowerShell 7 but has not been run on a Mac yet; the studio demo runs anywhere Node runs; the studio live mode, the execution window, the quota HUD and both guards are Windows-only today and need a macOS rewrite.

---

## 从零用起来（Claude Code 当 Lead，Windows）

1. 读一遍 [`method/README.md`](method/README.md)，十分钟，知道每个角色做什么。
2. 把 [`claude/CLAUDE.snippet.md`](claude/CLAUDE.snippet.md) 放进你的 `CLAUDE.md`，路径改成本仓库位置；复制 `claude/executors.example.json` 为 `claude/executors.json`，只留你装了的 CLI，核对模型和权限参数。
3. 装内存护栏钩子（[`guards/README.md`](guards/README.md)），`pip install pywin32`。
4. 装 [`onestead/`](onestead/)：`python -m pip install ./onestead`，`onestead init <数据目录> --owner <你的名字>` 建公司，`onestead connect` 预览、`onestead connect --apply` 接进 Claude Code 等客户端。细节见 [onestead/README.zh.md](onestead/README.zh.md)。
5. 可选：装 [`hud/`](hud/) 看额度、开 [`studio/`](studio/) 的演示模式看看研究室（`npm --prefix studio install`，再 `npm --prefix studio run demo`）。
6. 对 Lead 说“调用外部执行，帮我做 XXX”。它会写卡、派单、挂守候，然后结束这一轮；执行者交卷时它自己醒来验收。

下面是各部分的单独用法。

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

`codex-mail-wake`（公共版 0.5.1）通过 Codex 插件 marketplace 安装；配置后用 `start_app_mailbox_watch` 持续接收登记交付，并经用户已安装的官方 App Tools 通知原 Lead。0.5.1 修复大量历史恢复扫描对桥接响应的阻塞；0.5.0 新增原 owner 已处理通知回执、同 owner 积压合批与逐件收据恢复，并修正 UTF-8 跨块解码；原发送锁、超时隔离和身份委托继续保留。安装、配置、恢复及验证边界见 [Codex 说明](codex/README.md)。

### 打开 Windows 执行窗

下载 **[CLIWatch-0.1.0-win-x64.zip](https://github.com/captpascallv-dev/agent-baton/releases/download/watch-v0.1.0/CLIWatch-0.1.0-win-x64.zip)**，完整解压，双击 **`Start-CLI-Watch.cmd`**。包内自带运行时，无需编译或安装 .NET SDK / PowerShell 7。首次可查看本机 CLI；关联派单任务时，在 `config/default.json` 填自己的任务目录即可，见 [配置和启动说明](watch/README.md#下载成品包windows)。

### 两个 Lead 互相写信

```powershell
$env:AGENT_MAILBOX_ROOT = 'D:\work\mailbox'   # 信箱根目录；也可以每次传 -Root
# 参与方写在根目录的 parties.json 里（样例：mailbox/parties.example.json）

# 发信：写完再原子改名，对方不会读到半截。只有两方时可以省略 -To
pwsh -NoProfile -File mailbox/bin/Send-Mail.ps1 -From alpha -Subject "接口改好了" -BodyFile note.md -Thread api-v2 -NeedsReply

# 守候：有信退出码 0（叫醒），到点退出码 2（再挂一轮）
pwsh -NoProfile -File mailbox/bin/Wait-Mail.ps1 -Me beta -TimeoutMinutes 10080

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

测试任务只是"读卡、写一行报告、写 READY.json"，五个执行者并行派出，全部自动叫醒，只证明链路是通的。真实使用中，2026-09-30 到 2026-11-03 一个 Claude Code Lead 派出 1500 多张任务卡，执行者以 Cursor CLI 和 Codex CLI 为主，单张从几分钟到六小时，Lead 全程不轮询。

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
npm --prefix studio ci; npm --prefix studio run test:live
python -m pip install -e "./onestead[dev]"; python -m pytest onestead/tests -q
```

`hud/` 和 `watch/` 一样需要 Windows 和 .NET SDK：`pwsh -NoProfile -File hud/scripts/test.ps1`。

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

- `claude/`、`codex/`、`mailbox/`、`acp/`、`guards/`、`tools/`、`method/` 及根文档：[MIT](LICENSE)。
- `watch/`：[MPL-2.0](watch/LICENSE)，来源与第三方声明见 [watch/NOTICE.txt](watch/NOTICE.txt)。
- `studio/`：代码 [MIT](studio/LICENSE)；角色与场景素材 CC BY-NC-SA 4.0，仅限非商业使用，原设计署名见 [studio/CREDITS.md](studio/CREDITS.md) 和 [studio/ASSETS-LICENSE.md](studio/ASSETS-LICENSE.md)。
- `hud/`：[MIT](hud/LICENSE)，第三方声明见 [hud/THIRD_PARTY_NOTICES.md](hud/THIRD_PARTY_NOTICES.md)。
- `onestead/`：[Apache-2.0](onestead/LICENSE)。

## 致谢

“合并前只读交叉审查”和“定了不做的事”两条做法，参考了数字生命卡兹克公开分享的 Agent 自动化系统。
