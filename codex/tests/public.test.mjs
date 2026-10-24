import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import readline from 'node:readline';
import { loadConfig } from '../plugin/lib/config.mjs';
import { AppMailboxEngine, MailboxDriver } from '../plugin/lib/app-mailbox.mjs';
import { OfficialTools, resolveInstalledOfficialEntry } from '../plugin/lib/official-tools.mjs';
import { handleRequest, TOOLS, GRANT_DELEGATION_TOOL as GRANT, REVOKE_DELEGATION_TOOL as REVOKE } from '../plugin/lib/mcp-service.mjs';
import { readJson, createJsonOnce } from '../plugin/lib/store.mjs';
import { key } from '../plugin/lib/ids.mjs';
import { WakeBatchJournal, combinedPrompt, selectPromptBatch, MAX_WAKE_PROMPT_BYTES } from '../plugin/lib/wake-batch.mjs';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const ledger = path.join(root, 'tools', 'mailbox.py');
const python = [process.env.MAIL_WAKE_TEST_PYTHON, 'python3', 'python'].filter(Boolean).find(command => {
  const result = spawnSync(command, ['-c', 'import sys; sys.exit(0 if sys.version_info >= (3,10) else 1)'], { windowsHide: true });
  return result.status === 0;
});
assert.ok(python, 'Python 3.10+ must be on PATH, or set MAIL_WAKE_TEST_PYTHON');
const owner = 'test-lead', coordinator = 'test-coordinator', actor = 'test-actor';
const write = (filename, value) => { fs.mkdirSync(path.dirname(filename), { recursive: true }); fs.writeFileSync(filename, JSON.stringify(value)); };
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(check) { for (let count = 0; count < 250; count++) { if (check()) return; await pause(20); } throw new Error('Timed out waiting for test state'); }
function cli(f, args, success = true, cwd) {
  const options = { encoding: 'utf8', windowsHide: true, shell: false };
  if (cwd) options.cwd = cwd;
  const result = spawnSync(python, ['-B', '-X', 'utf8', ledger, '--root', f.mailboxRoot, ...args], options);
  const detail = `${result.stdout}\n${result.stderr}`;
  if (success) assert.equal(result.status, 0, detail); else assert.equal(result.status, 1, detail);
  return JSON.parse(result.stdout);
}
function fixture(t) {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), 'agent-lead-wake-test-'));
  const canonicalBase = fs.realpathSync(base);
  const f = { base, mailboxRoot: path.join(base, 'ledger'), stateDir: path.join(base, 'state'), control: path.join(base, 'control.json'), log: path.join(base, 'host.jsonl'), engines: [] };
  const home = path.join(base, 'fake-codex-home'); const cache = path.join(home, 'plugins', 'cache', 'openai-bundled', 'codex-app-tools', '0.0.1');
  write(path.join(cache, '.codex-plugin', 'plugin.json'), { name: 'codex-app-tools', version: '0.0.1', author: { name: 'OpenAI' } });
  fs.copyFileSync(path.join(root, 'tests', 'fixtures', 'fake-host.mjs'), path.join(cache, 'server.mjs'));
  write(f.control, {});
  f.config = path.join(base, 'config.json'); write(f.config, { mailboxRoot: './ledger', stateDir: './state', coordinatorThreadId: coordinator, pythonExecutable: python });
  const inherited = Object.fromEntries(Object.entries(process.env).filter(([name]) => !name.startsWith('MAIL_WAKE_')));
  f.env = { ...inherited, CODEX_HOME: home, CODEX_APP_TOOLS_PIPE_PATH: 'test-launch-pipe', CODEX_THREAD_ID: actor,
    HOME: base, USERPROFILE: base,
    MAIL_WAKE_CONFIG: f.config, TEST_CONTROL: f.control, TEST_LOG: f.log, TEST_STATE: f.stateDir };
  f.env.TEST_KEY_MODULE = pathToFileURL(path.join(root, 'plugin', 'lib', 'ids.mjs')).href;
  cli(f, ['route', '--project', 'demo', '--owner', owner]);
  t.after(async () => {
    for (const engine of f.engines) engine.close(); await pause(100);
    // Recursive cleanup is limited to the exact freshly created OS temp fixture.
    const resolved = fs.realpathSync(base); const temp = fs.realpathSync(os.tmpdir());
    assert.equal(resolved, canonicalBase); assert.equal(path.dirname(resolved), temp); assert.ok(path.basename(resolved).startsWith('agent-lead-wake-test-'));
    fs.rmSync(resolved, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 });
  });
  return f;
}
function job(f, jobId = 'job', attempt = '1', source = 'cli', marker = {}, project = 'demo') {
  const outbox = path.join(f.base, 'outboxes', `${jobId}-${attempt}`); fs.mkdirSync(outbox, { recursive: true });
  const registration = cli(f, ['register', '--project', project, '--job', jobId, '--attempt', attempt, '--outbox', outbox, '--source', source]);
  fs.writeFileSync(path.join(outbox, 'REPORT.md'), `Original ${source} report ${jobId}/${attempt}.\n`);
  fs.writeFileSync(path.join(outbox, 'result.txt'), 'Example artifact');
  write(path.join(outbox, 'READY.json'), { status: 'completed', deliverables: ['result.txt'], notes: 'Example', ...marker });
  return { id: registration.key, outbox };
}
function engine(f, options = {}) {
  const instance = new AppMailboxEngine({ stateDir: f.stateDir, coordinatorThreadId: coordinator, env: f.env,
    driver: new MailboxDriver({ root: f.mailboxRoot, pythonPath: python }), client: new OfficialTools({ env: f.env, timeoutMs: 250 }), ...options });
  f.engines.push(instance); return instance;
}
const events = f => fs.existsSync(f.log) ? fs.readFileSync(f.log, 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse) : [];
const sends = f => events(f).filter(item => item.params?.name === 'send_message_to_thread');
const deliveries = f => sends(f).filter(item => item.params.arguments.prompt.startsWith('[app-mailbox:'));
const faults = f => sends(f).filter(item => item.params.arguments.prompt.startsWith('[app-mailbox-transport-fault:'));
const central = (f, id) => readJson(path.join(f.mailboxRoot, 'deliveries', `${id}.json`));

test('default send deadline preserves real 31-second acceptance and refusal without repeating either delivery', { timeout: 45000 }, async t => {
  await Promise.all(['accept', 'reject'].map(async mode => {
    const f = fixture(t); const item = job(f, mode);
    const e = engine(f, { client: new OfficialTools({ env: f.env }) });
    assert.equal(e.client.timeoutMs, 20000); assert.equal(e.client.sendTimeoutMs, 60000);
    write(f.control, { sendDelayMs: 31000, sendMode: mode });
    e.startWatch(coordinator); assert.equal(await e.canPoll(), true); await e.driver.run('scan');
    const started = Date.now(); await e.processReady(item);
    assert.ok(Date.now() - started >= 31000);
    assert.equal(central(f, item.id).state, mode === 'accept' ? 'sent' : 'sending');
    assert.equal(readJson(e.localPath(item.id, 'outcome.json')).status, mode === 'accept' ? 'sent' : 'uncertain');
    assert.equal(readJson(e.localPath(item.id, 'receipt.json')).isError, mode === 'reject');
    assert.equal(e.bridgeUnavailable, false);
    await e.processReady(item); assert.equal(deliveries(f).length, 1);
  }));
});

test('one read deadline leaves a concurrent original send and the engine bridge alive', async t => {
  const f = fixture(t); const item = job(f);
  const e = engine(f, { client: new OfficialTools({ env: f.env, timeoutMs: 250, sendTimeoutMs: 1500 }) });
  write(f.control, { sendDelayMs: 800, delayedReadThread: 'slow-reader', readDelayMs: 650 });
  e.startWatch(coordinator); assert.equal(await e.canPoll(), true); await e.driver.run('scan');
  const sending = e.processReady(item); await until(() => deliveries(f).length === 1);
  const originalHost = e.client.child;
  await assert.rejects(e.client.readThread('slow-reader', actor), error => {
    assert.equal(error.code, 'MCP_REQUEST_TIMEOUT'); assert.equal(e.bridgeFailed(error), false); return true;
  });
  assert.equal(e.client.child, originalHost); assert.equal(e.bridgeUnavailable, false);
  await sending;
  assert.equal(central(f, item.id).state, 'sent'); assert.equal(e.client.pending.size, 0);
  assert.equal(deliveries(f).length, 1);
});

test('target OS leases exclude a second process, preserve case-sensitive targets and release on holder exit', async t => {
  const f = fixture(t), a = new MailboxDriver({ root: f.mailboxRoot, pythonPath: python }), b = new MailboxDriver({ root: f.mailboxRoot, pythonPath: python });
  f.engines.push({ close() { a.close(); b.close(); } });
  let first, other, recovered;
  try {
    first = await a.acquireTarget('same-owner', f.stateDir);
    assert.equal(await b.acquireTarget('same-owner', f.stateDir), null);
    other = await b.acquireTarget('SAME-owner', f.stateDir); assert.equal(other.alive, true);
    const exited = once(first.child, 'close'); first.child.kill(); await exited;
    assert.equal(first.alive, false);
    assert.ok(fs.existsSync(path.join(f.stateDir, 'target-send-locks', key('same-owner') + '.lock')));
    recovered = await b.acquireTarget('same-owner', f.stateDir); assert.equal(recovered.alive, true);
  } finally { await other?.release(); await recovered?.release(); }
});

