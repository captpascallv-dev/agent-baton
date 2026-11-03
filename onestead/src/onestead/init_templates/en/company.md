---
id: company
type: handbook
title: Company
sensitivity: internal
---

# Company

Onestead keeps the company facts in this git repository. The program reads this repository. It does not keep a second copy.

{{owner_name}} (`{{owner_key}}`) is the owner. That identity is reserved for the local web app. MCP and the CLI do not accept it as an agent.

An agent starts by calling `company()`, then opens a project with `get_project`. This repository is the authority for company facts.
