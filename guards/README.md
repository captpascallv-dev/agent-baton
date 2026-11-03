# guards：给执行者加护栏

一群 agent 同时在一台电脑上跑测试、编译，最怕的不是哪张卡写错，而是一批没设上限的测试把整机内存吃光：编辑器、聊天工具、所有在跑的会话一起崩，最后只能强制关机。贡献者遇到过一次：一批 pytest 吃到约 72 GB（机器 32 GB），整机卡死。这里的两个文件就是事后装上的护栏，之后再没出过事。

> guards/ holds two small Windows guards: `capped_run.py` runs any command tree inside a Windows Job with a hard memory cap and a wall-clock timeout; `guard_test_runs.py` is a PreToolUse hook that refuses test commands not wrapped by it.

## 两个文件

| 文件 | 干什么 |
| --- | --- |
| `capped_run.py` | 内存上限运行器。命令先挂起创建、放进 Windows Job，读回上限确认无误才放行；它派生的所有子进程都在同一个 Job 里。碰到内存上限或超时，整棵进程树一起结束，不留残留进程。 |
| `guard_test_runs.py` | 命令执行前的拦截钩子。拦的是没套上限、直接跑测试的命令：`-m pytest`、命令开头的 pytest/vitest/jest、`npx vitest`、`node --test`、`npm test`。命令里带 `capped_run.py` 的放行；正文里只是提到 “pytest” 这个词的不拦。 |

## 用法

```powershell
pip install pywin32
python guards/capped_run.py --mem-gb 2 --timeout 900 --log D:\logs\unit.log -- pytest -q tests
```

- 退出码：原命令自己的退出码；超时 124；碰到内存上限 137；运行器本身出错 125。
- 单次上限默认最多 6 GB，用环境变量 `CAPPED_RUN_MAX_GB` 调。建议同时最多 3 个上限进程，上限合计不超过机器内存的四分之一。

## 装成钩子

Claude Code（`~/.claude/settings.json`）：

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "Bash|PowerShell",
        "hooks": [{ "type": "command", "command": "python -X utf8 <安装目录>/guards/guard_test_runs.py --client claude" }] }
    ]
  }
}
```

`--client` 还支持 `codex`、`grok`、`cursor`，拒绝时按对应客户端的钩子格式输出。脚本始终以 0 退出：拒绝是协议回答，不是崩溃。各家钩子配置的写法以你所装版本的官方文档为准。

## 任务卡里怎么写

钩子拦得住 Lead 自己，拦不住没装钩子的执行者。所以任务卡里也要写一句：“每条测试、构建命令套 `python <安装目录>/guards/capped_run.py --mem-gb 6 --timeout 3600 --log <交卷目录里的日志> -- ...`”。

## License

MIT，见仓库根目录 LICENSE。
