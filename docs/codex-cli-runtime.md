# Optional Codex CLI state adapter

The adapter adds native running/terminal evidence to **current external Codex jobs already admitted by CLI Watch**. It does not add CLI sessions to Codex App counts, scan historical jobs, dispatch work, or guess the current model from a launch setting. Without compatible inputs, the existing unknown/activity display remains.

## Configuration

Use Node.js with the built-in `node:sqlite` module (the adapter was verified with Node.js 24). Add both fields to the external studio configuration:

```json
{
  "codexCliStateRoot": "C:/REPLACE/launcher-state",
  "codexCliHome": "C:/REPLACE/isolated-codex-home"
}
```

These are opt-in paths; neither is inferred from a user profile. Omit both to disable the adapter. The files remain outside the repository and static web root.

## Compatible launcher records

Each admitted CLI Watch project must have a `<job UUID>--<positive attempt>` ID and a current Codex work item. The adapter reads at most 32 such candidates per refresh.

The state root has `jobs/<job UUID>/owner.json`, optional `terminal.json`, and `codex.log`. `owner.json` identifies the **launcher process**, with `job_id`, integer `attempt`, integer `pid`, and `started_at_utc` and/or decimal `start_time_utc_ticks` (Windows UTC ticks). When both birth fields exist, they must agree. The live native `codex.exe exec` must descend from that exact launcher identity. A matching terminal record repeats those identity fields and adds integer `exit_code` and `finished_at_utc`; it can prove that this attempt has ended.

`codex.log` must begin with the native OpenAI Codex banner, including its version, approval policy and session UUID between the two `--------` lines. Only this bounded banner is read; the reader stops at the second separator, before prompt or output text. It never searches conversation text for a session ID.

## Native state and limits

The running-state adapter currently accepts **Codex 0.162.0 headless exec**, `approval=never`, and `history_mode=paginated`. It opens `state_5.sqlite` and `thread_history_1.sqlite` read-only and reads thread/turn metadata. It also compares the native projection checkpoint to the rollout file's size, without opening the rollout content. A live process alone cannot prove running; a stale database row alone cannot prove running either.

Missing or locked databases, an unsupported version, stale materialization, an unverified process birth/parent chain, or an invalid record produce unknown. A validated terminal result produces idle/ended evidence. The external model remains unknown because these metadata tables do not prove the model currently executing. Evidence expires independently of Codex App observations.

Fixture tests cover running and terminal turns, process exit and PID reuse, unrelated parents, banner boundaries, database locks, projection lag, path boundaries, and metadata limits. The local deployment verified an already-completed native job; a newly running real job was not available during that check, so live in-progress coverage remains a deployment validation gap.
