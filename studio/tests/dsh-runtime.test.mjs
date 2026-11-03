import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdir, writeFile, unlink, symlink, utimes } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { collectDshRuntime, MAX_FRAME_BYTES } from '../server/dsh-runtime.mjs';
import { collectLocalAgents, projectLocalAgents } from '../server/local-agents.mjs';

const now = Date.parse('2026-10-22T20:00:00.000Z');
const iso = value => new Date(value).toISOString();
const fixtureRoot = fileURLToPath(new URL('./.dsh-runtime-fixtures/', import.meta.url));
async function directory() { const target = path.join(fixtureRoot, randomUUID()); await mkdir(target, { recursive: true }); return target; }
function snapshot() {
  return { available: true, windowsAvailable: true,
    processes: [{ pid: 100, parentPid: 1, agentId: 'dsh', type: 'desktop', startedAt: iso(now - 20000), windows: ['window'] }],
    parents: [{ pid: 100, parentPid: 1, startedAt: iso(now - 20000) }, { pid: 200, parentPid: 100, startedAt: iso(now - 10000) }] };
}
function frame(overrides = {}) {
  return { schemaVersion: 1, producerId: randomUUID(), producerPid: 200, producerStartedAt: iso(now - 9900),
    observedAt: iso(now), validUntil: iso(now + 10000), sourceLive: true, complete: true,
    instances: [{ id: randomUUID(), phase: 'generating', currentModel: 'actual-model' }], ...overrides };
}
async function put(target, value, name = `dsh-status-${value.producerPid}-${value.producerId}.json`) {
  const filename = path.join(target, name); await writeFile(filename, JSON.stringify(value));
  await utimes(filename, new Date(now), new Date(now)); return filename;
}
const collect = (target, observed = snapshot(), at = now) => collectLocalAgents({ dshStatusDirectory: target,
  readProcessSnapshot: async () => observed, now: () => new Date(at) });
const dsh = rows => rows.find(row => row.agentId === 'dsh');
const conversations = rows => dsh(rows).instances.filter(instance => instance.kind === 'conversation');
const baseline = observed => projectLocalAgents(observed, { observedAt: iso(now) });

test('unconfigured, missing, or empty source preserves the existing window-and-unknown fallback', async () => {
  const empty = await directory(), observed = snapshot();
  for (const target of [undefined, path.join(empty, 'absent'), empty]) assert.deepEqual(await collect(target, observed), baseline(observed));
  assert.deepEqual(await collect(empty, { available: false }), baseline({ available: false }));
});

test('a verified descendant runtime shows only observed sessions and keeps all-machine totals unknown', async () => {
  const target = await directory(), value = frame();
  value.instances.push({ id: randomUUID(), phase: 'waiting_approval', currentModel: 'old-model' },
    { id: randomUUID(), phase: 'idle', currentModel: 'old-model' }, { id: randomUUID(), phase: 'unknown', currentModel: 'old-model' });
  value.title = 'PRIVATE_CHAT_TITLE'; value.messages = ['PRIVATE_CHAT_BODY']; value.credentials = 'PRIVATE_SECRET';
  await put(target, value);
  const rows = await collect(target), row = dsh(rows), seen = conversations(rows);
  assert.equal(row.openWindows, 1); assert.equal(row.sourceStatus, 'partial');
  for (const key of ['openSessions', 'activeSessions', 'idleSessions', 'unknownSessions']) assert.equal(row[key], null);
  assert.deepEqual(seen.map(instance => [instance.state, instance.model]), [['running', 'actual-model'], ['idle', null], ['idle', null], ['unknown', null]]);
  assert.match(row.coverage, /部分 DSH 对话状态/);
  const output = JSON.stringify(rows);
  assert.ok(!/PRIVATE_|producerPid|producerId|producerStartedAt|coveredProcessIds/.test(output));
  assert.ok(!output.includes(target) && !output.includes(value.instances[0].id));
});

test('producer death, wrong start, unrelated ancestry, and parent PID reuse all reject the frame', async () => {
  const target = await directory(), value = frame(); await put(target, value);
  const dead = snapshot(); dead.parents = dead.parents.filter(row => row.pid !== 200);
  const wrongStart = snapshot(); wrongStart.parents.find(row => row.pid === 200).startedAt = iso(now - 2000);
  const foreign = snapshot(); foreign.parents.find(row => row.pid === 200).parentPid = 300;
  foreign.parents.push({ pid: 300, parentPid: 1, startedAt: iso(now - 15000) });
  const reusedParent = snapshot(); reusedParent.processes[0].startedAt = iso(now - 5000);
  const noStart = snapshot(); delete noStart.parents.find(row => row.pid === 200).startedAt;
  for (const observed of [dead, wrongStart, foreign, reusedParent, noStart]) assert.deepEqual(await collect(target, observed), baseline(observed));
});

