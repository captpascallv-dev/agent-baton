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
| [`guards/`](guards/) | **内存护栏**：测试和构建套 Windows Job 内存上限，再加一个钩子拦住没套上限的测试命令，一群 agent 同时跑测试也不会把整机拖垮。删除护栏在 [`file-guard/`](guards/file-guard/)，删和移只认解析后的字面绝对路径 |
| [`tools/`](tools/) | `quota_pace.py` 读额度快照算各家消耗进度；`extract_dialogue.py` 把旧会话提成对话正文，换新会话时交接用 |

**公司事实底座**

| 目录 | 干什么 | 适用 |
| --- | --- | --- |
| [`onestead/`](onestead/) | **Onestead**：一个人或小公司的 AI 后台。项目现状、成员、决定、待办、要人点头的事、应用和 Skill 广场都在这里；任何 agent 经 MCP 或命令行接进来，按权限读到项目现状，不用人再复述。Lead 每完成一个阶段在这里记一条进展，需要人批准或动手的事登记成待办，人回来一眼看全 | Python 3.12+ |

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

- **派活**用 `claude/`：Lead 写任务卡，`Dispatch.ps1` 启动执行者，`wait_delivery.py` 在交卷时退出，Claude Code 回到原会话。Codex App 里的 Lead 不走这套脚本，而是用 `codex/` 插件接收交卷通知。
- **执行者只会 ACP** 时，把 `acp/acp_client.py` 当成启动命令，写进 `claude/executors.json`。任务卡仍然要求执行者把产物和 `READY.json` 写进交卷目录：ACP 客户端只负责跑协议，不代替交卷。
- **不止一个 Lead** 时，加上 `mailbox/`。它不启动执行者，也不看 `READY.json`，只负责 Lead 之间的信。
- **想随时看执行情况** 时，加上 [`watch/`](watch/)。它读取登记和进程身份，不派单，也不把进程结束或交卷当成业务验收。
- **想让一群 Grok Bot 当执行者**：见 [docs/grokbot.md](docs/grokbot.md)。本仓库不带调用代码，只讲清怎么用官方 BDK 扩展接入、要加哪层约定、踩过哪些坑。
- **公司事实**放在 [`onestead/`](onestead/)：Lead 开工前从这里读项目现状，阶段做完记进展，要人批准、动手或知道的事登记成待办。人不用翻聊天记录，回来看 Onestead 就知道每个项目到哪了、有哪些事等他。

## 运作流程：一个任务从头到尾

1. **人提需求。** 一句话说清想要什么（在对话里说，或者写进 Onestead 的待办），然后去忙别的。
2. **Lead 读现状。** Lead 开工先调 Onestead 的 `company()`：拿到公司规矩、项目一览，在项目目录里开工时还会先带出这个项目的现状、下一步和入口。派活前再读一眼额度快照（`hud/` + `tools/quota_pace.py`），按任务难度和各家剩余额度选执行者。
3. **写任务卡。** 按 [`method/`](method/) 的模板写：要交付的真实结果、写面（能改哪些文件）、点名的必要测试、交付目录、不要做的事。一张卡一个工作树、一个交付目录。
4. **派单，然后睡觉。** `claude/bin/Dispatch.ps1` 启动执行者，打印一行守候命令；Lead 把它放进后台运行，结束本轮。从这里开始到交卷，没有任何模型在轮询。
5. **执行者干活。** 测试和构建套 `guards/` 的内存上限，删文件过删除护栏；研究室、观察窗、额度 HUD 让人随时看到谁在干活、额度还剩多少。干完（或干不下去）最后写 `READY.json`。
6. **交卷叫醒。** 守候看到 `READY.json` 就退出，Claude Code 的原会话被叫醒；Codex App 里的 Lead 由 `codex/` 插件把交卷投回原会话。
7. **验收。** Lead 看实际产物、回原文抽查、跑卡里点名的测试；产品代码合并前另派一个只读审查。不合格就写退修卡，回到第 4 步。
8. **合并、收尾、记账。** 合格的合并，当场清掉这张卡的构建产物和临时目录；在 Onestead 记一条进展（只写结果：做了什么、结果在哪、下一步），定下来的事记成决定。
9. **要人点头的事登记成待办。** 推送、发布、花钱、永久删除、改凭据这几类，Lead 把能做的都做完，登记成 Onestead 待办：是什么、为什么、各选项后果、推荐。人回来在 Onestead 一次看全、拍板。
10. **当场派下一批。** 一批收口就派下一批，项目没完成就始终有执行者在跑；多个 Lead 分工时，用 `mailbox/` 交接、提问、回执。