test('delivery and fault notifications share the target gate and delivery rechecks the owner after contention', async t => {
  const f = fixture(t); cli(f, ['route', '--project', 'demo', '--owner', coordinator, '--replace-owner']);
  const item = job(f); const e = engine(f); e.startWatch(coordinator);
  assert.equal(await e.canPoll(), true); await e.driver.run('scan');
  e.recordFault(central(f, item.id), 'fixture', 'Synthetic fault', e.localPath(item.id, 'wait.json'));
  const other = new MailboxDriver({ root: f.mailboxRoot, pythonPath: python }); f.engines.push({ close() { other.close(); } });
  let held = await other.acquireTarget(coordinator, e.stateDir);
  try {
    await e.processReady(item); await e.flushFaults();
    assert.equal(events(f).length, 0, 'busy target does not even start preflight or send');
    assert.equal(central(f, item.id).state, 'pending');
    const faultId = fs.readdirSync(path.join(e.stateDir, 'faults'))[0];
    assert.equal(fs.existsSync(e.faultPath(faultId, 'intent.json')), false);
    await held.release(); held = null;
    await e.flushFaults(); assert.equal(faults(f).length, 1);
    write(f.control, { ownerStatus: 'active' }); await e.processReady(item);
    assert.equal(central(f, item.id).state, 'pending'); assert.equal(deliveries(f).length, 0);
    write(f.control, { ownerStatus: 'idle' }); await e.processReady(item);
    assert.equal(central(f, item.id).state, 'sent');
    await e.flushFaults(); await e.processReady(item);
    assert.equal(faults(f).length, 1); assert.equal(deliveries(f).length, 1);
  } finally { await held?.release(); }
});

test('loss of the target lease during preflight leaves the original delivery unclaimed', async t => {
  const f = fixture(t); const item = job(f); const e = engine(f);
  const acquire = e.driver.acquireTarget.bind(e.driver); let targetLease;
  e.driver.acquireTarget = async (...args) => { targetLease = await acquire(...args); return targetLease; };
  const read = e.client.readThread.bind(e.client);
  e.client.readThread = async (...args) => {
    const result = await read(...args); const closed = once(targetLease.child, 'close'); targetLease.child.kill(); await closed; return result;
  };
  e.startWatch(coordinator); await e.tick();
  assert.equal(central(f, item.id).state, 'pending'); assert.equal(sends(f).length, 0);
  assert.equal(fs.existsSync(path.join(f.mailboxRoot, 'claims', item.id + '.lock')), false);
});

test('known legacy read incompatibility uses only an exact visible local status and keeps refusal or uncertainty pending', async t => {
  const f = fixture(t); const item = job(f); const e = engine(f); e.startWatch(coordinator);
  const entry = status => ({ id: owner, kind: 'codex', hostId: 'local', status });
  const blocked = [
    { listedThreads: [entry('active')] }, { listedThreads: [entry('systemError')] },
    { listedThreads: [] }, { listedThreads: [entry('unknown')] },
    { listedThreads: [{ ...entry('idle'), id: 'different-owner' }] },
    { listedThreads: [{ ...entry('idle'), hostId: 'remote' }] },
    { listedThreads: [{ ...entry('idle'), kind: 'chatgpt' }] },
    { listedPinned: [entry('idle')], listedThreads: [entry('active')] },
    { listedThreads: [entry('idle')], listReject: true },
  ];
  for (const control of blocked) {
    write(f.control, { readLegacy: true, ...control }); await e.tick();
    assert.equal(central(f, item.id).state, 'pending'); assert.equal(deliveries(f).length, 0);
  }
  const lists = () => events(f).filter(event => event.params?.name === 'list_threads');
  const listCount = lists().length;
  write(f.control, { readReject: true, listedThreads: [entry('idle')] }); await e.tick();
  assert.equal(lists().length, listCount, 'ordinary refusals never enter compatibility lookup');
  write(f.control, { readLegacy: true, listedPinned: [entry('idle')] }); await e.tick();
  assert.equal(central(f, item.id).state, 'sent');
  const second = job(f, 'not-loaded');
  write(f.control, { readLegacy: true, listedThreads: [entry('notLoaded')] }); await e.tick();
  assert.equal(central(f, second.id).state, 'sent'); assert.equal(deliveries(f).length, 2);
  assert.ok(lists().every(event => event.params.arguments.limit === 50));
});

test('watch withdrawal before or during the legacy status lookup never claims or sends', async t => {
  for (const stopAfter of ['read_thread', 'list_threads']) {
    const f = fixture(t); const item = job(f); const e = engine(f); e.startWatch(coordinator);
    write(f.control, { readLegacy: true, listedThreads: [{ id: owner, kind: 'codex', hostId: 'local', status: 'idle' }] });
    const call = e.client.call.bind(e.client);
    e.client.call = async (name, ...args) => { const result = await call(name, ...args); if (name === stopAfter) e.stopWatch(coordinator); return result; };
    await e.tick(); assert.equal(central(f, item.id).state, 'pending'); assert.equal(sends(f).length, 0);
    assert.equal(fs.existsSync(path.join(f.mailboxRoot, 'claims', item.id + '.lock')), false);
    if (stopAfter === 'read_thread') assert.equal(events(f).filter(event => event.params?.name === 'list_threads').length, 0);
  }
});

test('configuration and MCP identity fail closed; absent launch pipe or actor cannot scan', async t => {
  const f = fixture(t);
  const homes = { CODEX_HOME: path.join(f.base, 'default-home'), HOME: f.base, USERPROFILE: f.base };
  assert.throws(() => loadConfig(homes), /configuration is required/);
  assert.equal(TOOLS.length, 6); assert.ok(TOOLS.every(tool => !tool.name.includes('subscription')));
  const missing = spawnSync(process.execPath, [path.join(root, 'plugin', 'server.mjs')], { env: homes, encoding: 'utf8', windowsHide: true });
  assert.equal(missing.status, 1); assert.ok(missing.stderr.includes('configuration is required'));
  const standard = { mailboxRoot: '../ledger', stateDir: '../state', coordinatorThreadId: coordinator, pythonExecutable: python };
  write(path.join(homes.CODEX_HOME, 'mail-wake.config.json'), standard);
  assert.equal(loadConfig(homes).mailboxRoot, f.mailboxRoot);
  write(path.join(f.base, '.codex', 'mail-wake.config.json'), standard);
  assert.equal(loadConfig({ HOME: f.base, USERPROFILE: f.base }).stateDir, f.stateDir);
  assert.equal(loadConfig({ HOME: f.base }).coordinatorThreadId, coordinator);
  write(path.join(homes.CODEX_HOME, 'mail-wake.config.json'), { ...standard, coordinatorThreadId: null });
  assert.throws(() => loadConfig(homes), /coordinatorThreadId configuration is required/);
  assert.equal(loadConfig({ ...homes, MAIL_WAKE_CONFIG: f.config }).coordinatorThreadId, coordinator, 'explicit file wins over standard file');
  assert.throws(() => loadConfig({ ...homes, MAIL_WAKE_CONFIG: path.join(f.base, 'missing.json') }), /does not exist/);
  const item = job(f); const config = loadConfig({ MAIL_WAKE_CONFIG: f.config });
  assert.equal(config.mailboxRoot, f.mailboxRoot); assert.equal(config.stateDir, f.stateDir);
  const noPipe = engine(f, { client: new OfficialTools({ env: { ...f.env, CODEX_APP_TOOLS_PIPE_PATH: '' } }) });
  noPipe.startWatch(coordinator); await noPipe.tick(); assert.equal(central(f, item.id), null); assert.equal(noPipe.getStatus(coordinator).service.runtimeState, 'no_pipe'); noPipe.close();
  const noActor = engine(f, { env: {} }); await noActor.tick(); assert.equal(central(f, item.id), null);
  assert.equal(noActor.getStatus(coordinator).service.runtimeState, 'identity_unavailable');
  const call = (name, args, who) => handleRequest({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name, arguments: args,
    _meta: { 'x-codex-turn-metadata': { thread_id: who } } } }, noActor, {});
  assert.equal((await call('start_app_mailbox_watch', { project: 'demo', target: 'other', model: 'override' }, owner)).isError, true);
  assert.equal((await call('start_app_mailbox_watch', {}, owner)).isError, true);
  assert.equal((await call('app_mailbox_status', { project: 'demo' }, owner)).isError, false);
  assert.equal(noActor.actorThreadId, owner); await noActor.tick(); assert.equal(central(f, item.id).state, 'sent');
  cli(f, ['route', '--project', 'Demo', '--owner', owner]);
  noActor.stopWatch(owner, { project: 'Demo' });
  const projects = noActor.getStatus(owner).projects;
  assert.equal(projects.find(item => item.project === 'demo').watchEnabled, true);
  assert.equal(projects.find(item => item.project === 'Demo').watchEnabled, false);
  assert.notEqual(key('demo').toLowerCase(), key('Demo').toLowerCase());
  assert.ok(key('X'.repeat(220)).length <= 80);
  const upperOutbox = path.join(f.base, 'upper-outbox'); fs.mkdirSync(upperOutbox);
  const upperJob = cli(f, ['register', '--project', 'Demo', '--job', 'job', '--attempt', '1', '--outbox', upperOutbox]);
  assert.notEqual(upperJob.key.toLowerCase(), item.id.toLowerCase());
  assert.equal(readJson(path.join(f.mailboxRoot, 'jobs', `${upperJob.key}.json`)).project, 'Demo');
  assert.equal(readJson(path.join(f.mailboxRoot, 'jobs', `${item.id}.json`)).project, 'demo');
});

