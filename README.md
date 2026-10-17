# agent-lead-wake

**用本地程序守候交卷，让原来的 Lead 会话继续。**

> Let an expensive "lead" agent (Claude Code or Codex App) only plan, dispatch and review, while cheaper agent CLIs do the work. When an executor delivers, a local watcher resumes the lead's original conversation. Local programs do the waiting; no relay model handles each delivery. Host notifications and optional watchdog turns still consume model tokens when they run.

这个仓库分四块：

| 目录 | 是什么 | 适用 |
| --- | --- | --- |
| [`claude/`](claude/) | 派单和守候脚本：Claude Code 当 Lead，把任务卡派给任意 agent CLI，交卷后自动叫醒 | Claude Code（CLI / 桌面版） |
| [`codex/`](codex/) | `codex-mail-wake` 插件：让 Codex App 的原会话在执行者交卷时被自动叫醒 | Codex App（桌面版） |
| [`mailbox/`](mailbox/) | Lead 之间的对等信箱：一封 Markdown 一封信，本地守着来信 | 两个或多个 Lead 会话 |
| [`acp/`](acp/) | 通用 ACP 客户端：按 Agent Client Protocol 驱动一个会说 ACP 的执行者 | 提供 ACP stdio 的 agent |

## 四块怎么一起用

- **派活**用 `claude/`。Lead 写任务卡，`Dispatch.ps1` 启动执行者，`Wait-Delivery.ps1` 在交卷时退出，Claude Code 回到原会话。Codex App 里的 Lead 不走这套脚本，用 `codex/` 插件接收交付通知。
- **Lead 之间说话**用 `mailbox/`。它不启动执行者，也不看 `READY.json`。
- **执行者只会 ACP** 时，用 `acp/` 做启动命令，再写进 `claude/executors.json`。任务卡仍然要求执行者把产物和 `READY.json` 写进交卷目录。ACP 客户端只跑协议，不代替交卷。
- 等待都是本地文件轮询。模型只在 Lead 被叫醒之后，以及执行者真正干活的时候运行。

---

## 为什么要做这个

一个顺手的分工是：**判断力强的模型当 Lead**，只负责理解需求、写任务卡、验收；**便宜、额度多的执行者**（各家 agent CLI）负责埋头干活。

卡住的地方在"等"上。执行者一跑就是几分钟到几小时，Lead 怎么知道它交卷了？

- **让模型自己轮询**："查一下好了没"，每查一次都要花一轮模型额度，等得越久花得越多。
- **用心跳中转**：另开一个便宜模型，每隔 N 分钟醒来查信箱，有交卷就转告 Lead。省钱，但交卷后最多要等 N 分钟。一个任务来回几轮，就多等几个 N 分钟。

这个仓库的做法是：**等待交给本地程序**。Claude 侧守候退出后，由后台任务通知接续会话；Codex 侧持续 MCP 直接通知原 Lead。普通文件轮询不调用模型，Lead 接续、Claude 超时续挂及可选的低频看门会启动模型轮次。通知延迟取决于轮询周期和宿主状态，不承诺固定5秒。

---

## Claude 这一半：后台命令退出 = 叫醒会话

### 原理

Claude Code 的 Bash / PowerShell 工具有后台模式（`run_in_background`）。**后台命令退出时，Claude Code 会往原会话里插一条"任务完成"通知，并开始新的一轮。** 利用这一点：

```
Claude（Lead）──写任务卡──▶ Dispatch.ps1 ──后台隐藏进程──▶ agent CLI（干活，写交卷目录）
      ▲                                                            │
      │                                                   最后一步写 READY.json
      └── 自动叫醒 ◀── Wait-Delivery.ps1（后台运行，只查文件）◀──────┘
```

1. Lead 写任务卡，调用 `Dispatch.ps1` 派单。执行者在独立进程里跑，Claude 会话重启也不会把它停掉。
2. Lead 把 `Dispatch.ps1` 输出的 `WATCH:` 命令用**后台模式**运行，然后结束本轮。
3. 执行者全部写完后，最后写一个 `READY.json`。
4. 守候脚本看到 `READY.json` 就退出，Claude Code 把 Lead 叫醒。Lead 取回产物、抽查、写验收记录。

### 守候脚本的核心

整个机制的关键就这十几行：只看文件和进程，看到就退出。

```powershell
# claude/bin/Wait-Delivery.ps1（节选）
while ($true) {
    if (Test-Path -LiteralPath $ready) {                                  # 交卷了
        $status = (Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json).status
        Finish 0 'ready' "ready_status=$status"                           # 退出码 0 → Lead 被叫醒
    }
    if ($ProcessId -gt 0 -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 5
        if (-not (Test-Path -LiteralPath $ready)) { Finish 4 'process-gone-without-ready' "pid=$ProcessId" }
    }                                                                     # 执行者没交卷就退出了 → 也叫醒
    if ((Get-Date) -ge $deadline) { Finish 2 'timeout' "after_min=$TimeoutMinutes" }
    Start-Sleep -Seconds $IntervalSeconds                                 # 每 15–20 秒看一眼，不调模型
}
```

