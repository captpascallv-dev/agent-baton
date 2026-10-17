# agent-mailbox

Lead 和 Lead 之间的对等信箱。一封信一个 Markdown 文件，写完原子改名；对方用本地脚本等来信。等待不调用模型。

> A peer mailbox for lead agents. One Markdown file per note, renamed into place, with a local wait that exits when mail arrives. Waiting makes no model call.

这不是 [`../claude/`](../claude/) 的派单。派单把任务卡交给执行者，并守着 `READY.json`。信箱只在 Lead 之间传话：不启动执行者，不看交卷目录，也不代替验收。

## 目录

```
bin/Send-Mail.ps1        发信：临时文件写完后改名
bin/Read-Mail.ps1        读未读信，并移到 read\
bin/Wait-Mail.ps1        有未读信就退出；超时退出码 2
bin/Mailbox-Common.ps1   解析根目录和参与方，被上面三个脚本点源
parties.example.json     参与方样例（复制到信箱根目录的 parties.json，或用参数传入）
tests/Smoke.ps1          不依赖 Pester 的收、发、等、归档检查
```

## 和派单的区别

| | `claude/` 派单 | `mailbox/` 信箱 |
| --- | --- | --- |
| 发给谁 | 执行者 CLI | 另一个 Lead |
| 内容 | 任务卡，要求交卷 | 一封对话 |
| 结束信号 | 交卷目录里的 `READY.json` | 收件箱里出现一封未读信 |
| 叫醒 | `Wait-Delivery.ps1` | `Wait-Mail.ps1` |
| 读完之后 | Lead 验收产物 | `Read-Mail.ps1` 把信移到 `read\` |

两边可以同时用：派活走派单，两个 Lead 互相问进度走信箱。

## 安装

1. 需要 PowerShell 7（`pwsh`）。
2. 选一个空目录当信箱根。不要把根写进脚本。
3. 参与方用字母或数字开头，只含字母、数字、`_` 和 `-`。两方或多方都可以。

根目录：`-Root`，否则环境变量 `AGENT_MAILBOX_ROOT`。

参与方，按这个顺序，用到一个就停：

1. `-PartiesFile`（JSON：`{"parties":["alpha","beta"]}`）
2. `-Parties`（逗号分隔）
3. 环境变量 `AGENT_MAILBOX_PARTIES`
4. 根目录下的 `parties.json`

`-Root` 优先于环境变量。正好两方时可以省略 `-To`，信会投给另一方。三方及以上必须写 `-To`。

## 约定

每封信是收件人目录 `to-<名字>\` 下的一个 `.md`。文件名：

```text
<UTC 时间>_<发件人>_<标题>.md
```

时间格式 `yyyyMMddTHHmmssfffZ`。标题里的空格和 `\/:*?"<>|` 会收成 `-`，最长 40 个字符。

正文前的元信息顺序固定：

```text
---
from:
to:
subject:
created_at_utc:
thread:
reply_to:
needs_reply:
---
```

`needs_reply` 是 `true` 或 `false`。`thread` 和 `reply_to` 可以空着。脚本先把完整内容写入同目录的临时文件，再改成正式文件名。

`Read-Mail.ps1` 只读收件箱目录这一层的 `.md`，读完移到 `to-<名字>\read\`。`-Peek` 只看不移。没有未读信时打印 `NO-MAIL`，退出码 0。

`Wait-Mail.ps1` 看到未读信就退出码 0，到点还没有就退出码 2。它不把信移走。默认每轮 29 分钟、每 15 秒看一次，和 Claude 侧守候的习惯对齐；这个期限取决于你自己的后台命令限制。

## 用法

```powershell
pwsh -NoProfile -File mailbox/bin/Send-Mail.ps1 -Root D:\mail -Parties alpha,beta `
  -From alpha -Subject "进度" -Body "卡片写好了。" -Thread demo -NeedsReply

pwsh -NoProfile -File mailbox/bin/Wait-Mail.ps1 -Root D:\mail -Parties alpha,beta -Me beta -TimeoutMinutes 29

pwsh -NoProfile -File mailbox/bin/Read-Mail.ps1 -Root D:\mail -Parties alpha,beta -Me beta
```

也可以把正文放在文件里：`-BodyFile <路径>`。

## 安全提醒

- 信箱目录里的信是普通文件。谁能读这个目录，谁就能读信。不要在信里写凭据。
- 参与方名字决定子目录名。脚本拒绝路径分隔符和其他文件名危险字符。
- 这套脚本不登录、不发网络请求。

## 冒烟

从仓库根或本目录运行：

```powershell
pwsh -NoProfile -File mailbox/tests/Smoke.ps1
```

在临时目录里检查收、发、等、窥视、归档、两方推断、环境变量和参数优先级，以及拒绝未知参与方。不依赖 Pester。

## License

MIT，见仓库根目录 LICENSE。
