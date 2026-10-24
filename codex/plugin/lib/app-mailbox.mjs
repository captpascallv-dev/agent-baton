import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { key } from './ids.mjs';
import { readJson, writeJsonAtomic, createJsonOnce } from './store.mjs';
import { exactSendReceipt } from './official-tools.mjs';
import { BackgroundDelegation, identitySources, validWatchEpoch } from './delegation.mjs';
import { WakeBatchJournal, combinedPrompt, selectPromptBatch } from './wake-batch.mjs';

const pluginRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const driverPath = path.join(pluginRoot, 'driver', 'mailbox_driver.py');
const validId = value => typeof value === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,220}$/.test(value);
const validProject = value => typeof value === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,150}$/.test(value);
const terminalOutcomes = new Set(['sent', 'uncertain', 'manual_review_required']);
const batchLabel = '[app-mailbox:batch] Registered delivery backlog';
const uniqueFacts = items => [...new Map(items.map(item => {
  const { observationId, ...facts } = item;
  return [JSON.stringify(facts), facts];
})).values()];

export class MailboxDriver {
  constructor({ root, pythonPath, timeoutMs = 120000 }) {
    if (!root || !pythonPath) throw new Error('Explicit mailbox root and Python executable are required.');
    this.root = path.resolve(root); this.pythonPath = pythonPath; this.timeoutMs = timeoutMs; this.children = new Set();
  }
  run(operation, args = {}) {
    if (!['scan', 'claim', 'ack'].includes(operation)) throw new Error('Unknown fixed driver operation.');
    return new Promise((resolve, reject) => {
      const argv = ['-B', '-X', 'utf8', driverPath, '--root', this.root];
      const child = spawn(this.pythonPath, argv, { shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
      child.stdout.setEncoding('utf8'); child.stderr.setEncoding('utf8');
      this.children.add(child);
      let stdout = '', stderr = '', settled = false;
      const finish = (error, value) => {
        if (settled) return; settled = true; clearTimeout(timer); this.children.delete(child);
        if (error) reject(error); else resolve(value);
      };
      const timer = setTimeout(() => { child.kill(); finish(new Error('Fixed mailbox driver timed out; state retained, no provider restart.')); }, this.timeoutMs);
      child.stdout.on('data', data => { stdout += data; if (stdout.length > 8 * 1024 * 1024) { child.kill(); finish(new Error('Driver output exceeded 8 MiB.')); } });
      child.stderr.on('data', data => { stderr += data; });
      child.on('error', error => finish(error));
      child.stdin.on('error', error => finish(error));
      child.on('close', code => {
        let value;
        try { value = JSON.parse(stdout); } catch { return finish(new Error(`Driver returned invalid JSON (${code}): ${stderr.slice(0, 500)}`)); }
        if (code !== 0 || value.error) finish(new Error(`Fixed mailbox driver rejected: ${value.error ?? code}`));
        else finish(null, value);
      });
      child.stdin.end(JSON.stringify({ operation, ...args }));
    });
  }
  acquireLeader() { return this.acquireLease('--lease'); }
  acquireControl() { return this.acquireLease('--authorization-lease'); }
  acquireTarget(ownerThreadId, stateDir) {
    if (typeof ownerThreadId !== 'string' || !ownerThreadId.trim()) throw new Error('Exact target identity is required.');
    return this.acquireLease('--target-send-lease', { root: stateDir, targetKey: key(ownerThreadId) });
  }
  acquireLease(flag, { root = this.root, targetKey = null } = {}) {
    return new Promise((resolve, reject) => {
      const argv = ['-B', '-X', 'utf8', driverPath, flag];
      if (targetKey !== null) argv.push(targetKey);
      argv.push('--root', root);
      const child = spawn(this.pythonPath, argv, { shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
      this.children.add(child);
      let markClosed;
      const closed = new Promise(resolve => { markClosed = resolve; });
      const lease = { child, alive: false, release: async () => { lease.alive = false; child.stdin.end(); await closed; } };
      let buffer = '', settled = false;
      const finish = (error, value) => {
        if (settled) return; settled = true; clearTimeout(timer);
        if (error) { child.kill(); reject(error); } else resolve(value);
      };
      const timer = setTimeout(() => finish(new Error('OS lease handshake timed out.')), 10000);
      child.stderr.on('data', () => {});
      child.stdin.on('error', error => { lease.alive = false; finish(error); });
      child.on('error', error => finish(error));
      child.on('close', () => { lease.alive = false; this.children.delete(child); markClosed(); finish(new Error('OS lease exited before acquisition.')); });
      child.stdout.on('data', data => {
        buffer += data;
        if (buffer.length > 4096) return finish(new Error('Invalid leader lease response.'));
        if (!buffer.includes('\n')) return;
        try {
          const result = JSON.parse(buffer.split('\n')[0]);
          if (result.error || result.operation !== 'lease') throw new Error(result.error ?? 'Invalid lease identity.');
          if (result.busy) { child.stdin.end(); finish(null, null); }
          else { lease.alive = true; finish(null, lease); }
        } catch (error) { finish(error); }
      });
    });
  }
  close() { for (const child of this.children) { child.stdin.end(); child.kill(); } }
}

export class AppMailboxEngine {
  constructor({ stateDir, client, driver, coordinatorThreadId, now = () => Date.now(), pollMs = 15000, env = process.env }) {
    if (!stateDir || !driver || !coordinatorThreadId?.trim()) throw new Error('Explicit state, driver and coordinator configuration are required.');
    this.coordinator = coordinatorThreadId;
    this.stateDir = path.join(path.resolve(stateDir), 'app-mailbox'); this.root = driver.root;
    this.driver = driver; this.client = client; this.now = now; this.pollMs = pollMs;
    this.instanceId = randomUUID(); this.running = false; this.closed = false; this.busy = false; this.timer = null;
    this.actorThreadId = typeof env.CODEX_THREAD_ID === 'string' && env.CODEX_THREAD_ID.trim() ? env.CODEX_THREAD_ID : null;
    this.actorSource = this.actorThreadId ? 'launch_environment' : null;
    this.pollActor = null;
    this.delegationUse = null;
    this.leader = null; this.nextLeaderAttempt = 0; this.runtimeState = 'standby'; this.bridgeUnavailable = false;
    this.nextBridgeProbeAttempt = 0; this.bridgeProbe = null; this.lastBridgeProbe = null;
    this.client.onTransportFailure = error => this.bridgeFailed(error);
    this.recordProblems = new Map();
    this.batches = new WakeBatchJournal(path.join(this.stateDir, 'wake-batches'));
    for (const folder of ['projects', 'deliveries', 'faults', 'record-problems']) fs.mkdirSync(path.join(this.stateDir, folder), { recursive: true });
    const bindingPath = path.join(this.stateDir, 'binding.json');
    createJsonOnce(bindingPath, { mailboxRoot: this.root });
    const binding = readJson(bindingPath);
    if (!binding || binding._unreadable || binding.mailboxRoot !== this.root) throw new Error('Receiver state is bound to another or unreadable ledger. Use the same configured roots.');
    this.delegation = new BackgroundDelegation({ stateDir: this.stateDir, mailboxRoot: this.root, coordinator: this.coordinator,
      allWatch: () => this.allWatch(), iso: () => this.iso(), instanceId: this.instanceId });
    this.lastScan = readJson(path.join(this.stateDir, 'scan-status.json')) ?? { ready: [], held: [], uncertain: [], errors: [] };
  }
  iso() { return new Date(this.now()).toISOString(); }
  allPath() { return path.join(this.stateDir, 'all-watch.json'); }
  projectPath(project) { return path.join(this.stateDir, 'projects', `${key(project)}.json`); }
  localPath(id, filename) { return path.join(this.stateDir, 'deliveries', key(id), filename); }
  faultPath(id, filename) { return path.join(this.stateDir, 'faults', id, filename); }
  bindActualCaller(owner, source = 'turn_metadata') {
    // Only a REAL tools/call may supply this fallback; never persisted permission.
    if (!this.actorThreadId && Object.hasOwn(identitySources, source) && typeof owner === 'string' && owner.trim()) {
      this.actorThreadId = owner; this.actorSource = identitySources[source]; this.delegationUse = null; this.nextLeaderAttempt = 0; this.kick();
    }
  }
  currentActor() {
    if (this.actorThreadId) return { threadId: this.actorThreadId, source: this.actorSource, grantId: null };
    const checked = this.delegation.inspect();
    if (!checked.valid) { this.delegationUse = null; return null; }
    const actor = { threadId: checked.record.delegateThreadId, source: 'persisted_delegation', grantId: checked.record.grantId };
    this.delegationUse = actor;
    return actor;
  }
  stillActing(actor) {
    const current = this.currentActor();
    return Boolean(actor && current && actor.threadId === current.threadId && actor.source === current.source && actor.grantId === current.grantId);
  }
  transportAuthorized(actor = this.pollActor) {
    return !this.closed && Boolean(this.leader?.alive) && !this.bridgeUnavailable && this.client.pipePresent && this.stillActing(actor);
  }
  deliveryAuthorized(delivery, actor, epoch) {
    if (!this.transportAuthorized(actor)) return false;
    const route = this.routes()[delivery.project];
    if (!route || route.thread_id !== delivery.target_thread_id) return false;
    const permission = this.permission(delivery.project, route);
    return Boolean(permission && permission.epoch === epoch);
  }
  delegationSummary(owner, checked = this.delegation.inspect()) {
    const summary = { state: checked.state, reason: checked.reason,
      inUseByThisInstance: Boolean(!this.actorThreadId && checked.valid && this.delegationUse?.grantId === checked.record.grantId) };
    return owner === this.coordinator ? { ...summary, grantId: checked.record?.grantId ?? null } : summary;
  }
  async delegationControl(owner, source, operation) {
    this.delegation.requireCoordinator(owner, source);
    const lease = await this.driver.acquireControl();
    if (!lease) throw new Error('Delegation control is busy; no permission changed. Retry the explicit tool call.');
    try {
      if (this.closed || !lease.alive) throw new Error('Service/control lease ended; no permission changed.');
      return this.delegation[operation](owner, source);
    } finally { lease.release(); }
  }
  async grantDelegation(owner, source) {
    const outcome = await this.delegationControl(owner, source, 'grant');
    this.nextLeaderAttempt = 0; this.kick();
    return { outcome, delegation: this.delegationSummary(owner) };
  }
  async revokeDelegation(owner, source) {
    const outcome = await this.delegationControl(owner, source, 'revoke');
    if (!this.actorThreadId) { this.releaseLeader(); this.pollActor = null; this.delegationUse = null; }
    return { outcome, delegation: this.delegationSummary(owner) };
  }
  leaderPath() { return path.join(this.stateDir, 'leader-status.json'); }
  publishLeader(state) {
    writeJsonAtomic(this.leaderPath(), { schemaVersion: 1, instanceId: this.instanceId, actorThreadId: this.pollActor?.threadId ?? this.actorThreadId,
      actorSource: this.pollActor?.source ?? this.actorSource, delegationGrantId: this.pollActor?.grantId ?? null, state, atUtc: this.iso() });
  }
  releaseLeader() {
    if (this.leader?.alive && readJson(this.leaderPath())?.instanceId === this.instanceId) this.publishLeader('released');
    this.leader?.release(); this.leader = null;
  }
  bridgeFailed(error) {
    if (error?.code === 'MCP_REQUEST_TIMEOUT') return false;
    const confirmed = this.client.adapterStatus?.state === 'unavailable'
      || /timed out|process exited|input is closed|EPIPE|ECONNRESET|ENOENT/i.test(String(error));
    if (confirmed) {
      this.nextBridgeProbeAttempt = Math.max(this.nextBridgeProbeAttempt, this.now() + 60000);
      this.bridgeUnavailable = true; this.runtimeState = 'adapter_unavailable'; this.releaseLeader(); this.client.close();
    }
    return confirmed;
  }
  async recoverBridge() {
    const actor = this.currentActor();
    if (this.closed || !this.client.pipePresent || !actor) return false;
    if (this.bridgeProbe) return this.bridgeProbe;
    if (this.now() < this.nextBridgeProbeAttempt) return false;
    this.nextBridgeProbeAttempt = this.now() + 60000;
    this.lastBridgeProbe = { actorThreadId: actor.threadId, actorSource: actor.source, attemptAtUtc: this.iso(), status: 'checking' };
    // Only standard handshake plus a read of THIS actual actor. The client uses
    // its original launch-env snapshot; no persisted/new/guessed pipe or send.
    this.bridgeProbe = (async () => {
      try {
        await this.client.readThread(actor.threadId, actor.threadId, () => !this.closed && this.stillActing(actor));
        if (this.closed || !this.stillActing(actor)) return false;
        this.bridgeUnavailable = false; this.runtimeState = 'standby'; this.nextLeaderAttempt = 0;
        this.lastBridgeProbe = { ...this.lastBridgeProbe, status: 'verified', verifiedAtUtc: this.iso() };
        return true;
      } catch (error) {
        this.bridgeUnavailable = true; this.runtimeState = 'adapter_unavailable'; this.releaseLeader(); this.client.close();
        this.lastBridgeProbe = { ...this.lastBridgeProbe, status: 'failed', reason: String(error) };
        return false;
      }
    })();
    try { return await this.bridgeProbe; } finally { this.bridgeProbe = null; }
  }
  async canPoll() {
    if (!this.client.pipePresent) { this.runtimeState = 'no_pipe'; this.releaseLeader(); this.pollActor = null; return false; }
    const actor = this.currentActor();
    if (!actor) { this.runtimeState = 'identity_unavailable'; this.releaseLeader(); this.pollActor = null; return false; }
    this.pollActor = actor;
    if (this.bridgeUnavailable) {
      this.runtimeState = 'adapter_unavailable'; this.releaseLeader();
      if (!await this.recoverBridge() || !this.stillActing(actor)) return false;
    }
    if (!this.leader?.alive) {
      this.runtimeState = 'standby';
      if (this.now() < this.nextLeaderAttempt) return false;
      this.nextLeaderAttempt = this.now() + 60000;
      this.leader = await this.driver.acquireLeader();
      if (this.closed || this.bridgeUnavailable || !this.client.pipePresent || !this.stillActing(actor)) { this.releaseLeader(); return false; }
    }
    this.runtimeState = this.leader?.alive ? 'leader' : 'standby';
    if (this.runtimeState === 'leader') this.publishLeader('leader');
    return this.runtimeState === 'leader';
  }
  allWatch() {
    const value = readJson(this.allPath());
    if (fs.existsSync(this.allPath()) && (!value || value._unreadable || typeof value !== 'object' || Array.isArray(value)
      || value.schemaVersion !== 1 || typeof value.enabled !== 'boolean' || !validWatchEpoch(value.epoch))) throw new Error('Stored all-watch permission is unreadable or invalid; no scanning or send.');
    return value ?? { enabled: false, epoch: 'initial', registeredBy: null };
  }
  routes() {
    const value = readJson(path.join(this.root, 'routes.json'));
    if (!value || value._unreadable || !value.projects || typeof value.projects !== 'object') throw new Error('Configured routes are unavailable.');
    return value.projects;
  }
  requireOwner(owner, project, { active = false } = {}) {
    if (!validProject(project)) throw new Error('project must select an existing registered route.');
    const route = this.routes()[project];
    if (!route || typeof route.thread_id !== 'string') throw new Error('No registered exact project owner.');
    if (owner !== this.coordinator && owner !== route.thread_id) throw new Error('Actual caller is not the current exact project owner.');
    if (active && route.status !== 'active') throw new Error('The route is not active; this tool cannot restore held/closed business.');
    return route;
  }
  startWatch(owner, args = {}) {
    for (const key of Object.keys(args)) if (key !== 'project') throw new Error(`Unsupported app mailbox argument: ${key}.`);
    if (args.project == null) {
      if (owner !== this.coordinator) throw new Error('Only the configured coordinator can enable all registered active owners.');
      const previous = this.allWatch();
      if (!previous.enabled) writeJsonAtomic(this.allPath(), { schemaVersion: 1, enabled: true, epoch: randomUUID(),
        registeredBy: owner, activatedAtUtc: this.iso() });
    } else {
      const route = this.requireOwner(owner, args.project, { active: true }); const all = this.allWatch();
      writeJsonAtomic(this.projectPath(args.project), { schemaVersion: 1, project: args.project, ownerThreadId: route.thread_id,
        registeredBy: owner, enabled: true, epoch: all.epoch, activatedAtUtc: this.iso() });
    }
    this.kick(); return this.getStatus(owner, args.project ?? null);
  }
  stopWatch(owner, args = {}) {
    for (const key of Object.keys(args)) if (key !== 'project') throw new Error(`Unsupported app mailbox argument: ${key}.`);
    if (args.project == null) {
      if (owner !== this.coordinator) throw new Error('A normal Lead must name its own project; it cannot stop all owners.');
      writeJsonAtomic(this.allPath(), { schemaVersion: 1, enabled: false, epoch: randomUUID(), registeredBy: owner, stoppedAtUtc: this.iso() });
    } else {
      const route = this.requireOwner(owner, args.project); const all = this.allWatch();
      writeJsonAtomic(this.projectPath(args.project), { schemaVersion: 1, project: args.project, ownerThreadId: route.thread_id,
        registeredBy: owner, enabled: false, epoch: all.epoch, stoppedAtUtc: this.iso() });
    }
    return this.getStatus(owner, args.project ?? null);
  }
  permission(project, route) {
    if (route.status !== 'active') return null;
    const all = this.allWatch(); const override = readJson(this.projectPath(project));
    if (override?._unreadable) return null;
    if (override?.epoch === all.epoch && override.ownerThreadId === route.thread_id) {
      if (![route.thread_id, this.coordinator].includes(override.registeredBy)) return null;
      return override.enabled ? override : null;
    }
    return all.enabled && all.registeredBy === this.coordinator ? all : null;
  }
  delivery(id) {
    if (!validId(id)) throw new Error('Invalid central delivery ID.');
    const value = readJson(path.join(this.root, 'deliveries', `${id}.json`));
    if (!value || value._unreadable || value.id !== id || !validProject(value.project)) throw new Error('Central delivery is unreadable or identity is invalid.');
    return value;
  }
  exactRoute(delivery) {
    const route = this.routes()[delivery.project];
    if (!route || route.status !== 'active' || route.thread_id !== delivery.target_thread_id) throw new Error('Current active route does not match the registered exact delivery owner; handoff required.');
    return route;
  }
  notificationIdentity(delivery) {
    if (typeof delivery.created_at !== 'string' || !Number.isFinite(Date.parse(delivery.created_at))
      || typeof delivery.prompt !== 'string' || !delivery.prompt.length) throw new Error('Original immutable notification identity is unavailable.');
    return { id: delivery.id, project: delivery.project, target_thread_id: delivery.target_thread_id,
      created_at: delivery.created_at, promptSha256: createHash('sha256').update(delivery.prompt, 'utf8').digest('hex') };
  }
  matchingHandledProof(delivery) {
    const proof = readJson(this.localPath(delivery.id, 'handled.json'));
    if (!proof || proof._unreadable || proof.schemaVersion !== 1 || proof.kind !== 'owner_handled_notification'
      || proof.acknowledgedBy !== delivery.target_thread_id || !Object.values(identitySources).includes(proof.callerSource)) return null;
    if (this.routes()[delivery.project]?.thread_id !== delivery.target_thread_id) return null;
    let identity;
    try { identity = this.notificationIdentity(delivery); } catch { return null; }
    return Object.keys(identity).every(name => proof.identity?.[name] === identity[name]) ? proof : null;
  }
  suppressHandled(delivery) {
    if (delivery.state !== 'pending' || readJson(this.localPath(delivery.id, 'intent.json'))
      || fs.existsSync(path.join(this.root, 'claims', `${delivery.id}.lock`))) return false;
    const proof = this.matchingHandledProof(delivery); if (!proof) return false;
    const previous = readJson(this.localPath(delivery.id, 'outcome.json'));
    if (previous?._unreadable || terminalOutcomes.has(previous?.status)) return false;
    if (previous?.status !== 'suppressed' || previous.handledAtUtc !== proof.acknowledgedAtUtc) {
      this.localOutcome(delivery.id, 'suppressed', { reason: 'The exact owner handled this notification before a committed send.',
        handledAtUtc: proof.acknowledgedAtUtc, handledProofPath: this.localPath(delivery.id, 'handled.json') }, delivery);
    }
    return true;
  }
  async acknowledgeDeliveries(owner, args, source) {
    if (!Object.hasOwn(identitySources, source)) throw new Error('Notification acknowledgment requires actual tool caller identity.');
    if (!args || Array.isArray(args) || typeof args !== 'object' || Object.keys(args).some(name => name !== 'deliveryIds')
      || !Array.isArray(args.deliveryIds) || args.deliveryIds.length < 1 || args.deliveryIds.length > 32
      || args.deliveryIds.some(id => !validId(id)) || new Set(args.deliveryIds).size !== args.deliveryIds.length) {
      throw new Error('deliveryIds must contain 1 to 32 unique exact delivery IDs; no extra fields are accepted.');
    }
    const validate = () => args.deliveryIds.map(id => {
      const delivery = this.delivery(id);
      if (delivery.target_thread_id !== owner || this.routes()[delivery.project]?.thread_id !== owner) {
        throw new Error('Only the exact original target who is still the current route owner may acknowledge; no coordinator override.');
      }
      const identity = this.notificationIdentity(delivery);
      const prior = readJson(this.localPath(id, 'handled.json'));
      if (prior && !this.matchingHandledProof(delivery)) throw new Error('Existing acknowledgment is unreadable or bound to another notification; retained.');
      return { delivery, identity };
    });
    // Validate every item before writing, then recheck under the send lock.
    const initial = validate();
    const lease = await this.driver.acquireTarget(owner, this.stateDir);
    if (!lease) return { outcome: 'target_send_busy', recorded: false, acknowledgments: [] };
    try {
      if (this.closed || !lease.alive) throw new Error('Target lock ended; nothing recorded.');
      const current = validate();
      if (current.some((item, index) => JSON.stringify(item.identity) !== JSON.stringify(initial[index].identity))) {
        throw new Error('Notification identity changed while acquiring the target lock; nothing recorded.');
      }
      const acknowledgments = [];
      for (const { delivery, identity } of current) {
        try {
          const created = createJsonOnce(this.localPath(delivery.id, 'handled.json'), { schemaVersion: 1,
            kind: 'owner_handled_notification', identity, acknowledgedBy: owner, callerSource: identitySources[source],
            acknowledgedAtUtc: this.iso(), businessAcceptanceRecorded: false, notificationAccepted: false });
          acknowledgments.push({ deliveryId: delivery.id, outcome: created ? 'recorded' : 'already_recorded',
            pendingNotificationSuppressed: this.suppressHandled(delivery), priorTransportState: delivery.state,
            businessAcceptanceRecorded: false, notificationAccepted: delivery.state === 'sent' });
        } catch (error) {
          acknowledgments.push({ deliveryId: delivery.id, outcome: 'record_uncertain', error: String(error),
            reason: 'Inspect this exact acknowledgment; prior claims, receipts and transport outcomes are retained.' });
        }
      }
      return { outcome: 'owner_notification_acknowledgments', acknowledgments };
    } finally { await lease.release(); }
  }
  getStatus(owner, project = null) {
    if (this.bridgeUnavailable) this.kick(); // A status consumer can nudge the running poll loop; rate limit still applies.
    // A standby instance must report current shared evidence, not its startup cache.
    const persistedScan = readJson(path.join(this.stateDir, 'scan-status.json'));
    if (persistedScan && !persistedScan._unreadable) this.lastScan = persistedScan;
    const routes = this.routes();
    if (project != null) this.requireOwner(owner, project);
    const selected = Object.entries(routes).filter(([name, route]) => project != null ? name === project : owner === this.coordinator || route.thread_id === owner);
    if (owner !== this.coordinator && !selected.length) throw new Error('Actual caller has no registered project owner route.');
    const visibleProjects = new Set(selected.map(([name]) => name));
    const ledgerProblems = [];
    const problems = new Map(this.recordProblems);
    for (const name of fs.readdirSync(path.join(this.stateDir, 'record-problems')).filter(name => name.endsWith('.json'))) {
      const filename = path.join(this.stateDir, 'record-problems', name); const problem = readJson(filename);
      if (problem?._unreadable) { if (owner === this.coordinator) ledgerProblems.push({ path: filename, reason: 'Unreadable receiver problem record.' }); }
      else if (problem && !problems.has(problem.problemId)) problems.set(problem.problemId, problem);
    }
    for (const problem of problems.values()) if (owner === this.coordinator || visibleProjects.has(problem.project)) ledgerProblems.push(problem);
    for (const folder of fs.readdirSync(path.join(this.stateDir, 'deliveries')).filter(name => /^id-[A-Za-z0-9_.%-]+$/.test(name))) {
      const filename = path.join(this.stateDir, 'deliveries', folder, 'outcome.json'); const value = readJson(filename);
      if (value?.centralLedgerProblem && (owner === this.coordinator || visibleProjects.has(value.project))) {
        ledgerProblems.push({ deliveryId: value.deliveryId, project: value.project, ownerThreadId: value.ownerThreadId,
          reason: value.reason, error: value.error, path: value.relatedStatePath, outcomePath: filename, metadataSource: 'original_intent' });
      }
    }
    const deliveries = fs.existsSync(path.join(this.root, 'deliveries')) ? fs.readdirSync(path.join(this.root, 'deliveries')).filter(name => name.endsWith('.json'))
      .flatMap(name => {
        const value = readJson(path.join(this.root, 'deliveries', name));
        if (!value || value._unreadable) {
          if (owner === this.coordinator) ledgerProblems.push({ path: path.join(this.root, 'deliveries', name), reason: 'Unreadable central delivery; no send.' });
          return [];
        }
        if (!visibleProjects.has(value.project)) return [];
        if (!validId(value.id)) { ledgerProblems.push({ path: path.join(this.root, 'deliveries', name), project: value.project, reason: 'Invalid central delivery identity; no send.' }); return []; }
        const local = readJson(this.localPath(value.id, 'outcome.json'));
        const wait = readJson(this.localPath(value.id, 'wait.json'));
        const handled = this.matchingHandledProof(value);
        return [{ id: value.id, project: value.project, targetThreadId: value.target_thread_id,
          centralState: value.state, notificationAccepted: value.state === 'sent', actualLeadContinuationVerified: false,
          ownerHandled: Boolean(handled), handledAtUtc: handled?.acknowledgedAtUtc ?? null,
          receiverState: local?._unreadable ? 'uncertain' : local?.status ?? (value.state === 'sending' ? 'manual_review_required' : 'not_sent'),
          reason: local?.reason ?? (value.state === 'sending' ? 'Central sending/claim retained; never blind resend.' : null),
          ownerStatus: wait?.ownerStatus ?? null, blockedSinceUtc: wait?.blockedSinceUtc ?? null }];
      }) : [];
    const filterItems = items => (items ?? []).filter(item => visibleProjects.has(item.project));
    const faults = fs.readdirSync(path.join(this.stateDir, 'faults')).flatMap(id => {
      const value = readJson(this.faultPath(id, 'fault.json'));
      if (value?._unreadable) { if (owner === this.coordinator) ledgerProblems.push({ path: this.faultPath(id, 'fault.json'), reason: 'Unreadable transport fault; no automatic send.' }); return []; }
      if (!value || !visibleProjects.has(value.project)) return [];
      const outcome = readJson(this.faultPath(id, 'outcome.json'));
      const intent = readJson(this.faultPath(id, 'intent.json'));
      return [{ ...value, notificationState: outcome?.status ?? (intent ? 'uncertain' : 'pending'), outcomePath: this.faultPath(id, 'outcome.json') }];
    });
    // Read-only status never adopts or creates delegation, nor revives permission.
    const checkedDelegation = this.delegation.inspect();
    const effectiveActor = this.actorThreadId ? { threadId: this.actorThreadId, source: this.actorSource }
      : checkedDelegation.valid ? { threadId: checkedDelegation.record.delegateThreadId, source: 'persisted_delegation' } : null;
    return { service: { adapter: 'registered-ledger', mailboxRoot: this.root, defaultOff: true, running: this.running,
      pipePresent: this.client.pipePresent, adapterStatus: this.client.adapterStatus ?? null,
      runtimeState: !this.client.pipePresent ? 'no_pipe' : !effectiveActor ? 'identity_unavailable'
        : this.bridgeUnavailable ? 'adapter_unavailable' : this.leader?.alive ? 'leader' : 'standby',
      actorThreadId: effectiveActor?.threadId ?? null, actorSource: effectiveActor?.source ?? null,
      actualActor: { threadId: this.actorThreadId, source: this.actorSource }, delegation: this.delegationSummary(owner, checkedDelegation),
      nextLeaderAttemptUtc: new Date(this.nextLeaderAttempt).toISOString(),
      observedLeader: readJson(this.leaderPath()),
      bridgeHealth: { lastProbe: this.lastBridgeProbe, nextProbeAtUtc: this.bridgeUnavailable ? new Date(this.nextBridgeProbeAttempt).toISOString() : null,
        sameLaunchEnvironmentOnly: true },
      automaticProcessRestart: false, actualAppRestartVerified: false },
      allWatch: owner === this.coordinator ? this.allWatch() : undefined,
      projects: selected.map(([name, route]) => ({ project: name, ownerThreadId: route.thread_id, routeStatus: route.status,
        watchEnabled: Boolean(this.permission(name, route)), override: readJson(this.projectPath(name)) })), deliveries, faults, ledgerProblems,
      scan: { atUtc: this.lastScan.atUtc ?? null, ready: filterItems(this.lastScan.ready), held: filterItems(this.lastScan.held),
        uncertain: filterItems(this.lastScan.uncertain), errors: owner === this.coordinator ? this.lastScan.errors ?? [] : filterItems(this.lastScan.errors) } };
  }
  localOutcome(id, status, extra = {}, delivery = null) {
    writeJsonAtomic(this.localPath(id, 'outcome.json'), { schemaVersion: 1, deliveryId: id, status, atUtc: this.iso(),
      project: delivery?.project ?? null, ownerThreadId: delivery?.target_thread_id ?? null,
      notificationAccepted: status === 'sent', actualLeadContinuationVerified: false, ...extra });
    if (['uncertain', 'manual_review_required'].includes(status)) {
      this.recordFault(delivery, 'delivery-uncertain', extra.reason ?? status, this.localPath(id, 'outcome.json'), id);
    }
  }
  recordProblem(problemKey, facts) {
    const problemId = `${key(facts.deliveryId ?? 'unknown')}--${key(problemKey.split(':')[0])}`; const filename = path.join(this.stateDir, 'record-problems', `${problemId}.json`);
    const value = { schemaVersion: 1, problemId, atUtc: this.iso(), path: filename, persisted: true, ...facts };
    // Preserve status visibility even if the entire state filesystem cannot write.
    this.recordProblems.set(problemId, value);
    try { writeJsonAtomic(filename, value); }
    catch (error) { this.recordProblems.set(problemId, { ...value, persisted: false, persistenceError: String(error) }); }
  }
  recordFault(delivery, category, reason, relatedStatePath, knownId = delivery?.id) {
    try {
      if (!delivery || !validId(delivery.id) || delivery.id !== knownId || !validProject(delivery.project)
        || typeof delivery.target_thread_id !== 'string' || !delivery.target_thread_id.trim()) throw new Error('Known original fault metadata is unavailable or invalid; no owner/route guessing.');
      const faultId = `${key(delivery.id)}--${key(category)}`;
      createJsonOnce(this.faultPath(faultId, 'fault.json'), { schemaVersion: 1, faultId, category,
        deliveryId: delivery.id, project: delivery.project, targetThreadId: delivery.target_thread_id,
        reason: String(reason).slice(0, 400), detectedAtUtc: this.iso(), relatedStatePath });
    } catch (error) {
      this.recordProblem(`fault-record:${knownId}:${category}`, { deliveryId: knownId ?? null, project: delivery?.project ?? null,
        reason: 'Transport fault record could not be published; other deliveries continue.', error: String(error), relatedStatePath });
    }
  }
  observeBlocked(delivery, ownerStatus, reason) {
    const filename = this.localPath(delivery.id, 'wait.json'); const previous = readJson(filename);
    const blockedSinceUtc = previous && !previous._unreadable && previous.blockedSinceUtc ? previous.blockedSinceUtc : this.iso();
    writeJsonAtomic(filename, { deliveryId: delivery.id, ownerStatus, blockedSinceUtc, lastObservedAtUtc: this.iso(), reason });
    this.localOutcome(delivery.id, 'pending', { reason, ownerStatus });
    if (this.now() - Date.parse(blockedSinceUtc) >= 2 * 60 * 60 * 1000) {
      this.recordFault(delivery, 'owner-blocked', `Original owner unavailable for at least 2 hours; status=${ownerStatus}.`, filename);
    }
  }
  observeCollectorErrors(errors) {
    const filename = path.join(this.stateDir, 'collector-observations.json'); const previous = readJson(filename);
    const next = {};
    for (const item of uniqueFacts(errors)) {
      // An invalid/unmapped file is not proof of an actual registered delivery.
      if (!item.job_file || !validProject(item.project)) continue;
      const job = readJson(item.job_file); const route = this.routes()[item.project];
      if (!job || job._unreadable || !validId(job.key) || job.project !== item.project || !route || !this.permission(item.project, route)) continue;
      const observationKey = JSON.stringify([job.key, item.error]);
      const count = (previous && !previous._unreadable ? previous[observationKey]?.consecutive ?? 0 : 0) + 1;
      next[observationKey] = { deliveryId: job.key, project: item.project, error: String(item.error).slice(0, 400), consecutive: count, lastObservedAtUtc: this.iso() };
      if (count >= 3) this.recordFault({ id: job.key, project: item.project, target_thread_id: job.target_thread_id },
        'collector', `Same registered collector error in 3 consecutive polls: ${String(item.error).slice(0, 250)}`, filename);
    }
    writeJsonAtomic(filename, next);
  }
  async flushFaults() {
    const actor = this.pollActor;
    for (const faultId of fs.readdirSync(path.join(this.stateDir, 'faults'))) {
      if (!this.transportAuthorized(actor)) return;
      const fault = readJson(this.faultPath(faultId, 'fault.json'));
      if (!fault || fault._unreadable) continue;
      const outcomePath = this.faultPath(faultId, 'outcome.json');
      if (readJson(outcomePath)) continue;
      const intentPath = this.faultPath(faultId, 'intent.json');
      const existing = readJson(intentPath);
      if (existing) {
        const receipt = readJson(this.faultPath(faultId, 'receipt.json'));
        // A durable intent has already consumed its one attempt, including crash.
        createJsonOnce(outcomePath, { faultId, status: exactSendReceipt(receipt, this.coordinator) ? 'accepted' : 'uncertain',
          atUtc: this.iso(), reason: 'Recovered persisted fault intent; no repeat notification.' });
        continue;
      }
      const prompt = `[app-mailbox-transport-fault:${faultId}]\n此消息由收件系统自动投递。\nid: ${fault.deliveryId}\nproject: ${fault.project}\nreason: ${fault.reason}\nstate: ${fault.relatedStatePath}\nDelivery retained; inspect original state. No automatic resend.`;
      const route = this.routes()[fault.project];
      const permission = route && this.permission(fault.project, route);
      if (!permission) continue;
      const lease = await this.driver.acquireTarget(this.coordinator, this.stateDir);
      if (!lease) continue;
      try {
        // An old owner's fault still targets coordinator; the project permit is current.
        const authorized = () => {
          if (!lease.alive || !this.transportAuthorized(actor)) return false;
          const currentRoute = this.routes()[fault.project];
          return Boolean(currentRoute && this.permission(fault.project, currentRoute)?.epoch === permission.epoch);
        };
        if (!authorized()) return;
        if (!createJsonOnce(intentPath, { schemaVersion: 1, faultId, coordinatorThreadId: this.coordinator,
          actorThreadId: actor.threadId, actorSource: actor.source, delegationGrantId: actor.grantId,
          instanceId: this.instanceId, intentAtUtc: this.iso(), sentFields: ['threadId', 'prompt'], metadataFields: ['x-codex-turn-metadata.thread_id'] })) continue;
        try {
          if (!authorized()) throw new Error('Fault authorization ended before send; intent retained without retry.');
          const receipt = await this.client.send(this.coordinator, prompt, actor.threadId, authorized);
          createJsonOnce(this.faultPath(faultId, 'receipt.json'), receipt);
          createJsonOnce(outcomePath, { faultId, status: receipt?.isError ? 'rejected' : exactSendReceipt(receipt, this.coordinator) ? 'accepted' : 'uncertain',
            atUtc: this.iso(), reason: 'Fault notification attempted once; receipt retained without retry.' });
        } catch (error) {
          createJsonOnce(outcomePath, { faultId, status: 'uncertain', atUtc: this.iso(), error: String(error), reason: 'Fault send failed or ambiguous; no repeat.' });
          this.bridgeFailed(error);
        }
      } finally { await lease.release(); }
    }
  }
  async recoverReceipt(delivery) {
    if (delivery.state !== 'sending') return false;
    const local = readJson(this.localPath(delivery.id, 'receipt.json'));
    const intent = readJson(this.localPath(delivery.id, 'intent.json'));
    const receipt = exactSendReceipt(local, delivery.target_thread_id) ? local
      : this.batches.receipt(intent, delivery.id, delivery.target_thread_id);
    if (!receipt) return false;
    try {
      // Ack records a PRIOR exact acceptance. It cannot start or resume business;
      // use the existing ack's registered-target rule even if the watch was stopped.
      const response = await this.driver.run('ack', { id: delivery.id, receipt });
      if (response.busy) return true;
      this.localOutcome(delivery.id, 'sent', { reason: 'Persisted exact original receipt acknowledged without another send.' }, delivery);
    } catch (error) { this.localOutcome(delivery.id, 'uncertain', { reason: 'Receipt retained; ack could not finish.', error: String(error) }, delivery); }
    return true;
  }
  async recoverLocalReceipts() {
    for (const folder of fs.readdirSync(path.join(this.stateDir, 'deliveries'))) {
      if (!/^id-[A-Za-z0-9_.%-]+$/.test(folder)) continue;
      const outcome = readJson(path.join(this.stateDir, 'deliveries', folder, 'outcome.json'));
      if (outcome && !outcome._unreadable && terminalOutcomes.has(outcome.status)) continue;
      const intentPath = path.join(this.stateDir, 'deliveries', folder, 'intent.json'); const intent = readJson(intentPath);
      if (!intent) continue;
      const original = intent && !intent._unreadable ? { id: intent.deliveryId, project: intent.project, target_thread_id: intent.ownerThreadId } : null;
      try {
        if (!original || !validId(original.id) || key(original.id) !== folder || !validProject(original.project)
          || typeof original.target_thread_id !== 'string' || !original.target_thread_id.trim()) throw new Error('Persisted original intent identity is unreadable or invalid.');
        const delivery = this.delivery(intent.deliveryId);
        if (delivery.target_thread_id !== original.target_thread_id || delivery.project !== original.project) throw new Error('Central identity differs from the persisted original intent; no ack or resend.');
        await this.recoverReceipt(delivery);
      } catch (error) {
        const reason = 'Persisted receipt recovery could not inspect/ack the original ledger; old intent retained without resend.';
        if (original && validId(original.id) && key(original.id) === folder) {
          try {
            this.localOutcome(original.id, 'uncertain', { reason, error: String(error), centralLedgerProblem: true,
              relatedStatePath: path.join(this.root, 'deliveries', `${original.id}.json`) }, original);
          } catch (writeError) {
            this.recordProblem(`recovery-outcome:${folder}`, { deliveryId: original.id, project: original.project,
              reason: 'Recovery outcome could not be published; other deliveries continue.', error: String(writeError), originalError: String(error), relatedStatePath: intentPath });
            this.recordFault(original, 'delivery-uncertain', reason, intentPath);
          }
        } else this.recordProblem(`recovery-intent:${folder}`, { reason, error: String(error), relatedStatePath: intentPath });
      }
    }
  }
  readyDelivery(item, lockedOwner = null) {
    const delivery = this.delivery(item.id); const route = this.exactRoute(delivery);
    if (lockedOwner && delivery.target_thread_id !== lockedOwner) return null;
    const permission = this.permission(delivery.project, route);
    if (!permission || delivery.state !== 'pending') return null;
    if (typeof delivery.prompt !== 'string') throw new Error('Original pending prompt is unavailable.');
    // Local attempts remain one-shot guards even if central pending is restored.
    if (readJson(this.localPath(delivery.id, 'intent.json'))) return null;
    const outcome = readJson(this.localPath(delivery.id, 'outcome.json'));
    if (outcome?._unreadable || terminalOutcomes.has(outcome?.status) || this.suppressHandled(delivery)) return null;
    if (fs.existsSync(path.join(this.root, 'claims', `${delivery.id}.lock`))) return null;
    return { delivery, permission };
  }
  async processReady(item) { return this.processReadyBatch([item]); }
  async processReadyBatch(items) {
    if (!items.length || !this.transportAuthorized()) return;
    const owner = this.delivery(items[0].id).target_thread_id;
    if (items.some(item => this.delivery(item.id).target_thread_id !== owner)) throw new Error('A wake batch cannot cross owners.');
    const lease = await this.driver.acquireTarget(owner, this.stateDir);
    if (!lease) return;
    try { await this.processReadyBatchLocked(items, owner, lease); }
    finally { await lease.release(); }
  }
  async processReadyLocked(item, owner, lease) { return this.processReadyBatchLocked([item], owner, lease); }
  async processReadyBatchLocked(items, owner, lease) {
    const actor = this.pollActor;
    if (!lease.alive || !this.transportAuthorized(actor)) return;
    const candidates = [];
    for (const item of items) {
      try { const ready = this.readyDelivery(item, owner); if (ready) candidates.push(ready); }
      catch (error) { this.recordProblem(`batch-ready:${item.id}`, { deliveryId: item.id, project: item.project, reason: String(error) }); }
    }
    // This ledger stores the FINAL envelope before claim; no private wrapper or
    // separate preview protocol is required. Recheck the exact envelope later.
    const selected = selectPromptBatch(candidates, item => item.delivery.prompt, batchLabel);
    if (!selected.length) return;
    const authorized = () => lease.alive && this.transportAuthorized(actor)
      && selected.every(({ delivery, permission }) => this.deliveryAuthorized(delivery, actor, permission.epoch));
    if (!authorized()) return;
    const caller = actor.threadId; let observed;
    try { observed = await this.client.readThread(owner, caller, authorized); }
    catch (error) {
      if (!authorized()) return;
      for (const { delivery } of selected) this.observeBlocked(delivery, 'read_unavailable', `Preflight unavailable; central pending retained: ${String(error).slice(0, 300)}`);
      this.bridgeFailed(error); return;
    }
    if (!authorized()) return;
    if (observed.thread?.id !== owner) throw new Error('Preflight returned a different owner.');
    const ownerStatus = observed.thread.status?.type ?? 'unknown';
    if (!['idle', 'notLoaded'].includes(ownerStatus)) {
      for (const { delivery } of selected) this.observeBlocked(delivery, ownerStatus, `Original owner status ${ownerStatus}; central pending retained.`);
      return;
    }
    const batchId = this.batches.allocate(); const claimedItems = [];
    try {
      for (const { delivery: original, permission } of selected) {
        if (!authorized()) throw new Error('Watch/route/leader changed during batch assembly; retain claims without sending.');
        const ready = this.readyDelivery(original, owner); if (!ready) continue;
        const { delivery } = ready;
        if (delivery.project !== original.project || delivery.created_at !== original.created_at || delivery.prompt !== original.prompt) {
          throw new Error('Original pending notification changed during preflight; do not claim changed content.');
        }
        writeJsonAtomic(this.localPath(delivery.id, 'wait.json'), { deliveryId: delivery.id, ownerStatus, blockedSinceUtc: null, lastObservedAtUtc: this.iso() });
        let response;
        try { response = await this.driver.run('claim', { id: delivery.id }); }
        catch { continue; } // A claim loser never writes the winner's outcome.
        if (response.busy) continue;
        const claimed = response.result;
        if (claimed.id !== delivery.id || claimed.project !== delivery.project || claimed.state !== 'sending'
          || claimed.target_thread_id !== owner || typeof claimed.prompt !== 'string') throw new Error('Claim identity differs from its original delivery.');
        claimedItems.push(claimed);
        if (claimed.prompt !== original.prompt || claimed.created_at !== original.created_at) throw new Error('Claim envelope differs from the selected final prompt; retain claim without send.');
        if (!authorized()) throw new Error('Authorization ended after claim; retain sending without sending.');
        const intent = { schemaVersion: 1, deliveryId: claimed.id, project: claimed.project, batchId,
          ownerThreadId: owner, callerThreadId: caller, permissionRegisteredBy: permission.registeredBy, actorSource: actor.source,
          delegationGrantId: actor.grantId, instanceId: this.instanceId, intentAtUtc: this.iso(),
          sentFields: ['threadId', 'prompt'], metadataFields: ['x-codex-turn-metadata.thread_id'] };
        if (!createJsonOnce(this.localPath(claimed.id, 'intent.json'), intent)) throw new Error('Existing receiver intent retained; never resend.');
      }
      if (!claimedItems.length) return;
      if (!authorized()) throw new Error('Authorization ended before send; intents retained without resend.');
      this.batches.publish(batchId, owner, claimedItems.map(item => item.id), this.iso());
      const receipt = await this.client.send(owner, combinedPrompt(claimedItems.map(item => item.prompt), batchLabel), caller, authorized);
      this.batches.saveReceipt(batchId, receipt);
      for (const claimed of claimedItems) {
        try {
          createJsonOnce(this.localPath(claimed.id, 'receipt.json'), receipt);
          if (!exactSendReceipt(receipt, owner)) {
            this.localOutcome(claimed.id, 'uncertain', { batchId, reason: receipt?.isError ? 'Official batch send rejected; claim retained.' : 'No exact owner acceptance; claim retained.' }, claimed);
            continue;
          }
          const ack = await this.driver.run('ack', { id: claimed.id, receipt });
          this.localOutcome(claimed.id, ack.busy ? 'receipt_pending_ack' : 'sent', { batchId,
            reason: ack.busy ? 'Exact batch receipt persisted; recover ack only.' : 'Exact batch receipt accepted by per-item central ack.' }, claimed);
        } catch (error) {
          try {
            this.localOutcome(claimed.id, exactSendReceipt(receipt, owner) ? 'receipt_pending_ack' : 'uncertain',
              { batchId, reason: 'Batch receipt retained after interrupted item bookkeeping; never resend.', error: String(error) }, claimed);
          } catch (writeError) { this.recordProblem(`batch-outcome:${claimed.id}`, { deliveryId: claimed.id, project: claimed.project, reason: String(writeError) }); }
        }
      }
    } catch (error) {
      for (const claimed of claimedItems) {
        try {
          const receipt = this.batches.receipt(readJson(this.localPath(claimed.id, 'intent.json')), claimed.id, owner);
          this.localOutcome(claimed.id, receipt ? 'receipt_pending_ack' : 'uncertain', { batchId,
            reason: receipt ? 'Exact batch receipt retained; recover ack only.' : 'Claim retained after failed or ambiguous batch; never blind resend.', error: String(error) }, claimed);
        } catch (writeError) { this.recordProblem(`batch-outcome:${claimed.id}`, { deliveryId: claimed.id, project: claimed.project, reason: String(writeError) }); }
      }
      this.bridgeFailed(error);
    }
  }
  async tick() {
    if (this.closed || this.busy) return; this.busy = true;
    try {
      if (!await this.canPoll()) return;
      await this.recoverLocalReceipts();
      if (!this.transportAuthorized()) return;
      const routes = this.routes();
      if (!Object.entries(routes).some(([project, route]) => this.permission(project, route))) { await this.flushFaults(); return; }
      const scanned = await this.driver.run('scan');
      if (scanned.busy || !this.transportAuthorized()) return;
      const scan = scanned.result;
      const errors = (scan.errors ?? []).map(item => {
        if (!item.job_file) return item;
        const relative = path.relative(path.join(this.root, 'jobs'), path.resolve(item.job_file));
        if (relative.startsWith('..') || path.isAbsolute(relative)) return item;
        const job = readJson(item.job_file); return { ...item, project: job?.project ?? null };
      });
      this.lastScan = { ...scan, errors, atUtc: this.iso() };
      writeJsonAtomic(path.join(this.stateDir, 'scan-status.json'), this.lastScan);
      this.observeCollectorErrors(errors);
      for (const item of scan.uncertain ?? []) {
        if (!this.transportAuthorized()) break;
        try {
          const delivery = this.delivery(item.id); const route = this.exactRoute(delivery);
          if (!this.permission(delivery.project, route)) continue;
          if (!await this.recoverReceipt(delivery)) {
            const outcome = readJson(this.localPath(delivery.id, 'outcome.json'));
            if (!outcome) this.localOutcome(delivery.id, 'manual_review_required', { reason: 'Existing central sending/claim has no exact persisted receiver receipt; never resend.' }, delivery);
            else if (outcome._unreadable || ['uncertain', 'manual_review_required'].includes(outcome.status)) {
              this.recordFault(delivery, 'delivery-uncertain', outcome.reason ?? 'Unreadable outcome; manual review required.', this.localPath(delivery.id, 'outcome.json'));
            }
          }
        } catch (error) { errors.push({ id: item.id, project: item.project, error: String(error) }); }
      }
      const batches = new Map();
      for (const item of scan.ready ?? []) {
        if (!this.transportAuthorized()) break;
        try {
          const ready = this.readyDelivery(item); if (!ready) continue;
          const owner = ready.delivery.target_thread_id;
          if (!batches.has(owner)) batches.set(owner, []);
          batches.get(owner).push(item);
        }
        catch (error) { errors.push({ id: item.id, project: item.project, error: String(error) }); }
      }
      for (const items of batches.values()) {
        if (!this.transportAuthorized()) break;
        try { await this.processReadyBatch(items); }
        catch (error) { errors.push({ id: items[0].id, project: items[0].project, error: String(error) }); }
      }
      this.lastScan.errors = uniqueFacts(errors);
      writeJsonAtomic(path.join(this.stateDir, 'scan-status.json'), this.lastScan);
      await this.flushFaults();
    } catch (error) {
      this.lastScan = { ...this.lastScan, atUtc: this.iso(), errors: [{ source: 'receiver', error: String(error) }] };
      writeJsonAtomic(path.join(this.stateDir, 'scan-status.json'), this.lastScan);
    } finally {
      if (this.pollActor && !this.stillActing(this.pollActor)) {
        this.releaseLeader(); this.pollActor = null; this.runtimeState = this.currentActor() ? 'standby' : 'identity_unavailable';
      }
      this.busy = false;
    }
  }
  kick() { if (this.running && !this.closed && !this.busy) setTimeout(() => void this.tick(), 0); }
  start() {
    if (this.running || this.closed) return; this.running = true;
    const loop = async () => { await this.tick(); if (this.running && !this.closed) this.timer = setTimeout(loop, this.pollMs); };
    this.timer = setTimeout(loop, 0);
  }
  close() { this.closed = true; this.running = false; clearTimeout(this.timer); this.releaseLeader(); this.driver.close(); this.client.close(); }
}