test('expired, stopped, future, and disappearing producer files retract old instances and models', async () => {
  const target = await directory(), value = frame(), filename = await put(target, value);
  assert.equal(conversations(await collect(target))[0].model, 'actual-model');
  assert.deepEqual(await collect(target, snapshot(), now + 10000), projectLocalAgents(snapshot(), { observedAt: iso(now + 10000) }));
  for (const patch of [{ sourceLive: false }, { complete: false }, { observedAt: iso(now + 1) }]) {
    await put(target, { ...value, ...patch }); assert.deepEqual(await collect(target), baseline(snapshot()));
  }
  await unlink(filename); assert.deepEqual(await collect(target), baseline(snapshot()));
});

test('same-PID valid producer conflict is unknown; expired peers are ignored, separate runtimes remain partial', async () => {
  const target = await directory(), one = frame(), two = frame();
  await put(target, one); await put(target, two);
  assert.deepEqual(await collect(target), baseline(snapshot()));
  await put(target, { ...two, observedAt: iso(now - 20000), validUntil: iso(now - 10000) });
  assert.equal(conversations(await collect(target)).length, 1);
  const observed = snapshot(); observed.parents.push({ pid: 201, parentPid: 100, startedAt: iso(now - 10000) });
  await put(target, frame({ producerPid: 201 }));
  const rows = await collect(target, observed);
  assert.equal(conversations(rows).length, 2); assert.equal(dsh(rows).activeSessions, null);
});

test('only matching bounded regular metadata files are consumed', async t => {
  const target = await directory(), value = frame();
  await put(target, value, 'credentials.json');
  await put(target, value, `dsh-status-201-${value.producerId}.json`);
  await mkdir(path.join(target, `dsh-status-200-${randomUUID()}.json`));
  const oversized = path.join(target, `dsh-status-200-${randomUUID()}.json`);
  await writeFile(oversized, ' '.repeat(MAX_FRAME_BYTES + 1));
  await put(target, { ...value, producerId: randomUUID() }, `dsh-status-200-${randomUUID()}.json`);
  const malformed = path.join(target, `dsh-status-200-${randomUUID()}.json`); await writeFile(malformed, '{');
  const link = path.join(target, `dsh-status-200-${value.producerId}.json`);
  try { await symlink(path.join(target, 'credentials.json'), link, 'file'); }
  catch (error) { if (error.code !== 'EPERM') throw error; t.diagnostic('File symlink creation unavailable; directory/non-file boundary still verified.'); }
  assert.deepEqual(await collect(target), baseline(snapshot()));
  if (process.platform === 'win32') {
    const validTarget = await directory(), junctionParent = await directory();
    await put(validTarget, frame());
    const junction = path.join(junctionParent, 'linked-status');
    await symlink(validTarget, junction, 'junction');
    assert.equal(conversations(await collect(validTarget)).length, 1);
    assert.deepEqual(await collect(junction), baseline(snapshot()));
  }
});

test('historical dead or expired files do not consume the current-producer budget', async () => {
  const target = await directory();
  for (let index = 0; index < 150; index++) {
    await put(target, frame({ producerPid: 1000 + index }));
    const old = frame({ observedAt: iso(now - 20000), validUntil: iso(now - 10000) });
    const filename = await put(target, old);
    await utimes(filename, new Date(now - 20000), new Date(now - 20000));
    await writeFile(path.join(target, `ignored-${index}`), '');
  }
  await put(target, frame());
  const result = await collectDshRuntime(target, snapshot(), { now });
  assert.equal(result.runtimeCount, 1);
  assert.deepEqual(result.instances.map(instance => [instance.state, instance.model]), [['running', 'actual-model']]);
});

test('covered CLI fallback is replaced, while a different CLI remains unknown', async () => {
  const target = await directory(), observed = snapshot();
  observed.processes[0].type = 'cli'; observed.processes[0].windows = [];
  observed.processes.push({ pid: 300, parentPid: 1, agentId: 'dsh', type: 'cli', startedAt: iso(now - 10000), windows: [] });
  await put(target, frame());
  const seen = conversations(await collect(target, observed));
  assert.equal(seen.length, 2);
  assert.deepEqual(seen.map(instance => instance.state).sort(), ['running', 'unknown']);
});
