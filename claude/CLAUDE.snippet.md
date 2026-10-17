<!-- 把下面这段放进你的 CLAUDE.md（全局 ~/.claude/CLAUDE.md 或项目级），路径改成你的实际位置。 -->

## 调用外部执行

我说"调用外部执行"或让你当 Lead 时：你只负责判断、写任务卡和验收，执行交给外部 agent CLI。

1. 按 `<安装目录>/templates/TASK_CARD.md` 写任务卡，交卷目录（Outbox）每个任务独立。派发拒绝已含 `READY.json` 的 Outbox 并保留旧交付；新 JobId 也要使用新的交卷目录。
2. 派单：`pwsh -NoProfile -File "<安装目录>/bin/Dispatch.ps1" -Executor <名字> -Workspace "<工作目录>" -Outbox "<交卷目录>" -PromptFile "<任务卡>" -Task "<任务名>"`。可用执行者见 `<安装目录>/executors.json`；使用前确认参数、模型和权限符合用户授权，样例中的自动批准参数需要用户显式选择。续接执行者已有会话时加 `-ResumeSessionId`（需要文件路径再加 `-ResumeSessionPath`）；该执行者要配置 `args_on_resume`，否则派单会拒绝，不会悄悄新开。
3. 把输出的 `WATCH:` 命令用 Bash/PowerShell 工具的**后台模式**（run_in_background）运行，然后结束本轮。不要轮询、不要 sleep 等待。
4. 本方法依赖 Claude Code 后台命令退出后通知原会话的行为，先按你所装版本确认。退出码 0 = 已交卷（READY.json），2 = 超时，3/4 = 执行者没交卷就结束了。默认每轮守候 29 分钟是基于贡献者报告的约 30 分钟后台期限；退出码 2 而执行者进程还活着，可再挂一轮。同时派了多单时，可以在 PowerShell 中以数组调用 `Wait-Many.ps1` 盯全部（见 README 示例）。本地守候不调用模型；通知处理、验收和续挂仍使用正常 Claude 会话用量。
5. 验收：核对实际产物（不只看 REPORT.md 自报），抽查内容，写 `ACCEPTANCE.md`，不合格就写退修卡重新派。Claude 守候脚本每次遇到终态就退出，不自带跨重挂的永久去重；处理 READY 后把该任务的 Outbox/PID 从下一轮 `Wait-Many` 数组移除，否则重挂仍会看到旧 READY。
6. 会话重启后先跑 `<安装目录>/bin/Pending.ps1`，把还没交卷的任务的守候重新挂上。不再跟进的任务，在对应 `jobs/<id>/` 放一个 `CLOSED.txt`，Pending 会跳过。
