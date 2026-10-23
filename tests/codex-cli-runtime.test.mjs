import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, readFile, rename, rm, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { collectCodexCliRuntime } from "../server/codex-cli-runtime.mjs";

const JOB = "11111111-1111-7111-8111-111111111111";
const THREAD = "22222222-2222-7222-8222-222222222222";
const START = "2026-10-24T03:00:00.1234567+08:00";
const NOW = Date.parse("2026-10-24T03:10:00+08:00");
const START_MS = Date.parse(START);
const TICKS = 621355968000000000n + BigInt(START_MS) * 10000n + 4567n;
const SECRET = "PRIVATE_PROMPT_ITEM_OR_ERROR_MUST_NOT_ESCAPE";
const candidate = { jobId: JOB, attempt: 1, jobKey: `${JOB}--1` };
const iso = ms => new Date(ms).toISOString();

function header({ id = THREAD, version = "0.162.0", approval = "never", tail = SECRET } = {}) {
  return `2026-10-24T02:59:59Z WARN startup warning\n\x1b[1mOpenAI Codex v${version}\x1b[0m\n--------\nworkdir: fixture\nmodel: configured-only\nprovider: openai\napproval: ${approval}\nsandbox: workspace-write\nreasoning effort: xhigh\nreasoning summaries: none\nsession id: ${id}\n--------\nuser\n${tail}\n`;
}
function processSnapshot() {
  return { available: true,
    parents: [{ pid: 10, parentPid: 9, startedAt: "2026-10-24T03:00:00.1234560+08:00" },
      { pid: 20, parentPid: 10, startedAt: iso(START_MS + 500) }],
    processes: [{ pid: 30, parentPid: 20, startedAt: iso(START_MS + 800), agentId: "codex", type: "cli" }] };
}
function rawJson(value) {
  return JSON.stringify(value).replace(/"start_time_utc_ticks":"(\d+)"/, '"start_time_utc_ticks":$1');
}
async function fixture(t) {
  const directory = await mkdtemp(path.join(tmpdir(), "studio-codex-cli-"));
  t.after(async () => {
    const resolved = path.resolve(directory);
    assert.equal(path.dirname(resolved), path.resolve(tmpdir()));
    assert.ok(path.basename(resolved).startsWith("studio-codex-cli-"));
    await rm(resolved, { recursive: true, force: true });
  });
  const stateRoot = path.join(directory, "launcher"), codexHome = path.join(directory, "codex-home");
  const jobDirectory = path.join(stateRoot, "jobs", JOB), sessions = path.join(codexHome, "sessions");
  await mkdir(jobDirectory, { recursive: true }); await mkdir(sessions, { recursive: true });
  const rollout = path.join(sessions, "fixture.jsonl");
  const canonical = Buffer.from(SECRET);
  await writeFile(rollout, canonical);
  const owner = { job_id: JOB, attempt: 1, pid: 10, started_at_utc: START,
    start_time_utc_ticks: TICKS.toString(), model: "configured-only", effort: "xhigh", service_tier: "default" };
  await writeFile(path.join(jobDirectory, "owner.json"), rawJson(owner));
  await writeFile(path.join(jobDirectory, "codex.log"), header());
  const state = new DatabaseSync(path.join(codexHome, "state_5.sqlite"));
  state.exec(`CREATE TABLE threads (id TEXT PRIMARY KEY, source TEXT, cli_version TEXT, history_mode TEXT,
    approval_mode TEXT, created_at_ms INTEGER, rollout_path TEXT, model TEXT, title TEXT);`);
  state.prepare("INSERT INTO threads VALUES (?, 'exec', '0.162.0', 'paginated', 'never', ?, ?, ?, ?)")
    .run(THREAD, START_MS + 1000, rollout, "configured-model-not-current", SECRET);
  state.close();
  const history = new DatabaseSync(path.join(codexHome, "thread_history_1.sqlite"));
  history.exec(`CREATE TABLE thread_turns (thread_id TEXT, turn_id TEXT, status TEXT, started_at INTEGER,
    completed_at INTEGER, rollout_ordinal INTEGER);
    CREATE TABLE thread_history_projection_state (thread_id TEXT PRIMARY KEY, next_rollout_byte_offset INTEGER,
      next_rollout_ordinal INTEGER);`);
  history.prepare("INSERT INTO thread_turns VALUES (?, 'turn-1', 'inProgress', ?, NULL, 1)")
    .run(THREAD, Math.floor((START_MS + 2000) / 1000));
  history.prepare("INSERT INTO thread_history_projection_state VALUES (?, ?, 3)").run(THREAD, canonical.length);
  history.close();
  return { directory, stateRoot, codexHome, jobDirectory, rollout, owner,
    options: { candidates: [candidate], processSnapshot: processSnapshot(), stateRoot, codexHome, now: NOW },
    async ownerPatch(patch) {
      await writeFile(path.join(jobDirectory, "owner.json"), rawJson({ ...owner, ...patch }));
    },
    stateSql(sql, ...params) {
      const db = new DatabaseSync(path.join(codexHome, "state_5.sqlite"));
      try { db.prepare(sql).run(...params); } finally { db.close(); }
    },
    historySql(sql, ...params) {
      const db = new DatabaseSync(path.join(codexHome, "thread_history_1.sqlite"));
      try { db.prepare(sql).run(...params); } finally { db.close(); }
    },
  };
}
async function one(f, overrides = {}) {
  const rows = await collectCodexCliRuntime({ ...f.options, ...overrides });
  assert.equal(rows.length, 1);
  assert.equal(rows[0].jobKey, candidate.jobKey);
  assert.equal(rows[0].model, null);
  assert.equal(JSON.stringify(rows).includes(SECRET), false);
  return rows[0];
}

