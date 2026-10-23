import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { collectLocalAgents, projectLocalAgents } from '../server/local-agents.mjs';

const at = '2026-10-22T15:40:00.000Z';
const snapshot = processes => ({ available: true, windowsAvailable: true, processes });
const processRow = (pid, parentPid, agentId, type, windows = []) => ({ pid, parentPid, agentId, type, windows });

test('one Electron window stays one window, embedded Claude CLI is not a second session', () => {
  const observed = snapshot([
    processRow(10, 1, 'claude', 'desktop', ['100']),
    processRow(11, 10, 'claude', 'desktop'),
    processRow(12, 10, 'claude', 'desktop', ['100']),
    processRow(13, 14, 'claude', 'cli'),
  ]);
  observed.parents = [{ pid: 14, parentPid: 10 }];
  const rows = projectLocalAgents(observed, { observedAt: at });
  const claude = rows.find(row => row.agentId === 'claude');
  assert.equal(claude.openWindows, 1);
  assert.equal(claude.instances.length, 1);
  assert.equal(claude.instances[0].kind, 'window');
  assert.equal(claude.instances[0].state, 'unknown');
  assert.equal(claude.openSessions, null);
  assert.equal(claude.activeSessions, null);
  assert.equal(claude.idleSessions, null);
});

test('standalone CLI roots are distinct from windows; nested CLI wrappers are deduplicated', () => {
  const rows = projectLocalAgents(snapshot([
    processRow(20, 1, 'grok-bot', 'desktop', ['200']),
    processRow(21, 1, 'grok', 'cli'),
    processRow(22, 21, 'grok', 'cli'),
    processRow(23, 1, 'pi', 'cli'),
  ]));
  const grok = rows.find(row => row.agentId === 'grok');
  assert.equal(grok.openWindows, 0);
  assert.equal(grok.instances.length, 1);
  assert.equal(grok.instances[0].kind, 'conversation');
  assert.equal(grok.instances[0].state, 'unknown');
  assert.equal(rows.find(row => row.agentId === 'grok-bot').openWindows, 1);
  assert.equal(grok.sourceStatus, 'partial');
  assert.equal(grok.activeSessions, null);
});

test('missing source stays null, while a successful empty window observation is zero', async () => {
  const missing = await collectLocalAgents({ readProcessSnapshot: async () => ({ available: false }), now: () => new Date(at) });
  assert.equal(missing.length, 7);
  assert.ok(missing.every(row => row.sourceStatus === 'unavailable' && row.openWindows === null && row.activeSessions === null));
  assert.ok(missing.every(row => row.observedAt === at && row.instances.length === 0));
  const empty = projectLocalAgents(snapshot([]));
  assert.ok(empty.every(row => row.openWindows === 0 && row.activeSessions === null));
  assert.ok(!empty.some(row => row.agentId === 'codex'));
});

test('process fields never infer active, idle, or a current model', () => {
  const rows = projectLocalAgents(snapshot([
    { ...processRow(40, 1, 'dsh', 'desktop', ['400']), model: 'process-model', state: 'running' },
    { ...processRow(41, 1, 'grok-bot', 'desktop', ['401']), model: 'process-model', state: 'idle' },
  ]));
  assert.ok(rows.flatMap(row => row.instances).every(instance => instance.model === null && instance.state !== 'running'));
  assert.ok(rows.flatMap(row => row.instances).every(instance => instance.state === 'unknown'));
  assert.ok(!JSON.stringify(rows).includes('process-model'));
});

test('private fields are not inspected or projected', () => {
  const observed = { ...processRow(50, 1, 'dsh', 'desktop', ['500']) };
  for (const key of ['title', 'todo', 'inbox', 'messages', 'commandLine', 'executablePath']) Object.defineProperty(observed, key,
    { get() { throw new Error(`Private field accessed: ${key}`); } });
  const row = projectLocalAgents(snapshot([observed]))[0];
  const serialized = JSON.stringify(row);
  assert.ok(!/commandLine|executablePath|messages|title|inbox|todo/.test(serialized));
  assert.deepEqual(Object.keys(row.instances[0]).sort(), ['id', 'kind', 'model', 'state']);
  assert.notEqual(row.instances[0].id, '500');
});

test('native window helper never requests titles or emits command lines', async () => {
  const source = await readFile(new URL('../server/local-agent-process.ps1', import.meta.url), 'utf8');
  assert.ok(!/GetWindowText|MainWindowTitle|GetForegroundWindow|SetForegroundWindow/.test(source));
  const emitted = source.slice(source.indexOf('$rows.Add'));
  assert.ok(!/CommandLine|ExecutablePath|\$command|\$exe/.test(emitted));
});
