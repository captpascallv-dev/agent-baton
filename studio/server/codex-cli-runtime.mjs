import { open, realpath, stat } from "node:fs/promises";
import { readSync } from "node:fs";
import path from "node:path";

const UUID = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const MAX_CANDIDATES = 32;
const MAX_METADATA_BYTES = 8192;
const MAX_BANNER_BYTES = 32768;
const MAX_BANNER_LINES = 64;
const UNIX_EPOCH_TICKS = 621355968000000000n;
const CIM_TOLERANCE_TICKS = 10n; // CIM CreationDate can truncate to microseconds.

function isoTicks(value) {
  if (typeof value !== "string") return null;
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!match) return null;
  const milliseconds = Date.parse(match[1] + match[3]);
  if (!Number.isFinite(milliseconds)) return null;
  return UNIX_EPOCH_TICKS + BigInt(milliseconds) * 10000n + BigInt((match[2] ?? "").padEnd(7, "0"));
}
function sameBirth(left, right) {
  return left !== null && right !== null && (left > right ? left - right : right - left) <= CIM_TOLERANCE_TICKS;
}
function positive(value) { return Number.isSafeInteger(value) && value > 0; }
function result(jobKey, state, basis) { return { jobKey, state, model: null, basis }; }
function candidateValid(value) {
  return UUID.test(value?.jobId ?? "") && positive(value.attempt)
    && value.jobKey === `${value.jobId}--${value.attempt}`;
}

async function smallJson(filename) {
  let file;
  try {
    file = await open(filename, "r");
    const info = await file.stat();
    if (!info.isFile() || info.size > MAX_METADATA_BYTES) throw new Error("metadata_invalid");
    const buffer = Buffer.alloc(MAX_METADATA_BYTES + 1);
    const { bytesRead } = await file.read(buffer, 0, buffer.length, 0);
    if (bytesRead > MAX_METADATA_BYTES) throw new Error("metadata_invalid");
    const text = buffer.subarray(0, bytesRead).toString("utf8").replace(/^\uFEFF/, "");
    const value = JSON.parse(text);
    // Read the integer token before JSON's Number conversion loses the low bits.
    const rawTicks = /"start_time_utc_ticks"\s*:\s*"?(\d{16,22})"?\s*(?=[,}])/.exec(text)?.[1];
    const ticks = rawTicks ? BigInt(rawTicks) : null;
    const fromIso = isoTicks(value.started_at_utc);
    if (value.started_at_utc !== undefined && fromIso === null) throw new Error("metadata_invalid");
    if (ticks !== null && fromIso !== null && !sameBirth(ticks, fromIso)) throw new Error("metadata_invalid");
    return { value, ticks: fromIso ?? ticks };
  } catch (error) {
    if (error.code === "ENOENT") return null;
    throw new Error("metadata_invalid");
  } finally { await file?.close(); }
}

function recordMatches(record, candidate) {
  return record && typeof record.value.job_id === "string" && record.value.job_id.toLowerCase() === candidate.jobId.toLowerCase()
    && record.value.attempt === candidate.attempt && positive(record.value.pid) && record.ticks !== null;
}
function processIndex(snapshot) {
  const entries = new Map();
  for (const row of [...(snapshot.parents ?? []), ...(snapshot.processes ?? [])]) {
    if (!positive(row?.pid)) continue;
    const entry = { ...row, ticks: isoTicks(row.startedAt) };
    if (entries.has(row.pid)) {
      const old = entries.get(row.pid);
      if (!old || !sameBirth(old.ticks, entry.ticks) || old.parentPid !== entry.parentPid) entries.set(row.pid, null);
    } else entries.set(row.pid, entry);
  }
  return entries;
}
function hasExecDescendant(snapshot, owner, entries) {
  const root = entries.get(owner.value.pid);
  if (!root || !sameBirth(root.ticks, owner.ticks)) return false;
  return snapshot.processes.some(row => {
    if (row?.agentId !== "codex" || row.type !== "cli" || row.pid === root.pid) return false;
    let current = entries.get(row.pid);
    const visited = new Set();
    for (let depth = 0; current && depth < 64; depth++) {
      if (visited.has(current.pid) || current.ticks === null) return false;
      visited.add(current.pid);
      const parent = entries.get(current.parentPid);
      if (!parent || parent.ticks === null || current.ticks + CIM_TOLERANCE_TICKS < parent.ticks) return false;
      if (parent.pid === root.pid) return sameBirth(parent.ticks, owner.ticks);
      current = parent;
    }
    return false;
  });
}

