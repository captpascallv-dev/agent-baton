# DSH 状态观察插件

这个插件只观察本机 DSH Desktop 已经加载的会话，把运行、休眠和当前请求的模型写成状态文件。它不启动、resume 或恢复任何会话，不挂接 SDK 业务 transport，不使用 HTTP／Cookie／凭据，也不改 DSH 业务状态。安装后默认关闭，需要在 profile patch 里单独启用。

适用版本已从当前桌面 ASAR 的 package 元数据核对：DSH Desktop／runtime／agent-loop `0.2.1-rc.2`，Cordis `4.0.5`。`engines.dsh` 暂限定该版本。没有 npm 运行依赖、install script 或构建步骤。

## 输出与状态规则

插件仅观察当前 runtime 的 `ctx.agents.list()` 所持有的 Agent。安装前已经打开的会话可直接被枚举，不需要重新打开或 resume。它为每个实际 Agent 对象生成随机 opaque ID，不输出 session ID、标题、聊天内容、工具名／参数／结果、账号或认证数据。

| 真实公开钩子 | 只读取的字段 | 状态用途 |
| --- | --- | --- |
| `agent/created`、`agent/disposed`、`agent/status` | agent 对象身份、status | 已加载实例、移除、明确 idle |
| `agent/assistant-stream` | frame.type、attemptId | start/end 证明一个实际模型 stream attempt 的存续；chunk 完全不读 |
| start 时的 `agent.session.requestHeader()` | config.model | 当前 attempt 已由 prepareCall 解析并写入的请求模型，不读 options 或模型选择设置 |
| `tools/execute` waterfall | agent、callId、rootCallId | 已排程 around-dispatch 的工具存续；原样执行一次 next、原样返回／抛出 |
| `approval/request` waterfall | agent、callId | 审批等待存续 |
| `user-questions/request` waterfall | agent、wait.callId | 前台回答等待存续；不读 questions |

phase 为 `generating`、`tool_running`、`waiting_input`、`waiting_approval`、`idle`、`unknown`。只有前两项可带 `currentModel`；工具期间的模型来自本轮已经观察到的实际 stream request，没有该证据则 null。审批／问题关联的工具不被当成正在执行；若另一个独立工具确实还在跑，仍显示 tool_running。无法把等待关联到具体工具时，保持 unknown。

插件中途加载时，已有 running Agent 未必正在生成，也可能已在等待；在捕获新 stream/tool/wait 生命周期前保持 unknown。已错过的开始事件不通过历史消息补读。只枚举到 idle 的实例可以明确显示 idle。

审批／回答请求的 signal 取消时，观察到的等待立即结束，业务 continuation 仍原样返回；这对应官方 ApprovalService 会先完成取消 race 的语义。插件卸载解除自有事件和 signal 监听，不代替回答者完成或取消请求。

所有 handler 只投影元数据；发布失败不能改变业务 next、工具结果、审批结果或用户回答。模型结束到下个明确工作开始之间的空档保持 unknown，避免把宽泛 running 位解释成生成。

## 文件契约与失效

唯一配置项 `outputDirectory` 必须是绝对路径，父目录已存在，不能经过 reparse point／目录联接。Windows 创建一个新私有子目录，只授权当前用户和 SYSTEM；若目录已存在但 ACL 不符合要求，拒绝使用，不替用户修改其权限。

每次插件挂载写自己的 `dsh-status-<pid>-<producer UUID>.json`，通过同目录新临时文件和原子 rename 发布。每 2 秒完成一次心跳后再调度下一次，不重叠；`validUntil` 为观察时间后 10 秒。正常卸载发布 sourceLive=false；写端崩溃或写入失败，由 consumer 的 PID／启动身份检查与到期时间降为未知。

```json
{
  "schemaVersion": 1,
  "producerId": "random UUID per plugin mount",
  "producerPid": 0,
  "producerStartedAt": "ISO process start estimate",
  "observedAt": "ISO",
  "validUntil": "ISO",
  "sourceLive": true,
  "complete": true,
  "instances": [
    { "id": "random UUID per live Agent object", "phase": "idle", "currentModel": null }
  ]
}
```

`complete` 表示当前 AgentRegistry 的完整已加载集合；移除的实例从下一份完整快照消失，consumer 必须替换集合，不能继续沿用旧 running。不同 runtime／producer 不能因 PID 或会话名字相似而合并。`producerStartedAt` 由进程 uptime 推算，consumer 检查启动身份时要容纳毫秒误差，不能仅用 PID 断定是原进程。`observeSnapshot()` 是随候选提供的消费投影：调用方明确传入已验证的 `producerAlive`；过期、未来时间、死亡、未完成或 sourceLive=false 都会清掉活动与模型。

研究室的 `server/dsh-runtime.mjs` 已接齐文件消费，`collectLocalAgents({ dshStatusDirectory })` 接收目录参数；store／入口参数注入由 Lead 完成。未配置目录、目录不存在或没有有效帧时，完整沿用窗口与未知状态。目录内可能保留旧 producer 文件；consumer 只接受当前身份且有效期内的 producer，同一 PID 的多个有效 producer 视为冲突，不挑“最新”或相加。

consumer 只读规范文件名、普通文件及有界 JSON（每帧 128 KiB、每帧 512 实例、最多 64 份当前新鲜候选和 32 个有效 producer）。目录流式枚举，1 秒扫描截止；先按同次进程快照中的当前 DSH 归属和文件 mtime 筛掉死亡／过期旧文件，历史文件数量不占当前候选预算，reader 不删除文件。文件 PID／producer UUID、DSH 归属或有效父链、启动时间（容许 uptime 估计 2 秒误差）、有效期与完整性必须一起成立。父进程出生晚于子进程时拒绝，避免父 PID 重用误认。它复用 `observeSnapshot()` 并再次白名单输出；PID、路径、原始身份字段不进入 API。过期、写端消失、身份不符、文件消失或来源冲突会撤下旧会话与模型，恢复窗口 fallback。

