import net from "node:net";
import { randomUUID } from "node:crypto";
import { CodexMetadataJson, relevantCodexPath } from "./codex-metadata-json.mjs";

const MAX_FRAME_BYTES = 64 * 1024 * 1024;
const frame = value => { const data = Buffer.from(JSON.stringify(value)), h = Buffer.alloc(4); h.writeUInt32LE(data.length); return Buffer.concat([h, data]); };
const clone = value => structuredClone(value);

export class CodexIpcObserver {
  constructor(endpoint, options = {}) {
    this.endpoint = endpoint; this.options = options; this.socket = null; this.clientId = null; this.wanted = new Set();
    this.owners = new Map(); this.requests = new Map(); this.states = new Map(); this.revisions = new Map(); this.absent = new Set();
    this.resyncNeeded = new Set();
    this.confirmed = new Set(); this.unconfirmed = new Set(); this.requestTimer = null;
    this.waiters = new Set(); this.phase = "disconnected"; this.closed = false;
    this.header = Buffer.alloc(4); this.headerBytes = 0; this.remaining = 0; this.parser = null;
    this.traffic = { receivedBytes: 0, frames: 0, snapshotFrames: 0, patchFrames: 0, largestFrameBytes: 0, rejectedFrameBytes: 0, selectedBytes: 0, peakTokenBytes: 0 };
  }
  connect() {
    if (this.socket || this.closed) return;
    this.phase = "loading";
    try { this.socket = (this.options.createConnection ?? net.createConnection)(this.endpoint.path); }
    catch { this.fail("disconnected"); return; }
    const socket = this.socket;
    socket.once("connect", () => this.send({ type: "request", requestId: randomUUID(), sourceClientId: "initializing-client",
      version: 0, method: "initialize", params: { clientType: "ai-moe-studio-observer" } }));
    socket.on("data", bytes => { if (this.socket === socket) this.onData(bytes); });
    socket.once("error", () => { if (this.socket === socket) this.fail("disconnected"); });
    socket.once("close", () => { if (this.socket === socket) this.fail("disconnected"); });
    socket.unref?.();
  }
  send(value) { try { if (this.socket && !this.socket.destroyed) this.socket.write(frame(value)); } catch { this.fail("disconnected"); } }
  follow(id, following, owner = this.owners.get(id)) {
    if (owner && this.clientId) this.send({ type: "broadcast", method: "thread-stream-following-changed", sourceClientId: this.clientId,
      targetClientIds: [owner], version: 1, params: { conversationId: id, hostId: "local", following } });
  }
  discover() {
    if (!this.clientId || !this.socket || this.socket.destroyed) return;
    const sentAt = Date.now(), ownerTimeoutMs = this.options.ownerTimeoutMs ?? 1_500;
    for (const id of this.wanted) {
      if ([...this.requests.values()].some(request => request.id === id)) continue;
      const requestId = randomUUID(); this.requests.set(requestId, { id, sentAt });
      this.confirmed.delete(id); this.absent.delete(id);
      this.send({ type: "request", requestId, sourceClientId: this.clientId, version: 1, method: "thread-owner-discovery",
        params: { hostId: "local", conversationId: id }, timeoutMs: ownerTimeoutMs });
    }
    if (this.requests.size && !this.requestTimer) this.requestTimer = setTimeout(() => this.expireRequests(), ownerTimeoutMs);
  }
  expireRequests() {
    this.requestTimer = null;
    const now = Date.now(), limit = this.options.ownerTimeoutMs ?? 1_500;
    for (const [key, request] of this.requests) if (now - request.sentAt >= limit) {
      this.requests.delete(key); this.confirmed.delete(request.id); this.absent.delete(request.id);
      this.states.delete(request.id); this.revisions.delete(request.id); this.unconfirmed.add(request.id); this.resyncNeeded.add(request.id);
    }
    if (this.unconfirmed.size) this.phase = "owner_unconfirmed";
    if (this.requests.size) this.requestTimer = setTimeout(() => this.expireRequests(), Math.max(1,
      Math.min(...[...this.requests.values()].map(request => request.sentAt + limit - now))));
    this.notify();
  }
  onData(bytes) {
    if (this.closed) return;
    this.traffic.receivedBytes += bytes.length;
    let offset = 0;
    try {
      while (offset < bytes.length) {
        if (!this.remaining) {
          const size = Math.min(4 - this.headerBytes, bytes.length - offset);
          bytes.copy(this.header, this.headerBytes, offset, offset + size); offset += size; this.headerBytes += size;
          if (this.headerBytes < 4) return;
          const length = this.header.readUInt32LE(0); this.headerBytes = 0;
          this.traffic.largestFrameBytes = Math.max(this.traffic.largestFrameBytes, length);
          if (!length || length > MAX_FRAME_BYTES) { this.traffic.rejectedFrameBytes = length; this.fail("frame_limit"); return; }
          this.remaining = length; this.parser = new CodexMetadataJson();
        }
        const size = Math.min(this.remaining, bytes.length - offset);
        this.parser.write(bytes.subarray(offset, offset + size)); offset += size; this.remaining -= size;
        if (!this.remaining) {
          const message = this.parser.finish(); this.traffic.frames++;
          this.traffic.selectedBytes += this.parser.selectedBytes;
          this.traffic.peakTokenBytes = Math.max(this.traffic.peakTokenBytes, this.parser.peakTokenBytes);
          this.parser = null; this.onMessage(message);
          if (!this.socket || this.closed) return;
        }
      }
    } catch { this.fail("invalid_metadata"); }
  }
  onMessage(message) {
    if (!message || typeof message !== "object" || Array.isArray(message)) { this.fail("invalid_metadata"); return; }
    if (message.type === "response" && message.method === "initialize") {
      if (message.resultType !== "success" || typeof message.result?.clientId !== "string") { this.fail("disconnected"); return; }
      this.clientId = message.result.clientId; this.discover(); this.notify(); return;
    }
    if (message.type === "response" && this.requests.has(message.requestId)) {
      const { id } = this.requests.get(message.requestId); this.requests.delete(message.requestId);
      if (!this.requests.size) { clearTimeout(this.requestTimer); this.requestTimer = null; }
      if (!this.wanted.has(id)) return;
      if (message.resultType === "success" && typeof message.handledByClientId === "string") {
        const previous = this.owners.get(id), owner = message.handledByClientId;
        this.absent.delete(id); this.confirmed.add(id); this.unconfirmed.delete(id);
        if (previous !== owner) {
          this.follow(id, false, previous); this.states.delete(id); this.revisions.delete(id);
          this.owners.set(id, owner); this.follow(id, true);
        } else if (this.resyncNeeded.delete(id)) {
          this.phase = "loading"; this.follow(id, true);
        }
      } else if (message.error === "no-client-found") {
        this.follow(id, false); this.owners.delete(id); this.states.delete(id); this.revisions.delete(id); this.absent.add(id);
        this.resyncNeeded.delete(id); this.confirmed.add(id); this.unconfirmed.delete(id);
      } else {
        this.absent.delete(id); this.states.delete(id); this.revisions.delete(id); this.confirmed.delete(id);
        this.unconfirmed.add(id); this.resyncNeeded.add(id); this.phase = "owner_unconfirmed";
      }
      this.notify(); return;
    }
    if (message.type === "client-discovery-request") {
      this.send({ type: "client-discovery-response", requestId: message.requestId, response: { canHandle: false } }); return;
    }
    if (message.type === "broadcast" && message.method === "client-status-changed" && message.params?.status === "disconnected") {
      for (const [id, owner] of this.owners) if (owner === message.params.clientId) {
        this.owners.delete(id); this.states.delete(id); this.revisions.delete(id); this.absent.delete(id);
        this.confirmed.delete(id); this.unconfirmed.add(id);
      }
      this.phase = "loading"; this.notify(); return;
    }
    const id = message.params?.conversationId, change = message.params?.change;
    if (message.type !== "broadcast" || message.method !== "thread-stream-state-changed" || message.version !== 11
      || message.params?.hostId !== "local" || !this.wanted.has(id) || this.owners.get(id) !== message.sourceClientId
      || !Array.isArray(message.targetClientIds) || !message.targetClientIds.includes(this.clientId)) return;
    if (change?.type === "snapshot") {
      if (!Number.isInteger(change.revision) || !change.conversationState?.threadRuntimeStatus) { this.phase = "invalid_metadata"; this.notify(); return; }
      this.traffic.snapshotFrames++; this.states.set(id, change.conversationState); this.revisions.set(id, change.revision);
      this.resyncNeeded.delete(id); this.phase = this.resyncNeeded.size ? "revision_gap" : "loading";
    } else if (change?.type === "patches") {
      this.traffic.patchFrames++;
      if (this.revisions.get(id) !== change.baseRevision || change.revision !== change.baseRevision + 1 || !this.states.has(id)) {
        this.states.delete(id); this.revisions.delete(id); this.resyncNeeded.add(id); this.phase = "revision_gap"; this.notify(); return;
      }
      for (const patch of change.patches ?? []) applyMetadataPatch(this.states.get(id), patch);
      pruneCompletedTurns(this.states.get(id)); this.revisions.set(id, change.revision);
    }
    this.notify();
  }
  complete() {
    return !!this.clientId && !!this.socket && !this.socket.destroyed && this.requests.size === 0
      && [...this.wanted].every(id => this.confirmed.has(id) && (this.absent.has(id) || this.states.has(id))) && this.remaining === 0;
  }
  phaseName() { return this.complete() ? "ready" : this.unconfirmed.size ? "owner_unconfirmed" : this.phase; }
  snapshot() {
    const now = typeof this.options.now === "function" ? this.options.now() : this.options.now ?? Date.now();
    return { threads: [...this.states].filter(([id]) => this.wanted.has(id) && this.confirmed.has(id)).map(([id, value]) => project(value, id, now)),
      complete: this.complete(), phase: this.phaseName(), stats: this.diagnostics() };
  }
  async collect(ids, options = {}) {
    if (this.closed) return { threads: [], complete: false, phase: "disconnected", stats: this.diagnostics() };
    for (const id of this.wanted) if (!ids.includes(id)) {
      this.follow(id, false); this.owners.delete(id); this.states.delete(id); this.revisions.delete(id); this.absent.delete(id);
      this.confirmed.delete(id); this.unconfirmed.delete(id); this.resyncNeeded.delete(id);
    }
    this.wanted = new Set(ids);
    if (this.phase === "revision_gap" && this.resyncNeeded.size) this.phase = "loading";
    // Retry only on an actual caller refresh, never in a background loop.
    if (!this.socket || this.socket.destroyed) { this.socket = null; this.connect(); }
    else this.discover();
    if (this.complete()) return this.snapshot();
    await new Promise(resolve => {
      const wake = () => {
        const phase = this.phaseName();
        if (this.complete() || ["disconnected", "revision_gap", "frame_limit", "invalid_metadata"].includes(phase)
          || phase === "owner_unconfirmed" && this.requests.size === 0) done();
      };
      const done = () => { clearTimeout(timer); this.waiters.delete(wake); resolve(); };
      const timer = setTimeout(done, options.timeoutMs ?? 10_000);
      this.waiters.add(wake); wake();
    });
    return this.snapshot();
  }
  notify() { for (const wake of [...this.waiters]) wake(); }
  fail(phase) {
    this.phase = phase; this.clientId = null; this.requests.clear(); this.states.clear(); this.revisions.clear(); this.absent.clear(); this.owners.clear(); this.resyncNeeded.clear();
    this.confirmed.clear(); this.unconfirmed.clear(); clearTimeout(this.requestTimer); this.requestTimer = null;
    const socket = this.socket; this.socket = null; socket?.destroy();
    this.remaining = 0; this.headerBytes = 0; this.parser = null; this.notify();
  }
  diagnostics() { return { ...this.traffic, connected: !!this.clientId && !!this.socket && !this.socket.destroyed,
    retainedThreads: this.states.size, pendingOwnerQueries: this.requests.size, unconfirmedOwners: this.unconfirmed.size, phase: this.phaseName() }; }
  dispose() {
    if (this.closed) return;
    for (const id of this.owners.keys()) this.follow(id, false);
    this.closed = true; this.phase = "disconnected"; const socket = this.socket; this.socket = null; this.clientId = null;
    this.states.clear(); this.owners.clear(); this.requests.clear(); this.revisions.clear(); this.absent.clear(); this.resyncNeeded.clear(); this.notify();
    this.confirmed.clear(); this.unconfirmed.clear(); clearTimeout(this.requestTimer); this.requestTimer = null;
    socket?.end(); const timer = setTimeout(() => socket?.destroy(), 50); timer.unref?.();
  }
}

