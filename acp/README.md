# acp-client

用 Agent Client Protocol（ACP）驱动一个你指定的执行者。协议是 stdio 上按行分隔的 JSON-RPC：先 `initialize`，再 `session/new` 或 `session/resume`，然后 `session/prompt`，边收 `session/update` 边回答 `session/request_permission`。

> A generic ACP client. You pass the agent command. Nothing here launches a vendor CLI by default, and permission requests are denied unless you opt in.

适合已经会说 ACP 的执行者：编辑器或 agent 以无界面子进程提供 ACP，而不是只有自己那套命令行参数的 CLI。普通 CLI 仍按 [`../claude/executors.example.json`](../claude/executors.example.json) 直接配参数。ACP 客户端可以当成其中一条 `command`，由派单脚本把它拉起来。

客户端不写 `READY.json`。任务卡里仍然要求执行者把产物和 `READY.json` 放进交卷目录。这里只负责把协议跑完。

## 目录

```
acp_client.py       客户端，只用 Python 标准库
tests/fake_agent.py 冒烟用的假 ACP 子进程，不调用模型
tests/smoke.py      initialize → 会话 → prompt → 收流 → 权限请求
```

## 参数

| 参数 | 含义 |
| --- | --- |
| `--agent` | 要启动的程序。必填。没有默认命令 |
| `--agent-arg` | 给这个程序的一个参数，可重复。不经 shell |
| `--cwd` | 会话工作目录，必须已存在 |
| `--log-dir` | 日志目录。事件、转写、权限、结果都写在这里 |
| `--prompt-file` | UTF-8 提示。正式跑任务时必填 |
| `--model` / `--effort` | 想设置的模型和档位 |
| `--model-config-id` / `--effort-config-id` | 配置项 id，默认 `model` 和 `reasoning_effort` |
| `--resume-session` | 续接已有 ACP 会话。失败不会悄悄新开 |
| `--permission-mode` | `deny`（默认）或 `approve` |
| `--probe` | 只建会话并打印对方公布的配置，不发 prompt |
| `--rpc-timeout` | 握手和改配置的秒数，默认 120 |
| `--timeout-min` | 等 `session/prompt` 的分钟数，默认 180 |

模型和档位只在会话结果的 `configOptions` 里有对应 id 时才调用 `session/set_config_option`。调用后读取返回的 `currentValue`，不一致就失败。对方没有公布该项时，不发送设置，并报错。

日志：

- `events.jsonl`：收发的协议消息。不记录环境变量。
- `transcript.md`：模型文本、工具调用摘要、权限决定。
- `permissions.jsonl`：每一次权限请求，含工具标题、原始输入、选择和结果。
- `result.json`：会话 id、停止原因、回读后的配置、计数。出错时退出码为 1。

## 权限

默认 `deny`：在请求给出的选项里选 `reject_once`，没有再选 `reject_always`。如果只有允许类选项，就回复 `cancelled`，不挑允许。

`approve` 会按 `allow_once`、`allow_always` 的顺序自动选一个允许项。这等于让执行者在你的机器上跑它请求的工具，包括命令、改文件和读取任务能碰到的数据。只在你明确接受这个风险时使用。两种模式都会把每一次请求记到 `permissions.jsonl`。日志里的工具输入和任务本身一样敏感。

拒绝或取消只回答这一次请求，不会由客户端把执行者杀掉。执行者可以停下来，也可以再问一次。

## 示例

DeepSeek DSH 只是一个会说 ACP 的例子，不是默认值。不带 `--agent` 时客户端直接退出，不会去找任何厂商程序。

```powershell
python acp/acp_client.py --agent dsh --agent-arg --profile --agent-arg acp `
  --cwd D:\work\demo --prompt-file D:\work\demo\CARD.md --log-dir D:\work\demo\acp-log `
  --model <模型> --effort <档位> --permission-mode deny
```

接到 Claude 派单时，可以在 `executors.json` 里加一条，把 `{card}` 和 `{jobdir}` 填进上面的参数。派单仍看交卷目录里的 `READY.json`，不看 `result.json`。

## 冒烟

```powershell
python acp/tests/smoke.py
```

假 ACP 子进程走完 initialize、新建会话、设置并回读配置、prompt、文本流和一次权限请求。另有自动批准、续接、对方不公布配置、回读不一致，以及 `--probe`。不调用真实模型或厂商 CLI。

## License

MIT，见仓库根目录 LICENSE。
