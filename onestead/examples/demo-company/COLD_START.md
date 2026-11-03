# Cold start check

## 中文说明

这是给验收的人看的。示例公司正文用英文，因为这份数据随开源仓库给国际用户。不要把本文件贴进 agent 的对话。

做法：把 `examples/demo-company` 复制到别处，在副本里 `git init` 并提交一次，把底座的数据目录指到副本。身份用 `claude-code`（Alex 名下、全读、能写）。开一个新对话，先只说下面的起手话。答完后再逐题问，对照标准答案里的要点。

在配置里设 `owner_key = "alex"`。同目录的 `config.example.toml` 已经写了这个键，把 `data_dir` 的占位换成副本的绝对路径。先备份已有的 `~/.onestead/config.toml`，再把示例配置复制到这个位置；也可以保留原配置，让 `ONESTEAD_CONFIG` 指向单独保存的示例配置文件。使用默认位置时，先清除已有的 `ONESTEAD_CONFIG` 和旧名 `COMPANY_BASE_CONFIG`，否则它们会优先于默认文件；使用独立配置时，确保 `ONESTEAD_CONFIG` 指向它。网页、CLI 和 agent 启动的 MCP 子进程都必须使用同一份示例配置（必要时在客户端的 MCP 环境变量中显式设置 `ONESTEAD_CONFIG`），并清除会覆盖文件值的 `ONESTEAD_DATA`、`ONESTEAD_OWNER` 及对应旧名 `COMPANY_BASE_DATA`、`COMPANY_BASE_OWNER`。这份示例只在本机跑。

下面的读取和权限现在就成立。网页上只有主人能做的操作，以及关键角色要主人批准才生效的那一步，在 `owner_key` 设成 `alex` 之后算数。

## How to run

1. Copy `examples/demo-company` to a new folder. The copy is the data repository. This sample folder itself has no `.git`.
2. In the copy, run `git init`, add the files, and make one commit.
3. Point the base at that copy. In `config.example.toml`, set `data_dir` to the copy's absolute path. `owner_key` is already `"alex"`. Back up any existing `~/.onestead/config.toml` before copying the sample config there, or leave it intact and set `ONESTEAD_CONFIG` to a separately saved sample config file. When using the default location, clear any existing `ONESTEAD_CONFIG` and legacy `COMPANY_BASE_CONFIG`, which take precedence over the default file; when using a separate config, make sure `ONESTEAD_CONFIG` points to it. The web app, CLI, and the MCP subprocess started by your agent must use the same sample config (set `ONESTEAD_CONFIG` explicitly in the client's MCP environment if needed). Clear file-value overrides `ONESTEAD_DATA`, `ONESTEAD_OWNER`, and their legacy aliases `COMPANY_BASE_DATA`, `COMPANY_BASE_OWNER`. This sample runs on the local machine only.
4. Connect your agent with member key `claude-code`.
5. Open a new chat with no other background. Say only: `Go to the company base and tell me where the website relaunch stands.`
6. After that answer, ask the questions below one at a time. Do not paste this file.

The member key `alex` is the owner of this sample. Set `owner_key = "alex"` in the config, as above. Reading, the permission checks below, owner-only actions on the local website, and approval of a key-role handover all use that owner.

## Questions

### 1. What should a new agent do first?

**Answer:** Call `company()` and read it before answering. Then use `get_project` for a single project. Do not invent status or names.

**Where:** `handbook/collaboration.md` via `get_handbook("collaboration")`. The tool is `company()`.

**Pass if:** The agent calls `company()` before giving shop facts, and it does not answer the relaunch from memory.

### 2. Where does the website relaunch stand, and who holds the roles?

**Answer:** State is `进行中` (in progress). The preview shop is up, hosted checkout works, and product pages still show last year's photos. Project lead is Alex (a key role, since 2026-08-01). Site builder is Codex (since 2026-08-15). The launch checklist at `C:/hearthleaf/launch-checklist.md` is the authority. Summary date is 2026-10-08.

**Where:** `projects/website-relaunch/project.md` and `projects/website-relaunch/status.md`, via `get_project("website-relaunch")`.

**Pass if:** The state, both role holders, the photo gap, and the checklist are present. Missing either holder fails.

### 3. Why did Hearthleaf choose a hosted checkout?

**Answer:** A custom cart would miss the 2 November launch, and a hosted checkout keeps card numbers off our servers. Alex decided this on 2026-09-02 for project `website-relaunch`.

