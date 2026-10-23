import assert from 'node:assert/strict';
import test from 'node:test';
import { AgentRuntimeStore, publicAgentObservation } from '../server/agent-runtime-store.mjs';

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
