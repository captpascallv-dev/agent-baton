import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, mkdir, readdir, lstat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { StatusState, observeSnapshot, TTL_MS } from '../state.mjs';
import { attachObserver } from '../index.mjs';
import { createPublisher } from '../publisher.mjs';
import * as plugin from '../index.mjs';

// Imports only the installed event framework. No DSH profile/runtime is booted.
const runtime = process.env.DSH_STATUS_TEST_RUNTIME;
if (!runtime) throw new Error('DSH_STATUS_TEST_RUNTIME must name the inspected installed runtime');
const { Context } = await import(pathToFileURL(path.join(runtime, 'node_modules/@deepseek-ai/cordis/lib/index.js')));
const tick = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const phase = observer => observer.state.snapshot().instances[0]?.phase;
const model = observer => observer.state.snapshot().instances[0]?.currentModel;
const guard = (object, fields) => { for (const field of fields) Object.defineProperty(object, field, { get() { throw new Error('private field accessed'); } }); return object; };

function harness(status = 'idle') {
  const ctx = new Context(), loaded = [], frames = [];
  const header = { config: guard({ model: 'actual-resolved-model' }, ['providerToken', 'credentials']) };
  const agent = guard({ status, session: { requestHeader: () => header } }, ['id', 'title', 'messages', 'options']);
  loaded.push(agent);
  ctx.provide('agents', { list: () => [...loaded] });
  const observer = attachObserver(ctx, { publish: async frame => { frames.push(frame); }, heartbeatMs: 60000 });
  return { ctx, loaded, agent, header, frames, observer };
}

test('real Cordis event bus: baseline, actual stream start/end, idle, and no chunk inspection', async () => {
  const { ctx, agent, observer } = harness('running');
  try {
    await observer.ready;
    assert.equal(phase(observer), 'unknown');
    ctx.emit('agent/assistant-stream', { agent, frame: guard({ type: 'chunk' }, ['chunk', 'text', 'content']) });
    assert.equal(phase(observer), 'unknown');
    ctx.emit('agent/assistant-stream', { agent, frame: { type: 'start', attemptId: 'internal-attempt' } });
    assert.equal(phase(observer), 'generating');
    assert.equal(model(observer), 'actual-resolved-model');
    ctx.emit('agent/assistant-stream', { agent, frame: { type: 'end', attemptId: 'older-attempt' } });
    assert.equal(phase(observer), 'generating');
    ctx.emit('agent/assistant-stream', { agent, frame: guard({ type: 'end', attemptId: 'internal-attempt' }, ['outcome']) });
    assert.equal(phase(observer), 'unknown');
    assert.equal(model(observer), null);
    agent.status = 'idle'; ctx.emit('agent/status', { agent, status: 'idle' });
    assert.equal(phase(observer), 'idle');
    assert.equal(model(observer), null);
  } finally { await observer.stop(); }
});

test('actual tool wrapper preserves result/error and distinguishes parallel work from approval wait', async () => {
  const { ctx, agent, observer } = harness('running');
  try {
    await observer.ready;
    ctx.emit('agent/assistant-stream', { agent, frame: { type: 'start', attemptId: 'a' } });
    ctx.emit('agent/assistant-stream', { agent, frame: { type: 'end', attemptId: 'a' } });
    const answer = deferred(), other = deferred();
    const first = ctx.waterfall('tools/execute', guard({ agent, callId: 'one', rootCallId: 'one' }, ['arguments', 'name']),
      () => ctx.waterfall('approval/request', guard({ agent, callId: 'one' }, ['reason', 'toolName']), () => answer.promise));
    await tick();
    assert.equal(phase(observer), 'waiting_approval');
    assert.equal(model(observer), null);
    const second = ctx.waterfall('tools/execute', guard({ agent, callId: 'two', rootCallId: 'two' }, ['arguments']), () => other.promise);
    await tick();
    assert.equal(phase(observer), 'tool_running');
    assert.equal(model(observer), 'actual-resolved-model');
    const result = guard({}, ['content']); other.resolve(result);
    assert.equal(await second, result);
    assert.equal(phase(observer), 'waiting_approval');
    answer.resolve('allowed-once'); assert.equal(await first, 'allowed-once');
    assert.equal(phase(observer), 'unknown');
    const failure = new Error('sentinel');
    await assert.rejects(ctx.waterfall('tools/execute', { agent, callId: 'error', rootCallId: 'error' }, () => Promise.reject(failure)), error => error === failure);
    assert.equal(phase(observer), 'unknown');
  } finally { await observer.stop(); }
});

test('user-question wait only observes metadata, and uncorrelated waits stay unknown', async () => {
  const { ctx, agent, observer } = harness('running');
  try {
    await observer.ready;
    const answer = deferred();
    const task = ctx.waterfall('tools/execute', guard({ agent, callId: 'question', rootCallId: 'question' }, ['arguments']),
      () => ctx.waterfall('user-questions/request', guard({ agent, wait: { callId: 'question' } }, ['questions', 'prompt', 'options']), () => answer.promise));
    await tick(); assert.equal(phase(observer), 'waiting_input'); assert.equal(model(observer), null);
    answer.resolve({ accepted: true }); await task;
    const unknownWait = deferred(), tool = deferred();
    const running = ctx.waterfall('tools/execute', { agent, callId: 'known', rootCallId: 'known' }, () => tool.promise);
    const waiting = ctx.waterfall('user-questions/request', guard({ agent }, ['questions']), () => unknownWait.promise);
    await tick(); assert.equal(phase(observer), 'unknown');
    unknownWait.resolve(null); tool.resolve(null); await Promise.all([running, waiting]);
  } finally { await observer.stop(); }
});

