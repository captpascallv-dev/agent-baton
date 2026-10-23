import { randomUUID } from 'node:crypto';

const runningPhases = new Set(['generating', 'tool_running']);
const phases = new Set([...runningPhases, 'waiting_input', 'waiting_approval', 'idle', 'unknown']);
export const HEARTBEAT_MS = 2000;
export const TTL_MS = 10000;
const modelValue = value => typeof value === 'string' && value.length > 0 && value.length <= 160 && !/[\x00-\x1f\x7f]/.test(value) ? value : null;

export class StatusState {
  constructor({ now = Date.now, producerPid = process.pid, producerStartedAt = new Date(Date.now() - process.uptime() * 1000).toISOString() } = {}) {
    this.now = now;
    this.producerPid = producerPid;
    this.producerStartedAt = producerStartedAt;
    this.producerId = randomUUID();
    this.records = new Map();
    this.faulted = false;
  }
  ensure(agent) {
    let record = this.records.get(agent);
    if (!record) {
      record = { id: randomUUID(), status: agent.status, stream: null, turnModel: null, tools: new Map(), waits: new Map() };
      this.records.set(agent, record);
    }
    return record;
  }
  reconcile(agents) {
    const present = new Set(agents);
    for (const agent of this.records.keys()) if (!present.has(agent)) this.records.delete(agent);
    for (const agent of agents) {
      const record = this.ensure(agent);
      if (agent.status === 'idle') this.status(agent, 'idle');
      else if (record.status === 'idle') record.status = 'running';
    }
  }
  status(agent, status) {
    const record = this.ensure(agent);
    record.status = status;
    if (status === 'idle') {
      record.stream = null; record.turnModel = null;
    }
  }
  stream(agent, frame) {
    // Never read frame.chunk, text, outcome, or any streamed content.
    if (frame.type !== 'start' && frame.type !== 'end') return;
    const record = this.ensure(agent);
    if (frame.type === 'start') {
      const model = modelValue(agent.session.requestHeader()?.config?.model);
      record.stream = { attemptId: frame.attemptId, model };
      record.turnModel = model;
    } else if (record.stream?.attemptId === frame.attemptId) record.stream = null;
  }
  begin(agent, kind, callId, rootCallId) {
    const record = this.ensure(agent), token = Symbol(kind);
    const collection = kind === 'tool' ? record.tools : record.waits;
    collection.set(token, { kind, callId, rootCallId });
    return () => { if (this.records.get(agent) === record) collection.delete(token); };
  }
  remove(agent) { this.records.delete(agent); }
  phase(record) {
    if (record.stream) return 'generating';
    const blockedRoots = new Set();
    let unmatchedWait = false;
    for (const wait of record.waits.values()) {
      let matched = false;
      for (const tool of record.tools.values()) if (wait.callId !== undefined && (tool.callId === wait.callId || tool.rootCallId === wait.callId)) {
        blockedRoots.add(tool.rootCallId); matched = true;
      }
      if (!matched && record.tools.size) unmatchedWait = true;
    }
    if (unmatchedWait) return 'unknown';
    if ([...record.tools.values()].some(tool => !blockedRoots.has(tool.rootCallId))) return 'tool_running';
    if (record.waits.size) return [...record.waits.values()].some(wait => wait.kind === 'approval') ? 'waiting_approval' : 'waiting_input';
    return record.status === 'idle' ? 'idle' : 'unknown';
  }
  snapshot(sourceLive = true) {
    const live = sourceLive && !this.faulted, now = this.now();
    return { schemaVersion: 1, producerId: this.producerId, producerPid: this.producerPid, producerStartedAt: this.producerStartedAt,
      observedAt: new Date(now).toISOString(), validUntil: new Date(live ? now + TTL_MS : now).toISOString(),
      sourceLive: live, complete: live, instances: [...this.records.values()].map(record => {
        const phase = live ? this.phase(record) : 'unknown';
        return { id: record.id, phase, currentModel: runningPhases.has(phase) ? record.stream?.model ?? record.turnModel : null };
      }) };
  }
}

// Consumer-side projection: death, expired input, or incomplete observation can
// never keep a previously running model visible. The caller verifies identity.
export function observeSnapshot(frame, { now = Date.now(), producerAlive = false } = {}) {
  const observed = Date.parse(frame?.observedAt), until = Date.parse(frame?.validUntil);
  const live = frame?.schemaVersion === 1 && producerAlive === true && frame.sourceLive === true && frame.complete === true
    && Number.isFinite(observed) && observed <= now && Number.isFinite(until) && until > now && until - observed <= TTL_MS;
  return { sourceLive: live, complete: live, instances: Array.isArray(frame?.instances) ? frame.instances.map(instance => {
    const phase = live && phases.has(instance?.phase) ? instance.phase : 'unknown';
    return { id: typeof instance?.id === 'string' && /^[0-9a-f-]{36}$/i.test(instance.id) ? instance.id : null, phase,
      currentModel: runningPhases.has(phase) ? modelValue(instance.currentModel) : null };
  }) : [] };
}
