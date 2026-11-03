import { constants } from 'node:fs';
import { lstat, open, opendir } from 'node:fs/promises';
import path from 'node:path';
import { observeSnapshot, TTL_MS } from '../integrations/dsh-status-plugin/state.mjs';

export const MAX_FRAME_BYTES = 128 * 1024;
const MAX_CANDIDATE_FILES = 64, MAX_PRODUCERS = 32, MAX_INSTANCES = 512;
const DIRECTORY_SCAN_MS = 1000;
const START_TOLERANCE_MS = 2000;
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}';
const filenamePattern = new RegExp(`^dsh-status-([1-9][0-9]{0,9})-(${uuid})\\.json$`, 'i');
const uuidPattern = new RegExp(`^${uuid}$`, 'i');

function processIdentity(snapshot) {
  const rows = [...(Array.isArray(snapshot.parents) ? snapshot.parents : []), ...snapshot.processes];
  const byPid = new Map(rows.filter(row => Number.isInteger(row?.pid) && row.pid > 0).map(row => [row.pid, row]));
  return { byPid, dshPids: new Set(snapshot.processes.filter(row => row?.agentId === 'dsh').map(row => row.pid)) };
}

function producerAncestors(pid, claimedStart, { byPid, dshPids }) {
  const producer = byPid.get(pid), actualStart = Date.parse(producer?.startedAt), claimed = Date.parse(claimedStart);
  if (!Number.isFinite(actualStart) || !Number.isFinite(claimed) || Math.abs(actualStart - claimed) > START_TOLERANCE_MS) return null;
  const chain = [], seen = new Set();
  let current = producer;
  while (current && chain.length < 128 && !seen.has(current.pid)) {
    chain.push(current.pid); seen.add(current.pid);
    if (dshPids.has(current.pid)) return chain;
    const parent = byPid.get(current.parentPid);
    const childStart = Date.parse(current.startedAt), parentStart = Date.parse(parent?.startedAt);
    // Parent PID reuse must not make an unrelated old process a DSH child.
    if (!Number.isFinite(childStart) || !Number.isFinite(parentStart) || parentStart > childStart) return null;
    current = parent;
  }
  return null;
}

export async function readFrame(filename) {
  let handle;
  try {
    const before = await lstat(filename);
    if (!before.isFile() || before.isSymbolicLink() || before.size > MAX_FRAME_BYTES) return null;
    handle = await open(filename, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
    const opened = await handle.stat();
    if (!opened.isFile() || opened.size > MAX_FRAME_BYTES || opened.dev !== before.dev || opened.ino !== before.ino) return null;
    const buffer = Buffer.alloc(MAX_FRAME_BYTES + 1);
    let offset = 0;
    while (offset < buffer.length) {
      const { bytesRead } = await handle.read(buffer, offset, buffer.length - offset, offset);
      if (!bytesRead) break;
      offset += bytesRead;
    }
    return offset > MAX_FRAME_BYTES ? null : JSON.parse(buffer.subarray(0, offset).toString('utf8'));
  } catch { return null; }
  finally { await handle?.close().catch(() => {}); }
}

// Metadata files only. No clients are opened, queried, authenticated or resumed.
// Returned process IDs remain internal to collectLocalAgents; none reach its API.
export async function collectDshRuntime(directory, snapshot, { now } = {}) {
  if (typeof directory !== 'string' || !path.isAbsolute(directory) || snapshot?.available !== true || !Array.isArray(snapshot.processes)) return null;
  const identity = processIdentity(snapshot), files = [], eligiblePids = new Map();
  const observedNow = now ?? Date.now(), minimumMtime = observedNow - TTL_MS;
  try {
    const info = await lstat(directory);
    if (!info.isDirectory() || info.isSymbolicLink()) return null;
    const deadline = performance.now() + DIRECTORY_SCAN_MS;
    for await (const entry of await opendir(directory)) {
      if (performance.now() > deadline) return null;
      const match = filenamePattern.exec(entry.name);
      if (!match || !entry.isFile() || entry.isSymbolicLink()) continue;
      const pid = Number(match[1]);
      if (!eligiblePids.has(pid)) eligiblePids.set(pid, Boolean(producerAncestors(pid, identity.byPid.get(pid)?.startedAt, identity)));
      if (!eligiblePids.get(pid)) continue;
      const filename = path.join(directory, entry.name);
      let candidate;
      try { candidate = await lstat(filename); } catch { continue; }
      // Old mounts remain on disk. Only live DSH identities with fresh files use
      // the JSON-read budget; historical directory size cannot exhaust it.
      if (!candidate.isFile() || candidate.isSymbolicLink() || candidate.size > MAX_FRAME_BYTES || candidate.mtimeMs < minimumMtime) continue;
      if (files.length >= MAX_CANDIDATE_FILES) return null;
      files.push({ filename, pid, producerId: match[2].toLowerCase() });
    }
  } catch { return null; }
  const accepted = new Map(), coveredProcessIds = new Set(), instances = [];
  for (const file of files) {
    const frame = await readFrame(file.filename);
    if (frame?.producerPid !== file.pid || typeof frame.producerId !== 'string' || frame.producerId.toLowerCase() !== file.producerId
      || !Array.isArray(frame.instances) || frame.instances.length > MAX_INSTANCES) continue;
    const ancestors = producerAncestors(file.pid, frame.producerStartedAt, identity);
    if (!ancestors) continue;
    const observed = observeSnapshot(frame, { now: observedNow, producerAlive: true });
    if (!observed.sourceLive || !observed.complete || observed.instances.some(instance => !uuidPattern.test(instance.id ?? ''))
      || new Set(observed.instances.map(instance => instance.id.toLowerCase())).size !== observed.instances.length) continue;
    if (accepted.has(file.pid)) return null;
    if (accepted.size >= MAX_PRODUCERS) return null;
    accepted.set(file.pid, file.producerId);
    for (const pid of ancestors) coveredProcessIds.add(pid);
    for (const instance of observed.instances) instances.push({ id: `${file.producerId}:${instance.id}`,
      state: ['generating', 'tool_running'].includes(instance.phase) ? 'running'
        : ['idle', 'waiting_input', 'waiting_approval'].includes(instance.phase) ? 'idle' : 'unknown',
      model: instance.currentModel });
  }
  return accepted.size ? { runtimeCount: accepted.size, coveredProcessIds, instances } : null;
}