async function banner(filename) {
  let file;
  try {
    file = await open(filename, "r");
    if (!(await file.stat()).isFile()) throw new Error("banner_unavailable");
    let bytes = 0, phase = 0, sessionId, version, approval;
    const byte = Buffer.alloc(1);
    // One-byte reads deliberately avoid prefetching the prompt after the second separator.
    for (let lineNumber = 0; lineNumber < MAX_BANNER_LINES && bytes < MAX_BANNER_BYTES; lineNumber++) {
      const lineBytes = [];
      let ended = false;
      while (bytes < MAX_BANNER_BYTES && lineBytes.length <= 4096) {
        if (readSync(file.fd, byte, 0, 1, null) !== 1) break;
        bytes++;
        if (byte[0] === 10) { ended = true; break; }
        lineBytes.push(byte[0]);
      }
      if (!ended || lineBytes.length > 4096) break;
      const line = Buffer.from(lineBytes).toString("utf8").replace(/^\uFEFF/, "")
        .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, "").trim();
      if (phase === 0) {
        const found = /^OpenAI Codex v([0-9][a-z0-9.+-]*)$/i.exec(line);
        if (found) { version = found[1]; phase = 1; }
        else if (line === "user") break;
      } else if (phase === 1) {
        if (line !== "--------") break;
        phase = 2;
      } else if (line === "--------") {
        if (!sessionId || !approval) break;
        return { sessionId, version, approval };
      } else {
        if (!/^(?:workdir|model|provider|approval|sandbox|reasoning effort|reasoning summaries|session id):\s/.test(line)) break;
        const id = /^session id:\s*([0-9a-f-]+)$/i.exec(line)?.[1];
        if (id) {
          if (!UUID.test(id) || sessionId) break;
          sessionId = id.toLowerCase();
        }
        const policy = /^approval:\s*([a-z-]+)$/.exec(line)?.[1];
        if (policy) {
          if (approval) break;
          approval = policy;
        }
      }
    }
    throw new Error("banner_unavailable");
  } finally { await file?.close(); }
}

async function openDatabases(codexHome) {
  const { DatabaseSync } = await import("node:sqlite");
  let state, history;
  try {
    state = new DatabaseSync(path.join(codexHome, "state_5.sqlite"), { readOnly: true });
    history = new DatabaseSync(path.join(codexHome, "thread_history_1.sqlite"), { readOnly: true });
    for (const db of [state, history]) db.exec("PRAGMA query_only = ON; PRAGMA busy_timeout = 0; BEGIN");
    return { state, history };
  } catch {
    state?.close(); history?.close();
    throw new Error("history_unavailable");
  }
}
async function currentProjection(databases, codexHome, header, owner) {
  const thread = databases.state.prepare(`SELECT source, cli_version, history_mode, approval_mode,
    created_at_ms, rollout_path FROM threads WHERE id = ?`).get(header.sessionId);
  if (thread?.source !== "exec" || thread.cli_version !== "0.162.0" || header.version !== "0.162.0"
    || thread.history_mode !== "paginated" || thread.approval_mode !== "never" || header.approval !== "never") {
    return { basis: "thread_metadata_unverified" };
  }
  if (!Number.isSafeInteger(thread.created_at_ms)
    || UNIX_EPOCH_TICKS + BigInt(thread.created_at_ms) * 10000n + 10000n < owner.ticks) {
    return { basis: "thread_identity_mismatch" };
  }
  const turn = databases.history.prepare(`SELECT status, started_at, completed_at, rollout_ordinal
    FROM thread_turns WHERE thread_id = ? ORDER BY rollout_ordinal DESC LIMIT 1`).get(header.sessionId);
  const checkpoint = databases.history.prepare(`SELECT CAST(next_rollout_byte_offset AS TEXT) AS byte_offset,
    next_rollout_ordinal FROM thread_history_projection_state WHERE thread_id = ?`).get(header.sessionId);
  if (!turn || !checkpoint || !/^\d+$/.test(checkpoint.byte_offset)
    || !Number.isSafeInteger(turn.started_at) || !Number.isSafeInteger(turn.rollout_ordinal)
    || !Number.isSafeInteger(checkpoint.next_rollout_ordinal) || checkpoint.next_rollout_ordinal <= turn.rollout_ordinal
    || UNIX_EPOCH_TICKS + BigInt(turn.started_at + 1) * 10000000n < owner.ticks) {
    return { basis: "projection_not_ready" };
  }
  // Native materialization can fail after canonical writes. Compare metadata only;
  // this observer never opens the rollout or reads its conversation records.
  if (typeof thread.rollout_path !== "string" || !path.isAbsolute(thread.rollout_path)
    || path.extname(thread.rollout_path) !== ".jsonl") return { basis: "projection_unavailable" };
  const sessions = await realpath(path.join(codexHome, "sessions"));
  const rollout = await realpath(thread.rollout_path);
  const relative = path.relative(sessions, rollout);
  if (!relative || relative.startsWith(`..${path.sep}`) || relative === ".." || path.isAbsolute(relative)
    || path.extname(rollout) !== ".jsonl") return { basis: "projection_unavailable" };
  const info = await stat(rollout, { bigint: true });
  if (!info.isFile() || BigInt(checkpoint.byte_offset) !== info.size) return { basis: "projection_not_ready" };
  if (turn.status === "inProgress" && turn.completed_at === null) return { state: "running", basis: "native_turn_active" };
  if (["completed", "failed", "interrupted"].includes(turn.status)
    && Number.isSafeInteger(turn.completed_at) && turn.completed_at >= turn.started_at) {
    return { state: "idle", basis: "native_turn_terminal" };
  }
  return { basis: "turn_unverified" };
}

