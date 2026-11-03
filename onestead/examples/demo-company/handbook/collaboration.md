---
id: collaboration
type: handbook
title: How we work
sensitivity: internal
---

# How we work

A new agent starts by calling `company()`. Read that page before answering questions about the shop. Then open a project with `get_project`. Do not invent status, role holders, or reasons.

Append a progress entry with `update_project_status`. Change the sections 现状, 下一步, 分工, or 入口 with `update_project_summary`. Record a decision with `record_decision`, and include the reason. Replacing the holder of a key role waits for the owner's approval.

Write where a credential lives and the item name. Do not write the value.

Nora, member key `nora`, is the outside packaging partner. Nora's access mode is assigned, and Nora cannot write. Nora can read two projects, `packaging` and `holiday-tins`, and one asset, `brand-kit`. Nora cannot read `supplier-terms`. Nora Assistant, member key `nora-assistant`, belongs to Nora and follows the same access.

Todos and the home page are personal. Alex's agents are Claude Code (`claude-code`) and Codex (`codex`). They read Alex's todos and Alex's home. ChatGPT (`chatgpt`) belongs to Bri and does not read Alex's todos or Alex's home.