test("active native exec is running; PID birth accepts CIM microsecond truncation and never guesses a model", async t => {
  const f = await fixture(t);
  assert.deepEqual(await one(f), { jobKey: candidate.jobKey, state: "running", model: null, basis: "native_turn_active" });
  assert.equal((await one(f)).state, "running"); // re-observation is also bounded and read-only
});

test("native completed, failed and interrupted turns are idle even with live CLI teardown", async t => {
  const f = await fixture(t);
  for (const status of ["completed", "failed", "interrupted"]) {
    f.historySql("UPDATE thread_turns SET status=?, completed_at=started_at+3", status);
    assert.deepEqual(await one(f), { jobKey: candidate.jobKey, state: "idle", model: null, basis: "native_turn_terminal" });
  }
});

test("matching launcher terminal is sufficient after the process exits, including exact large tick tokens", async t => {
  const f = await fixture(t);
  const terminal = { job_id: JOB, attempt: 1, pid: 10, start_time_utc_ticks: TICKS.toString(),
    exit_code: 0, finished_at_utc: iso(START_MS + 10000) };
  await writeFile(path.join(f.jobDirectory, "terminal.json"), rawJson(terminal));
  assert.deepEqual(await one(f, { processSnapshot: { available: false } }),
    { jobKey: candidate.jobKey, state: "idle", model: null, basis: "launcher_terminal" });
  await writeFile(path.join(f.jobDirectory, "terminal.json"), rawJson({ ...terminal, attempt: 2 }));
  assert.equal((await one(f)).state, "unknown");
  await writeFile(path.join(f.jobDirectory, "terminal.json"), rawJson({ ...terminal, job_id: 1 }));
  assert.equal((await one(f)).state, "unknown");
});

test("dead process, reused PID, missing ancestor, unrelated child and reversed parent birth never report running", async t => {
  const f = await fixture(t);
  const variants = [
    { available: false, parents: [], processes: [] },
    { ...processSnapshot(), processes: [] },
    { ...processSnapshot(), parents: [{ pid: 10, parentPid: 9, startedAt: iso(START_MS + 1) }, processSnapshot().parents[1]] },
    { ...processSnapshot(), parents: [processSnapshot().parents[0]] },
    { ...processSnapshot(), processes: [{ ...processSnapshot().processes[0], parentPid: 99 }] },
    { ...processSnapshot(), parents: [processSnapshot().parents[0], { ...processSnapshot().parents[1], startedAt: iso(START_MS + 900) }] },
    { ...processSnapshot(), processes: [{ ...processSnapshot().processes[0], type: "app-server" }] },
  ];
  for (const processSnapshot of variants) assert.equal((await one(f, { processSnapshot })).state, "unknown");
  assert.equal((await one(f)).state, "running");
});

test("duplicate conflicting PID identities and cyclic ancestry are unknown", async t => {
  const f = await fixture(t), snapshot = processSnapshot();
  snapshot.parents.push({ ...snapshot.parents[1], startedAt: iso(START_MS + 501) });
  assert.equal((await one(f, { processSnapshot: snapshot })).state, "unknown");
  const loop = processSnapshot(); loop.parents[1].parentPid = 20;
  assert.equal((await one(f, { processSnapshot: loop })).state, "unknown");
});

test("native banner stops before the private prompt and ignores forged prompt session ids", async t => {
  const f = await fixture(t);
  await writeFile(path.join(f.jobDirectory, "codex.log"), header({ tail: `OpenAI Codex v0.162.0\n--------\nsession id: ${JOB}\n--------\n${SECRET}` }));
  assert.equal((await one(f)).state, "running");
  await writeFile(path.join(f.jobDirectory, "codex.log"), header({ id: "" }));
  assert.equal((await one(f)).state, "unknown");
});

test("truncated banner, prompt inside an unfinished header, missing native banner and bounded oversized lines are unknown", async t => {
  const f = await fixture(t);
  const logs = [
    `OpenAI Codex v0.162.0\n--------\napproval: never\nsession id: ${THREAD}`,
    `OpenAI Codex v0.162.0\n--------\napproval: never\nuser\nsession id: ${THREAD}\n--------\n${SECRET}`,
    `user\nOpenAI Codex v0.162.0\n--------\napproval: never\nsession id: ${THREAD}\n--------\n`,
    `${"x".repeat(4097)}\n${header()}`,
    `${"startup warning\n".repeat(65)}${header()}`,
  ];
  for (const log of logs) {
    await writeFile(path.join(f.jobDirectory, "codex.log"), log);
    assert.equal((await one(f)).state, "unknown");
  }
});