人在这条链里只做两件事：说清要什么，批准对外和不可逆的动作。其余的判断、执行、验收、记账都由 agent 完成。

## 平台支持：Windows 和 macOS

这套东西是在 Windows 11 上天天用出来的，Windows 上全部组件都有实际运行验证。macOS 的情况按组件分三类：

| 组件 | macOS | 说明 |
| --- | --- | --- |
| `method/` 工作流方法 | 适用 | 纯文档 |
| `claude/` 派活和叫醒、`mailbox/` Lead 之间的信箱 | Mac 离线实测通过 | PowerShell 7.7：claude 12项、mailbox 8项；假执行者完整交卷，真实模型派活尚未执行 |
| `acp/` ACP 客户端 | Mac 离线实测通过 | Intel macOS 15.7：mock smoke 退出 0；非真实模型调用 |
| `tools/extract_dialogue.py` | 适用 | 纯 Python |
| `onestead/` | Mac 离线分片实测通过 | 1312个收集节点均已覆盖；首跑限时后续跑尾片，路径fixture修复后模块7项重跑通过；网页打开本机应用/目录仍不可用 |
| `codex/` Codex App 叫醒插件 | Mac 离线实测通过；真实唤醒未测 | 36/36；慢 Intel 的普通 mock 启动时限为2秒，专用超时测试不变；未改 App 设置 |
| `studio/` 研究室 | Mac 离线测试通过；真实模式未测 | `test:live` 29/29，假执行者与本地 HTTP；不能据此宣称真实采集器可用 |
| `watch/` 执行观察窗 | 需要重新做 | WPF，只支持 Windows |
| `hud/` 额度 HUD | 需要重新做 | WPF / net8.0-windows，只支持 Windows |
| `tools/quota_pace.py` | 需要额度快照 | 读 `hud/` 写出的快照；macOS 没有 HUD，可以按同样格式自己写一份 |
| `guards/` 内存护栏和删除护栏 | Mac 基础实测通过，边界见文档 | RSS 采样与进程组清理，不是硬上限；原生废纸篓；Finder alias 保守拒绝，跨卷和超大目标未测 |

一句话：在 macOS 上，“写卡 → 派活 → 等交卷 → 被叫醒 → Lead 之间写信 → 记进 Onestead”主线的离线 mock 已在 Intel Mac 实测；真实模型派活和 App 唤醒尚未验证；三个“看得见”的窗口里只有研究室演示模式能开，两道护栏已有 Mac 分支，采样上限和文件回收的限制见各自文档。

> **macOS in one line:** offline mock dispatch/wake, mailbox, ACP and plugin checks pass on Intel Mac with PowerShell 7; real model dispatch and App wake remain untested; the studio demo runs anywhere Node runs; the studio live mode, the execution window and quota HUD remain Windows-only. The guards have Mac branches: sampled RSS is not a hard cap, and native trash fails closed. See component reports for tested versus untested scope.

---

## 从零用起来（Mac）

需要 Node、Python 3.12+、PowerShell 7。可以将 Microsoft 官方 macOS x64 / arm64 压缩包解压到用户目录，按官方 `hashes.sha256` 核验；仅将该目录加入**本次 shell** 的 PATH，不必改系统目录。Intel 实测使用 PowerShell 7.7.0；Python 使用 PyPI 的 uv 在独立用户目录安装 3.12.16，再建项目专用 venv。不要替换系统 Python。

