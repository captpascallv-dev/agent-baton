---
id: company
type: handbook
title: 公司概况
sensitivity: internal
---

# 公司概况

## 置顶规则：接进 Onestead 的 agent 必须做到

1. **项目更新及时记进来。** 持续建设中的项目，每完成一个阶段记一条进展；阶段长的，24 小时里有实际推进就至少记一次，空等不用写“还在等”。不用每一轮都记，也不等主人提醒。
   - 一条进展只写一个结果：交付了什么、哪个阶段完成了、定了什么、卡在哪要谁做、项目停了。几行大白话写完，详细过程留在项目自己的台账里，这里只放路径。
   - 派了单、在等回件、读了哪些文件、自证没做什么，这些都不记。
   - 分了很多条线的大项目，由项目 Lead 汇总着记，各条线的执行者不各自往这里写。
   - 进展用 `update_project_status`，写做了什么、结果在哪、下一步。
   - 现状、下一步、分工、入口变了，用 `update_project_summary`。
   - 定下来的事用 `record_decision`，写上理由。
   - 项目停了或暂时不做，也算更新：用 `update_project_summary` 把状态改成暂停或已结束，写清原因，不用硬补进展。
2. **搭好的工具，登记到应用广场。** 网页、服务、脚本、小工具，做出来能用就用 `register_app` 登记；地址、用法、状态变了用 `update_app`。
3. **写好的 Skill，登记到 Skill 广场。** 新写或改过的 Skill 用 `register_skill` 补上名字、一句话、正本在哪、谁维护，改了用 `update_skill`。
4. **要主人批准、动手或知道的事，登记成待办。** 用 `add_todos` 带上 ask，写清是什么、为什么、各个选项的后果、你的推荐，让主人不翻聊天记录也能直接拍板；当面告诉主人后标成已通知。

**做不到这几条的 agent，不接入。** 用命令行的伙伴对应 `status`、`project-summary`、`decide`、`app-register`、`app-update`、`skill-register`、`skill-update`、`todo-add`、`todo-update`，加 `--as <你的身份>`。

## 我们是谁

（一两段：公司做什么、主人是谁、主人定方向和批准哪些事；哪些 agent 在干活、各自擅长什么。成员明细见 `people/`。）

## 在做什么

项目一览由 Onestead 按读者权限现场生成（`company()` 的输出里就有，也可以用 `list_projects`），这里不手写，免得和项目文件对不上。每个项目的目标、现状、下一步、入口和担任者，用 `get_project` 读。

## 怎么协作

见 `handbook/collaboration.md`：规则正本在哪、怎么派活、哪些事要主人批准。