有效帧只证明该 producer 所在 runtime 的已加载会话。API 会提供已见会话的 running／idle／unknown 与当前模型，但 `openSessions`、`activeSessions`、`idleSessions`、`unknownSessions` 继续为 null、`sourceStatus=partial`；研究室按“已见”展示，不能把局部状态当成全部本机 DSH 总数。读到 frame 的 CLI fallback 会被替换，其他未覆盖 CLI 仍为未知。

## 接入

安装时建议加上 `--ignore-scripts --save-exact`，并保留供应链检查。不要为了装这个插件关闭检查或执行安装脚本。

1. 使用 DSH 桌面 Plugins 页面安装本目录，或使用**桌面安装自带**的命令管理 desktop profile：

   ```powershell
   & '<DSH desktop install>/resources/runtime/cli/bin/dsh.cmd' plugin --profile desktop add 'file:<this repo>/integrations/dsh-status-plugin'
   ```

   npm 全局 dsh 不允许管理保留的 desktop profile。bundle 中的 row 默认 `disabled: true`，安装后不会自动开始观察。

2. 在 `$DSH_HOME/profiles/desktop/cordis.patch.yml` 增加该 row 的覆盖项，保留文件中已有其它内容：

   ```yaml
   - id: studio-status-observer
     name: '@ai-moe-studio/dsh-status-observer'
     disabled: false
     config:
       outputDirectory: '<studio state>/dsh-status'
   ```

   这里只接受 `outputDirectory`；不要增加 API key、token、聊天路径或 Agent 配置。输出目录放在研究室状态目录里，使用绝对路径，不要放进仓库。研究室通过 `AgentRuntimeStore` 的 `collectOptions.dshStatusDirectory` 读取同一路径。

3. desktop 的有效配置按 bundle、profile patch、home patch 依次组合。安装源 `app.asar!/dsh/node_modules/@deepseek-ai/dsh-base/cordis.patch.yml` 在存在 `profileContext` 时启用 HMR，desktop-host 的 `runProfile()` 提供该上下文。

   普通新增／启用本候选的 row 走增量 Loader 更新；已有 leaf 的配置与实现均未变时，不会停止或重建已加载 Agent，通常可在当前进程接入，无需先重启。源码链为 `dsh-hmr/lib/index.js` 的 `reconcileProfile()` → app-boot `recomposeProfile()` → Include／EntryGroup／Entry 的 `update()`。如果同次安装使已有加载包文件改变，或同时修改／移除已有 row，HMR 会停止并重载受影响的插件；需要中断的是这些具体变化。若 HMR 未运行或报告需要 restart，则等用户正常重启或明确的安全窗口。候选自身只注册观察者，不创建 Agent、不恢复会话、不改变模型或权限。

   上述桌面 `dsh.cmd` 使用安装自带 Electron-as-Node 和 `dsh-desktop-host/lib/cli.js` 的 `runDesktopCli()`，有 reserved desktop profile 插件管理权限。已初始化 desktop profile 的正常 CLI 路径没有无条件“必须先退出”检查；提示先启动再退出仅用于 profile 尚未初始化的错误分支。

4. 正常启用后才做一次真实闭环：在用户本来就要进行的工作中看生成→工具→等待／空闲、模型及失联降未知。不要为验证补发业务任务。离线通过不能替代这一步。

## 关闭与回退

在同一 profile patch 中将该 row 设为 `disabled: true`，或删除仅为它添加的覆盖项，再从 Plugins 页面取消该 bundle。HMR 关闭时到下次正常重启才停止；不要主动重启共享宿主。卸载通过同一个桌面命令 `plugin --profile desktop remove @ai-moe-studio/dsh-status-observer` 或 Plugins 页面进行；不在运行中强删正在使用的包目录。

插件卸载会解除监听、停止自有心跳，发布失效帧。输出目录与过期文件只作本地状态记录，确认 consumer 已停止使用后，可按确切路径送回收站。它没有写入会话历史、模型选择、审批策略或业务收件，所以无需逆向业务迁移。

## 离线验证与尚未验证项

测试只导入现有安装的 Cordis event framework，AgentRegistry 是测试提供的只读对象集合；没有 boot DSH、调用模型、建立真实会话或读取历史。

插件测试需要本机已安装的 DSH runtime，并用环境变量 `DSH_STATUS_TEST_RUNTIME` 指向它。建议给 Node 测试加上内存上限。

```powershell
$env:DSH_STATUS_TEST_RUNTIME='<DSH runtime directory>'
node --test integrations/dsh-status-plugin/tests/lifecycle.test.mjs
node --test tests/dsh-runtime.test.mjs
```

生命周期测试覆盖旧 attempt、并行工具与等待、模型限定、私密字段未被读取、实例消失、过期和写端死亡、Windows 私有 ACL、原子发布，以及通过 `ctx.plugin()` 挂载和卸载。消费端测试覆盖 metadata 文件、producer 身份和父链、过期与冲突、局部覆盖，以及未启用时的窗口 fallback。未配置输出目录时，研究室只保留窗口观察，会话总数、活动和模型保持未知。

## Grok Bot 后续契约

Grok Bot 不包含在该插件内，也没有客户端 patch。产品可从其现有 live-state＋feed-liveness 投影同一 phase／失效契约；省略 activity.detail／target、awaiting 原文、handoff 指令和对话内容。当前 live-state 没有模型字段，必须由产品提供实际当前请求模型证据，缺失时保持 null。它需要产品支持受限导出，不能借启用 DSH 插件获得 Grok Bot 覆盖。
