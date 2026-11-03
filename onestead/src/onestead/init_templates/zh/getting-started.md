---
id: getting-started
type: project
title: 开始使用
state: 进行中
goal: "接上一个伙伴，并读完入职说明。"
roles:
  - role: 主人
    who: {{owner_name_yaml}}
    since: {{today}}
summary_as_of: {{today}}
sensitivity: internal
updated: {{today}}
---

# 开始使用

## 目标

接上一个伙伴，并读完入职说明。

## 现状

`onestead init` 在 {{today}} 建好了这个仓库。配置文件指向这里。还没有伙伴接上。

## 下一步

打开本机网页：`python -m onestead.web`。或者让 agent 先调用 `company()`。

在 `people/` 下给这个 agent 加一个成员，`kind` 写 `agent`，`owner` 写 `{{owner_key}}`。

把 `onestead init` 打印的 Claude Code、Codex、Cursor 或 WorkBuddy 片段贴进对应的配置，并把 `ONESTEAD_AGENT` 设成那个成员的身份键。

## 分工

{{owner_name}}（`{{owner_key}}`）是主人，用本机网页。agent 用自己的身份键。

## 入口

本机网页：`python -m onestead.web`

格式：Onestead 仓库里的 `docs/data-format.md`

手册：`company`、`collaboration`、`format`。

## 边界与注意

不要写入密钥、token、密码、cookie 的值。只写凭据在哪、叫什么。

上面的节名是程序规定的中文。项目状态也是，这里是 `进行中`。
