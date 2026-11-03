# tools：Lead 手边的两个小工具

| 文件 | 干什么 | 用法 |
| --- | --- | --- |
| `quota_pace.py` | 读 [`hud/`](../hud/) 写出的额度快照，列出各家剩多少、什么时候重置、按均匀消耗现在应剩多少，标出“落后”和“48 小时内重置还剩很多”。派活前看一眼，就知道哪家额度快作废了该先用。 | `python tools/quota_pace.py [快照路径] [--utc-offset 8]` |
| `extract_dialogue.py` | 把 Claude Code 会话记录（`~/.claude/projects/<目录>/<会话id>.jsonl`）提成只含对话的 Markdown：保留用户的话和 Lead 每回合最后说的话，去掉工具调用、思考、系统提醒。换新会话时给新 Lead 通读。 | `python tools/extract_dialogue.py <会话.jsonl> [输出.md] [--user 名字] [--lead 名字]` |

> `quota_pace.py` reads the HUD quota snapshot and prints each quota's even-burn pace. `extract_dialogue.py` turns a Claude Code session log into a dialogue-only Markdown for handing over to a fresh session.

两者都只读本地文件、不联网、不调模型。会话记录里可能有你的私人内容，提出来的正文别直接公开。

## License

MIT，见仓库根目录 LICENSE。
