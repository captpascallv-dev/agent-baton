CLI Watch - Windows x64 portable edition
========================================

中文说明见下面。English instructions follow.

适用：Windows 10/11 x64。自带 .NET 运行时，无需安装 .NET SDK、
PowerShell 7、Node.js 或 Python，也不需要管理员权限。
本版未做代码签名，Windows 首次运行时可能显示 SmartScreen 确认提示。

1. 将 ZIP 完整解压到一个可写目录，不要直接在压缩包里运行。
2. 双击 Start-CLI-Watch.cmd 打开执行窗。
3. 初次启动会显示可识别的本机 CLI 进程；没有任务时空列表是正常的。

关联 agent-baton 的任务：用记事本打开 config/default.json，填写自己的目录。
claude_mailbox_roots 指向含 jobs/ 的 claude/ 目录；mailbox_roots 指向
Codex 插件配置中的实际 mailboxRoot，而不是插件安装目录。例如：

  "claude_mailbox_roots": ["D:/work/agent-baton/claude"],
  "mailbox_roots": ["D:/work/wake-demo/mailbox"]

使用 JSON 双引号，路径推荐用 /；修改配置后关闭并重新打开本窗口。
窗口偏好和观察缓存位于 %LOCALAPPDATA%/AgentBatonWatch。
升级时解压到新目录，再复制旧包的 config/default.json；不要覆盖自己的配置。

程序只读观察，不派单、不重试、不杀进程、不替 Lead 验收。
它不调用模型；运行已有 CLI 的账号、工具和任务配置仍由使用者自行管理。

English
-------
Requires Windows 10/11 x64. The .NET runtime is included. No SDK, PowerShell 7,
Node.js, Python or administrator privileges are needed to run this package.
Extract the entire ZIP, then double-click Start-CLI-Watch.cmd.
This portable release is unsigned; Windows SmartScreen may ask for confirmation.

Recognized local CLI processes appear immediately. To associate dispatched
jobs, edit config/default.json in Notepad and restart CLI Watch. Set
claude_mailbox_roots to your claude/ directory containing jobs/, and/or
mailbox_roots to the actual Codex plugin mailboxRoot. Empty task lists before
configuration are normal. Window preferences/cache are stored under
%LOCALAPPDATA%/AgentBatonWatch. Keep your configuration when upgrading.

Source and license
------------------
CLI Watch is MPL-2.0; see LICENSE and NOTICE.txt. Bundled runtime notices are
under licenses/. This archive's RELEASE.txt identifies its version and the
corresponding public source tag. Source and documentation:
https://github.com/captpascallv-dev/agent-baton/tree/main/watch