```sh
# 假设 python3 已是 3.12+；在仓库根目录执行
python3 -m venv ../baton-venv
../baton-venv/bin/python -m pip install --only-binary=:all: -e './onestead[docs]' pytest
# 插件没有 lockfile / 安装依赖，不需要 npm ci
npm --prefix codex/plugin test
# Onestead 必须隔离真实家目录
iso=$(mktemp -d /tmp/ost-XXXXXX)
mkdir -p "$iso/home" "$iso/temp"
HOME="$iso/home" USERPROFILE="$iso/home" TMPDIR="$iso/temp" TEMP="$iso/temp" TMP="$iso/temp" \
  ../baton-venv/bin/python -m pytest onestead/tests -q
```

老机器测试串行运行，使用 `guards/capped_run.py --mem-gb 1 --timeout 900 --log <日志> -- <命令>` 限制测试。需要进程采样权限；拒绝时运行器退出125，不要关闭保护来冒充测试通过。只测试临时文件，清理时用系统废纸篓；不要改现有 Codex 插件、配置或其他项目。真实 Codex 派活需额外获得模型使用授权，离线通过不等于真实派活已验证。

## 从零用起来（Claude Code 当 Lead，Windows）

1. 读一遍 [`method/README.md`](method/README.md)，十分钟，知道每个角色做什么。
2. 把 [`claude/CLAUDE.snippet.md`](claude/CLAUDE.snippet.md) 放进你的 `CLAUDE.md`，路径改成本仓库位置；复制 `claude/executors.example.json` 为 `claude/executors.json`，只留你装了的 CLI，核对模型和权限参数。
3. 装内存护栏钩子（[`guards/README.md`](guards/README.md)），`pip install pywin32`。
4. 装 [`onestead/`](onestead/)：`python -m pip install ./onestead`，`onestead init <数据目录> --owner <身份键，如 alex> --name <你的名字> --lang zh` 建公司（`--owner` 是小写英文、数字、连字符组成的身份键，`--lang zh` 让初始手册用中文）；在本机网页的成员页给每个 agent 建一个成员；把 [`method/onestead/`](method/onestead/) 里两份推荐手册复制进数据仓库的 `handbook/` 并按自己情况改；最后 `onestead connect` 预览、`onestead connect --apply` 接进 Claude Code 等客户端。详见下面的 [Onestead 怎么用](#onestead-怎么用规则接入更新)。
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

## Onestead 怎么用：规则、接入、更新

Onestead 的完整说明在 [onestead/README.zh.md](onestead/README.zh.md)（英文 [onestead/README.md](onestead/README.md)），数据格式在 [onestead/docs/data-format.md](onestead/docs/data-format.md)。下面只讲它在这套工作流里怎么用。

### 规则写在哪

Onestead 的数据仓库是一个独立的 git 仓库，内容全是 Markdown。规则主要写在两份手册里：

| 文件 | 什么时候被读到 | 写什么 |
| --- | --- | --- |
| `handbook/company.md` | 每个 agent 调 `company()` 时，公司概况整段原文带出 | **置顶规则**（接进来的 agent 必须做到的几条）、公司是谁、谁定方向 |
| `handbook/collaboration.md` | 同上，`company()` 原文带出 | 规则正本在哪、怎么派活、哪些事要主人批、Onestead 本身怎么用 |
| `handbook/<其他>.md` | `company()` 只列目录，agent 需要时用 `get_handbook` 读 | 专题手册：格式说明、某类业务的做法 |
| 项目正文 `projects/<id>/project.md` | `get_project` 读；agent 在项目 `paths` 里的目录开工时，`company()` 先带出它 | 这个项目的目标、现状、下一步、分工、入口 |

两份手册会被所有接进来的 agent 读到，规矩写在这里一次，各家就都照着做，不用再分别抄进每个 agent 的 `CLAUDE.md` / `AGENTS.md`。各 agent 的入口文件和记忆里只放指向 Onestead 的指针。

改法有两种：主人在本机网页上新建、修改手册；或者直接改数据仓库里的 Markdown 再提交。每次写入都是署名的 git 提交，主人能在网页上看改动，也能撤回允许撤回的提交。每份文件带 `sensitivity`（public / internal / restricted），restricted 要写 `visible_to`，决定谁能读到。

### 推荐规则（我们现在在用的）

[`method/onestead/company.md`](method/onestead/company.md) 和 [`method/onestead/collaboration.md`](method/onestead/collaboration.md) 是我们每天在用的两份手册去掉个人信息后的版本，可以直接复制进数据仓库的 `handbook/` 再改。核心是 `company.md` 开头的四条置顶规则：

1. **项目更新及时记进来**：每完成一个阶段记一条，阶段长的 24 小时里有实际推进至少记一次；一条只写一个结果（交付了什么、定了什么、卡在哪要谁做），派单、等回件、读了什么文件这些过程不记；大项目由 Lead 汇总着记，执行者不各自写；项目停了就改状态、写原因。
2. **搭好的工具登记到应用广场**（`register_app` / `update_app`）。
3. **写好的 Skill 登记到 Skill 广场**（`register_skill` / `update_skill`）。
4. **要主人批准、动手或知道的事登记成待办**：写清是什么、为什么、各选项后果、推荐，当面告诉主人后标已通知。

我们的经验：**做不到这几条的 agent 就不接入。** 记账靠规矩约定比靠人提醒可靠，而且进展只写结果，Onestead 才不会被过程记录淹没。人回来翻 Onestead，就知道每个项目到哪了、哪些事在等他。

### agent 怎么接进来

1. **每个 agent 一个成员。** 在本机网页的成员页添加，用独立的成员键（例如 `claude`、`codex`、`cursor`），“属于谁”设成你；本机伙伴需要读全部公司事实就选“全部（本机伙伴）”，要不要能写用“能用底座的写接口”控制。主人键只给本机网页用，不发给 agent。
2. **写进各家客户端配置。** `onestead connect` 先预览会改哪些文件，`onestead connect --apply` 再写入；支持 Claude Code、Codex、Cursor、WorkBuddy，`--client` 可以只处理一家。原文件会先备份。
3. **验证。** 让 agent 调一次 `company()`，看它能不能说出有哪些项目、要守哪些规矩。
4. **不能接 MCP 的执行者**用命令行，权限规则完全一样：`onestead company --as <成员键>`、`onestead status <项目> --text "..." --as <成员键>`。本地程序用 JSON 接口 `onestead api`。
5. 多个对话共用一个身份时（比如同一家 agent 的几个会话），写入带署名：MCP 的 `signed_as`，命令行的 `--signed-as`。

### agent 什么时候更新什么

| 什么时候 | MCP 工具 | 命令行 |
| --- | --- | --- |
| 完成一个阶段 | `update_project_status`：做了什么、结果在哪、下一步 | `status` |
| 现状、下一步、分工、入口变了；项目暂停或结束 | `update_project_summary` | `project-summary` |
| 定下一件事 | `record_decision`，写上理由 | `decide` |
| 要主人批准、动手或知道 | `add_todos` 带 ask；通知后 `update_todo` 标已通知 | `todo-add` / `todo-update` |
| 接手一个项目 | `get_project` 读清楚，再 `handover_role`；关键角色要主人批准 | `project` / `handover` |
| 做好一个工具 / 写好一个 Skill | `register_app` / `register_skill`，变了用 `update_app` / `update_skill` | `app-register` / `skill-register` |

同一项目里同一身份 2 小时内又记，或者一条超过 800 字，Onestead 会在回复里提醒；较早的进展自动按月归档，`search` 仍能搜到。

### 升级 Onestead 本身

数据仓库和程序是分开的，升级程序不动数据。拉取本仓库新版本后，在仓库根目录重新运行 `python -m pip install ./onestead`（用 `-e` 装的不用重装）；已经开着的 agent 会话重开一次才会用上新版。升级前可以先跑 `onestead-backup` 打包数据仓库。

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

## 注意事项

**安全**

- `--force`、`--always-approve` 这类参数，以及 `acp/` 的 `approve` 模式，都等于允许执行者在你的机器上跑任意命令。优先用带沙箱的执行者，工作目录里只放这个任务需要的东西。
- 配置、任务卡、信件、Onestead 和公开日志里都不要写入凭据；Onestead 里只写“凭据放在哪、叫什么”。
- 执行者可能会把任务卡和它读到的文件发送给对应的服务。敏感数据派给谁，先想清楚。
- `codex/` 插件只在你授权的范围内给你自己的会话发消息，不要改成往任意会话发。
- 删除护栏是“看得见的命令护栏”，靠钩子拒绝危险命令，不是不可绕过的沙箱；重要数据另做备份。

**机器和磁盘**（几个 agent 同时干活时最容易出事的地方）

- 测试和构建一律套 `guards/` 的内存上限。我们出过一次裸跑测试吃掉 72GB 内存、整机崩溃的事故。
- 编译缓存很占地方：一张卡一个构建目录的话，每个十几到四十 GB，很快撑满磁盘。固定两三个构建目录轮流复用，合并后当场清掉。
- 回收站和文件在同一块盘上，送进回收站不腾空间；超出回收站容量的大目录，系统会不提示直接永久删除。要留的东西先搬出来。
- 执行者的 `timeout` 往往只杀外壳，子进程会残留空转。跑完按整棵进程树清理。
- 任务卡里写明不建计划任务、不起常驻进程；需要“几点后再跑”的，由 Lead 到点另派。
- 同时开几十个 agent 会话会拖慢甚至卡死桌面 App；按内存设个在跑上限。
- 大量测试临时文件会拖垮 Windows Defender。我们遇到过引擎扫临时文件时挂死，之后所有 PowerShell 连启动都卡住，用 PowerShell 写的守候退不出，Lead 一晚上没被叫醒。守候因此改成了 Python。建议把测试临时目录和构建目录加进 Defender 排除项（要管理员权限，代价是这些目录不再实时扫描），值守脚本里也顺带查一下 PowerShell 能不能应答。

**账号和额度**

- 派活前看额度快照，按难度和剩余额度选执行者；不要为了“用完额度”编任务。
- 同一个订阅账号不要在很多进程、很多台机器上频繁并发登录，容易触发服务方的风控；需要真实账号的测试一次只跑一个。

**Windows 小坑**

- Windows PowerShell 5.1 读不带 BOM 的非 ASCII 脚本会解析错，脚本统一用 PowerShell 7 跑。
- 脱离控制台的长跑脚本起子进程要加 `CREATE_NO_WINDOW`，否则会不停弹终端窗口。
- Codex App 更新后打不开、部署日志报 `0x80073CF6` / `0x8007041C` 时，我们遇到的原因是它的沙箱服务卡在“启动中”停不下来，新版本替换不了。用管理员权限结束 `codex-windows-sandbox-service.exe`，再打开 App 就装上了。

## 本地离线检查

```powershell
npm --prefix codex/plugin test
node codex/tools/check-public.mjs
pwsh -NoProfile -File claude/tests/Smoke.ps1
pwsh -NoProfile -File mailbox/tests/Smoke.ps1
python acp/tests/smoke.py
npm --prefix studio ci; npm --prefix studio run test:live
python -m pytest guards/file-guard/tests -q
```

`onestead/` 的测试要用一个短路径下的空目录当 HOME 和 TEMP 来跑：测试会建很深的目录，路径太长会超过 Windows 的长度上限，用真实家目录还会读到本机配置。抽文档的可选依赖 `[docs]` 装了才跑文档类测试，不装会跳过 3 条：

```powershell
python -m pip install -e "./onestead[docs]" pytest
$iso = "C:\ost-$(Get-Random)"; New-Item -ItemType Directory "$iso\home", "$iso\temp" | Out-Null
$env:HOME = "$iso\home"; $env:USERPROFILE = "$iso\home"; $env:TEMP = "$iso\temp"; $env:TMP = "$iso\temp"
python -m pytest onestead/tests -q
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