test("job/attempt/start identity mismatch, oversized owner and invalid candidate are unknown", async t => {
  const f = await fixture(t);
  await f.ownerPatch({ attempt: 2 }); assert.equal((await one(f)).state, "unknown");
  await f.ownerPatch({ job_id: THREAD }); assert.equal((await one(f)).state, "unknown");
  await f.ownerPatch({ started_at_utc: iso(START_MS + 1) }); assert.equal((await one(f)).state, "unknown");
  await writeFile(path.join(f.jobDirectory, "owner.json"), "x".repeat(8193));
  assert.equal((await one(f)).state, "unknown");
  const invalid = { ...candidate, jobId: "../../private" };
  assert.equal((await one(f, { candidates: [invalid] })).basis, "input_invalid");
});

test("source, format, version, never approval and same-generation thread metadata are required", async t => {
  const f = await fixture(t);
  for (const [column, bad, good] of [["source", "cli", "exec"], ["history_mode", "legacy", "paginated"],
    ["cli_version", "0.163.0", "0.162.0"], ["approval_mode", "on-request", "never"],
    ["created_at_ms", START_MS - 5000, START_MS + 1000]]) {
    f.stateSql(`UPDATE threads SET ${column}=?`, bad); assert.equal((await one(f)).state, "unknown");
    f.stateSql(`UPDATE threads SET ${column}=?`, good);
  }
  await writeFile(path.join(f.jobDirectory, "codex.log"), header({ approval: "on-request" }));
  assert.equal((await one(f)).state, "unknown");
});

test("missing, corrupt and exclusively locked SQLite sources return unknown without raw errors", async t => {
  const f = await fixture(t), filename = path.join(f.codexHome, "thread_history_1.sqlite");
  await rename(filename, `${filename}.fixture-backup`);
  assert.equal((await one(f)).state, "unknown");
  await writeFile(filename, SECRET); assert.equal((await one(f)).state, "unknown");
  await rm(filename); await rename(`${filename}.fixture-backup`, filename);
  const lock = new DatabaseSync(filename); lock.exec("BEGIN EXCLUSIVE");
  try { assert.equal((await one(f)).state, "unknown"); } finally { lock.exec("ROLLBACK"); lock.close(); }
  assert.equal((await one(f)).state, "running");
});

test("missing or lagging checkpoint, partial canonical writes and stale native turn are unknown", async t => {
  const f = await fixture(t), size = (await readFile(f.rollout)).length;
  f.historySql("UPDATE thread_history_projection_state SET next_rollout_byte_offset=?", size - 1);
  assert.equal((await one(f)).basis, "projection_not_ready");
  f.historySql("UPDATE thread_history_projection_state SET next_rollout_byte_offset=?, next_rollout_ordinal=1", size);
  assert.equal((await one(f)).state, "unknown");
  f.historySql("UPDATE thread_history_projection_state SET next_rollout_ordinal=3");
  f.historySql("UPDATE thread_turns SET started_at=?", Math.floor(START_MS / 1000) - 5);
  assert.equal((await one(f)).state, "unknown");
  f.historySql("DELETE FROM thread_history_projection_state");
  assert.equal((await one(f)).state, "unknown");
});

test("compressed, missing, outside-home and symlink-escaped canonical metadata cannot validate a projection", async t => {
  const f = await fixture(t);
  for (const filename of [f.rollout + ".gz", path.join(f.codexHome, "sessions", "missing.jsonl"),
    path.join(f.directory, "outside.jsonl")]) {
    if (!filename.endsWith("missing.jsonl")) await writeFile(filename, SECRET);
    f.stateSql("UPDATE threads SET rollout_path=?", filename);
    assert.equal((await one(f)).state, "unknown");
  }
  const target = path.join(f.directory, "outside-dir"), link = path.join(f.codexHome, "sessions", "escape");
  await mkdir(target); await writeFile(path.join(target, "fixture.jsonl"), SECRET);
  try { await symlink(target, link, process.platform === "win32" ? "junction" : "dir"); }
  catch (error) { if (["EPERM", "EACCES"].includes(error.code)) return; throw error; }
  f.stateSql("UPDATE threads SET rollout_path=?", path.join(link, "fixture.jsonl"));
  assert.equal((await one(f)).basis, "projection_unavailable");
});

test("newest native turn wins; no history-directory discovery and at most 32 explicit candidates", async t => {
  const f = await fixture(t);
  f.historySql("INSERT INTO thread_turns VALUES (?, 'later', 'completed', ?, ?, 2)", THREAD,
    Math.floor((START_MS + 3000) / 1000), Math.floor((START_MS + 4000) / 1000));
  assert.equal((await one(f)).state, "idle");
  assert.deepEqual(await collectCodexCliRuntime({ ...f.options, candidates: [] }), []);
  const rows = await collectCodexCliRuntime({ ...f.options, candidates: Array(33).fill(candidate) });
  assert.equal(rows.length, 32); assert.ok(rows.every(row => row.state === "idle" && row.model === null));
});
