import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { key } from './ids.mjs';
import { readJson, writeJsonAtomic, createJsonOnce } from './store.mjs';
import { exactSendReceipt } from './official-tools.mjs';
import { BackgroundDelegation, identitySources, validWatchEpoch } from './delegation.mjs';

const pluginRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const driverPath = path.join(pluginRoot, 'driver', 'mailbox_driver.py');
const validId = value => typeof value === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,220}$/.test(value);
const validProject = value => typeof value === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,150}$/.test(value);
const terminalOutcomes = new Set(['sent', 'uncertain', 'manual_review_required']);
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
        return [{ id: value.id, project: value.project, targetThreadId: value.target_thread_id,
          centralState: value.state, notificationAccepted: value.state === 'sent', actualLeadContinuationVerified: false,
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
    const receipt = readJson(this.localPath(delivery.id, 'receipt.json'));
    if (!receipt || receipt._unreadable || !exactSendReceipt(receipt, delivery.target_thread_id)) return false;
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
  async processReady(item) {
    const actor = this.pollActor;
    if (!this.transportAuthorized(actor)) return;
    const candidate = this.delivery(item.id); const route = this.exactRoute(candidate);
    if (candidate.state !== 'pending' || !this.permission(candidate.project, route)) return;
    const lease = await this.driver.acquireTarget(candidate.target_thread_id, this.stateDir);
    if (!lease) return;
    try { await this.processReadyLocked(item, candidate.target_thread_id, lease); }
    finally { await lease.release(); }
  }
  async processReadyLocked(item, lockedOwner, lease) {
    const actor = this.pollActor;
    if (!lease.alive || !this.transportAuthorized(actor)) return;
    let delivery = this.delivery(item.id); const route = this.exactRoute(delivery);
    if (delivery.target_thread_id !== lockedOwner) return;
    const permission = this.permission(delivery.project, route);
    if (!permission || delivery.state !== 'pending') return;
    if (fs.existsSync(path.join(this.root, 'claims', `${delivery.id}.lock`))) return;
    if (!this.client.pipePresent) return;
    const caller = actor.threadId;
    const authorized = () => lease.alive && this.deliveryAuthorized(delivery, actor, permission.epoch);
    if (!authorized()) return;
    let observed;
    try { observed = await this.client.readThread(delivery.target_thread_id, caller, authorized); }
    catch (error) {
      if (!authorized()) return;
      this.observeBlocked(delivery, 'read_unavailable', `Preflight unavailable; central pending retained: ${String(error).slice(0, 300)}`);
      this.bridgeFailed(error); return;
    }
    if (!authorized()) return;
    const ownerStatus = observed.thread.status?.type ?? 'unknown';
    if (!['idle', 'notLoaded'].includes(ownerStatus)) {
      this.observeBlocked(delivery, ownerStatus, `Original owner status ${ownerStatus}; central pending retained.`); return;
    }
    writeJsonAtomic(this.localPath(delivery.id, 'wait.json'), { deliveryId: delivery.id, ownerStatus, blockedSinceUtc: null, lastObservedAtUtc: this.iso() });
    delivery = this.delivery(item.id); const latestRoute = this.exactRoute(delivery);
    if (!this.permission(delivery.project, latestRoute) || !authorized()) return;
    let claimed;
    try {
      const response = await this.driver.run('claim', { id: delivery.id });
      if (response.busy) return; claimed = response.result;
    } catch { return; } // A claim loser must never write the winner's outcome or raise a fault.
    try {
      if (claimed.id !== delivery.id || claimed.project !== delivery.project || claimed.state !== 'sending'
        || claimed.target_thread_id !== delivery.target_thread_id || typeof claimed.prompt !== 'string') throw new Error('Claim identity/owner/prompt differs from the registered delivery.');
      this.exactRoute(claimed);
      if (!authorized()) throw new Error('Watch/leader/delegation stopped after central claim; retain sending without sending.');
      const intent = { schemaVersion: 1, deliveryId: claimed.id, project: claimed.project,
        ownerThreadId: claimed.target_thread_id, callerThreadId: caller, permissionRegisteredBy: permission.registeredBy, actorSource: actor.source,
        delegationGrantId: actor.grantId, instanceId: this.instanceId,
        intentAtUtc: this.iso(), sentFields: ['threadId', 'prompt'],
        metadataFields: ['x-codex-turn-metadata.thread_id'] };
      if (!createJsonOnce(this.localPath(claimed.id, 'intent.json'), intent)) throw new Error('Existing receiver intent retained; no automatic resend.');
      if (!authorized()) throw new Error('Authorization ended before send; intent retained without resend.');
      const receipt = await this.client.send(claimed.target_thread_id, claimed.prompt, caller, authorized);
      createJsonOnce(this.localPath(claimed.id, 'receipt.json'), receipt);
      if (!exactSendReceipt(receipt, claimed.target_thread_id)) {
        this.localOutcome(claimed.id, 'uncertain', { reason: receipt?.isError ? 'Official send rejected; central sending/lock retained.' : 'No exact owner acceptance; central sending/lock retained.' }, claimed); return;
      }
      const ack = await this.driver.run('ack', { id: claimed.id, receipt });
      if (ack.busy) this.localOutcome(claimed.id, 'receipt_pending_ack', { reason: 'Exact receipt persisted; OS lock busy. Next tick may ack only.' }, claimed);
      else this.localOutcome(claimed.id, 'sent', { reason: 'Original exact receipt accepted by the existing central ack.' }, claimed);
    } catch (error) {
      this.localOutcome(delivery.id, 'uncertain', { reason: 'Central sending/claim retained after failed or ambiguous send; never blind resend.', error: String(error) }, delivery);
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
      for (const item of scan.ready ?? []) {
        if (!this.transportAuthorized()) break;
        try { await this.processReady(item); }
        catch (error) { errors.push({ id: item.id, project: item.project, error: String(error) }); }
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
