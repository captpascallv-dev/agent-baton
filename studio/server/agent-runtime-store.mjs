import { disposeCodexRuntime } from './codex-runtime.mjs';

const AGENTS = ['claude', 'codex', 'dsh', 'grok', 'grok-bot', 'cursor', 'pi', 'opencode'];
const STATUSES = new Set(['live', 'partial', 'unavailable']);
const STATES = new Set(['running', 'idle', 'unknown']);
const counts = ['openWindows', 'openSessions', 'activeSessions', 'idleSessions', 'unknownSessions'];
const JOB_KEY = /^([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})--([1-9]\d*)$/i;
const isCodexWork = work => /\bcodex\b/i.test(`${work?.actorName ?? ''} ${work?.actorKind ?? ''}`);
const isCurrentWork = work => work && !work.isHistorical && work.quality !== 'LastKnown';

function count(value) { return Number.isSafeInteger(value) && value >= 0 ? value : null; }
function at(value) { return typeof value === 'string' && Number.isFinite(Date.parse(value)) ? value : null; }
function safeText(value, limit = 180) {
  if (typeof value !== 'string') return null;
  const text = value.trim();
  if (!text || /(?:[a-z]:[\\/]|\\\\|https?:\/\/|\bBearer\s|\bsk-[a-z0-9]|(?:token|secret|password|api[_-]?key)\s*[:=])/i.test(text)) return null;
  return text.slice(0, limit);
}

export function publicAgentObservation(raw, now = Date.now(), staleAfterMs = 60_000) {
  if (!AGENTS.includes(raw?.agentId)) return null;
  const observedAt = at(raw.observedAt);
  const expired = !observedAt || now - Date.parse(observedAt) > staleAfterMs || Date.parse(observedAt) - now > 5_000;
  const sourceStatus = expired || !STATUSES.has(raw.sourceStatus) ? 'unavailable' : raw.sourceStatus;
  const result = {
    agentId: raw.agentId, observedAt, sourceStatus,
    ...Object.fromEntries(counts.map(key => [key, sourceStatus === 'unavailable' ? null : count(raw[key])])),
    instances: [],
    coverage: expired ? '状态尚未更新，运行情况暂时未知。' : safeText(raw.coverage) || '只能显示已确认的本机状态。',
  };
  if (sourceStatus !== 'unavailable' && Array.isArray(raw.instances)) {
    result.instances = raw.instances.slice(0, 500).map((item, index) => {
      const state = STATES.has(item?.state) ? item.state : 'unknown';
      return { id: `${raw.agentId}:${index}`, kind: item?.kind === 'window' ? 'window' : 'conversation', state,
        model: state === 'running' ? safeText(item.model, 100) : null };
    });
  }
  // External CLI evidence has its own clock and is independent of App coverage.
  const externalObservedAt = at(raw.externalObservedAt);
  if (raw.agentId === 'codex' && externalObservedAt && now - Date.parse(externalObservedAt) <= staleAfterMs
    && now - Date.parse(externalObservedAt) >= -5_000 && Array.isArray(raw.externalJobs)) {
    result.externalObservedAt = externalObservedAt;
    result.externalJobs = raw.externalJobs.slice(0, 32).filter(job => JOB_KEY.test(job?.jobKey ?? '')).map(job => ({
      jobKey: job.jobKey, state: STATES.has(job.state) ? job.state : 'unknown', model: null,
    }));
  }
  return result;
}

// Resolve only current Codex jobs already admitted by CLI Watch; never walk the
// historical mailbox tree or infer an executor from the configured model.
export function codexCliCandidates(snapshot) {
  if (!snapshot || snapshot.source?.stale || !['live', 'degraded'].includes(snapshot.source?.status)) return [];
  const result = new Map();
  for (const project of snapshot.projects ?? []) {
    const match = JOB_KEY.exec(project.id ?? '');
    if (!project.isCurrent || project.quality === 'LastKnown' || !match
      || !Number.isSafeInteger(Number(match[2])) || !project.works?.some(work => isCurrentWork(work) && isCodexWork(work))) continue;
    result.set(project.id, { jobId: match[1], attempt: Number(match[2]), jobKey: project.id });
    if (result.size === 32) break;
  }
  return [...result.values()];
}

