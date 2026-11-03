---
id: getting-started
type: project
title: Getting started
state: 进行中
goal: "Connect one agent and read the company page."
roles:
  - role: Owner
    who: {{owner_name_yaml}}
    since: {{today}}
summary_as_of: {{today}}
sensitivity: internal
updated: {{today}}
---

# Getting started

## 目标

Connect one agent and read the company page.

## 现状

`onestead init` created this repository on {{today}}. The config file points here. No agent is connected yet.

## 下一步

Open the local web app with `python -m onestead.web`, or ask an agent to call `company()`.

Add a member for that agent under `people/`. Set `kind: agent` and `owner: {{owner_key}}`.

Paste the Claude Code, Codex, Cursor, or WorkBuddy snippet printed by `onestead init`, and set `ONESTEAD_AGENT` to that member's key.

## 分工

{{owner_name}} (`{{owner_key}}`) owns the company and uses the local web app. Agents use their own keys.

## 入口

Local web: `python -m onestead.web`

Format: `docs/data-format.md` in the Onestead repository.

Handbooks: `company`, `collaboration`, and `format`.

## 边界与注意

Do not write secret values. Write where a credential lives and what it is called.

The headings on this page stay in Chinese. The program matches those words. The project state is also one of the Chinese words, here `进行中`.