test('portable CLI/Bot READY contract snapshots original reports and delivers exact claim prompt with actual source', async t => {
  const f = fixture(t); write(f.control, { ownerStatus: 'notLoaded' });
  const a = job(f, 'cli-job'); const b = job(f, 'bot-job', '1', 'bot', { status: 'blocked' }); const e = engine(f);
  e.startWatch(coordinator); await e.tick();
  for (const item of [a, b]) {
    const value = central(f, item.id); assert.equal(value.state, 'sent');
    const sent = deliverySends(f, item.id)[0];
    assert.ok(sent.params.arguments.prompt.includes(value.prompt)); assert.equal(sent.params.arguments.threadId, owner);
    assert.deepEqual(sent.params._meta, { 'x-codex-turn-metadata': { thread_id: actor } });
    assert.deepEqual(Object.keys(sent.params.arguments).sort(), ['prompt', 'threadId']);
    assert.equal(value.prompt.split('\n')[1], '此消息由收件系统自动投递。');
    assert.equal(fs.readFileSync(path.join(f.mailboxRoot, 'snapshots', item.id, 'REPORT.md'), 'utf8'), fs.readFileSync(path.join(item.outbox, 'REPORT.md'), 'utf8'));
    assert.equal(fs.readFileSync(path.join(f.mailboxRoot, 'snapshots', item.id, 'result.txt'), 'utf8'), 'Example artifact');
  }
  // Later same-source events/READY changes do not replace a consumed attempt.
  write(path.join(a.outbox, 'READY.json'), { status: 'failed', deliverables: [] });
  await e.tick(); assert.equal(deliveries(f).length, 1); assert.equal(central(f, a.id).state, 'sent');
  const next = job(f, 'cli-job', '2'); await e.tick(); assert.equal(central(f, next.id).state, 'sent'); assert.equal(deliveries(f).length, 2);
  const intent = readJson(e.localPath(a.id, 'intent.json')); assert.equal(intent.permissionRegisteredBy, coordinator); assert.equal(intent.callerThreadId, actor);
  assert.equal(Object.hasOwn(intent, 'promptSha256'), false); assert.equal(Object.hasOwn(intent, 'pipeFingerprint'), false);
  e.stopWatch(owner, { project: 'demo' }); assert.equal(e.getStatus(owner).projects[0].watchEnabled, false);
});

test('ledger isolates escaping or damaged inputs, pins registrations and preserves atomic claims with idempotent exact ack', async t => {
  const f = fixture(t); const good = job(f, 'good'); const bad = job(f, 'bad', '1', 'cli', { deliverables: ['../outside.txt'] });
  assert.equal(cli(f, ['register', '--project', 'demo', '--job', 'good', '--attempt', '1', '--outbox', good.outbox]).key, good.id);
  assert.ok(cli(f, ['register', '--project', 'demo', '--job', 'good', '--attempt', '2', '--outbox', good.outbox], false).error.includes('already bound'));
  assert.ok(cli(f, ['register', '--project', 'demo', '--job', 'other-job', '--attempt', '1', '--outbox', good.outbox], false).error.includes('already bound'));
  fs.writeFileSync(path.join(f.base, 'outboxes', 'outside.txt'), 'Outside input'); fs.writeFileSync(path.join(f.mailboxRoot, 'jobs', 'broken.json'), '{bad');
  const scan = cli(f, ['scan']); assert.equal(scan.ready.length, 1); assert.equal(scan.ready[0].id, good.id); assert.equal(scan.errors.length, 2); assert.equal(central(f, bad.id), null);
  const claimed = cli(f, ['claim', good.id]); assert.equal(claimed.state, 'sending');
  assert.ok(cli(f, ['claim', good.id], false).error.includes('not claimable'));
  const receipt = path.join(f.base, 'receipt.json'); write(receipt, { structuredContent: { threadId: 'wrong-owner' } });
  assert.ok(cli(f, ['ack', good.id, receipt], false).error.includes('exact original-owner'));
  write(receipt, { structuredContent: { threadId: owner }, isError: false }); assert.equal(cli(f, ['ack', good.id, receipt]).state, 'sent');
  write(receipt, { isError: true }); assert.equal(cli(f, ['ack', good.id, receipt]).state, 'sent');
  assert.ok(fs.existsSync(path.join(f.mailboxRoot, 'claims', `${good.id}.lock`)));
  assert.ok(cli(f, ['route', '--project', 'demo', '--owner', 'replacement'], false).error.includes('--replace-owner'));
  cli(f, ['route', '--project', 'demo', '--owner', 'replacement', '--replace-owner']);
  assert.ok(cli(f, ['register', '--project', 'demo', '--job', 'good', '--attempt', '1', '--outbox', good.outbox], false).error.includes('immutable'));
  assert.equal(central(f, good.id).target_thread_id, owner);
  const e = engine(f); e.startWatch(coordinator); await e.tick(); await e.tick(); assert.equal(faults(f).length, 0);
  await e.tick(); await e.tick(); assert.equal(faults(f).length, 1, 'same registered collector error alerts once after three polls');
  assert.equal(deliveries(f).length, 0);
});

test('one OS leader scans; retained lock files and permanent claims survive lease exit and takeover', async t => {
  const f = fixture(t); let at = Date.now(); const a = engine(f, { now: () => at }); const b = engine(f, { now: () => at });
  const first = job(f); a.startWatch(coordinator); await a.tick(); await b.tick();
  assert.equal(a.getStatus(coordinator).service.runtimeState, 'leader'); assert.equal(b.getStatus(coordinator).service.runtimeState, 'standby');
  assert.equal(deliveries(f).length, 1); assert.equal(b.getStatus(coordinator).service.observedLeader.instanceId, a.instanceId);
  const lease = a.leader.child; lease.kill(); await once(lease, 'close'); at += 60001;
  const next = job(f, 'job', '2'); await b.tick(); assert.equal(central(f, next.id).state, 'sent'); assert.equal(deliveries(f).length, 2);
  const blocked = job(f, 'job', '3'); write(f.control, { ownerStatus: 'active' }); await b.tick(); assert.equal(central(f, blocked.id).state, 'pending');
  at += 2 * 60 * 60 * 1000; await b.tick(); await b.tick(); assert.equal(faults(f).length, 1);
  write(f.control, { ownerStatus: 'notLoaded' }); await b.tick(); assert.equal(central(f, blocked.id).state, 'sent');
  assert.ok(fs.existsSync(path.join(f.mailboxRoot, '.codex-mail-wake.leader.lock'))); assert.ok(fs.existsSync(path.join(f.mailboxRoot, 'claims', `${first.id}.lock`)));
});

test('request deadlines stay isolated; confirmed process failure uses same-pipe health recovery without repeating uncertain sends', async t => {
  const f = fixture(t); let at = Date.now(); const e = engine(f, { now: () => at }); const first = job(f);
  write(f.control, { sendMode: 'timeout' }); e.startWatch(coordinator); await e.tick();
  assert.equal(central(f, first.id).state, 'sending'); assert.equal(readJson(e.localPath(first.id, 'outcome.json')).status, 'uncertain');
  assert.equal(e.bridgeUnavailable, false, 'a request deadline alone does not close the bridge');
  const failedHost = e.client.child; failedHost.kill(); await once(failedHost, 'close');
  assert.equal(e.bridgeUnavailable, true, 'actual host process exit still requires health recovery');
  createJsonOnce(e.localPath('old-history', 'intent.json'), { deliveryId: 'old-history', project: 'demo', ownerThreadId: owner });
  const next = job(f, 'job', '2'); write(f.control, { readReject: true }); at += 60000; await e.tick(); assert.equal(central(f, next.id), null);
  const actorReads = () => events(f).filter(event => event.params?.name === 'read_thread' && event.params.arguments.threadId === actor).length;
  assert.equal(actorReads(), 1); at += 59999; await e.tick(); assert.equal(actorReads(), 1);
  write(f.control, {}); at += 1; await e.tick(); assert.equal(central(f, next.id).state, 'sent'); assert.equal(actorReads(), 2);
  assert.equal(deliveries(f).filter(event => event.params.arguments.prompt.startsWith(`[app-mailbox:${first.id}]`)).length, 1);
  assert.equal(readJson(e.localPath('old-history', 'outcome.json')).status, 'uncertain'); assert.equal(faults(f).length, 2);
  await e.tick(); assert.equal(faults(f).length, 2); assert.ok(e.getStatus(coordinator).ledgerProblems.some(item => item.deliveryId === 'old-history'));
  assert.ok(events(f).every(event => event.pipe === 'test-launch-pipe'));
});

