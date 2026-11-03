# 本机 CLI Watch 桥接

此后端把 CLI Watch 0.1.0 的真实观察结果投影到研究室。原轻量窗与观察中的 CLI 保持独立；后端只写配置指定的独立快照文件。适配的版本是 CLI Watch 0.1.0。

## 构建与启动

桥接需要 .NET 8 SDK。

1. 从 CLI Watch 0.1.0 发布页（https://github.com/captpascallv-dev/agent-baton/releases/tag/watch-v0.1.0）下载 `CLIWatch-0.1.0-win-x64.zip` 并解压。
2. 用解压文件夹作为 `CliWatchRuntimeDirectory` 构建桥接：

    dotnet build bridge/StudioBridge.csproj -c Release -m:1 -p:UseSharedCompilation=false -p:CliWatchRuntimeDirectory="<解压后的 CLIWatch-0.1.0-win-x64 文件夹>"

3. 外置配置里 `cliWatchRuntimeDirectory` 填该文件夹，`cliWatchConfig` 填该文件夹下的 `config/default.json`。

会编译的人也可以用 agent-baton 源码里的 `Build-CliWatch.ps1` 自己构建，把构建输出目录当作上面的解压文件夹。

产物：bridge/bin/Release/net8.0-windows/AiMoeStudio.Bridge.exe。构建引用公开版的五个组件：`CliWatch.App`、`CliWatch.Contracts`、`CliWatch.Normalization`、`CliWatch.Projection`、`CliWatch.Views`，并从本仓库 `bridge/collection/` 编译桥接专用的 `CliWatch.Collection.dll`。不需要另取 CLI Watch 源码或重新构建它；来源与 MPL-2.0 许可见 [collection/README.md](collection/README.md)。保留下载解压目录作为独立输入，桥接构建输出与它分开。

用 config.example.json 的字段建立外置配置，运行：

    node server/live-server.mjs --config "<external studio config.json>"

所有相对路径均相对于外置配置所在目录。配置字段：

| 字段 | 用途 |
| --- | --- |
| port | 本机端口，默认 4173，仅绑定 127.0.0.1 |
| cliWatchRuntimeDirectory | CLI Watch 0.1.0 解压文件夹（含六个 CliWatch DLL），同时作为快照写入禁区 |
| cliWatchConfig | 该文件夹下的 config/default.json，只读 |
| bridgeExecutable | 已构建桥接 exe |
| snapshotPath | 独立 JSON 输出，放在静态文件与 CLI Watch 源目录之外 |
| refreshSeconds | 每次采集完成后再等待的秒数，默认 5 |
| staleAfterSeconds | 判断真实帧过期的秒数，最小 60，默认 60 |

HTTP 启动后立即可用，首次采集在后台进行。GET /health 返回 app=ai-moe-studio、schemaVersion=1、mode=live；演示模式为 mode=demo。GET /api/snapshot 返回展示数据。静态根仅 dist/client，支持前端页面路由。GET/HEAD 之外的请求、异源 Origin/Host、路径穿越和源码/配置文件请求会被拒绝。

Node 每个服务只启动一个隐藏桥接子进程；端口占用时不启动子进程。正常关闭结束自己的桥接，Windows 强制结束 Node 后，桥接也通过父进程句柄取消采集并自行退出。

## 观察与展示口径

桥接长期保留一个 MailboxCollector、IdentityNormalizer、CliWatchProjector 和 RefreshService。MailboxCollector 的 promptBindingDirectory 为 null；没有 prompt 绑定写入，也没有重放单次 probe 的轮询。采集严格串行，保留活动基线、预算游标和上一帧。

isCurrent 直接来自 CurrentList.IsDefaultCurrent。每条工作的 isHistorical 来自 StatusLanguage.IsHistoricalWork。projectLabel、statusLabel、statusBasis 直接使用 CLI Watch 的 ProjectLabels/CliWatchStatus。状态轴、质量和稳定 ID 直接沿用投影结果。交卷、进程退出与原 Lead 验收是不同的字段。

source 的状态含义：

- starting：尚无本轮真实帧。
- live：已取得新帧，来源质量 Fresh。
- degraded：新帧含来源缺口，或采集失败而保留上次真实帧。
- offline：桥接不可用且没有真实帧，或最后真实帧超过过期门槛。

全局 Conflicting 不覆盖每条工作自身的质量。历史问题范围直接复用 CurrentList.IsHistoricalIssue。快照失败保留原 collectedAt/publishedAt，重启失败也保留上次已发布帧；只有成功的新采集更新这两个时间。损坏或缺失的配置会显示不可用，不会变成成功的空列表。

输出只含公开 DTO 的展示字段；artifact 只有 label/state。原始 facts、命令行、prompt、transcript、evidence、导航目标和凭据不序列化；展示文字中的 Windows 绝对路径与常见凭据格式被隐藏。Node 再次按白名单投影。

## 验证

同轮身份关联在 LinkedIdentityNormalizer 中进行：先沿用 CLI Watch 0.1.0 的 IdentityNormalizer，再按已采集槽登记的 native_mailbox_job_id、精确 attempt、相同 workspace/outbox 关联原生登记。原始登记证据及两个 job key 保留，状态统一交给 CliWatchProjector 计算，不使用名称、编号或时间相近来推断。模型、交付与过程事实因此进入同一工作；READY 本身不会变成验收通过。

缺链接、错轮次、来源/进程身份冲突、多目标或多槽争同一原生轮次时保持分开。长期循环只缓存明确身份模板以跨越预算轮换；没有复用上一帧的存活进程或交卷事实，也没有额外读取私信或业务文件。

采集组件的性能优化只复用同一轮内的文档索引、进程分类、Grok 会话目录名索引和有界读取缓冲。每轮仍读取来源文件、PID/启动时间与活动计数；不跨轮缓存文件内容或运行/休眠状态，原完成后等待 5 秒的刷新间隔保持。

身份合并的自检：

    AiMoeStudio.Bridge.exe --identity-self-test

    AiMoeStudio.Bridge.exe --self-test
    node --test tests/live-server.test.mjs

采集性能改动的定向检查使用合成夹具，覆盖新增/更改/删除、半写/读取失败恢复、同 key 跨根、进程退出/PID 复用、Grok 目录刷新及字节预算。使用同一 CLI Watch 0.1.0 解压目录构建，给测试传入一个专用的新夹具目录：

    dotnet build bridge/tests/CollectorBoundaryTests.csproj -c Release -m:1 -p:UseSharedCompilation=false -p:CliWatchRuntimeDirectory="<CLI Watch 0.1.0 解压目录>" -o "<独立测试输出目录>"
    <独立测试输出目录>/CollectorBoundaryTests.exe "<专用的新夹具目录>"

在构建命令加 `-p:UseInstalledCollector=true` 可让同一组检查改用下载包里的原采集 DLL。检查时为进程设置 2GB 内存上限；夹具不使用真实聊天或运行配置。

self-test 使用内存夹具验证运行、明确等待、交卷未验收、未知、当前/历史、私密字段剔除、上一帧复用、失败/重启保留时间与损坏配置；Node 测试验证静态/API、缺失与过期、只读来源和路径边界、并发不重复启动。跑 Node 测试时请自行加上内存上限。

真实只读短采集可用 --once，输出到独立目录：

    AiMoeStudio.Bridge.exe --config "<CLI Watch config>" --snapshot "<independent snapshot.json>" --once

常驻数据放在外置配置指定的状态目录，不要和仓库或 CLI Watch 的输入目录放在一起。
