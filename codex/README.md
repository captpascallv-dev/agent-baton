# Codex Mail Wake 0.4.1

MIT。Node 22+、Python 3.10+。独立的可安装 Codex 插件：普通程序持续接收已登记 CLI/Bot 交付，完整通知发给登记时的原 Lead。没有私信功能、执行器启动、provider 调用或 PID 看门。收到一次交付后继续守候，默认没有总截止；等待本身不调用模型，Lead 处理通知仍会调用模型。

## 安装与配置

仓库 marketplace 的插件来源是 `./codex/plugin`。按 Codex 正常插件流程添加本仓库 marketplace，启用 `codex-mail-wake`。插件包含运行时及默认账本，不依赖仓库外的私人脚本；无需 npm install。官方 `codex-app-tools` 必须已由当前用户安装且当前宿主提供 launch pipe，本仓库不包含其源文件。

在 checkout 根执行正常 CLI 安装命令：

```text
codex plugin marketplace add .
codex plugin add codex-mail-wake@agent-lead-wake
```

将 `adapter-config.example.json` 复制到 checkout 外的工作目录，填写 `coordinatorThreadId` 为真正负责运输故障协调的 Codex 对话 ID。相对 mailbox/state 路径按配置文件位置解析。`pythonExecutable` 明确填当前 PATH 的 `python`/`python3`，或你的 Python 3.10+ 路径；不会猜其它用户的运行时。

| 配置 | 用途 |
| --- | --- |
| `mailboxRoot` | routes/jobs/deliveries/claims/snapshots 的账本根 |
| `stateDir` | 许可、发送 intent、收据、故障和恢复状态根；所有实例共用 |
| `coordinatorThreadId` | 实际协调者；仅它能启动/停止全体项目 |
| `pythonExecutable` | 显式 Python 命令或路径 |
| `pollIntervalMs` | 默认15000，允许1000..60000 |
| `appToolsEntry` | 可选，固定当前用户有效已安装 official cache 内的入口 |

让正常 MCP 宿主启动环境继承 `MAIL_WAKE_CONFIG`（配置文件绝对路径）。也可显式提供 `MAIL_WAKE_MAILBOX_ROOT`、`MAIL_WAKE_STATE_DIR`、`MAIL_WAKE_COORDINATOR`、`MAIL_WAKE_PYTHON`；环境覆盖配置文件。缺必要项拒绝启动，示例的 null 协调者不会变成默认权限。

从 GUI 启动或已运行的 Desktop App 不会继承你刚在另一个 shell export 的变量。无需改变现有 App 环境时，将完整配置放到当前 `CODEX_HOME/mail-wake.config.json`；未设 CODEX_HOME 时，用当前用户 `%USERPROFILE%/.codex/mail-wake.config.json`（Unix为 `$HOME/.codex/mail-wake.config.json`）。这是用户明确放置的配置，里面的 owner/目录仍必须填写，不会猜默认值。显式 MAIL_WAKE_CONFIG 优先；默认文件缺失或必需字段缺失仍 fail closed。默认位置的相对目录相对该文件解析，应修改为你已有账本/状态的实际路径。

PowerShell：

```powershell
$env:MAIL_WAKE_CONFIG = (Resolve-Path '../wake-demo/config.json').Path
```

Bash：

```bash
export MAIL_WAKE_CONFIG="$(realpath ../wake-demo/config.json)"
```

`.mcp.json` 使用插件根 `cwd: "."`、`node ./server.mjs`，继承配置与本次宿主 `CODEX_APP_TOOLS_PIPE_PATH`、`CODEX_THREAD_ID` 和当前账号运行时环境。不要把 pipe 写进配置或持久状态；缺 pipe，或既无真实 actor 又无有效后台委托的实例不 scan。没有机器绑定的 stateDir 或 target/model/host 覆盖参数。

普通 stdio 入口也可用：`node codex/plugin/server.mjs --config /your/config.json`。普通终端没有宿主 pipe 时只保留状态，不伪造宿主发送。