/** Read-only exec projection for explicitly supplied current jobs; no process scan,
 * daemon connection, executor action, prompt/item JSON, or current-model guess. */
export async function collectCodexCliRuntime({ candidates, processSnapshot, stateRoot, codexHome, now = Date.now() } = {}) {
  const rows = Array.isArray(candidates) ? candidates.slice(0, MAX_CANDIDATES) : [];
  const output = [];
  let databases;
  const snapshotValid = processSnapshot?.available === true && Array.isArray(processSnapshot.processes)
    && Array.isArray(processSnapshot.parents);
  const entries = snapshotValid ? processIndex(processSnapshot) : null;
  try {
    for (const candidate of rows) {
      const jobKey = typeof candidate?.jobKey === "string" && /^[0-9a-f-]{36}--[1-9]\d{0,15}$/i.test(candidate.jobKey)
        ? candidate.jobKey : "";
      const unknown = basis => output.push(result(jobKey, "unknown", basis));
      if (!candidateValid(candidate) || typeof stateRoot !== "string" || !path.isAbsolute(stateRoot)
        || typeof codexHome !== "string" || !path.isAbsolute(codexHome)
        || !Number.isFinite(now)) { unknown("input_invalid"); continue; }
      const directory = path.join(stateRoot, "jobs", candidate.jobId.toLowerCase());
      let owner, terminal;
      try {
        owner = await smallJson(path.join(directory, "owner.json"));
        if (!recordMatches(owner, candidate) || owner.ticks > UNIX_EPOCH_TICKS + BigInt(Math.trunc(now + 5000)) * 10000n) {
          unknown("owner_unverified"); continue;
        }
        terminal = await smallJson(path.join(directory, "terminal.json"));
      } catch { unknown("owner_unverified"); continue; }
      if (terminal) {
        const finished = isoTicks(terminal.value.finished_at_utc);
        if (recordMatches(terminal, candidate) && terminal.value.pid === owner.value.pid
          && sameBirth(terminal.ticks, owner.ticks) && Number.isSafeInteger(terminal.value.exit_code)
          && finished !== null && finished >= owner.ticks) {
          output.push(result(jobKey, "idle", "launcher_terminal"));
        } else unknown("terminal_unverified");
        continue;
      }
      if (!snapshotValid || !hasExecDescendant(processSnapshot, owner, entries)) { unknown("process_unverified"); continue; }
      let header;
      try { header = await banner(path.join(directory, "codex.log")); }
      catch { unknown("banner_unavailable"); continue; }
      try {
        databases ??= await openDatabases(codexHome);
        const projection = await currentProjection(databases, codexHome, header, owner);
        output.push(result(jobKey, projection.state ?? "unknown", projection.basis));
      } catch { unknown("history_unavailable"); }
    }
  } finally {
    for (const db of [databases?.state, databases?.history]) {
      if (db) { try { db.exec("ROLLBACK"); } catch {} try { db.close(); } catch {} }
    }
  }
  return output;
}