export function applyCodexCliRuntime(snapshot, runtime, now = Date.now()) {
  const allowed = new Set(codexCliCandidates(snapshot).map(job => job.jobKey));
  const raw = runtime?.agents?.find(agent => agent.agentId === 'codex');
  const observed = raw && publicAgentObservation(raw, now);
  const byJob = new Map((observed?.externalJobs ?? []).map(job => [job.jobKey, job]));
  if (!allowed.size || !byJob.size) return snapshot;
  return { ...snapshot, projects: snapshot.projects.map(project => {
    const signal = allowed.has(project.id) && byJob.get(project.id);
    if (!signal) return project;
    return { ...project, works: project.works.map(work => isCurrentWork(work) && isCodexWork(work)
      ? { ...work, runtimeState: signal.state, runtimeModel: null } : work) };
  }) };
}

function unavailable(agentId, now, coverage = '暂时读不到这个 agent 的本机状态。') {
  return { agentId, observedAt: new Date(now).toISOString(), sourceStatus: 'unavailable', coverage };
}

export async function collectAgentRuntime(options = {}) {
  const local = await import('./local-agents.mjs');
  // CLI identity checks and other local agents share the same process frame.
  const processSnapshot = Promise.resolve().then(() => (options.readProcessSnapshot ?? local.readProcessSnapshot)());
  const cli = options.codexCliStateRoot && options.codexCliHome && options.readExternalCandidates
    ? Promise.all([processSnapshot, options.readExternalCandidates()]).then(async ([snapshot, candidates]) => {
      const collect = options.collectCliRuntime ?? (await import('./codex-cli-runtime.mjs')).collectCodexCliRuntime;
      const externalJobs = await collect({ candidates, processSnapshot: snapshot,
        stateRoot: options.codexCliStateRoot, codexHome: options.codexCliHome });
      return { externalJobs, externalObservedAt: new Date().toISOString() };
    }) : Promise.resolve(null);
  const results = await Promise.allSettled([
    import('./codex-runtime.mjs').then(module => module.collectCodexRuntime(options)),
    local.collectLocalAgents({ ...options, readProcessSnapshot: () => processSnapshot }),
    cli,
  ]);
  const now = Date.now();
  const codex = results[0].status === 'fulfilled' ? [results[0].value] : [unavailable('codex', now)];
  if (results[2].status === 'fulfilled' && results[2].value) Object.assign(codex[0], results[2].value);
  const others = results[1].status === 'fulfilled' && Array.isArray(results[1].value) ? results[1].value
    : AGENTS.filter(id => id !== 'codex').map(id => unavailable(id, now));
  return [...codex, ...others];
}

// One bounded collection at a time; HTTP reads never wait on local app discovery.
export class AgentRuntimeStore {
  constructor({ collect = collectAgentRuntime, dispose = collect === collectAgentRuntime ? disposeCodexRuntime : () => {}, collectOptions = {}, now = Date.now, refreshMs = 5_000, staleAfterMs = 60_000 } = {}) {
    this.collect = collect; this.now = now; this.refreshMs = refreshMs; this.staleAfterMs = staleAfterMs;
    this.dispose = dispose; this.disposed = null; this.collectOptions = collectOptions;
    this.observations = []; this.observedAt = null; this.timer = null; this.running = false;
    this.controller = null; this.started = false; this.closed = false;
  }
  start() {
    if (this.started || this.closed) return;
    this.started = true;
    void this.refresh();
  }
  async refresh() {
    if (this.running || this.closed) return;
    this.running = true;
    this.controller = new AbortController();
    try {
      const raw = await this.collect({ ...this.collectOptions, signal: this.controller.signal });
      if (this.closed) return;
      const now = this.now();
      const seen = new Set();
      this.observations = (Array.isArray(raw) ? raw : []).map(item => publicAgentObservation(item, now, this.staleAfterMs))
        .filter(item => item && !seen.has(item.agentId) && seen.add(item.agentId));
      this.observedAt = new Date(now).toISOString();
    } catch {
      // Failure invalidates active claims immediately; no raw error or path is exposed.
      const now = this.now();
      this.observations = AGENTS.map(id => unavailable(id, now));
      this.observedAt = new Date(now).toISOString();
    } finally {
      this.running = false;
      if (!this.closed && this.started) {
        this.timer = setTimeout(() => void this.refresh(), this.refreshMs);
        this.timer.unref?.();
      }
    }
  }
  get() {
    return { observedAt: this.observedAt, agents: this.observations.map(item => publicAgentObservation(item, this.now(), this.staleAfterMs)) };
  }
  stop() {
    if (this.closed) return this.disposed;
    this.closed = true;
    clearTimeout(this.timer);
    this.controller?.abort();
    try { this.disposed = Promise.resolve(this.dispose()).catch(() => {}); }
    catch { this.disposed = Promise.resolve(); }
    return this.disposed;
  }
}
