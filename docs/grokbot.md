# 接入 Grok Bot：把一群云端/本地 bot 当成执行者

> How to use Grok Bot agents as executors for a lead agent, via the official Cursor BDK extension. This repo does not ship a Grok Bot client; it documents the integration pattern we use.

Grok Bot 是官方出的 bot 应用。一个账号下可以建很多个 bot，每个 bot 有自己的设定和长期对话。Cursor 的官方开发包 [`@cursor/bdk`](https://www.npmjs.com/package/@cursor/bdk) 带了一个 `cursor-grokbot-agents` 扩展，可以从命令行直接给某个 bot 发消息、收回复。

**本仓库不包含调用 Grok Bot 的代码。** 官方工具已经够用，我们只在上面加了一层很薄的约定。这篇文档讲清这层约定，你可以按自己的情况实现。

## 适合什么样的活

我们的用法是"司令 + 士兵"：
- 建一个**司令 bot**，专门接 Lead 的派单；
- 司令按任务卡叫起若干个**士兵 bot** 并行干活，最后由司令收卷、写 `READY.json`。

这种结构适合能拆成多份、彼此独立的活，比如批量核对、多路探索、同一问题让几个 bot 赛马。士兵并行起来很快，准确率要靠 Lead 按正常标准抽查。

**改代码这类活建议让 bot 在本地干。** 云端共用机器在高峰期可能很卡。

## 接入步骤

### 1. 建一个本地 BDK 项目（不用全局安装）

```json
// package.json
{ "private": true, "type": "module", "dependencies": { "@cursor/bdk": "<固定一个版本>" } }
```

```ts
// bot/agent.ts：工具允许列表留空，只挂 Grok Bot 扩展提供的工具
import { defineAgent } from "@cursor/bdk";
export default defineAgent({ tools: [] });
```

```ts
// bot/extensions/grokbot.ts：只列你要联系的 bot
import cursorGrokBotAgents from "@cursor/bdk/extensions/cursor-grokbot-agents";

export default cursorGrokBotAgents({
  pollIntervalMs: 5000,  // 默认 750ms。派单动辄等几十分钟，放慢一点能少发很多请求
  agents: [
    { name: "<Grok Bot 里显示的准确名字>", description: "接派单、分派士兵、收卷" },
  ],
});
```

**名字必须和 Grok Bot 里显示的一字不差。** 写错不会报错，而是会在你的账号里新建一个空 bot。

### 2. 登录

```bash
npx bdk login
```

浏览器登录后，bdk 会在本机生成并保存密钥，不用手动创建。这个密钥可以在 Cursor 的仪表盘里吊销。不要把它写进任务卡、日志或仓库。

### 3. 用扩展提供的工具

- `grokbot__list`：列出可以联系的 bot。
- `grokbot__ask`：给某个 bot 发一条消息，可以顺带等一会儿回复。
- `grokbot__check`：等这个 bot 的新回复，单次最长 600 秒。

可以用 `bdk call <工具名>` 在脚本里直接调用这些工具，不需要另外起一个模型来转发。

## 我们加的那层约定（重点）

**每个 bot 在一个账号下只有一条对话。** 你在 App 里手动发的消息、Codex 的 Lead 发的消息、Claude 的 Lead 发的消息，全都进这一条，回复也混在一起。所以：

1. **每张派单都带编号**，比如 `[job:report-0412]`。任务卡里写明：凡是关于这单的回复，都必须以这个编号开头；做完回复 `[job:…] 交卷`，卡住回复 `[job:…] 卡住` 并说明原因。
2. **守候只认自己的编号。** 一个 Lead 同时派了几单时，每单各挂一个守候，各找各的编号，互不干扰。
3. **`check` 会推进本机的"已读位置"。** 两个进程同时对同一个 bot 调 `check`，会把对方要看的回复"读掉"。所以要做两件事：
   - 同一个 bot 的 `ask` 和 `check` 用文件锁串行；
   - 读到的回复先追加进这个 bot 的共享记录，各单的守候再从记录里找自己的编号。
4. **守候和 Lead 的叫醒机制接在一起。** 守候是一个后台程序，读到"交卷"或"卡住"就退出；退出码 0 表示有结果，2 表示到点没结果、要重挂。这样就能复用 [`claude/`](../claude/) 的叫醒方式：后台命令一退出，Lead 就醒。
5. **交卷仍以文件为准。** 让司令最后在交卷目录写 `READY.json`，Lead 验收看实际产物，不只看聊天里的"交卷"二字。

## 踩过的坑

- **"发送失败"不等于没送到。** `ask` 报网络错误，可能是在"等回复"那一步失败的，消息其实已经发出去了。改派给别人之前，先查共享记录里有没有这单的"收到"。如果确实没送到才改派，而且改派后要给原 bot 发一句"这单已改派，不用接"，不然两边会做同一份活。
- **守候拿着锁等太久，会饿死要补发的消息。** 我们每次 `check` 只等 120 秒左右就放锁，让一下再继续，`send` 也更频繁地抢锁。
- **bot 忙的时候可能不回"收到"。** 发送端可以隔一段时间自动重发同一条带编号的消息。bot 按编号去重即可。
- **卡住必须马上报，不能静默。** 我们在每张卡里都写明：卡住立刻回 `[job:…] 卡住`，一次不报，这个士兵就不再派活。静默一个多小时的 bot 比报错的 bot 难处理得多。
- **不要用"客户端不在就拉起"的看门狗。** 它分不清是崩溃了，还是你主动关掉的，会反复把客户端拉起来，严重时会把机器拖死。如果一定要做看门，只在"确实有单子在排队"时才拉起，并设冷却时间和每日上限。

## 不包含的

本仓库不包含：
- Grok Bot 客户端；
- 我们自己的派单脚本、bot 名单、账号配置或运行记录；
- 任何密钥。

上面的约定只是一种用法，官方工具的行为以 `@cursor/bdk` 的文档为准。