退出码：`0` 已交卷，`2` 到点，`3` / `4` 执行者结束了却没交卷。不管哪种，Lead 都会醒来，看情况处理。

### 快速开始

```powershell
# 1. 配置执行者（Codex / Cursor / Grok Build / PI / DeepSeek DSH 已有样例）
Copy-Item claude/executors.example.json claude/executors.json

# 2. 写任务卡（模板：claude/templates/TASK_CARD.md），然后派单
pwsh -NoProfile -File claude/bin/Dispatch.ps1 -Executor codex `
  -Workspace D:\work\demo -Outbox D:\work\demo\out -PromptFile D:\work\demo\CARD.md -Task "demo"

# 3. 把它输出的 WATCH: 那一行，交给 Claude 用后台模式运行
```

把 [`claude/CLAUDE.snippet.md`](claude/CLAUDE.snippet.md) 放进你的 `CLAUDE.md`，以后对 Claude 说一句"调用外部执行"，它就会按这个流程走。脚本里不写死任何模型或账号，接入新 CLI 只要在 `executors.json` 里加一条，前提是它能非交互地跑一个 prompt、能读写本地文件。

### 两个实测出来的细节

- **后台命令约 30 分钟会被强制停止。** 所以该环境的守候每轮只守 29 分钟。退出码 2 而执行者进程还活着，就原样再挂一轮。脚本只看进程和文件，不读日志正文；后台超时通知和续挂仍可能调用模型。
- **同时派了多单，用 `Wait-Many.ps1`**：一个守候盯全部，任意一单交卷就叫醒你，免得到点时一下跳出一串超时通知。处理后的目录从下一轮守候列表移除，不能让旧READY重复触发新监控。

```powershell
& './claude/bin/Wait-Many.ps1' -Outbox @('D:\job\A', 'D:\job\B') -ProcessId @(111, 222) -TimeoutMinutes 29
```

---

## Codex 这一半：持续 MCP 通知原会话

`codex-mail-wake` 不依赖任意后台进程退出来启动模型轮次。它轮询登记交付，通过用户已经安装的官方 `codex-app-tools` 宿主环境，调用 `send_message_to_thread` 向原 Lead 投递。执行者交卷 → 普通接收程序 → exact 原 Lead，不需要中转模型逐件读报告。

### 配置与安装

1. 按 [codex/README.md](codex/README.md) 创建自己的配置和交付账本，填写实际 coordinator 与各项目 owner。
2. 把本仓库作为本地 marketplace 加入 Codex，安装 `codex-mail-wake`，按宿主要求刷新插件。
3. 原 Lead 明确调用 `start_app_mailbox_watch` 开始收件，派发自己的外部任务。
4. 交付到达后，程序原文投给 exact owner；Lead 读产物、验收或退修。模型与档位不被运输层覆盖。

0.4.0 增加可撤销的后台身份委托：配置协调者先明确启用全体接收，再按正常审批调用 `grant_app_mailbox_delegation({})`。新服务被宿主加载时，即使没有启动 actor 或任何工具调用，也可凭当前有效 grant 与本次 launch pipe 恢复接收；状态如实标注 `persisted_delegation`。grant 绑定配置协调者、两个状态根和当前 all-watch epoch，不开启接收权限，也不代替普通工具 caller。`revoke_app_mailbox_delegation({})` 撤回后台委托；停止全体接收或改变 epoch 后必须重新明确 grant。见 [委托契约](codex/README.md#后台身份委托040)。

公开包自带最小交付账本，不需要作者的私有 `mailbox.py`、业务 collector、账号或本机目录。Lead 之间的对等信箱在 [`mailbox/`](mailbox/)，和本插件的交付账本不是同一条链路。Codex 插件本身只负责收件，不替你启动执行模型或判断业务验收。

### 交付与恢复

- 先落盘发送意图，再发送。超时、断开、拒绝或不明收据保留状态，不盲重发。
- 0.3.1 支持可信启动包装器的明确启动前拒绝：登记后、调用执行者之前，写与本轮身份一致的 `PRELAUNCH_REJECTION.json`，账本将其转为失败/受阻回执，仍只交原 Lead 一次。[契约和示例](codex/README.md)规定证据与写入顺序；进程消失或缺少报告本身不能生成失败回执。
- 0.3.2 明确拒绝 Windows 盘符相对等不明确附件路径。公开账本的合法相对附件一直以登记 outbox 为基准；本轮新增不同工作目录和同名外部诱饵的消费者回归检查，保留目录边界及旧交付保护。
- claim 原子独占；sent 保留；持久精确收据可以只补 ack。同一任务与同一attempt只登记一份终态交付，异常恢复的新执行应使用新attempt和独立outbox。
- 消息标注“此消息由收件系统自动投递”。发送 actor 优先为当前实例真实身份；缺身份时仅明确 grant 可授权后台使用配置协调者身份，不代表该模型逐件读完并转发。
- 多实例共用 OS 领班锁；缺本次宿主管道，或既无真实 actor 又无有效委托时不扫描。撤权后停止新领取和发送，已提交的发送保留收据并完成 ack。
- 历史坏记录逐件隔离；故障和未送达的告警可见，单个坏件不阻塞其它交付。
- 启停遵循用户明确授权和宿主正常审批；原生拒绝如实保存，不换路径绕过。

调用依赖桌面 App 提供的环境和已安装官方工具。不要猜管道路径、复制官方插件源码，或另外启动 App Server 冒充当前桌面会话。官方工具只作为安装依赖调用；这是有明确已验边界的实验性集成。

### 成本与验证边界

接收循环本身不调用模型，真正交卷时原 Lead 恢复工作会消耗它自己的额度。Codex 接收器没有29分钟总截止；App关闭或进程结束后，已保存记录仍保留。

某些宿主会延迟加载MCP，可选低频状态检查作为临时看门。它只读状态、不逐件转交报告，但检查会调用模型。是否需要、多久一次、何时取消，应按自己的启动与恢复实测决定。

- 原型和私有0.2.3已观察到：真实App安装/加载、原会话自动接续、首份真实任务报告被原Lead读取；普通桥曾运行34分46秒后接续。
- 公共版本的自包含配置、账本和假宿主检查见 [codex/VALIDATION.md](codex/VALIDATION.md)，不公开私人测试实录。
- 公共0.4.0已沿 stdio/默认账本以假官方宿主验证显式grant后服务重建恢复；隔离CLI安装另行检查。完整Desktop关闭再打开、新真实交付和原Lead接续仍需各使用环境验证。宿主未加载服务或未提供pipe时，持久grant不能自行启动它。精确收据不等于Lead已经阅读，收到报告也不等于业务已验收。

示例轮询为15–20秒，正在运行的Lead会话可能先等待。此项目不由OpenAI或Anthropic发布，也不包含其私有工具源码。配置参考：[Codex插件](https://developers.openai.com/plugins/build/plugins)、[MCP](https://learn.chatgpt.com/docs/extend/mcp)。

---

## 实测数据

**Claude 贡献者报告的本地派单测量**（Windows 11，PowerShell 7）：

| 执行者 | 测试模型 | 派单 → 交卷 |
| --- | --- | --- |
| Codex CLI | gpt-6-luna / max | 20 秒 |
| Cursor CLI | grok-4.7-xhigh | 64 秒 |
| Grok Build | grok-4.7 / xhigh | 16 秒 |
| PI | gpt-6-luna / max | 19 秒 |
| DeepSeek DSH | 默认 | 4 秒 |

这些历史测量的测试任务只是"读卡、写一行报告、写 READY.json"，五个并行派出，全部被自动叫醒。这只证明链路是通的。真实任务里，我们用同一套脚本跑过一小时上下的调研和代码核对任务，三个执行者并行，Lead 全程不轮询。

---

## 我们学到的几条

1. **执行者自报 ≠ 交付。** 验收要看实际产物、回原文抽查，不能只看 REPORT 写了什么。
2. **`READY.json` 必须最后写**，而且做不完也要写（blocked / failed）。静默退出是最难排查的失败。
3. **宁可不发，也不重发。** 不确定有没有送达时，标记出来交给人或协调者，不要自动再发一遍。
4. **卡住的东西必须有人看得见。** "不重发"之后一定要有通知或看门，否则就变成静默丢件。
5. **守候不读日志正文。** 只看文件在不在、进程活不活，等待才能真正做到零成本。
6. **校验要相称。** 只在跨信任边界或长期存档时加哈希校验；自己刚写、马上又读的文件不用校验，更不要放进轮询的热路径。

---

## 安全提醒

- `--force`、`--always-approve` 这类参数，等于允许执行者在你的机器上跑任意命令。优先用带沙箱的执行者，工作目录里只放这个任务需要的东西。
- 本仓库不管理 CLI 登录；各 CLI 使用自己的登录状态或已有系统环境。配置、任务卡和公开日志中不要写入凭据。
- 执行者可能按自己的配置，把任务卡和它读到的文件发送给对应服务。敏感数据派给谁，自己先想清楚。
- Codex 插件会在用户授权的范围内自动给会话发消息，只用于你自己的会话，不要改成往任意会话发。

## 本地离线检查

```powershell
npm --prefix codex/plugin test
pwsh -NoProfile -File claude/tests/Smoke.ps1
pwsh -NoProfile -File mailbox/tests/Smoke.ps1
python acp/tests/smoke.py
```

使用本地模拟执行者和假 MCP 宿主，不需要模型账号、真实桌面唤醒或生产数据。[Windows/Ubuntu CI示例](docs/offline-ci.example.yml)可复制到 `.github/workflows/ci.yml` 启用；当前仓库没有自动工作流，首次发布只完成本地Windows检查。启用CI需要相应的仓库工作流写权限。离线检查与真实模型任务及App生命周期验收是不同范围。

## License

[MIT](LICENSE)