## 可运行的默认账本

以下命令在仓库根执行。先创建 `../wake-demo/outbox`；`YOUR_LEAD_THREAD_ID` 替换为真正的收件 Lead ID。将配置例子复制为 `../wake-demo/config.json`；其 `./mailbox`/`./receiver-state` 指这个目录下的两个根。

```text
python codex/tools/mailbox.py --root ../wake-demo/mailbox route --project demo --owner YOUR_LEAD_THREAD_ID
python codex/tools/mailbox.py --root ../wake-demo/mailbox register --project demo --job example --attempt 1 --outbox ../wake-demo/outbox
```

Linux 可把 python 换成 python3。已安装插件内同一 CLI 为 `plugin/driver/ledger.py`；仓库 tools/mailbox.py 只是它的入口。route 初次配置自动初始化目录。状态为 active/held/closed；held/closed 不通知，也不会因启用接收而恢复业务。

执行器在已登记 outbox 写 UTF-8 `REPORT.md`、所列产物，最后写 `READY.json`：

```json
{"status":"completed","deliverables":["result.txt"],"notes":"One sentence"}
```

status 也可为 blocked/failed，做不完仍交 READY。deliverables 为相对 outbox 的文件路径，使用 `/`。合法相对路径按登记 outbox 解析，调用者的当前目录不参与定位；嵌套、空格和非 ASCII 文件名沿用这一契约。不执行报告或命令。绝对路径、反斜杠、越界路径和逃逸的符号链接拒绝。Windows 上盘符相对（`C:name`、`C:sub/file`）和无盘符根相对（`/name`）在拼接前拒绝，避免被当成登记目录内的别名或当前盘根上的文件。POSIX 上 `C:name` 仍是合法相对文件名。固定报告为 REPORT.md。单文件上限10MiB、整份快照25MiB、最多64个 deliverables；更大交付需另设计 adapter。单件不可读显示 error，其它登记继续。

常规终态来源仍是READY。0.3.1另支持可信外部wrapper的明确启动前拒绝契约（下节）：文件身份和未尝试调用标志匹配后生成blocked/failed READY。静默退出、超时或可能已调用不适用此转换，仍需执行器/adapter交failed READY或Lead查日志；账本不凭PID猜完成，没有provider专用native recover/receipt collector。CLI/Bot 只是两类登记来源；Bot 使用同一 outbox 协议，在 register 加 `--source bot`。

READY 无内嵌 job/attempt ID，因此规范化 outbox 永久绑定一个 project/job/attempt。相同注册幂等；不同 job/attempt 复用同一 outbox 拒绝，不能把旧 READY 当新轮。首次注册可绑定已有 READY，这是登记者明确确认归属；建议先登记、再启动执行器。新 attempt 必须新 outbox。旧 owner/outbox/source 不可通过重复 register 静默变更；route 换 owner 必须显式 `--replace-owner`，旧登记仍保留原 owner，需明确交接。project/job/attempt 为1..40个安全 ASCII 字符，允许字母、数字、`_`、`-`、`.`，必须以字母或数字开头。

单独检查：`python codex/tools/mailbox.py --root ../wake-demo/mailbox scan`。这是文件收集，不发 Codex 消息；正常发送由用户明确启动 MCP 守候后完成。

## MCP 使用与身份

| 工具 | 行为 |
| --- | --- |
| `start_app_mailbox_watch({"project":"demo"})` | 实际 caller 是该项目当前原 owner；持久持续接收许可 |
| `app_mailbox_status({"project":"demo"})` | 普通 Lead只见自己当前登记项目及运输状态 |
| `stop_app_mailbox_watch({"project":"demo"})` | 停本项目，保留所有 pending/claim/intent/receipt |
| `grant_app_mailbox_delegation({})` | 仅本次真实 caller 为配置协调者；明确授予可撤销后台身份委托，不开启 watch |
| `revoke_app_mailbox_delegation({})` | 仅本次真实 caller 为配置协调者；撤回后台委托，保留交付与收据 |

