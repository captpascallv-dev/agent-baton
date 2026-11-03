---
id: collaboration
type: handbook
title: 协作方式
sensitivity: internal
---

# 协作方式

先调用 `company()`，再回答关于公司的问题。不要编造现状、担任者或理由。

进展用 `update_project_status` 往后追加。要改正文里的现状、下一步、分工、入口，用 `update_project_summary`。定下来的事用 `record_decision` 记下，并写上理由。关键角色换人要等主人批准。

凭据只写它放在哪、叫什么。不要写值。

对外发送、发布、花钱、删除，以及改凭据或真实数据，由主人批准。

每个 agent 在 `people/` 里单独建一个成员。接入时把 `ONESTEAD_AGENT` 设成那个成员的身份键。