function service(f, filename, env = f.env) {
  const child = spawn(process.execPath, [filename], { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let sequence = 0; const pending = new Map(); let stderr = '';
  child.stderr.on('data', data => { stderr += data; });
  readline.createInterface({ input: child.stdout }).on('line', line => { const result = JSON.parse(line); const operation = pending.get(result.id); if (operation) { pending.delete(result.id); operation.resolve(result.result); } });
  let toolCalls = 0;
  const call = (method, params = {}) => new Promise((resolve, reject) => {
    if (method === 'tools/call') toolCalls++;
    const id = ++sequence; const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Test service timeout: ${method}; ${stderr}`)); }, 10000);
    pending.set(id, { resolve: value => { clearTimeout(timer); resolve(value); } });
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
  });
  return { child, call, toolCalls: () => toolCalls, stderr: () => stderr };
}

test('standard stdio process restart recovers durable exact receipt with ack only; installed dependency lookup stays in current cache', async t => {
  const f = fixture(t); const item = job(f); const cache = path.join(f.env.CODEX_HOME, 'plugins', 'cache', 'openai-bundled', 'codex-app-tools');
  assert.equal(resolveInstalledOfficialEntry({ cacheRoot: cache }).version, '0.0.1');
  assert.throws(() => resolveInstalledOfficialEntry({ cacheRoot: cache, appToolsEntry: path.join(root, 'tests', 'fixtures', 'fake-host.mjs') }), /valid server.mjs/);
  const first = service(f, path.join(root, 'tests', 'fixtures', 'crash-service.mjs')); let second;
  try {
    assert.equal((await first.call('initialize')).serverInfo.version, '0.5.0');
    await first.call('tools/call', { name: 'start_app_mailbox_watch', arguments: { project: 'demo' }, _meta: { 'x-codex-turn-metadata': { thread_id: owner } } });
    await until(() => first.child.exitCode === 17); assert.equal(deliveries(f).length, 1); assert.equal(central(f, item.id).state, 'sending');
    second = service(f, path.join(root, 'plugin', 'server.mjs')); await second.call('initialize');
    await until(() => central(f, item.id).state === 'sent'); assert.equal(deliveries(f).length, 1);
    assert.equal((await second.call('tools/list')).tools.length, 6); assert.equal(second.stderr(), '');
  } finally {
    if (first.child.exitCode === null) { first.child.kill(); await once(first.child, 'close'); }
    if (second) { second.child.stdin.end(); await once(second.child, 'close'); }
  }
});

test('public prelaunch rejection contract binds identity and paths, delivers once and preserves existing or unknown work', async t => {
  const f = fixture(t);
  const prepare = (name, attempt = '1') => {
    const outbox = path.join(f.base, 'outboxes', `${name}-${attempt}`); fs.mkdirSync(outbox, { recursive: true });
    const record = cli(f, ['register', '--project', 'demo', '--job', name, '--attempt', attempt, '--outbox', outbox]);
    fs.writeFileSync(path.join(outbox, 'guard.log'), 'DUMMY_SECRET_NOT_FOR_MESSAGE');
    const proof = { schema_version: 1, kind: 'prelaunch_rejection', delivery_id: record.key,
      project: 'demo', job_id: name, attempt_id: attempt, target_thread_id: owner, stage: 'before_executor_spawn',
      executor_spawn_attempted: false, provider_invocation_attempted: false, execution_started: false,
      status: 'blocked', reason_code: 'write_scope_rejected', diagnostic_paths: ['guard.log'] };
    write(path.join(outbox, 'PRELAUNCH_REJECTION.json'), proof);
    return { id: record.key, outbox, proof };
  };
  const good = prepare('prelaunch-good');
  const wrong = prepare('prelaunch-wrong'); write(path.join(wrong.outbox, 'PRELAUNCH_REJECTION.json'), { ...wrong.proof, target_thread_id: 'not-original-owner' });
  const escaping = prepare('prelaunch-escape'); write(path.join(escaping.outbox, 'PRELAUNCH_REJECTION.json'), { ...escaping.proof, diagnostic_paths: ['../outside.log'] });
  const unknown = prepare('prelaunch-unknown'); write(path.join(unknown.outbox, 'PRELAUNCH_REJECTION.json'), { ...unknown.proof, executor_spawn_attempted: true });
  const existing = prepare('prelaunch-existing'); fs.writeFileSync(path.join(existing.outbox, 'REPORT.md'), 'Original executor report'); write(path.join(existing.outbox, 'READY.json'), { status: 'completed', deliverables: [] });
  const partial = prepare('prelaunch-partial'); fs.writeFileSync(path.join(partial.outbox, 'REPORT.md'), 'Preserved partial report');
  const claimed = prepare('prelaunch-claimed'); write(path.join(f.mailboxRoot, 'claims', `${claimed.id}.lock`), { previous: true });
  const retired = prepare('prelaunch-retired'); const retiredPath = path.join(f.mailboxRoot, 'jobs', `${retired.id}.json`); write(retiredPath, { ...readJson(retiredPath), state: 'retired' });
  const e = engine(f); e.startWatch(coordinator); await e.tick();
  assert.equal(central(f, good.id).state, 'sent'); assert.equal(central(f, existing.id).state, 'sent');
  const report = fs.readFileSync(path.join(good.outbox, 'REPORT.md'), 'utf8');
  assert.ok(report.includes('not an executor report')); assert.ok(report.includes('guard.log')); assert.equal(report.includes('DUMMY_SECRET'), false);
  const notification = deliverySends(f, good.id)[0];
  assert.equal(notification.params.arguments.threadId, owner); assert.ok(notification.params.arguments.prompt.includes('launch-failure receipt'));
  for (const item of [wrong, escaping, unknown, partial, claimed, retired]) assert.equal(fs.existsSync(path.join(item.outbox, 'READY.json')), false);
  assert.equal(fs.readFileSync(path.join(partial.outbox, 'REPORT.md'), 'utf8'), 'Preserved partial report');
  assert.equal(fs.readFileSync(path.join(existing.outbox, 'REPORT.md'), 'utf8'), 'Original executor report');
  const readyBytes = fs.readFileSync(path.join(good.outbox, 'READY.json')); const sentBytes = fs.readFileSync(path.join(f.mailboxRoot, 'deliveries', `${good.id}.json`));
  await e.tick(); assert.equal(deliveries(f).length, 1); assert.deepEqual(fs.readFileSync(path.join(good.outbox, 'READY.json')), readyBytes);
  assert.deepEqual(fs.readFileSync(path.join(f.mailboxRoot, 'deliveries', `${good.id}.json`)), sentBytes);
  assert.equal(cli(f, ['prelaunch', good.id]).prelaunch_ready_created, false);
  const newer = prepare('prelaunch-good', '2'); await e.tick(); assert.equal(central(f, newer.id).state, 'sent');
});

test('legal relative attachments snapshot the registered outbox from an independent cwd', async t => {
  const f = fixture(t);
  const cwdBefore = process.cwd();
  const decoy = path.join(f.base, 'decoy-cwd');
  const nested = 'nested/子 目录/note file.txt';
  const spaced = 'spaced name.txt';
  fs.mkdirSync(path.join(decoy, 'nested', '子 目录'), { recursive: true });
  fs.writeFileSync(path.join(decoy, 'nested', '子 目录', 'note file.txt'), 'DECOY-NESTED');
  fs.writeFileSync(path.join(decoy, spaced), 'DECOY-SPACE');
  const register = jobId => {
    const outbox = path.join(f.base, 'outboxes', jobId);
    fs.mkdirSync(outbox, { recursive: true });
    const registration = cli(f, ['register', '--project', 'demo', '--job', jobId, '--attempt', '1', '--outbox', outbox]);
    return { id: registration.key, outbox, jobId };
  };
  const good = register('relative-good');
  const nestedBytes = Buffer.from('OUTBOX-NESTED-中文');
  const spacedBytes = Buffer.from('OUTBOX-SPACED');
  fs.mkdirSync(path.join(good.outbox, 'nested', '子 目录'), { recursive: true });
  fs.writeFileSync(path.join(good.outbox, 'nested', '子 目录', 'note file.txt'), nestedBytes);
  fs.writeFileSync(path.join(good.outbox, spaced), spacedBytes);
  fs.writeFileSync(path.join(good.outbox, 'REPORT.md'), 'Original relative report\n');
  write(path.join(good.outbox, 'READY.json'), { status: 'completed', deliverables: [nested, spaced] });
  const scan = cli(f, ['scan'], true, decoy);
  assert.equal(scan.ready.length, 1); assert.equal(scan.ready[0].id, good.id); assert.equal(scan.errors.length, 0);
  const snap = relative => fs.readFileSync(path.join(f.mailboxRoot, 'snapshots', good.id, ...relative.split('/')));
  assert.deepEqual(snap(nested), nestedBytes); assert.deepEqual(snap(spaced), spacedBytes);
  assert.equal(snap('REPORT.md').toString('utf8'), 'Original relative report\n');
  const claimed = cli(f, ['claim', good.id], true, decoy);
  assert.equal(claimed.target_thread_id, owner); assert.equal(claimed.state, 'sending');
  const receipt = path.join(f.base, 'relative-receipt.json');
  write(receipt, { structuredContent: { threadId: owner }, isError: false });
  assert.equal(cli(f, ['ack', good.id, receipt], true, decoy).state, 'sent');
  const sentDelivery = fs.readFileSync(path.join(f.mailboxRoot, 'deliveries', `${good.id}.json`));
  const sentSnapshot = fs.readFileSync(path.join(f.mailboxRoot, 'snapshots', good.id, 'REPORT.md'));
  const preserve = (jobId, state) => {
    const item = register(jobId);
    const filename = path.join(f.mailboxRoot, 'deliveries', `${item.id}.json`);
    const body = `{"id":"${item.id}","project":"demo","target_thread_id":"${owner}","prompt":"kept-${state}","state":"${state}"}`;
    fs.writeFileSync(filename, body);
    fs.writeFileSync(path.join(item.outbox, 'REPORT.md'), `rewrite-${state}`);
    write(path.join(item.outbox, 'READY.json'), { status: 'completed', deliverables: [spaced] });
    fs.writeFileSync(path.join(item.outbox, spaced), `new-${state}`);
    return { ...item, filename, body };
  };
  const keptSent = preserve('kept-sent', 'sent');
  const keptSending = preserve('kept-sending', 'sending');
  const planted = path.join(f.mailboxRoot, 'snapshots', keptSent.id, 'REPORT.md');
  fs.mkdirSync(path.dirname(planted), { recursive: true });
  fs.writeFileSync(planted, 'ORIGINAL-SNAPSHOT');
  const reject = (jobId, deliverable, plant) => {
    const item = register(jobId);
    fs.writeFileSync(path.join(item.outbox, 'REPORT.md'), `report-${jobId}`);
    if (plant) plant(item.outbox);
    write(path.join(item.outbox, 'READY.json'), { status: 'completed', deliverables: [deliverable] });
    return item;
  };
  fs.writeFileSync(path.join(f.base, 'outside.txt'), 'OUTSIDE');
  const parent = reject('escape-parent', '../outside.txt');
  const absolutePath = path.join(f.base, 'absolute-outside.txt');
  fs.writeFileSync(absolutePath, 'ABSOLUTE-OUTSIDE');
  const absolute = reject('escape-absolute', absolutePath);
  const directory = reject('escape-directory', 'only-dir', box => fs.mkdirSync(path.join(box, 'only-dir')));
  const rejected = [parent, absolute, directory];
  if (process.platform === 'win32') {
    const drive = path.parse(good.outbox).root.replace(/[\\/]+$/, '');
    assert.match(drive, /^[A-Za-z]:$/);
    rejected.push(reject('win-drive-file', `${drive}alias-name.txt`, box => fs.writeFileSync(path.join(box, 'alias-name.txt'), 'INSIDE-ALIAS')));
    rejected.push(reject('win-drive-nested', `${drive}sub/file.txt`, box => {
      fs.mkdirSync(path.join(box, 'sub')); fs.writeFileSync(path.join(box, 'sub', 'file.txt'), 'INSIDE-SUB');
    }));
    rejected.push(reject('win-root-relative', '/alias-name.txt'));
  }
  const again = cli(f, ['scan'], true, decoy);
  const errors = new Map(again.errors.map(item => [item.id, item.error]));
  const walk = directory => {
    const chunks = [];
    if (!fs.existsSync(directory)) return chunks;
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) chunks.push(...walk(full)); else chunks.push(fs.readFileSync(full));
    }
    return chunks;
  };
  for (const item of rejected) {
    assert.equal(errors.has(item.id), true, item.jobId); assert.equal(central(f, item.id), null);
    const joined = Buffer.concat(walk(path.join(f.mailboxRoot, 'snapshots', item.id)));
    for (const marker of ['INSIDE-ALIAS', 'INSIDE-SUB', 'DECOY-', 'OUTSIDE', 'ABSOLUTE-OUTSIDE']) {
      assert.equal(joined.includes(Buffer.from(marker)), false, item.jobId);
    }
  }
  assert.match(errors.get(parent.id), /escape or alias/);
  assert.match(errors.get(absolute.id), /relative forward-slash paths/);
  assert.match(errors.get(directory.id), /not a regular file/);
  if (process.platform === 'win32') {
    for (const item of rejected.filter(entry => entry.jobId.startsWith('win-'))) assert.match(errors.get(item.id), /relative forward-slash paths/);
  }
  assert.equal(fs.readFileSync(keptSent.filename, 'utf8'), keptSent.body);
  assert.equal(fs.readFileSync(keptSending.filename, 'utf8'), keptSending.body);
  assert.equal(fs.readFileSync(planted, 'utf8'), 'ORIGINAL-SNAPSHOT');
  assert.equal(fs.existsSync(path.join(f.mailboxRoot, 'snapshots', keptSending.id)), false);
  assert.deepEqual(fs.readFileSync(path.join(f.mailboxRoot, 'deliveries', `${good.id}.json`)), sentDelivery);
  assert.deepEqual(fs.readFileSync(path.join(f.mailboxRoot, 'snapshots', good.id, 'REPORT.md')), sentSnapshot);
  assert.deepEqual(snap(nested), nestedBytes); assert.deepEqual(snap(spaced), spacedBytes);
  assert.equal(again.ready.some(item => item.id === good.id), false);
  assert.equal(again.uncertain.some(item => item.id === keptSending.id), true);
  assert.equal(again.ready.concat(again.uncertain).some(item => item.id === keptSent.id), false);
  assert.equal(process.cwd(), cwdBefore);
});
const tool = (e, name, args = {}, who = coordinator, env = {}) => handleRequest({ jsonrpc: '2.0', method: 'tools/call', params: {
  name, arguments: args, ...(who === null ? {} : { _meta: { 'x-codex-turn-metadata': { thread_id: who } } }),
} }, e, env);
const toolValue = result => { assert.equal(result.isError, false, result.content[0].text); return JSON.parse(result.content[0].text); };
const delegated = (f, options = {}) => engine(f, { env: {}, ...options });
async function permitAndGrant(f) {
  const admin = engine(f); admin.startWatch(coordinator);
  const value = toolValue(await tool(admin, GRANT)); admin.close(); await pause(50);
  return value.delegation.grantId;
}
async function stopService(s) {
  if (s && s.child.exitCode === null) { s.child.stdin.end(); await once(s.child, 'close'); }
}
const deliverySends = (f, id) => deliveries(f).filter(item => item.params.arguments.prompt.split('\n').includes(`[app-mailbox:${id}]`));

test('explicit delegation tools require actual coordinator identity, no overrides and a valid existing all-watch epoch', async t => {
  const f = fixture(t); const e = delegated(f);
  for (const name of [GRANT, REVOKE]) {
    const spec = TOOLS.find(item => item.name === name);
    assert.deepEqual(spec.inputSchema, { type: 'object', additionalProperties: false, properties: {} });
    assert.equal(spec.annotations.readOnlyHint, false);
  }
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'plugin', '.mcp.json'), 'utf8'));
  for (const name of [GRANT, REVOKE]) assert.equal(manifest.mcpServers.codex_mail_wake.tools[name].approval_mode, 'prompt');
  assert.equal((await tool(e, GRANT)).isError, true);
  assert.equal(readJson(e.delegation.filename), null); assert.equal(e.allWatch().enabled, false);
  toolValue(await tool(e, 'start_app_mailbox_watch'));
  for (const name of [GRANT, REVOKE]) {
    assert.equal((await tool(e, name, {}, owner)).isError, true);
    assert.equal((await tool(e, name, {}, null)).isError, true);
    for (const field of ['caller', 'actor', 'target', 'pipe', 'root', 'stateDir', 'model', 'host', 'project']) assert.equal((await tool(e, name, { [field]: 'override' })).isError, true);
  }
  const before = fs.readFileSync(e.allPath(), 'utf8');
  const grant = toolValue(await tool(e, GRANT)); assert.equal(grant.outcome, 'granted');
  const record = fs.readFileSync(e.delegation.filename, 'utf8');
  assert.equal(toolValue(await tool(e, GRANT)).outcome, 'already_active_same_scope');
  assert.equal(fs.readFileSync(e.delegation.filename, 'utf8'), record);
  assert.equal(fs.readFileSync(e.allPath(), 'utf8'), before, 'grant never changes permission or epoch');
  assert.equal((await tool(e, 'app_mailbox_status', {}, null)).isError, true, 'persistent delegation never becomes a tools/call identity');
  const wrongMeta = await handleRequest({ jsonrpc: '2.0', method: 'tools/call', params: { name: GRANT, arguments: {},
    _meta: { 'x-codex-turn-metadata': {} } } }, e, { CODEX_THREAD_ID: coordinator });
  assert.equal(wrongMeta.isError, true, 'bad metadata never falls back to environment');
  assert.equal(toolValue(await tool(e, REVOKE)).outcome, 'revoked');
  assert.equal(toolValue(await tool(e, REVOKE)).outcome, 'already_revoked');
  const fromEnv = toolValue(await tool(e, GRANT, {}, null, { CODEX_THREAD_ID: coordinator }));
  assert.notEqual(fromEnv.delegation.grantId, grant.delegation.grantId);
  assert.equal(readJson(e.delegation.filename).grantedBySource, 'tool_executor_environment');
  const lock = await e.driver.acquireControl();
  assert.ok(lock); assert.equal((await tool(e, REVOKE)).isError, true, 'another control cannot overwrite while OS lock held');
  lock.release();
});

test('public stdio grant then service reload with no actor or tools/call resumes exact registered CLI/Bot delivery', async t => {
  let first, second;
  t.after(async () => { await stopService(first); await stopService(second); });
  const f = fixture(t); write(f.config, { ...JSON.parse(fs.readFileSync(f.config)), pollIntervalMs: 1000 });
  const env = { ...f.env }; delete env.CODEX_THREAD_ID;
  first = service(f, path.join(root, 'plugin', 'server.mjs'), env);
  assert.equal((await first.call('initialize')).serverInfo.version, '0.5.0');
  const params = name => ({ name, arguments: {}, _meta: { 'x-codex-turn-metadata': { thread_id: coordinator } } });
  toolValue(await first.call('tools/call', params('start_app_mailbox_watch')));
  const grantId = toolValue(await first.call('tools/call', params(GRANT))).delegation.grantId;
  await stopService(first);
  const cliJob = job(f, 'reload-cli'); const botJob = job(f, 'reload-bot', '1', 'bot');
  write(f.control, { ownerStatus: 'notLoaded' });
  second = service(f, path.join(root, 'plugin', 'server.mjs'), { ...env, CODEX_APP_TOOLS_PIPE_PATH: 'second-launch-pipe' });
  await second.call('initialize'); assert.equal((await second.call('tools/list')).tools.length, 6);
  await until(() => [cliJob, botJob].every(item => central(f, item.id)?.state === 'sent'));
  assert.equal(second.toolCalls(), 0, 'startup/list only, no identity-binding call or hook');
  for (const item of [cliJob, botJob]) {
    const sent = deliverySends(f, item.id); assert.equal(sent.length, 1);
    assert.ok(sent[0].params.arguments.prompt.includes(central(f, item.id).prompt));
    assert.equal(sent[0].params.arguments.threadId, owner);
    assert.equal(sent[0].params._meta['x-codex-turn-metadata'].thread_id, coordinator);
    assert.deepEqual(Object.keys(sent[0].params.arguments).sort(), ['prompt', 'threadId']);
    assert.equal(sent[0].pipe, 'second-launch-pipe');
    const intent = readJson(path.join(f.stateDir, 'app-mailbox', 'deliveries', key(item.id), 'intent.json'));
    assert.equal(intent.actorSource, 'persisted_delegation'); assert.equal(intent.delegationGrantId, grantId);
    assert.ok(sent[0].params.arguments.prompt.includes(fs.readFileSync(path.join(item.outbox, 'REPORT.md'), 'utf8')));
  }
  assert.equal(second.stderr(), '');
  assert.equal(readJson(path.join(f.stateDir, 'app-mailbox', 'leader-status.json')).actorSource, 'persisted_delegation');
});

test('delegation refuses missing or malformed grants, scope mismatch, missing pipe and stale epoch; actual actor has priority', async t => {
  const f = fixture(t); const e = delegated(f); e.startWatch(coordinator); const item = job(f);
  await e.tick(); assert.equal(central(f, item.id), null, 'registeredBy alone is never delegation');
  toolValue(await tool(e, GRANT)); const good = readJson(e.delegation.filename); e.close(); await pause(50);
  const variants = [null, [], {}, { ...good, schemaVersion: 99 }, { ...good, grantedBy: owner }, { ...good, delegateThreadId: owner },
    { ...good, grantedBySource: 'persisted_delegation' }, { ...good, grantId: 'bad' }, { ...good, createdAtUtc: 'bad' },
    { ...good, scope: { ...good.scope, stateRoot: path.join(f.base, 'other-state') } },
    { ...good, scope: { ...good.scope, mailboxRoot: path.join(f.base, 'other-ledger') } },
    { ...good, scope: { ...good.scope, allWatchEpoch: 'old-epoch' } }];
  for (const variant of variants) {
    write(e.delegation.filename, variant); const instance = delegated(f); await instance.tick();
    assert.equal(central(f, item.id), null); assert.equal(instance.runtimeState, 'identity_unavailable'); instance.close();
  }
  write(e.delegation.filename, good); const goodWatch = readJson(e.allPath());
  for (const watch of [null, [], { ...goodWatch, enabled: 'true' }, { ...goodWatch, registeredBy: owner }, { ...goodWatch, epoch: 'initial' }]) {
    write(e.allPath(), watch); const instance = delegated(f); await instance.tick();
    assert.equal(central(f, item.id), null); assert.equal(instance.runtimeState, 'identity_unavailable'); instance.close();
  }
  write(e.allPath(), goodWatch);
  fs.writeFileSync(e.delegation.filename, '{'); const invalid = delegated(f); await invalid.tick();
  assert.equal((await tool(invalid, GRANT)).isError, true); toolValue(await tool(invalid, REVOKE)); invalid.close();
  write(e.delegation.filename, good);
  const noPipe = delegated(f, { client: new OfficialTools({ env: { ...f.env, CODEX_APP_TOOLS_PIPE_PATH: '' } }) });
  await noPipe.tick(); assert.equal(central(f, item.id), null); assert.equal(noPipe.runtimeState, 'no_pipe'); noPipe.close();
  const real = engine(f); await real.tick(); assert.equal(central(f, item.id).state, 'sent');
  assert.equal(readJson(real.localPath(item.id, 'intent.json')).actorSource, 'launch_environment');
  assert.equal(readJson(real.localPath(item.id, 'intent.json')).delegationGrantId, null); real.close(); await pause(50);
  const fallback = delegated(f); toolValue(await tool(fallback, 'app_mailbox_status', { project: 'demo' }, owner));
  const next = job(f, 'real-tool-priority'); await fallback.tick();
  assert.equal(readJson(fallback.localPath(next.id, 'intent.json')).actorSource, 'instance_tool_metadata');
  assert.equal(deliverySends(f, next.id)[0].params._meta['x-codex-turn-metadata'].thread_id, owner);
});

test('revoke, stop-all and epoch change cannot be revived by startup or status; opt-out and held/closed routes stay paused', async t => {
  const f = fixture(t); const grantId = await permitAndGrant(f); const d = delegated(f); let at = Date.now(); d.now = () => at;
  const first = job(f, 'first'); await d.tick(); assert.equal(central(f, first.id).state, 'sent');
  const admin = engine(f); admin.stopWatch(owner, { project: 'demo' }); const paused = job(f, 'opt-out');
  for (const status of ['held', 'closed']) {
    cli(f, ['route', '--project', status, '--owner', owner]);
    const outbox = path.join(f.base, status); fs.mkdirSync(outbox); fs.writeFileSync(path.join(outbox, 'REPORT.md'), status);
    write(path.join(outbox, 'READY.json'), { status: 'completed', deliverables: [] });
    cli(f, ['register', '--project', status, '--job', 'paused', '--attempt', '1', '--outbox', outbox]);
    cli(f, ['route', '--project', status, '--owner', owner, '--status', status]);
  }
  await d.tick(); assert.equal(deliverySends(f, paused.id).length, 0);
  assert.equal(admin.startWatch(coordinator).projects.find(p => p.project === 'demo').watchEnabled, false, 'idempotent all-start preserves opt-out');
  admin.startWatch(owner, { project: 'demo' });
  toolValue(await tool(admin, REVOKE)); const afterRevoke = job(f, 'revoked'); await d.tick();
  assert.equal(d.leader, null); assert.equal(central(f, afterRevoke.id), null);
  assert.equal(d.getStatus(coordinator).service.delegation.state, 'revoked');
  const fresh = delegated(f); await fresh.tick(); assert.equal(central(f, afterRevoke.id), null); fresh.close();
  const nextGrant = toolValue(await tool(admin, GRANT)).delegation.grantId; assert.notEqual(nextGrant, grantId);
  at += 60001; await d.tick(); assert.equal(central(f, afterRevoke.id).state, 'sent');
  admin.stopWatch(coordinator); const stopped = job(f, 'stopped'); await d.tick();
  assert.equal(d.delegationSummary(coordinator).state, 'inactive_permit'); assert.equal(central(f, stopped.id), null);
  admin.startWatch(coordinator); await d.tick();
  assert.equal(d.getStatus(coordinator).service.delegation.state, 'stale_epoch'); assert.equal(central(f, stopped.id), null);
  toolValue(await tool(admin, GRANT)); at += 60001; await d.tick(); assert.equal(central(f, stopped.id).state, 'sent');
  assert.ok(Object.values(d.routes()).filter(r => r.status !== 'active').every(r => r.status === 'held' || r.status === 'closed'));
  assert.ok(deliveries(f).every(item => item.params.arguments.prompt.includes('project: demo')));
});

test('revocation after leader/target lease, scan, read, claim, intent or bridge-connect awaits stops new effects; submitted sends retain receipts', async t => {
  for (const stage of ['lease', 'target-lease', 'scan', 'read', 'claim', 'intent', 'connect', 'send']) {
    const f = fixture(t); await permitAndGrant(f); const item = job(f, `during-${stage}`); const next = job(f, `next-${stage}`);
    const d = delegated(f); const revoke = () => d.delegation.revoke(coordinator, 'turn_metadata'); let triggered = false;
    if (stage === 'lease') {
      const original = d.driver.acquireLeader.bind(d.driver); d.driver.acquireLeader = async () => { const lease = await original(); revoke(); triggered = true; return lease; };
    } else if (stage === 'target-lease') {
      const original = d.driver.acquireTarget.bind(d.driver); d.driver.acquireTarget = async (...args) => { const lease = await original(...args); revoke(); triggered = true; return lease; };
    } else if (stage === 'scan' || stage === 'claim') {
      const original = d.driver.run.bind(d.driver); d.driver.run = async (op, args) => { const result = await original(op, args); if (op === stage) { revoke(); triggered = true; } return result; };
    } else if (stage === 'read') {
      const original = d.client.readThread.bind(d.client); d.client.readThread = async (...args) => { const result = await original(...args); revoke(); triggered = true; return result; };
    } else if (stage === 'intent') {
      const original = d.client.send.bind(d.client); d.client.send = (...args) => { revoke(); triggered = true; return original(...args); };
    } else if (stage === 'connect') {
      const original = d.client.connect.bind(d.client); d.client.connect = async () => { await original(); revoke(); triggered = true; };
    } else {
      write(f.control, { sendDelayMs: 100 });
      const original = d.client.send.bind(d.client); d.client.send = async (...args) => {
        const submitted = original(...args); await until(() => deliveries(f).length > 0);
        revoke(); triggered = true; return submitted;
      };
    }
    await d.tick(); assert.equal(triggered, true); assert.equal(d.leader, null);
    if (stage === 'send') {
      const submitted = [item, next].filter(x => deliverySends(f, x.id).length);
      assert.equal(deliveries(f).length, 1, 'one batch was already submitted before revocation');
      assert.equal(submitted.length, 2);
      for (const member of submitted) {
        assert.equal(central(f, member.id).state, 'sent');
        assert.ok(readJson(d.localPath(member.id, 'receipt.json')));
      }
    } else {
      assert.equal(deliveries(f).length, 0); assert.equal(faults(f).length, 0);
      if (['claim', 'intent'].includes(stage)) {
        assert.equal([item, next].filter(x => central(f, x.id)?.state === 'sending').length, stage === 'claim' ? 1 : 2,
          'retain the first claim on claim-time revocation, or all batch members when send is denied after intents');
      } else assert.ok([item, next].every(x => !central(f, x.id) || central(f, x.id).state === 'pending'));
    }
    if (stage === 'read') assert.ok([item, next].every(x => !readJson(d.localPath(x.id, 'wait.json'))), 'no new outcome after read authority ended');
  }
});

test('delegated recovery keeps unknown sending single-attempt, acknowledges exact prior receipt and emits each fault once', async t => {
  const f = fixture(t); await permitAndGrant(f); const prior = job(f, 'unknown'); const receiptJob = job(f, 'receipt'); const normal = job(f, 'normal');
  cli(f, ['scan']); cli(f, ['claim', prior.id]); cli(f, ['claim', receiptJob.id]);
  const d = delegated(f);
  write(d.localPath(receiptJob.id, 'intent.json'), { schemaVersion: 1, deliveryId: receiptJob.id, project: 'demo', ownerThreadId: owner });
  write(d.localPath(receiptJob.id, 'receipt.json'), { isError: false, content: [{ type: 'text', text: JSON.stringify({ threadId: owner }) }] });
  await d.tick(); await d.tick();
  assert.equal(central(f, prior.id).state, 'sending'); assert.equal(readJson(d.localPath(prior.id, 'outcome.json')).status, 'manual_review_required');
  assert.equal(central(f, receiptJob.id).state, 'sent'); assert.equal(central(f, normal.id).state, 'sent');
  assert.equal(deliverySends(f, prior.id).length, 0); assert.equal(deliverySends(f, receiptJob.id).length, 0); assert.equal(deliverySends(f, normal.id).length, 1);
  assert.equal(faults(f).length, 1); assert.equal(faults(f)[0].params.arguments.threadId, coordinator);
  assert.ok(fs.existsSync(path.join(f.mailboxRoot, 'claims', `${prior.id}.lock`)));
});

test('two delegated stdio instances share one OS leader; its exit releases ownership for a delegated standby', async t => {
  let a, b;
  t.after(async () => { await stopService(a); await stopService(b); });
  const f = fixture(t); await permitAndGrant(f); write(f.config, { ...JSON.parse(fs.readFileSync(f.config)), pollIntervalMs: 1000 });
  const env = { ...f.env }; delete env.CODEX_THREAD_ID;
  a = service(f, path.join(root, 'plugin', 'server.mjs'), env);
  b = service(f, path.join(root, 'plugin', 'server.mjs'), { ...env, CODEX_APP_TOOLS_PIPE_PATH: 'other-test-pipe' });
  await Promise.all([a.call('initialize'), b.call('initialize')]);
  const first = job(f, 'two-first'); await until(() => central(f, first.id)?.state === 'sent');
  const second = job(f, 'two-second'); await until(() => central(f, second.id)?.state === 'sent');
  const intent = id => readJson(path.join(f.stateDir, 'app-mailbox', 'deliveries', key(id), 'intent.json'));
  assert.equal(intent(first.id).instanceId, intent(second.id).instanceId);
  for (const item of [first, second]) assert.equal(deliverySends(f, item.id).length, 1);
  assert.equal(a.toolCalls() + b.toolCalls(), 0); await stopService(a); await stopService(b);
  let at = Date.now(); const x = delegated(f, { now: () => at }); const y = delegated(f, { now: () => at });
  await x.tick(); await y.tick(); assert.deepEqual([x.runtimeState, y.runtimeState].sort(), ['leader', 'standby']);
  const [lead, standby] = x.leader?.alive ? [x, y] : [y, x]; lead.close(); await pause(50);
  at += 60001; const third = job(f, 'takeover'); await standby.tick();
  assert.equal(central(f, third.id).state, 'sent'); assert.equal(deliverySends(f, third.id).length, 1);
  assert.equal(readJson(standby.leaderPath()).instanceId, standby.instanceId);
});

const acknowledge = (e, caller, ids, extra = {}) => handleRequest({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: {
  name: 'ack_app_mailbox_delivery', arguments: { deliveryIds: ids, ...extra },
  _meta: { 'x-codex-turn-metadata': { thread_id: caller } },
} }, e, {});

test('reliability: exact-owner handled proof survives reconstruction without fake sent; a new attempt still wakes', async t => {
  const f = fixture(t); const old = job(f, 'manual'); const e = engine(f);
  await e.driver.run('scan');
  const result = await acknowledge(e, owner, [old.id]); assert.equal(result.isError, false);
  const recorded = toolValue(result).acknowledgments[0];
  assert.equal(recorded.outcome, 'recorded'); assert.equal(recorded.pendingNotificationSuppressed, true);
  assert.equal(recorded.notificationAccepted, false); assert.equal(recorded.businessAcceptanceRecorded, false);
  assert.equal(toolValue(await acknowledge(e, owner, [old.id])).acknowledgments[0].outcome, 'already_recorded');
  e.startWatch(owner, { project: 'demo' }); await e.tick();
  assert.equal(central(f, old.id).state, 'pending'); assert.equal(deliveries(f).length, 0);
  assert.equal(fs.existsSync(path.join(f.mailboxRoot, 'claims', old.id + '.lock')), false);
  assert.equal(e.getStatus(owner, 'demo').deliveries[0].ownerHandled, true);
  const exited = once(e.leader.child, 'close'); e.close(); await exited;
  const newer = job(f, 'manual', '2'); const next = engine(f); await next.tick();
  assert.equal(central(f, newer.id).state, 'sent'); assert.equal(deliveries(f).length, 1);
  assert.equal(readJson(next.localPath(old.id, 'outcome.json')).status, 'suppressed');
  assert.equal(central(f, old.id).state, 'pending');
});

test('reliability: ack rejects foreign/mixed owners and malformed requests before any write', async t => {
  const f = fixture(t); const a = job(f, 'owned');
  cli(f, ['route', '--project', 'other', '--owner', 'other-lead']);
  const b = job(f, 'foreign', '1', 'cli', {}, 'other'); const e = engine(f); await e.driver.run('scan');
  for (const [caller, ids, extra] of [
    [coordinator, [a.id]], ['intruder', [a.id]], [owner, [a.id, b.id]], [owner, []],
    [owner, [a.id, a.id]], [owner, ['../outside']], [owner, ['missing']],
    [owner, Array.from({ length: 33 }, (_, i) => 'item' + i)], [owner, [a.id], { target: owner }],
  ]) {
    assert.equal((await acknowledge(e, caller, ids, extra)).isError, true);
    assert.equal(fs.existsSync(e.localPath(a.id, 'handled.json')), false);
    assert.equal(fs.existsSync(e.localPath(b.id, 'handled.json')), false);
  }
  await assert.rejects(e.acknowledgeDeliveries(owner, { deliveryIds: [a.id] }, 'persisted_delegation'), /actual tool caller/);
  assert.equal((await acknowledge(e, owner, [a.id])).isError, false);
  const snapshot = central(f, a.id); write(path.join(f.mailboxRoot, 'deliveries', a.id + '.json'), { ...snapshot, prompt: snapshot.prompt + 'changed' });
  assert.equal((await acknowledge(e, owner, [a.id])).isError, true, 'a proof cannot be rebound to changed content');
  write(path.join(f.mailboxRoot, 'deliveries', a.id + '.json'), snapshot);
  cli(f, ['route', '--project', 'demo', '--owner', 'replacement-owner', '--replace-owner']);
  assert.equal((await acknowledge(e, owner, [a.id])).isError, true, 'the former owner cannot acknowledge after migration');
});

test('reliability: ack shares target lock and preserves sent, uncertain and committed evidence', async t => {
  const f = fixture(t); const pending = job(f, 'pending'); const sent = job(f, 'accepted'); const unknown = job(f, 'ambiguous');
  const e = engine(f); e.startWatch(coordinator); await e.canPoll(); await e.driver.run('scan');
  const lock = await e.driver.acquireTarget(owner, e.stateDir);
  try {
    assert.equal(toolValue(await acknowledge(e, owner, [pending.id])).outcome, 'target_send_busy');
    assert.equal(fs.existsSync(e.localPath(pending.id, 'handled.json')), false);
  } finally { await lock.release(); }
  await e.processReady(sent); write(f.control, { sendMode: 'reject' }); await e.processReady(unknown);
  await e.driver.run('claim', { id: pending.id });
  const files = [sent, unknown].flatMap(item => ['intent.json', 'receipt.json', 'outcome.json'].map(name => e.localPath(item.id, name)));
  files.push(...[sent, unknown, pending].map(item => path.join(f.mailboxRoot, 'deliveries', item.id + '.json')));
  const before = files.map(filename => fs.readFileSync(filename));
  const values = toolValue(await acknowledge(e, owner, [sent.id, unknown.id, pending.id])).acknowledgments;
  assert.ok(values.every(value => value.pendingNotificationSuppressed === false && value.businessAcceptanceRecorded === false));
  files.forEach((filename, i) => assert.deepEqual(fs.readFileSync(filename), before[i]));
  assert.equal(deliveries(f).length, 2);
});

test('reliability: one real fake-host send batches one owner only and preserves original prompts and item acks', async t => {
  const f = fixture(t); const a = job(f, 'first'); const b = job(f, 'second', '1', 'bot');
  cli(f, ['route', '--project', 'other', '--owner', 'other-lead']);
  const c = job(f, 'separate', '1', 'cli', {}, 'other'); const e = engine(f); e.startWatch(coordinator);
  await e.tick(); assert.equal(deliveries(f).length, 2);
  const wake = deliveries(f).find(event => event.params.arguments.threadId === owner);
  assert.deepEqual(Object.keys(wake.params.arguments).sort(), ['prompt', 'threadId']);
  assert.equal(wake.params._meta['x-codex-turn-metadata'].thread_id, actor);
  for (const item of [a, b]) {
    assert.ok(wake.params.arguments.prompt.includes(central(f, item.id).prompt));
    assert.equal(central(f, item.id).state, 'sent');
    assert.equal(readJson(e.localPath(item.id, 'outcome.json')).status, 'sent');
  }
  assert.ok(!wake.params.arguments.prompt.includes(central(f, c.id).prompt));
  await e.tick(); assert.equal(deliveries(f).length, 2);
});

test('reliability: UTF-8 batch cap leaves overflow unclaimed and oversized originals stay single', async t => {
  const f = fixture(t); const items = ['a', 'b', 'c'].map(id => job(f, id));
  for (const item of items) fs.writeFileSync(path.join(item.outbox, 'REPORT.md'), '中文😀'.repeat(11000));
  const e = engine(f); e.startWatch(coordinator); await e.tick();
  assert.equal(deliveries(f).length, 1);
  const first = deliveries(f)[0].params.arguments.prompt;
  assert.ok(Buffer.byteLength(first, 'utf8') <= MAX_WAKE_PROMPT_BYTES);
  const left = items.filter(item => central(f, item.id).state === 'pending'); assert.equal(left.length, 1);
  assert.equal(fs.existsSync(path.join(f.mailboxRoot, 'claims', left[0].id + '.lock')), false);
  for (const item of items.filter(value => !left.includes(value))) assert.ok(first.includes(central(f, item.id).prompt));
  await e.tick(); assert.equal(deliveries(f).length, 2);
  const large = job(f, 'large'); const tail = job(f, 'tail');
  fs.writeFileSync(path.join(large.outbox, 'REPORT.md'), '中文😀'.repeat(30000)); await e.driver.run('scan');
  const original = central(f, large.id).prompt;
  await e.processReadyBatch([large, tail]);
  assert.equal(deliveries(f).at(-1).params.arguments.prompt, original);
  assert.equal(central(f, tail.id).state, 'pending');
  assert.equal(fs.existsSync(path.join(f.mailboxRoot, 'claims', tail.id + '.lock')), false);
});

test('reliability: driver preserves long non-ASCII JSON across native stream chunks', async t => {
  const f = fixture(t); const item = job(f, 'unicode');
  const report = '边界😀α\n'.repeat(60000); fs.writeFileSync(path.join(item.outbox, 'REPORT.md'), report);
  const e = engine(f); await e.driver.run('scan');
  const claimed = (await e.driver.run('claim', { id: item.id })).result;
  assert.ok(claimed.prompt.endsWith(report)); assert.equal(claimed.prompt, central(f, item.id).prompt);
  assert.equal(claimed.prompt.includes('\ufffd'), false);
});

test('reliability: busy partial claims do not consume overflow or interfere with accepted members', async t => {
  const f = fixture(t); const a = job(f, 'a'); const b = job(f, 'b'); const c = job(f, 'c');
  const e = engine(f); e.startWatch(coordinator); const run = e.driver.run.bind(e.driver);
  e.driver.run = (operation, args = {}) => operation === 'claim' && args.id === b.id ? Promise.resolve({ busy: true }) : run(operation, args);
  await e.tick(); assert.equal(deliveries(f).length, 1);
  assert.equal(central(f, a.id).state, 'sent'); assert.equal(central(f, c.id).state, 'sent');
  assert.equal(central(f, b.id).state, 'pending'); assert.equal(fs.existsSync(e.localPath(b.id, 'intent.json')), false);
  e.driver.run = run; await e.tick(); assert.equal(deliveries(f).length, 2); assert.equal(central(f, b.id).state, 'sent');
});

test('reliability: durable batch receipt repairs missing per-item fanout after reconstruction without resend', async t => {
  const f = fixture(t); const items = [job(f, 'one'), job(f, 'two')]; const e = engine(f); e.startWatch(coordinator);
  const run = e.driver.run.bind(e.driver);
  e.driver.run = (operation, args = {}) => operation === 'ack' ? Promise.resolve({ busy: true }) : run(operation, args);
  await e.tick(); assert.equal(deliveries(f).length, 1);
  for (const item of items) {
    assert.equal(central(f, item.id).state, 'sending');
    assert.equal(readJson(e.localPath(item.id, 'outcome.json')).status, 'receipt_pending_ack');
    fs.unlinkSync(e.localPath(item.id, 'receipt.json'));
  }
  const exited = once(e.leader.child, 'close'); e.close(); await exited;
  const next = engine(f); await next.tick();
  for (const item of items) assert.equal(central(f, item.id).state, 'sent');
  assert.equal(deliveries(f).length, 1);
});

test('reliability: ambiguous batch never replays after reconstruction while an independent new job works', async t => {
  const f = fixture(t); const items = [job(f, 'one'), job(f, 'two')]; const e = engine(f);
  write(f.control, { sendMode: 'timeout' }); e.startWatch(coordinator); await e.tick();
  assert.equal(deliveries(f).length, 1);
  for (const item of items) assert.equal(readJson(e.localPath(item.id, 'outcome.json')).status, 'uncertain');
  const exited = once(e.leader.child, 'close'); e.close(); await exited;
  write(f.control, {}); const added = job(f, 'fresh'); const next = engine(f); await next.tick();
  assert.equal(deliveries(f).length, 2); assert.equal(central(f, added.id).state, 'sent');
  for (const item of items) assert.equal(central(f, item.id).state, 'sending');
});

test('reliability: revocation during preflight leaves all unclaimed; after one claim retains uncertainty without sending', async t => {
  for (const phase of ['preflight', 'claim']) {
    await t.test(phase, async t => {
      const f = fixture(t); const items = [job(f, 'one'), job(f, 'two')]; const e = engine(f); e.startWatch(coordinator);
      const read = e.client.readThread.bind(e.client); const run = e.driver.run.bind(e.driver);
      if (phase === 'preflight') e.client.readThread = async (...args) => { const result = await read(...args); e.stopWatch(coordinator); return result; };
      else e.driver.run = async (operation, args = {}) => { const result = await run(operation, args); if (operation === 'claim') e.stopWatch(coordinator); return result; };
      await e.tick(); assert.equal(deliveries(f).length, 0);
      assert.equal(items.filter(item => central(f, item.id).state === 'sending').length, phase === 'claim' ? 1 : 0);
      assert.equal(items.filter(item => central(f, item.id).state === 'pending').length, phase === 'claim' ? 1 : 2);
    });
  }
});

test('reliability: batch receipt cannot be borrowed by a different owner, item or malformed manifest', async t => {
  const f = fixture(t); const journal = new WakeBatchJournal(path.join(f.base, 'journal')); const id = journal.allocate();
  journal.publish(id, owner, ['member'], new Date().toISOString());
  journal.saveReceipt(id, { content: [{ type: 'text', text: JSON.stringify({ threadId: owner }) }] });
  const intent = { batchId: id, deliveryId: 'member', ownerThreadId: owner };
  assert.ok(journal.receipt(intent, 'member', owner));
  assert.equal(journal.receipt(intent, 'foreign', owner), null);
  assert.equal(journal.receipt(intent, 'member', 'other'), null);
  assert.equal(journal.receipt({ ...intent, batchId: '../outside' }, 'member', owner), null);
  write(journal.path(id, 'manifest.json'), { id, ownerThreadId: owner, members: ['member'] });
  assert.equal(journal.receipt(intent, 'member', owner), null);
  const prompts = ['x'.repeat(MAX_WAKE_PROMPT_BYTES), 'next'];
  assert.deepEqual(selectPromptBatch(prompts, value => value, 'label'), [prompts[0]]);
  assert.equal(combinedPrompt([prompts[0]], 'label'), prompts[0]);
});