配置协调者可省略 project 启用/停止全体 active routes；普通 Lead不能这么做。全体模式下项目 stop 是持久 opt-out。已启用的全体 start 幂等，不覆盖 opt-out；协调者 stop 后再 start 才是新轮许可。

权限按真实 tools/call metadata（无 metadata 时真实 executor env）检查。后台 actor 优先取本次 `CODEX_THREAD_ID`，缺失才由本实例真实工具 metadata 绑定；仅这两者均缺失时，有效明确委托可提供后台 actor。持久 registrar 不构成委托，委托永不充当普通工具 caller。没有伪造 turn_id、改变原模型/档位/服务档。start/stop/grant/revoke 遵循正常 prompt 审批；官方审批配置未改，原生拒绝不换路处理。

`scan -> 原子 claim -> 官方 send -> exact receipt ack`。claim.prompt 原样发送，包括自动投递说明和原报告。读取原 owner，只有 idle/notLoaded 可送；active/systemError/未知保持 pending。[官方 ThreadStatus 类型](https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/schema/typescript/v2/ThreadStatus.ts)与[thread/read 文档](https://learn.chatgpt.com/docs/app-server#read-a-stored-thread-without-resuming)是接口依据，read 不会加载线程。sent 仅证明通知接受，Lead实际接续/任务验收另行确认。

## 后台身份委托（0.4.0）

只有配置的 `coordinatorThreadId` 可操作；工具不接受任何参数，包括 caller/actor/target/pipe/root/model/project。普通 Lead 仍按项目启停，不能授予委托。首次使用由协调者明确调用：

```text
start_app_mailbox_watch({})
grant_app_mailbox_delegation({})
```

grant 要求已启用且 epoch 有效的协调者 all-watch，将 coordinator、当前接收 state 根、mailbox 根和 epoch 写入 `stateDir/app-mailbox/delegation.json`，并保留 delegation-history。重复同范围有效 grant 幂等。grant/revoke 用独立短 OS 控制锁串行；争用时工具明确返回 busy，不改权限，可重新明确调用。记录不是防本机写权限持有者篡改的签名凭证；账本和接收状态目录须由用户保护。

新服务只要被正常宿主加载、收到本次 launch pipe 且 grant 当前仍有效，就可在没有 `CODEX_THREAD_ID`、tools/call 或 hook 的情况下后台接收。actorSource 为 `persisted_delegation`；leader、intent 和状态包含 grant ID。出现本次真实 actor 后它优先，status 分开展示 actualActor 与后台 actor。持久委托不会补齐缺身份的工具调用，也不授予别的账号、业务项目或发送目标。

```text
revoke_app_mailbox_delegation({})
```

revoke 后只停止依赖该委托的新后台动作；具有独立真实 actor 且仍有 watch 许可的实例沿原权限继续。如需停止全部收件，由协调者调用 `stop_app_mailbox_watch({})`。停止全体或 epoch 改变会使旧 grant 失效；之后 start/status/重建均不能复活旧 grant，必须重新明确 grant。项目 opt-out、held、closed 一直有效。

无 grant、坏记录、错 coordinator/根/epoch 或缺 launch pipe 均拒绝委托恢复。坏委托需协调者明确 revoke 后才能新 grant。在领取、intent/send 前和 OS lease、scan、read/claim、桥 handshake 等关键 await 后复核 actor、epoch 和项目许可。已经提交的 send 保留真实 receipt 并沿原规则 ack；旧 sending/uncertain/manual_review_required 不自动重发。委托文件不保存 pipe；每个新进程只使用本次宿主环境提供的 pipe。

## 0.4.1 收件兼容与并发修复

发送通知默认等待 60 秒；握手和读取仍默认 20 秒。一个请求到期只结束该请求的等待，不会取消同一连接上其它已提交请求。真正进程退出或管道失败仍走原健康恢复。发送超时保留原 intent 和不确定状态，不自动重发；延长等待也不改变官方工具的接受或拒绝结果。

登记交付与故障通知共用接收状态根下的 `app-mailbox/target-send-locks/`，按精确目标身份加非阻塞 OS 锁。同一目标正被另一进程发送时，本轮保留 pending；不同目标互不占锁。登记交付在持锁后重新核对原目标状态和许可，失锁或撤权后不领取/发送。锁由现有配置的 Python 驱动持有，进程退出或 stdin 关闭时释放；保留锁文件，不以删除文件恢复。已有领班锁、账本锁和永久交付 claim 继续各自生效。

当官方 `read_thread` 明确报告 `thread/items/list is not supported yet` 时，收件预检改从官方 `list_threads` 的当前可见结果核对原目标。只接受精确 ID、Codex 本地聊天和一致的已知实时状态；仅 `idle` / `notLoaded` 可进入正常交付。查询覆盖当前返回的置顶聊天和最多 50 条非置顶聊天，未找到、不明确、一般拒绝或仍忙均保留 pending。这个兼容分支不读取本地历史文件或聊天正文，也不改变权限。

## 持续、恢复和故障

`.codex-mail-wake.leader.lock` 由 Python lease 持有非阻塞 OS 锁，父 stdin EOF/进程退出由 OS 释放；其它实例每分钟尝试接替。每次 scan/claim/ack（包括默认 CLI）还共用 `.codex-mail-wake.driver.lock` 短锁；grant/revoke 共用 `.codex-mail-wake.authorization.lock` 短锁。永久 claims/<id>.lock 是交付独占权，不能删来重试；claim 输家直接返回，不写胜方 outcome。

intent 在 send 前完整 fsync、独占原子发布。超时/退出/拒绝/错目标保留 sending/claim 与局部 uncertain，不盲重发；精确持久收据可重建后仅补 ack。旧终态历史目录不反复读中央；历史中央缺失/损坏/身份不符逐件标异常，其它件继续。快照和不可变 JSON 先完整写临时文件，再 exclusive hard-link 发布；需要支持本地 hard links/atomic rename 的文件系统，不建议网络共享。

同一 attempt 即使 READY 后续变化或同源重复观察，sent/claim/intent 不重放；新 attempt/新 outbox 才有新交付。project 名称大小写敏感；状态文件用小写字节编码，Demo/demo 在 Windows 也互不启停。默认账本注册时一次性生成紧凑小写交付ID，并核对完整既有登记，不会采用另一个键的记录。超长 adapter ID 的文件名使用缓存的紧凑身份键以守住长度边界；这些摘要的消费者是文件身份/既有登记比对，不是报告/日志。快照只在首次交卷保存并比较已有副本，不在每轮计算内容摘要。

桥子进程失效后释放领班，同实例至少等60秒，再每分钟至多一次用**原 launch pipe** handshake＋read 当前授权 actor（真实身份或有效委托）；成功且授权仍有效才重新竞争领班。该只读检查不构成 grant 或普通 caller 身份证明。拒绝/错误身份不scan、不send。App 换 pipe 需新启动环境实例接替，不扫描内存/凭据、不猜或持久化 raw pipe。

限定故障向配置协调者单次通知：uncertain/manual review、同登记 collector error 连续3轮、同 pending 因 owner 状态阻塞2小时。短通知仅含ID/project/原因/状态路径；故障本身也有独占 intent/receipt/outcome，拒绝或未知不重试。不发正常进度/成功/空目录/held通知。故障发布失败写 record-problems，status可见，不让坏件堵整条线。

保留账本、OS 锁文件、接收许可、委托及历史、intent/receipt/outcome、faults、record-problems及快照。停止自己的 watcher/lease 让 OS 解锁，不删 claim/intent。更新插件不要删用户状态根。所有实例同一配置；状态已绑定其它账本时拒绝复用。

服务遵循 MCP stdio 生命周期。服务**被重新加载且授权/pipe有效时可恢复**，不等于 App 启动就会加载；没有系统服务、计划任务或自动重启 provider/App。公共版的服务重建以 stdio 与假官方宿主验证。完整 App 更新/关闭再打开、新真实交付、真实未加载线程及实际 Lead接续未在公共包现场验证；其它私有部署的局部恢复或只读桥证据不替代这项验收。

## 公共包检查

```text
npm --prefix codex/plugin test
```

或 `node --test codex/tests/public.test.mjs`。测试从 PATH 找 Python3.10+；可用 `MAIL_WAKE_TEST_PYTHON` 指定测试解释器。只用新建 OS 临时目录、默认公共账本与假 MCP cache/host，清理前验证目录范围。实际公共检查见 [VALIDATION.md](VALIDATION.md)，不沿用其它版本统计。

## 0.3.1：明确启动前拒绝的可用文件契约

付费执行仍由外部wrapper负责，MCP不启动provider。只有wrapper确认在任何executor spawn/provider invocation尝试之前拒绝，才在已登记outbox原子写PRELAUNCH_REJECTION.json；注册输出的key就是下面delivery_id。身份必须全部匹配原登记，不能用它换owner或job/attempt。

```json
{
  "schema_version": 1,
  "kind": "prelaunch_rejection",
  "delivery_id": "YOUR_REGISTERED_DELIVERY_KEY",
  "project": "demo",
  "job_id": "example",
  "attempt_id": "1",
  "target_thread_id": "YOUR_ORIGINAL_LEAD_THREAD_ID",
  "stage": "before_executor_spawn",
  "executor_spawn_attempted": false,
  "provider_invocation_attempted": false,
  "execution_started": false,
  "status": "blocked",
  "reason_code": "write_scope_rejected",
  "diagnostic_paths": ["launch-check.log"]
}
```

status仅failed/blocked；reason_code仅write_scope_rejected/input_validation_rejected/dependency_unavailable/adapter_preflight_rejected。诊断引用必须是存在的相对outbox文件，最多16项；不执行或内联内容，不写stderr/密钥/模型响应到证明字段。其他字段、错误身份或未知/可能已启动的标志拒收，不生成failed/completed。此协议相信被授权wrapper的明确陈述，不是PID检测，也不是MCP替wrapper证明付费调用。

默认scan自动处理；也可显式调用文件-only helper：

```text
python codex/tools/mailbox.py --root ../wake-demo/mailbox prelaunch YOUR_REGISTERED_DELIVERY_KEY
```

helper生成明确非执行者报告/非验收的REPORT.md和READY，然后沿原scan/claim/ack只通知原owner一次。已有READY/delivery/永久claim/retired优先；不同的既有REPORT.md不覆盖，同内容的helper半成品可完成READY。正常READY保持原样。旧sending/sent/intent不重放；新attempt必须新outbox。静默退出不能把这个文件当作缺request/超时的猜测性失败模板。

## 0.3.2：拒绝 Windows 歧义锚点

公共账本读取附件时，合法相对路径本来就锚定登记 outbox：`(outbox / lexical).resolve(strict=True)`。本次不把契约放宽成绝对路径，也不改快照或通知。补上的是拼接前的 Windows fail-closed：路径带盘符，或去掉盘符后的尾部以 `/` 开头时，直接拒绝。同盘 `C:name` 与 `C:sub/file` 否则会拼成登记目录里的别名；`/name` 否则会重新定到当前盘根。反斜杠路径仍由原规则拒绝。

消费者回归从独立工作目录运行真实 CLI。嵌套、中文和空格相对附件的快照是 outbox 字节。`../`、绝对路径和目录仍然拒绝，正常交付仍交给原 fixture owner，已有 sent/sending 不重写。Windows 专有断言只在实际运行的平台执行。本机私有适配的试运行记录不在本包；公共离线检查见 [VALIDATION.md](VALIDATION.md)。
