import assert from 'node:assert/strict';
import test from 'node:test';
import { AgentRuntimeStore, publicAgentObservation, codexCliCandidates, applyCodexCliRuntime, collectAgentRuntime } from '../server/agent-runtime-store.mjs';
import { flattenSnapshot, externalFor, runtimeFor } from '../src/live-data.mjs';

const now = Date.parse('2026-10-22T15:00:00Z');
function row(patch = {}) {
  return { agentId: 'codex', observedAt: new Date(now).toISOString(), sourceStatus: 'partial',
    openWindows: 1, openSessions: 3, activeSessions: 1, idleSessions: 1, unknownSessions: 1,
    instances: [{ id: 'PRIVATE_ID', kind: 'conversation', state: 'running', model: 'gpt-6.1-sol', title: 'PRIVATE_TOPIC' },
      { state: 'idle', model: 'must-not-show' }, { state: 'unknown', model: 'must-not-show' }],
    coverage: '可以看到正在工作的对话。', title: 'PRIVATE_TOPIC', content: 'PRIVATE_BODY', ...patch };
}
test('runtime public boundary keeps status only and model only while running', () => {
  const result = publicAgentObservation(row(), now);
  assert.equal(result.activeSessions, 1);
  assert.deepEqual(result.instances.map(item => item.model), ['gpt-6.1-sol', null, null]);
  assert.doesNotMatch(JSON.stringify(result), /PRIVATE_|must-not-show|title|content/);
  assert.equal(publicAgentObservation(row({ agentId: 'unexpected' }), now), null);
  assert.equal(publicAgentObservation(row({ instances: [{ state: 'running', model: 'Bearer secret-token' }] }), now).instances[0].model, null);
});
test('missing or stale runtime observation does not become an idle zero or current model', () => {
  const stale = publicAgentObservation(row(), now + 61_000);
  assert.equal(stale.sourceStatus, 'unavailable');
  assert.equal(stale.activeSessions, null);
  assert.equal(stale.openWindows, null);
  assert.deepEqual(stale.instances, []);
  assert.equal(publicAgentObservation(row({ activeSessions: null }), now).activeSessions, null);
  assert.equal(publicAgentObservation(row({ activeSessions: -1 }), now).activeSessions, null);
});
test('runtime refresh does not overlap and a failed refresh immediately invalidates running claims', async () => {
  let resolve, calls = 0;
  const store = new AgentRuntimeStore({ now: () => now, collect: () => { calls++; return new Promise(done => { resolve = done; }); } });
  const first = store.refresh();
  await store.refresh();
  assert.equal(calls, 1);
  resolve([row()]); await first;
  assert.equal(store.get().agents[0].activeSessions, 1);
  store.collect = async () => { throw new Error('PRIVATE_PATH'); };
  await store.refresh();
  assert.equal(store.get().agents.find(agent => agent.agentId === 'codex').activeSessions, null);
  assert.doesNotMatch(JSON.stringify(store.get()), /PRIVATE_PATH/);
  store.stop();
});
test('stopping during collection publishes nothing and cancels the collector signal', async () => {
  let signal, resolve, receivedDirectory, disposals = 0;
  const store = new AgentRuntimeStore({ now: () => now, collectOptions: { dshStatusDirectory: 'fixture-status-only' }, collect: options => { signal = options.signal; receivedDirectory = options.dshStatusDirectory; return new Promise(done => { resolve = done; }); }, dispose: async () => { disposals++; } });
  const pending = store.refresh(); await store.stop(); await store.stop();
  assert.equal(signal.aborted, true);
  assert.equal(receivedDirectory, 'fixture-status-only');
  assert.equal(disposals, 1);
  resolve([row()]); await pending;
  assert.deepEqual(store.get(), { observedAt: null, agents: [] });
});

