import { disposeCodexRuntime } from './codex-runtime.mjs';

const AGENTS = ['claude', 'codex', 'dsh', 'grok', 'grok-bot', 'cursor', 'pi', 'opencode'];
const STATUSES = new Set(['live', 'partial', 'unavailable']);
const STATES = new Set(['running', 'idle', 'unknown']);
const counts = ['openWindows', 'openSessions', 'activeSessions', 'idleSessions', 'unknownSessions'];

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
  return result;
}

function unavailable(agentId, now, coverage = '暂时读不到这个 agent 的本机状态。') {
  return { agentId, observedAt: new Date(now).toISOString(), sourceStatus: 'unavailable', coverage };
}

export async function collectAgentRuntime(options = {}) {
  const results = await Promise.allSettled([
    import('./codex-runtime.mjs').then(module => module.collectCodexRuntime(options)),
    import('./local-agents.mjs').then(module => module.collectLocalAgents(options)),
  ]);
  const now = Date.now();
  const codex = results[0].status === 'fulfilled' ? [results[0].value] : [unavailable('codex', now)];
  const local = results[1].status === 'fulfilled' && Array.isArray(results[1].value) ? results[1].value
    : AGENTS.filter(id => id !== 'codex').map(id => unavailable(id, now));
  return [...codex, ...local];
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