**Where:** `decisions/2026-09-02-hosted-checkout.md`, via `list_decisions("website-relaunch")`.

**Pass if:** The reason mentions both the launch date and keeping card numbers off Hearthleaf's servers.

### 4. Where is the supplier portal login, and what is the item called?

**Answer:** It is stored in Alex's password manager. The item name is `Hearthleaf supplier portal`. The file does not include the login value.

**Where:** `assets/supplier-terms.md`, via `get_asset("supplier-terms")`.

**Pass if:** The answer gives the password manager and the item name, and it does not contain a login value.

### 5. Who can read the restricted supplier terms?

**Answer:** Full access can read it: Alex, Bri, Cam, Claude Code, Codex, and ChatGPT. Assigned access can read it only when `supplier-terms` is granted and the human key is on `visible_to`. `visible_to` lists `alex` only. Nora's grant is `brand-kit` only, so Nora and Nora Assistant cannot read it.

**Where:** `assets/supplier-terms.md` via `get_asset("supplier-terms")` as a full reader. Confirm the denial as `nora` or `nora-assistant`.

**Pass if:** Nora is excluded, and the answer does not say that only Alex can read it. Full readers can read a restricted file; `visible_to` filters assigned access.

### 6. Which projects can Nora see?

**Answer:** `packaging` and `holiday-tins` only. Not `website-relaunch`, `wholesale`, or `tea-lab`. Nora Assistant sees the same two.

**Where:** `people/nora.md` and `handbook/collaboration.md`. Live check: `list_projects()` as `nora`.

**Pass if:** The set is exactly those two ids.

### 7. What should happen next on the website relaunch?

**Answer:** Photograph the winter tins after they arrive on 20 October 2026, then publish the catalog and open the shop on 2 November 2026.

**Where:** The `下一步` section of `projects/website-relaunch/project.md`, via `get_project("website-relaunch")`.

**Pass if:** Both the photo step and the 2 November open date are in the answer.

### 8. Which tool appends a progress entry?

**Answer:** `update_project_status`. Use `update_project_summary` only to change 现状, 下一步, 分工, or 入口. Use `record_decision` for a decision, and include the reason.

**Where:** `handbook/collaboration.md`. A full reader also sees the tool name in `company()`.

**Pass if:** The tool named for a new progress entry is `update_project_status`.

### 9. Why is the wholesale pilot paused?

**Answer:** Three cafes asked for net-30 terms, and Hearthleaf cannot carry that before the holiday rush. Bri decided on 2026-08-20 to stay paused until spring and not quote new cafes before March 2027. State is `暂停`.

**Where:** `decisions/2026-08-20-pause-wholesale.md` via `list_decisions("wholesale")`, and `projects/wholesale/project.md` via `get_project("wholesale")`.

**Pass if:** The reason is the net-30 terms, and the state is paused.

### 10. Which of Alex's todos are still open, and who cannot see them?

**Answer:** Open: `Confirm the 2 November launch checklist` (due 2026-10-16, project `website-relaunch`) and `Send Nora the matte black swatch photo` (due 2026-10-14, project `packaging`). Done, and hidden from the default open list: `File the holiday tin sell-through note`. Claude Code and Codex can read them because they belong to Alex. ChatGPT belongs to Bri and cannot. Nora cannot.

**Where:** `todos/2026-10-10-090000-1.md` and `todos/2026-10-10-090000-2.md`, via `list_todos()` as `claude-code`. The rule is in `handbook/collaboration.md`.

**Pass if:** Both open titles are present, and the answer does not give those todos to ChatGPT or Nora.

### 11. Which app tracks the winter tins, and how do you open it?

**Answer:** `tin-tracker` (Tin tracker), a `网页` app on project `packaging`, maintained by Cam. Open https://tins.hearthleaf.example.test. Nora can see this app because Nora can see `packaging`. Nora cannot see `shop-preview` or `blend-board`.

**Where:** `apps/tin-tracker.md`, via `get_app("tin-tracker")` or `list_apps()`.

**Pass if:** The id and the URL are both present.

### 12. What is on Alex's home, from top to bottom?

**Answer:** `today_todos`, then `projects`, then `apps` (ids `shop-preview` and `tin-tracker`), then `recent` with limit 8. There is no `shared_skills` module. This is Alex's home. ChatGPT does not see this layout.

**Where:** `home/alex.md`, via `get_home_layout()` as `claude-code` (the Python method is `home_layout`).

**Pass if:** The four kinds are in that order, the two app ids are present, and the answer is not the default colleague layout.