function applyMetadataPatch(root, patch) {
  const p = patch?.path;
  if (!relevantCodexPath(p) || !["add", "replace", "remove"].includes(patch.op)) return;
  if (!p.length) { for (const key of Object.keys(root)) delete root[key]; Object.assign(root, clone(patch.value ?? {})); return; }
  if (p.some(key => ["__proto__", "prototype", "constructor"].includes(key))) throw new Error("metadata_path");
  let target = root;
  for (let i = 0; i < p.length - 1; i++) {
    const key = p[i];
    if (!target[key] || typeof target[key] !== "object") target[key] = typeof p[i + 1] === "number" ? [] : {};
    target = target[key];
  }
  const key = p.at(-1);
  if (patch.op === "remove") { if (Array.isArray(target)) target.splice(Number(key), 1); else delete target[key]; }
  else if (patch.op === "add" && Array.isArray(target)) target.splice(Number(key), 0, clone(patch.value));
  else target[key] = clone(patch.value);
}
function pruneCompletedTurns(value) {
  const entities = value.turnHistory?.history?.entitiesByKey;
  if (entities) for (const [key, turn] of Object.entries(entities)) if (turn?.status !== "inProgress") delete entities[key];
  if (Array.isArray(value.turns)) value.turns.forEach((turn, index) => { if (turn?.status !== "inProgress") delete value.turns[index]; });
}
function project(value, id, now) {
  const entities = value.turnHistory?.kind === "canonical" ? Object.values(value.turnHistory.history?.entitiesByKey ?? {}) : value.turns;
  const active = Array.isArray(entities) ? entities.filter(turn => turn?.status === "inProgress" && turn.params) : [];
  const model = active.length === 1 ? active[0].params.collaborationMode?.settings?.model ?? active[0].params.model : null;
  return { id, status: clone(value.threadRuntimeStatus), model: typeof model === "string" ? model : null,
    modelSource: typeof model === "string" ? "activeTurn" : null, modelObservedAt: new Date(now).toISOString() };
}

// Compatibility for callers deliberately requesting a bounded one-shot read.
export async function readCodexIpcMetadata(endpoint, ids, options = {}) {
  const observer = new CodexIpcObserver(endpoint, options);
  try { return await observer.collect(ids, options); } finally { observer.dispose(); }
}
