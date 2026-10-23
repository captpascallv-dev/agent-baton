import { lstat, opendir } from 'node:fs/promises';
import path from 'node:path';
import { readFrame } from './dsh-runtime.mjs';

// Files written by integrations/claude-code-hooks/claude_status_hook.py:
// claude-status-<Claude Code session pid>-<sha256(session id)[:24]>.json
const filenamePattern = /^claude-status-([1-9][0-9]{0,9})-([0-9a-f]{24})\.json$/;
const modelPattern = /^[A-Za-z0-9._:\-[\]]{1,80}$/;
const PHASES = new Set(['running', 'idle', 'waiting']);
const MAX_CANDIDATE_FILES = 64, DIRECTORY_SCAN_MS = 1000, START_TOLERANCE_MS = 2000, FUTURE_TOLERANCE_MS = 5000;
// No hook fires while one tool runs, and none fires when the user interrupts a turn;
// a "running" claim this old is no longer evidence of work.
export const RUNNING_STALE_MS = 20 * 60_000;

// A session is a Claude Code CLI process that is not itself started by another one
// (the desktop app's Code tab runs one such process per open session).
export function claudeSessionProcesses(snapshot) {
  const rows = Array.isArray(snapshot?.processes) ? snapshot.processes.filter(row => row?.agentId === 'claude' && Number.isInteger(row.pid)) : [];
  const cli = new Map(rows.filter(row => row.type === 'cli').map(row => [row.pid, row]));
  const byPid = new Map([...(Array.isArray(snapshot?.parents) ? snapshot.parents : []), ...rows]
    .filter(row => Number.isInteger(row?.pid)).map(row => [row.pid, row]));
  const nested = row => {
    const seen = new Set([row.pid]);
    for (let parent = byPid.get(row.parentPid); parent && !seen.has(parent.pid); parent = byPid.get(parent.parentPid)) {
      if (cli.has(parent.pid)) return true;
      seen.add(parent.pid);
    }
    return false;
  };
  return new Map([...cli].filter(([, row]) => !nested(row)));
}

function frameState(frame, now) {
  const eventMs = frame.eventAt * 1000;
  if (frame.phase !== 'running') return 'idle';  // waiting for approval or input counts as sleeping
  return now - eventMs > RUNNING_STALE_MS ? 'unknown' : 'running';
}

// Metadata files only: state, model and timing. No transcript, prompt or session id is read.
// Returned process IDs stay inside collectLocalAgents; none reach its API.
export async function collectClaudeRuntime(directory, snapshot, { now } = {}) {
  if (typeof directory !== 'string' || !path.isAbsolute(directory) || snapshot?.available !== true || !Array.isArray(snapshot.processes)) return null;
  const sessions = claudeSessionProcesses(snapshot), observedNow = now ?? Date.now(), files = [];
  try {
    const info = await lstat(directory);
    if (!info.isDirectory() || info.isSymbolicLink()) return null;
    const deadline = performance.now() + DIRECTORY_SCAN_MS;
    for await (const entry of await opendir(directory)) {
      if (performance.now() > deadline) return null;
      const match = filenamePattern.exec(entry.name);
      // Closed sessions leave files behind until the hook prunes them; skip without reading.
      if (!match || !entry.isFile() || !sessions.has(Number(match[1]))) continue;
      if (files.length >= MAX_CANDIDATE_FILES) return null;
      files.push({ filename: path.join(directory, entry.name), pid: Number(match[1]), key: match[2] });
    }
  } catch { return null; }
  const latest = new Map();
  for (const file of files) {
    const frame = await readFrame(file.filename);
    const owner = sessions.get(file.pid), started = Date.parse(owner?.startedAt);
    if (frame?.schemaVersion !== 1 || frame.source !== 'claude-code-hooks' || frame.ownerPid !== file.pid || !PHASES.has(frame.phase)
      || typeof frame.eventAt !== 'number' || !Number.isFinite(frame.eventAt) || frame.eventAt * 1000 > observedNow + FUTURE_TOLERANCE_MS
      // PID reuse: the session process must have existed before the hook event it claims.
      || !Number.isFinite(started) || started > frame.eventAt * 1000 + START_TOLERANCE_MS) continue;
    // One process holds one session at a time; after /clear an old file may survive a lost end hook.
    const previous = latest.get(file.pid);
    if (previous && previous.frame.eventAt >= frame.eventAt) continue;
    latest.set(file.pid, { key: file.key, frame });
  }
  if (!latest.size) return null;
  const instances = [...latest.values()].map(({ key, frame }) => {
    const state = frameState(frame, observedNow);
    return { id: key, state, model: state === 'running' && typeof frame.model === 'string' && modelPattern.test(frame.model) ? frame.model : null };
  });
  return { instances, coveredProcessIds: new Set(latest.keys()), sessionCount: sessions.size, complete: latest.size === sessions.size };
}
