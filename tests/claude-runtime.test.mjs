import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { RUNNING_STALE_MS } from '../server/claude-runtime.mjs';
import { collectLocalAgents, projectLocalAgents } from '../server/local-agents.mjs';

const now = Date.parse('2026-10-23T03:00:00.000Z');
const iso = value => new Date(value).toISOString();
const fixtureRoot = fileURLToPath(new URL('./.claude-runtime-fixtures/', import.meta.url));
async function directory() { const target = path.join(fixtureRoot, randomUUID()); await mkdir(target, { recursive: true }); return target; }
// Desktop app (10) with one window and two Code-tab session processes (11, 12); 13 is a nested helper of 11.
function snapshot() {
  return { available: true, windowsAvailable: true, processes: [
    { pid: 10, parentPid: 1, agentId: 'claude', type: 'desktop', startedAt: iso(now - 3600_000), windows: ['w1'] },
    { pid: 11, parentPid: 10, agentId: 'claude', type: 'cli', startedAt: iso(now - 1800_000), windows: [] },
    { pid: 12, parentPid: 10, agentId: 'claude', type: 'cli', startedAt: iso(now - 1800_000), windows: [] },
    { pid: 13, parentPid: 11, agentId: 'claude', type: 'cli', startedAt: iso(now - 1000), windows: [] },
  ], parents: [] };
}
const frame = (ownerPid, overrides = {}) => ({ schemaVersion: 1, source: 'claude-code-hooks', ownerPid, phase: 'running',
  model: 'claude-opus-5-5', eventAt: (now - 5000) / 1000, observedAt: iso(now - 5000), phaseSince: iso(now - 5000), ...overrides });
async function put(target, value, key = randomBytes(12).toString('hex'), pid = value.ownerPid) {
  await writeFile(path.join(target, `claude-status-${pid}-${key}.json`), JSON.stringify(value)); return key;
}
const collect = (target, observed = snapshot(), at = now) => collectLocalAgents({ claudeStatusDirectory: target,
  readProcessSnapshot: async () => observed, now: () => new Date(at) });
const claude = rows => rows.find(row => row.agentId === 'claude');
const talks = rows => claude(rows).instances.filter(instance => instance.kind === 'conversation');

test('missing, unconfigured, or empty hook directory keeps the window-only fallback', async () => {
  const empty = await directory(), observed = snapshot(), baseline = projectLocalAgents(observed, { observedAt: iso(now) });
  for (const target of [undefined, path.join(empty, 'absent'), empty]) assert.deepEqual(await collect(target, observed), baseline);
});

test('every session reporting gives full counts; model only while running; no pid or session key leaks', async () => {
  const target = await directory();
  const keys = [await put(target, frame(11)), await put(target, frame(12, { phase: 'waiting', model: 'claude-haiku-5-5' }))];
  const rows = await collect(target), row = claude(rows);
  assert.equal(row.openWindows, 1);
  assert.deepEqual([row.openSessions, row.activeSessions, row.idleSessions, row.unknownSessions], [2, 1, 1, 0]);
  assert.deepEqual(talks(rows).map(instance => [instance.state, instance.model]).sort(), [['idle', null], ['running', 'claude-opus-5-5']]);
  assert.match(row.coverage, /全部 2 个会话/);
  const output = JSON.stringify(rows);
  assert.ok(!keys.some(key => output.includes(key)) && !output.includes(target) && !/ownerPid|eventAt/.test(output));
});

test('a session without a hook file keeps totals unknown and says it has not reported yet', async () => {
  const target = await directory(); await put(target, frame(11, { phase: 'idle' }));
  const row = claude(await collect(target));
  assert.deepEqual([row.openSessions, row.activeSessions, row.idleSessions], [null, null, null]);
  assert.equal(row.instances.filter(instance => instance.kind === 'conversation').length, 1);
  assert.match(row.coverage, /另有 1 个会话还没报过状态/);
});

test('closed sessions, reused PIDs, mismatched owners, and malformed frames are ignored', async () => {
  const target = await directory();
  await put(target, frame(99));                                  // session process gone
  await put(target, frame(13));                                  // nested helper is not a session
  await put(target, frame(11), undefined, 12);                   // filename pid differs from ownerPid
  await put(target, frame(12, { schemaVersion: 2 }));
  await put(target, frame(12, { phase: 'thinking' }));
  await put(target, frame(12, { eventAt: (now + 60_000) / 1000 }));
  assert.deepEqual(await collect(target), projectLocalAgents(snapshot(), { observedAt: iso(now) }));
  const reused = snapshot(); reused.processes[1].startedAt = iso(now - 1000);  // pid 11 started after the event
  const other = await directory(); await put(other, frame(11));
  assert.equal(talks(await collect(other, reused)).length, 0);
});

test('a running claim older than the stale limit becomes unknown, and the newest file per process wins', async () => {
  const target = await directory();
  await put(target, frame(11, { eventAt: (now - RUNNING_STALE_MS - 1000) / 1000 }));
  await put(target, frame(12, { phase: 'idle', eventAt: (now - 300_000) / 1000 }));
  await put(target, frame(12, { phase: 'running', eventAt: (now - 2000) / 1000 }));
  const rows = await collect(target), row = claude(rows);
  assert.deepEqual([row.openSessions, row.activeSessions, row.idleSessions, row.unknownSessions], [2, 1, 0, 1]);
  assert.deepEqual(talks(rows).map(instance => instance.state).sort(), ['running', 'unknown']);
});

test('a covered standalone terminal session replaces its unknown CLI row instead of adding a second one', async () => {
  const observed = { available: true, windowsAvailable: true, parents: [],
    processes: [{ pid: 40, parentPid: 1, agentId: 'claude', type: 'cli', startedAt: iso(now - 60_000), windows: [] }] };
  assert.equal(talks(projectLocalAgents(observed, { observedAt: iso(now) })).length, 1);
  const target = await directory(); await put(target, frame(40, { phase: 'idle' }));
  const rows = await collect(target, observed), row = claude(rows);
  assert.deepEqual(talks(rows).map(instance => instance.state), ['idle']);
  assert.deepEqual([row.openWindows, row.openSessions, row.idleSessions], [0, 1, 1]);
});
