# Claude Code 状态钩子 / Claude Code status hook

让研究室看到 Claude Code 会话在运行还是休眠，以及运行时的模型。没装这个钩子时，研究室只能看到窗口开没开。

Shows the studio whether a Claude Code session is running or sleeping, and which model it is using while it runs. Without this hook, the studio only sees whether a window is open.

## 隐私 / Privacy

只写状态、模型、时间和会话进程号。不写消息内容、会话原始编号和路径。

It writes only the state, the model, the time and the session process id. It does not write message content, the raw session id, or any path.

输出在研究室状态目录下的 `claude-status/`。每个会话一个文件，会话结束后会删掉自己的文件。

Files go in `claude-status/` under the studio state directory. One file per session; the session's own file is removed when the session ends.

已经开着的会话不用重开，下次有动静就会接上。

An already open session does not need to be restarted. The next event will connect it.

## settings.json

把下面的路径换成你自己的。`command` 加 `args` 是 exec 形式，9 条全部 `"async": true`，`timeout` 为 10。

Replace the placeholder paths. Each entry is exec form (`command` plus `args`), `"async": true`, and `timeout` 10. There are 9 entries.

- SessionStart、Stop、StopFailure、Notification（`idle_prompt`）→ `idle`
- UserPromptSubmit、PostToolUse → `running`
- Notification（`permission_prompt|elicitation_dialog`）→ `waiting`
- PostModelSwitch → `model`
- SessionEnd → `end`

```json
{
  "hooks": {
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python",
            "args": ["-I", "-S", "C:/REPLACE/ai-moe-studio/integrations/claude-code-hooks/claude_status_hook.py", "C:/REPLACE/ai-moe-studio-state/claude-status", "idle"],
            "async": true,
            "timeout": 10
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python",
            "args": ["-I", "-S", "C:/REPLACE/ai-moe-studio/integrations/claude-code-hooks/claude_status_hook.py", "C:/REPLACE/ai-moe-studio-state/claude-status", "idle"],
            "async": true,
            "timeout": 10
          }
        ]
      }
    ],
    "StopFailure": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python",
            "args": ["-I", "-S", "C:/REPLACE/ai-moe-studio/integrations/claude-code-hooks/claude_status_hook.py", "C:/REPLACE/ai-moe-studio-state/claude-status", "idle"],
            "async": true,
            "timeout": 10
          }
        ]
      }
    ],
    "Notification": [
      {
        "matcher": "idle_prompt",
        "hooks": [
          {
            "type": "command",
            "command": "python",
            "args": ["-I", "-S", "C:/REPLACE/ai-moe-studio/integrations/claude-code-hooks/claude_status_hook.py", "C:/REPLACE/ai-moe-studio-state/claude-status", "idle"],
            "async": true,
            "timeout": 10
          }
        ]
      },
      {
        "matcher": "permission_prompt|elicitation_dialog",
        "hooks": [
          {
            "type": "command",
            "command": "python",
            "args": ["-I", "-S", "C:/REPLACE/ai-moe-studio/integrations/claude-code-hooks/claude_status_hook.py", "C:/REPLACE/ai-moe-studio-state/claude-status", "waiting"],
            "async": true,
            "timeout": 10
          }
        ]
      }
    ],
    "UserPromptSubmit": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python",
            "args": ["-I", "-S", "C:/REPLACE/ai-moe-studio/integrations/claude-code-hooks/claude_status_hook.py", "C:/REPLACE/ai-moe-studio-state/claude-status", "running"],
            "async": true,
            "timeout": 10
          }
        ]
      }
    ],
    "PostToolUse": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python",
            "args": ["-I", "-S", "C:/REPLACE/ai-moe-studio/integrations/claude-code-hooks/claude_status_hook.py", "C:/REPLACE/ai-moe-studio-state/claude-status", "running"],
            "async": true,
            "timeout": 10
          }
        ]
      }
    ],
    "PostModelSwitch": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python",
            "args": ["-I", "-S", "C:/REPLACE/ai-moe-studio/integrations/claude-code-hooks/claude_status_hook.py", "C:/REPLACE/ai-moe-studio-state/claude-status", "model"],
            "async": true,
            "timeout": 10
          }
        ]
      }
    ],
    "SessionEnd": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python",
            "args": ["-I", "-S", "C:/REPLACE/ai-moe-studio/integrations/claude-code-hooks/claude_status_hook.py", "C:/REPLACE/ai-moe-studio-state/claude-status", "end"],
            "async": true,
            "timeout": 10
          }
        ]
      }
    ]
  }
}
```
