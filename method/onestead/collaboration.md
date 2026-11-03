---
id: collaboration
type: handbook
title: 协作方式与规则入口
sensitivity: internal
---

# 协作方式与规则入口

这里只给入口，不抄规则正文。规则冲突时以正本为准。

## 规则正本

| 内容 | 位置 |
|---|---|
| 各 agent 共用的全局规则 | （例如 `~/.codex/AGENTS.md`，Claude 侧用 `~/.claude/CLAUDE.md` 导入它） |
| 某一家 agent 的专属补充 | （例如 `~/.claude/CLAUDE.md` 里的补充段） |
| 各项目自己的规矩 | 项目目录里的 `AGENTS.md` / `CLAUDE.md`，路径见各项目的“入口” |

要点（细节以正本为准）：对外发送、推送、发布、花钱、永久删除、改凭据、改真实数据由主人最终批准，其余工作先做完，批准放在最后一步；删除走回收站；凭据只写放在哪、叫什么，不写值。

## 派活的几条路

| 谁派 | 怎么派 | 说明 |
|---|---|---|
| Claude Code 当 Lead | 写任务卡 → `claude/bin/Dispatch.ps1` 派给执行者 → 本地守候叫醒 → 验收 | agent-baton 的 `claude/README.md` |
| Codex App 当 Lead | `codex-mail-wake` 插件登记、收件、叫醒 | agent-baton 的 `codex/README.md` |
| Lead 和 Lead 之间 | 信箱：商量、交接、回执 | agent-baton 的 `mailbox/README.md` |

派活前先看额度快照（`hud/` 写出的 `quota-snapshot.json`），按任务难度、剩余额度和重置时间选执行者；主人明确指定的以主人为准。

## Onestead 怎么用

- 公司、项目、成员、决定这些事实以 Onestead 为准；各 agent 的入口文件和记忆里只放指针，不复制一份。
- 通过 Onestead 读写，不直接翻数据仓库的文件。能接 MCP 的用 MCP；不能接的用命令行 `onestead <子命令> --as <你的身份>`。两个入口的权限规则完全一样。
- 每次写入都会留下一次 git 提交，作者是你的身份。多个对话共用一个身份时（比如同一家 agent 的不同会话），写入时附上署名（MCP 的 `signed_as`，命令行的 `--signed-as`）。
- 接手一个项目：先 `get_project` 读清楚，再用 `handover_role` 把自己登记为担任者。关键角色（项目 Lead 等）只能先提交接任申请，主人点头后才生效。
- 格式说明见 `FORMAT.md`。