const jobId = '11111111-1111-7111-8111-111111111111';
const jobKey = `${jobId}--1`;
function externalFrame() {
  const work = { id: 'fixture', actorName: 'Codex', actorKind: 'executor', quality: 'Fresh', model: 'configured-model' };
  return { source: { status: 'live', stale: false }, projects: [
    { id: jobKey, isCurrent: true, phase: 'running_unknown', works: [work, { ...work, id: 'other', actorName: 'Cursor' }] },
    { id: `${jobId}--2`, isCurrent: false, phase: 'delivered', works: [work] },
    { id: '../private', isCurrent: true, phase: 'running_unknown', works: [work] },
  ] };
}
test('Codex CLI status decorates only its current job and does not change App counts or use configured models', () => {
  const frame = externalFrame();
  assert.deepEqual(codexCliCandidates(frame), [{ jobId, attempt: 1, jobKey }]);
  const agent = publicAgentObservation(row({ externalObservedAt: new Date(now).toISOString(),
    externalJobs: [{ jobKey, state: 'running', model: 'configured-model', threadId: 'PRIVATE_THREAD' }] }), now);
  const runtime = { observedAt: new Date(now).toISOString(), agents: [agent] };
  const output = { ...applyCodexCliRuntime(frame, runtime, now), agentRuntime: runtime };
  const rows = flattenSnapshot(output);
  assert.equal(rows.find(item => item.sourceProjectId === jobKey && item.agentId === 'codex').runtimeState, 'running');
  assert.equal(rows.find(item => item.sourceProjectId === jobKey && item.agentId === 'cursor').runtimeState, 'unknown');
  assert.equal(output.projects[1].works[0].runtimeState, undefined);
  assert.equal(frame.projects[0].works[0].runtimeState, undefined, 'projection must not mutate the stored CLI frame');
  assert.equal(runtimeFor(output, 'codex').activeSessions, 1);
  assert.equal(externalFor(rows.filter(item => item.sourceProjectId === jobKey), 'codex').activeCount, 1);
  assert.deepEqual(externalFor(rows.filter(item => item.sourceProjectId === jobKey), 'codex').models, ['模型未知']);
  assert.doesNotMatch(JSON.stringify(agent.externalJobs), /PRIVATE_|configured-model/);
});
test('Codex CLI evidence has an independent clock, expires, and cannot decorate stale or mismatched jobs', () => {
  const frame = externalFrame();
  const raw = row({ sourceStatus: 'unavailable', externalObservedAt: new Date(now).toISOString(), externalJobs: [
    { jobKey, state: 'running' }, { jobKey: '../private', state: 'running' },
  ] });
  const agent = publicAgentObservation(raw, now);
  assert.equal(agent.instances.length, 0);
  assert.equal(agent.externalJobs.length, 1, 'CLI remains observable when App is unavailable');
  assert.equal(applyCodexCliRuntime(frame, { agents: [agent] }, now).projects[0].works[0].runtimeState, 'running');
  assert.equal(applyCodexCliRuntime(frame, { agents: [agent] }, now + 61_000), frame);
  const stale = { ...frame, source: { ...frame.source, stale: true } };
  assert.deepEqual(codexCliCandidates(stale), []);
  assert.equal(applyCodexCliRuntime(stale, { agents: [agent] }, now), stale);
  const otherAttempt = publicAgentObservation({ ...raw, externalJobs: [{ jobKey: `${jobId}--3`, state: 'running' }] }, now);
  assert.equal(applyCodexCliRuntime(frame, { agents: [otherAttempt] }, now).projects[0].works[0].runtimeState, undefined);
});
test('Codex CLI and local agents reuse one process snapshot per refresh', async () => {
  const snapshot = { available: true, windowsAvailable: true, processes: [], parents: [] };
  let processReads = 0, cliReads = 0;
  const result = await collectAgentRuntime({
    readHost: async () => ({ observedAt: new Date().toISOString(), openWindows: 0, hosts: [], endpoints: [] }),
    readProcessSnapshot: async () => { processReads++; return snapshot; },
    codexCliStateRoot: 'fixture-state', codexCliHome: 'fixture-home',
    readExternalCandidates: async () => [{ jobId, attempt: 1, jobKey }],
    collectCliRuntime: async args => { cliReads++; assert.equal(args.processSnapshot, snapshot); assert.equal(args.candidates[0].jobKey, jobKey); return [{ jobKey, state: 'idle', model: null }]; },
  });
  assert.equal(processReads, 1); assert.equal(cliReads, 1);
  const codex = result.find(agent => agent.agentId === 'codex');
  assert.equal(codex.openSessions, 0);
  assert.deepEqual(codex.externalJobs, [{ jobKey, state: 'idle', model: null }]);
});
