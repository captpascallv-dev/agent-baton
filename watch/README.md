# CLI Watch / 本机 CLI 执行窗

Windows 上的只读执行观察窗：查看 agent-baton 派出的任务、真实 CLI 进程、活动新鲜度和交卷状态。它不调用模型，等待时不消耗模型额度。

当前列表只显示已确认仍在执行或正在启动的实例。仅登记、已结束及已交卷的记录保留在历史；没有足够证据就显示未知。进程退出、交卷和 Lead 验收是三个独立事实。

## 下载成品包（Windows）

普通使用者无需编译：从 [CLI Watch 0.1.0 发布页](https://github.com/captpascallv-dev/agent-baton/releases/tag/watch-v0.1.0) 下载 **[CLIWatch-0.1.0-win-x64.zip](https://github.com/captpascallv-dev/agent-baton/releases/download/watch-v0.1.0/CLIWatch-0.1.0-win-x64.zip)**，完整解压后双击 **`Start-CLI-Watch.cmd`**。

适用 Windows 10/11 x64；已附带 .NET 运行时，不需要安装 SDK、PowerShell 7、Node.js 或 Python，也不需要管理员权限。GitHub 自动提供的 `Source code` ZIP 是源码，请选择上面的成品包。

本版未做代码签名，Windows 首次运行时可能显示 SmartScreen 确认提示。

首次打开可查看已识别的本机 CLI 进程；没有任务时空列表是正常的。要关联派单记录，用记事本打开包内的 `config/default.json`，将 `claude_mailbox_roots` 填成自己含 `jobs/` 的 `claude/` 目录，或将 `mailbox_roots` 填成 Codex 插件实际使用的 `mailboxRoot`。例如：

```json
{
  "claude_mailbox_roots": ["D:/work/agent-baton/claude"],
  "mailbox_roots": ["D:/work/wake-demo/mailbox"]
}
```

在原配置中修改这两个字段即可，路径使用 `/`；保存后关闭并重新打开执行窗。窗口偏好及缓存保存在 `%LOCALAPPDATA%/AgentBatonWatch`。升级时解压到新目录，再复制旧包的 `config/default.json`，保留自己的观察目录。完整的中英文使用说明、许可证及第三方声明均随包附带。

## 从源码构建和打开

开发或修改源码需要 Windows、PowerShell 7 和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。Git 仓库只包含源码；成品包单独发布在 Releases。

```powershell
# 从 agent-baton 根目录运行
pwsh -NoProfile -File watch/Build-CliWatch.ps1
pwsh -NoProfile -File watch/Start-CliWatch.ps1
```

构建结果为 `watch/runtime/win-x64/CliWatch.App.exe`，自带运行时。SDK 不在 PATH 时，向构建脚本传 `-SdkCommand <dotnet.exe 的完整路径>`。

维护者可用 `pwsh -NoProfile -File watch/Package-CliWatch.ps1` 构建便携 ZIP（默认输出 `watch/runtime/releases/`）；它仅打包运行时、通用空配置、启动入口和许可文件，不使用本机配置或运行记录。`-SkipBuild` 可复用同版本的已编译产物。

默认启动会观察同一个 checkout 的 `claude/jobs/`。交互窗口经 Explorer 启动，独立于发起它的 agent 命令生命周期；没有系统服务或定时任务。窗口默认不抢键盘焦点，置顶、折叠、详情、历史和中英文切换可在界面操作。

窗口设置及观察缓存默认写到 `%LOCALAPPDATA%/AgentBatonWatch`，使用独立互斥锁；可传 `-DataDir` / `-InstanceName`。不读取其它程序的私有默认配置。

## 数据来源

- **公开 Claude 派单**：在 `claude_mailbox_roots` 填含有 `jobs/` 的 `claude/` 目录。支持本仓库的 `jobs/<id>/job.json`，也支持可选的 `jobs/<id>/<attempt>/job.json`。
- **Codex 交付账本**：在 `mailbox_roots` 填 `codex/` 插件配置中的实际 `mailboxRoot`，而不是插件安装目录。
- **CLI 进程**：只列已识别的 CLI。未能与任务精确关联的实例单独标为未关联，不从标题或共享工作目录猜测归属。

自定义配置时，复制 `config/example.json` 到 checkout 外，修改为自己的绝对路径：

```powershell
pwsh -NoProfile -File watch/Start-CliWatch.ps1 -ConfigPath D:/work/watch-config.json
```

支持普通环境变量路径（如 `%USERPROFILE%`）。只有默认模板的 `${REPOSITORY_ROOT}` 由启动脚本替换为当前 checkout；不要把该模板原样当作自定义配置。未配置的私人信箱不会被自动加入。

### 进程身份与状态

`claude/bin/Run-Agent.ps1` 在任务目录写 `owner.json`，包含 job、attempt、PID 和 UTC 启动刻度；终态文件保留同一身份。观察窗通过这份身份和真实子进程树关联执行者，不把包装进程还活着当作 CLI 还活着。旧任务没有这份记录时，仍可查看登记/交卷信息，但进程归属可能未知。

这里只读取允许的 JSON 元数据、文件存在/修改时间和进程身份。任务卡、日志、报告及聊天正文不进入采集结果；支持的原生适配器可比较指定任务卡的文件身份，但不保存正文。可选 Grok 活动来源只读取会话元数据、工具名和时间，不读会话正文。根、文件数量及大小均有限制；采集不足会标记部分/上次记录。

这个窗口不派单、不重试、不杀进程、不替 Lead 验收，也不修改生产交付账本。长时间安静本身不代表卡死。

## 检查

```powershell
dotnet test watch/src/CliWatch.Tests/CliWatch.Tests.csproj -c Release
pwsh -NoProfile -File watch/tools/Check-Public.ps1
pwsh -NoProfile -File watch/tests/Smoke.ps1
```

最后一项需要先构建，并使用 Node.js 22+ 运行隔离的本地假执行者，检查真实 Dispatch → job/owner/READY → 执行窗采集。它不调用真实 agent、模型或账号。首次还原 .NET 测试依赖需要访问 NuGet。

发布 ZIP 后，使用 `pwsh -NoProfile -File watch/tests/PortableSmoke.ps1 -ArchivePath <ZIP路径> -OutputDirectory <不存在的测试目录>` 检查解压包。它从含空格及中文的路径调用包内 CMD，并在不含 SDK / PowerShell 7 的 `PATH` 下验证自带运行时、默认配置、自定义任务目录和真实 WPF 渲染；结果及截图留在指定测试目录。

自动探针与截图可使用 `-ProbeOutput`、`-Screenshot`、`-ScreenshotExpanded`、`-ScreenshotHistory` 和 `-ExitAfterCapture`。探针/截图模式留在调用者进程树中，便于设置测试资源限制；截图后退出时不写窗口偏好，也不激活可见窗口。

## 来源和许可证

本目录来自一个独立维护的本机 Telephone cockpit UI fork，保留其 [MPL-2.0](LICENSE)。公开副本采用通用命名和配置，未携带旧仓历史、个人运行记录或编译依赖。来源及修改说明见 [NOTICE.txt](NOTICE.txt)，保留的 .NET 声明位于 `src/licenses/`。