test('complete registry reconciliation removes vanished instances; closing and expiry clear running models', async () => {
  const { ctx, loaded, agent, observer, frames } = harness('running');
  try {
    await observer.ready;
    ctx.emit('agent/assistant-stream', { agent, frame: { type: 'start', attemptId: 'private-session:1' } });
    const live = observer.state.snapshot(), now = Date.parse(live.observedAt);
    assert.equal(observeSnapshot(live, { now, producerAlive: true }).instances[0].phase, 'generating');
    assert.equal(observer.state.snapshot(false).instances[0].phase, 'unknown');
    assert.equal(observer.state.snapshot(false).instances[0].currentModel, null);
    for (const options of [{ now: now + TTL_MS, producerAlive: true }, { now, producerAlive: false }]) {
      const stale = observeSnapshot(live, options);
      assert.equal(stale.sourceLive, false); assert.equal(stale.instances[0].phase, 'unknown'); assert.equal(stale.instances[0].currentModel, null);
    }
    assert.ok(!JSON.stringify(live).includes('private-session'));
    loaded.splice(0); await observer.flush();
    assert.deepEqual(observer.state.snapshot().instances, []);
    await observer.stop(); assert.equal(frames.at(-1).sourceLive, false);
  } finally { await observer.stop(); }
});

test('snapshot output is an explicit metadata whitelist', () => {
  const state = new StatusState(), agent = guard({ status: 'idle' }, ['title', 'id', 'messages']);
  state.reconcile([agent]);
  const frame = state.snapshot();
  assert.deepEqual(Object.keys(frame.instances[0]).sort(), ['currentModel', 'id', 'phase']);
  const forged = { ...frame, credentials: 'never-copy', instances: [{ ...frame.instances[0], title: 'never-copy' }] };
  assert.ok(!JSON.stringify(observeSnapshot(forged, { now: Date.parse(frame.observedAt), producerAlive: true })).includes('never-copy'));
});

test('aborted approval stops being a wait even when its answerer has not settled', async () => {
  const { ctx, agent, observer, frames } = harness('running');
  try {
    await observer.ready;
    const answer = deferred(), controller = new AbortController();
    const pending = ctx.waterfall('approval/request', guard({ agent, callId: 'approval', signal: controller.signal }, ['reason']), () => answer.promise);
    await tick(); assert.equal(phase(observer), 'waiting_approval');
    controller.abort(); assert.equal(phase(observer), 'unknown');
    agent.status = 'idle'; ctx.emit('agent/status', { agent, status: 'idle' });
    assert.equal(phase(observer), 'idle');
    answer.resolve('cancelled'); assert.equal(await pending, 'cancelled');
    const later = deferred(), second = new AbortController();
    const pendingAfterUnload = ctx.waterfall('approval/request', { agent, callId: 'later', signal: second.signal }, () => later.promise);
    await observer.stop(); const published = frames.length;
    second.abort(); await tick(); assert.equal(frames.length, published);
    later.resolve('cancelled'); assert.equal(await pendingAfterUnload, 'cancelled');
  } finally { await observer.stop(); }
});

test('publisher creates a private directory and only publishes complete atomically replaced files', async () => {
  const parent = fileURLToPath(new URL('../.offline-fixtures/', import.meta.url));
  await mkdir(parent, { recursive: true });
  const state = new StatusState();
  const directory = path.join(parent, 'private-output-' + state.producerId);
  const publisher = await createPublisher(directory, state.producerId);
  await publisher.publish(state.snapshot());
  await publisher.publish(state.snapshot(false));
  const names = await readdir(directory);
  assert.deepEqual(names, [path.basename(publisher.filename)]);
  const frame = JSON.parse(await readFile(publisher.filename, 'utf8'));
  assert.equal(frame.sourceLive, false);
  assert.deepEqual(frame.instances, []);
  assert.ok((await lstat(publisher.filename)).isFile());
  // Fixtures contain synthetic metadata only and remain under this candidate.
});

test('normal Cordis plugin registration and disposal publish baseline then invalidate the producer', async () => {
  const parent = fileURLToPath(new URL('../.offline-fixtures/', import.meta.url));
  await mkdir(parent, { recursive: true });
  const directory = path.join(parent, 'plugin-' + crypto.randomUUID());
  const ctx = new Context(), agent = { status: 'idle' };
  ctx.provide('agents', { list: () => [agent] });
  const fiber = await ctx.plugin(plugin, { outputDirectory: directory });
  const names = await readdir(directory);
  assert.equal(names.length, 1);
  const filename = path.join(directory, names[0]);
  const baseline = JSON.parse(await readFile(filename, 'utf8'));
  assert.equal(baseline.sourceLive, true);
  assert.equal(baseline.instances[0].phase, 'idle');
  await fiber.dispose();
  const stopped = JSON.parse(await readFile(filename, 'utf8'));
  assert.equal(stopped.sourceLive, false);
  assert.equal(stopped.complete, false);
  assert.equal(stopped.instances[0].phase, 'unknown');
  assert.equal(stopped.instances[0].currentModel, null);
});
