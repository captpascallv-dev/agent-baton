import test from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { collectCodexRuntime, createCodexRuntimeCollector } from "../server/codex-runtime.mjs";
import { CodexIpcObserver } from "../server/codex-ipc.mjs";

const NOW = Date.parse("2026-10-22T15:00:00Z");
const START = "2026-10-22T00:00:00Z";
const iso = value => new Date(value).toISOString();
const ids = ["11111111-1111-7111-8111-111111111111", "22222222-2222-7222-8222-222222222222",
  "33333333-3333-7333-8333-333333333333", "44444444-4444-7444-8444-444444444444"];
const pipe = "\\\\.\\pipe\\codex-ipc";
const endpoint = { path: pipe, hostPid: 42, hostStartedAt: START };
const ipc = { ...endpoint, path: "\\\\.\\pipe\\codex-ipc" };
function host(overrides = {}) {
  return { observedAt: iso(NOW), openWindows: 2, hosts: [{ pid: 42, startedAt: START }],
    endpoints: [endpoint], ...overrides };
}
function metadata(threads, overrides = {}) {
  return { observedAt: iso(NOW), hostPid: 42, hostStartedAt: START, complete: true, threads, ...overrides };
}
function fixtureOptions(threads, overrides = {}) {
  return { now: () => NOW, readHost: async () => host(), readMetadata: async () => metadata(threads), ...overrides };
}
function turnModel(model) { return { model, modelSource: "activeTurn", modelObservedAt: iso(NOW) }; }
function encode(value) {
  const data = Buffer.from(JSON.stringify(value)), header = Buffer.alloc(4);
  header.writeUInt32LE(data.length); return Buffer.concat([header, data]);
}
function fakeTransport(snapshots = {}, unanswered = new Set()) {
  const messages = [], connections = [];
  function createConnection(address) {
    const socket = new EventEmitter(); socket.destroyed = false;
    const connection = { address, ended: false }; connections.push(connection);
    const emit = value => queueMicrotask(() => {
      if (socket.destroyed) return;
      const data = encode(value);
      socket.emit("data", data.subarray(0, 3)); socket.emit("data", data.subarray(3));
    });
    socket.write = data => {
      const message = JSON.parse(data.subarray(4).toString()); messages.push({ address, message });
      if (address === ipc.path && message.method === "initialize") emit({ type: "response", method: "initialize",
        resultType: "success", result: { clientId: "observer-client" } });
      if (message.method === "thread-owner-discovery" && !unanswered.has(message.params.conversationId)) {
        const id = message.params.conversationId;
        if (snapshots[id]) emit({ type: "response", requestId: message.requestId, method: message.method,
          resultType: "success", handledByClientId: `owner:${id}`, result: { supportsUntrustedAppInput: true } });
        else emit({ type: "response", requestId: message.requestId, resultType: "error", error: "no-client-found" });
      }
      if (address === ipc.path && message.method === "thread-stream-following-changed" && message.params.following) {
        const state = snapshots[message.params.conversationId];
        if (state) emit({ type: "broadcast", method: "thread-stream-state-changed", version: 11,
          sourceClientId: `owner:${message.params.conversationId}`, targetClientIds: ["observer-client"], params: {
            conversationId: message.params.conversationId, hostId: "local",
            change: { type: "snapshot", revision: 1, conversationState: state },
          } });
      }
      return true;
    };
    socket.end = () => { connection.ended = true; socket.destroy(); };
    socket.destroy = () => { if (!socket.destroyed) { socket.destroyed = true; socket.emit("close"); } };
    queueMicrotask(() => socket.emit("connect")); return socket;
  }
  return { createConnection, messages, connections };
}

test("generation/tool execution count as running; both kinds of human wait count as idle", async () => {
  const rows = [
    { id: ids[0], status: { type: "active", activeFlags: [] }, ...turnModel("gpt-6.1-sol") },
    { id: ids[1], status: { type: "active", activeFlags: [] }, ...turnModel("gpt-6-luna") },
    { id: ids[2], status: { type: "active", activeFlags: ["waitingOnUserInput"] }, ...turnModel("gpt-6-astra") },
    { id: ids[3], status: { type: "active", activeFlags: ["waitingOnApproval"] }, ...turnModel("gpt-6-astra") },
  ];
  const value = await collectCodexRuntime(fixtureOptions(rows));
  assert.equal(value.sourceStatus, "live");
  assert.deepEqual([value.openWindows, value.openSessions, value.activeSessions, value.idleSessions, value.unknownSessions], [2, 4, 2, 2, 0]);
  assert.deepEqual(value.instances.map(x => x.model), ["gpt-6.1-sol", "gpt-6-luna", null, null]);
});

test("bare App list active, unknown flags, and system errors stay unknown instead of zero/idle", async () => {
  const value = await collectCodexRuntime(fixtureOptions([
    { id: ids[0], status: "active" }, { id: ids[1], status: { type: "active", activeFlags: ["newFlag"] } },
    { id: ids[2], status: "systemError" }, { id: ids[3], status: "idle" },
  ]));
  assert.equal(value.activeSessions, null);
  assert.equal(value.idleSessions, 1); assert.equal(value.unknownSessions, 3);
  assert.equal(value.sourceStatus, "partial");
});

test("only a fresh active-turn model is displayed", async () => {
  const value = await collectCodexRuntime(fixtureOptions([
    { id: ids[0], status: { type: "active", activeFlags: [] }, model: "launch-default" },
    { id: ids[1], status: { type: "active", activeFlags: [] }, ...turnModel("gpt-6.1-sol"), modelObservedAt: iso(NOW - 31_000) },
    { id: ids[2], status: { type: "active", activeFlags: [] }, ...turnModel("sk-proj-private") },
  ]));
  assert.equal(value.activeSessions, 3); assert.equal(value.sourceStatus, "partial");
  assert.deepEqual(value.instances.map(x => x.model), [null, null, null]);
});

test("stale or unreadable signals never become an empty idle machine", async () => {
  for (const options of [
    fixtureOptions([], { readMetadata: async () => metadata([], { observedAt: iso(NOW - 31_000) }) }),
    fixtureOptions([], { readMetadata: async () => { throw new Error("private error"); } }),
    fixtureOptions([], { readMetadata: async () => metadata([], { complete: false }) }),
  ]) {
    const value = await collectCodexRuntime(options);
    assert.equal(value.openWindows, 2); assert.equal(value.openSessions, null);
    assert.equal(value.activeSessions, null); assert.equal(value.idleSessions, null);
    assert.equal(JSON.stringify(value).includes("private error"), false);
  }
  const staleHost = await collectCodexRuntime(fixtureOptions([], { readHost: async () => host({ observedAt: iso(NOW - 31_000) }) }));
  assert.equal(staleHost.openWindows, null); assert.equal(staleHost.sourceStatus, "unavailable");
});

test("a standalone/new app-server or reused PID is not accepted as the attached desktop host", async () => {
  let reads = 0;
  const value = await collectCodexRuntime(fixtureOptions([], {
    readHost: async () => host({ endpoints: [{ ...endpoint, hostPid: 99 }] }),
    readMetadata: async () => { reads++; return metadata([]); },
  }));
  assert.equal(reads, 0); assert.equal(value.openSessions, null);
  const wrongGeneration = await collectCodexRuntime(fixtureOptions([], {
    readMetadata: async () => metadata([], { hostStartedAt: "2026-10-21T00:00:00Z" }),
  }));
  assert.equal(wrongGeneration.activeSessions, null);
});

test("validated closed App and complete empty loaded inventory can report real zero", async () => {
  const closed = await collectCodexRuntime(fixtureOptions([], { readHost: async () => host({ openWindows: 0, hosts: [], endpoints: [] }) }));
  assert.deepEqual([closed.sourceStatus, closed.openWindows, closed.openSessions, closed.activeSessions], ["live", 0, 0, 0]);
  const empty = await collectCodexRuntime(fixtureOptions([]));
  assert.equal(empty.activeSessions, 0); assert.equal(empty.openSessions, 0);
});

test("independent observer filters private fields, resolves current model, and unsubscribes on close", async () => {
  const secret = "PRIVATE_TITLE_PROMPT_TOKEN_MUST_NOT_ESCAPE";
  const transport = fakeTransport({
    [ids[0]]: { threadRuntimeStatus: { type: "active", activeFlags: [] }, latestModel: "wrong-next-turn-model", title: secret,
      turnHistory: { kind: "canonical", history: { entitiesByKey: {
        old: { status: "completed", params: { model: "wrong-old-model", input: secret } },
        current: { status: "inProgress", params: { model: null, collaborationMode: { settings: { model: "gpt-6.1-sol" } }, input: secret } },
      } } } },
    [ids[1]]: { threadRuntimeStatus: { type: "active", activeFlags: ["waitingOnUserInput"] }, latestModel: "gpt-6-astra", turns: [] },
  });
  const value = await collectCodexRuntime({ now: () => NOW, readHost: async () => host(),
    readCatalog: async () => ({ observedAt: iso(NOW), complete: true, ids }), ...transport });
  assert.deepEqual([value.openWindows, value.openSessions, value.activeSessions, value.idleSessions, value.unknownSessions], [2, 2, 1, 1, 0]);
  assert.deepEqual(value.instances.map(x => x.model), ["gpt-6.1-sol", null]);
  assert.equal(JSON.stringify(value).includes(secret), false);
  assert.equal(JSON.stringify(value).includes("wrong-"), false);
  const ipcMessages = transport.messages.filter(x => x.address === ipc.path).map(x => x.message);
  assert.equal(ipcMessages[0].params.clientType, "ai-moe-studio-observer");
  assert.equal(ipcMessages.some(x => /resume|start-turn|update|interrupt|acquire|set.*owner|tools\/call/.test(x.method)), false);
  assert.equal(ipcMessages.filter(x => x.method === "thread-owner-discovery").length, 4);
  assert.equal(ipcMessages.filter(x => x.params?.following === true).length, 2);
  assert.equal(ipcMessages.filter(x => x.params?.following === false).length, 2);
  assert.ok(transport.connections.every(x => x.address !== ipc.path || x.ended));
});

test("an incomplete metadata catalog keeps exact totals unknown", async () => {
  const transport = fakeTransport();
  const value = await collectCodexRuntime({ now: () => NOW, readHost: async () => host(),
    readCatalog: async () => ({ observedAt: iso(NOW), complete: false, ids }), ...transport });
  assert.equal(value.openSessions, null); assert.equal(value.activeSessions, null);
});

test("an unanswered owner lookup cannot turn a partial observation into zero", async () => {
  const transport = fakeTransport({}, new Set([ids[0]]));
  const value = await collectCodexRuntime({ now: () => NOW, readHost: async () => host(), timeoutMs: 20,
    readCatalog: async () => ({ observedAt: iso(NOW), complete: true, ids: [ids[0]] }), ...transport });
  assert.equal(value.openSessions, null); assert.equal(value.activeSessions, null);
  assert.equal(value.sourceStatus, "partial");
  assert.ok(transport.connections.every(x => x.ended));
});

test("malformed IPC envelopes return unknown without escaping the collector", async () => {
  const createConnection = () => {
    const socket = new EventEmitter(); socket.destroyed = false;
    socket.write = () => { queueMicrotask(() => socket.emit("data", encode(null))); return true; };
    socket.end = socket.destroy = () => { socket.destroyed = true; };
    queueMicrotask(() => socket.emit("connect")); return socket;
  };
  const value = await collectCodexRuntime({ now: () => NOW, readHost: async () => host(), createConnection,
    readCatalog: async () => ({ observedAt: iso(NOW), complete: true, ids: [ids[0]] }) });
  assert.equal(value.openSessions, null); assert.equal(value.activeSessions, null);
  assert.equal(value.sourceStatus, "partial");
});

function incrementalHarness(options = {}) {
  const h = { sent: [], sockets: [], snapshotSends: 0, snapshotRevision: 1, release: null, holding: false, deferred: [] };
  h.emit = message => { if (h.holding) h.deferred.push(message); else if (!h.socket?.destroyed) h.socket.emit("data", encode(message)); };
  h.snapshot = () => {
    h.snapshotSends++;
    const message = { type: "broadcast", method: "thread-stream-state-changed", version: 11,
      sourceClientId: "owner", targetClientIds: ["observer"], params: { hostId: "local", conversationId: ids[0], change: {
        type: "snapshot", revision: h.snapshotRevision, conversationState: {
          threadRuntimeStatus: { type: "active", activeFlags: [] }, privatePayload: "SKIP_PAYLOAD",
          latestModel: "wrong-next-turn", turnHistory: { kind: "canonical", history: { entitiesByKey: {
            old: { status: "completed", params: { model: "old-model", input: "private-old-prompt" } },
            current: { status: "inProgress", turnId: ids[1], params: { collaborationMode: { settings: { model: "gpt-6.1-sol" } }, input: "private-current-prompt" } },
          } } },
        },
      } } };
    const json = JSON.stringify(message), needle = '"SKIP_PAYLOAD"', at = json.indexOf(needle);
    const prefix = Buffer.from(json.slice(0, at) + '"'), suffix = Buffer.from('"' + json.slice(at + needle.length));
    const payloadBytes = options.largeBytes ?? 0, header = Buffer.alloc(4);
    header.writeUInt32LE(options.rejectedFrameBytes ?? prefix.length + payloadBytes + suffix.length);
    h.socket.emit("data", header.subarray(0, 1)); h.socket.emit("data", header.subarray(1));
    if (options.rejectedFrameBytes) return;
    h.socket.emit("data", prefix);
    const finish = () => {
      const block = Buffer.alloc(64 * 1024, 120);
      for (let remaining = payloadBytes; remaining > 0; remaining -= block.length) {
        if (h.socket.destroyed) return;
        h.socket.emit("data", block.subarray(0, Math.min(remaining, block.length)));
      }
      h.socket.emit("data", suffix);
      h.holding = false;
      for (const queued of h.deferred.splice(0)) h.emit(queued);
    };
    if (options.hold) { h.holding = true; h.release = finish; } else finish();
  };
  h.createConnection = () => {
    const socket = new EventEmitter(); socket.destroyed = false; h.socket = socket; h.sockets.push(socket);
    socket.write = data => {
      const message = JSON.parse(data.subarray(4)); h.sent.push(message);
      if (message.method === "initialize") queueMicrotask(() => h.emit({ type: "response", method: "initialize", resultType: "success", result: { clientId: "observer" } }));
      if (message.method === "thread-owner-discovery" && !h.ignoreDiscoveries) queueMicrotask(() => h.emit({ type: "response", requestId: message.requestId,
        resultType: "success", handledByClientId: "owner" }));
      if (message.method === "thread-stream-following-changed" && message.params.following) queueMicrotask(h.snapshot);
      return true;
    };
    socket.end = socket.destroy = () => { if (!socket.destroyed) { socket.destroyed = true; socket.emit("close"); } };
    queueMicrotask(() => socket.emit("connect")); return socket;
  };
  h.patch = (baseRevision, revision, patches) => h.emit({ type: "broadcast", method: "thread-stream-state-changed", version: 11,
    sourceClientId: "owner", targetClientIds: ["observer"], params: { hostId: "local", conversationId: ids[0], change: {
      type: "patches", baseRevision, revision, patches,
    } } });
  return h;
}

test("long snapshot: stream over 16MiB, retain only active metadata, reuse owner without another snapshot", async () => {
  const h = incrementalHarness({ largeBytes: 36 * 1024 * 1024 });
  const observer = new CodexIpcObserver(ipc, { createConnection: h.createConnection, now: () => NOW });
  try {
    const first = await observer.collect([ids[0]]);
    assert.equal(first.complete, true); assert.equal(first.threads[0].model, "gpt-6.1-sol");
    assert.ok(first.stats.largestFrameBytes > 16 * 1024 * 1024);
    assert.ok(first.stats.selectedBytes < 16 * 1024); assert.ok(first.stats.peakTokenBytes <= 512);
    const retained = JSON.stringify([...observer.states.values()]);
    assert.ok(retained.length < 2_000); assert.equal(retained.includes("private"), false); assert.equal(retained.includes("old-model"), false);
    const before = observer.diagnostics().receivedBytes;
    const second = await observer.collect([ids[0]]);
    assert.equal(second.complete, true); assert.equal(h.snapshotSends, 1);
    assert.ok(observer.diagnostics().receivedBytes - before < 2_000);
    assert.equal(h.sent.filter(x => x.params?.following === true).length, 1);
  } finally { observer.dispose(); }
  assert.equal(h.sent.filter(x => x.params?.following === false).length, 1);
});

test("incremental lifecycle: apply state/model patches, reject revision gaps, resync once on next refresh", async () => {
  const h = incrementalHarness();
  const observer = new CodexIpcObserver(ipc, { createConnection: h.createConnection, now: () => NOW });
  try {
    await observer.collect([ids[0]]);
    h.patch(1, 2, [
      { op: "replace", path: ["threadRuntimeStatus"], value: { type: "active", activeFlags: ["waitingOnUserInput"] } },
      { op: "replace", path: ["turnHistory", "history", "entitiesByKey", "current", "params", "input"], value: "PRIVATE_PATCH_CONTENT" },
    ]);
    const waiting = await observer.collect([ids[0]]);
    assert.deepEqual(waiting.threads[0].status, { type: "active", activeFlags: ["waitingOnUserInput"] });
    assert.equal(JSON.stringify([...observer.states.values()]).includes("PRIVATE_PATCH_CONTENT"), false);
    h.patch(2, 3, [
      { op: "replace", path: ["threadRuntimeStatus", "activeFlags"], value: [] },
      { op: "replace", path: ["turnHistory", "history", "entitiesByKey", "current", "params", "collaborationMode", "settings", "model"], value: "gpt-6-luna" },
    ]);
    assert.equal((await observer.collect([ids[0]])).threads[0].model, "gpt-6-luna");
    h.patch(99, 100, []);
    assert.equal(observer.snapshot().complete, false); assert.equal(observer.snapshot().phase, "revision_gap");
    assert.equal(observer.snapshot().threads.length, 0);
    h.snapshotRevision = 101;
    const repaired = await observer.collect([ids[0]]);
    assert.equal(repaired.complete, true); assert.equal(h.snapshotSends, 2);
  } finally { observer.dispose(); }
});

test("incremental lifecycle: loading stays incomplete and refresh does not refollow while first frame is pending", async () => {
  const h = incrementalHarness({ hold: true });
  const observer = new CodexIpcObserver(ipc, { createConnection: h.createConnection });
  try {
    const partial = await observer.collect([ids[0]], { timeoutMs: 15 });
    assert.equal(partial.complete, false); assert.equal(partial.phase, "loading");
    const again = observer.collect([ids[0]], { timeoutMs: 15 });
    await new Promise(resolve => setTimeout(resolve, 1));
    assert.equal(h.snapshotSends, 1); h.release();
    assert.equal((await again).complete, true);
  } finally { observer.dispose(); }
});

test("incremental lifecycle: disposing an in-flight read resolves it and closes only the observer", async () => {
  const h = incrementalHarness({ hold: true });
  const observer = new CodexIpcObserver(ipc, { createConnection: h.createConnection });
  const pending = observer.collect([ids[0]]);
  await new Promise(resolve => setTimeout(resolve, 1)); observer.dispose();
  assert.equal((await pending).complete, false); assert.equal(h.socket.destroyed, true);
  assert.equal(h.sent.filter(x => x.params?.following === false).length, 1);
  assert.equal(h.sent.some(x => /resume|start-turn|set.*owner|interrupt/.test(x.method)), false);
});

test("incremental lifecycle: explicit frame limit and disconnect invalidate previous metadata", async () => {
  const limit = 64 * 1024 * 1024 + 1;
  const h = incrementalHarness({ rejectedFrameBytes: limit });
  const observer = new CodexIpcObserver(ipc, { createConnection: h.createConnection });
  const rejected = await observer.collect([ids[0]]);
  assert.equal(rejected.complete, false); assert.equal(rejected.phase, "frame_limit");
  assert.equal(rejected.stats.rejectedFrameBytes, limit); assert.equal(rejected.stats.largestFrameBytes, limit);
  observer.dispose();
  const connected = incrementalHarness(), second = new CodexIpcObserver(ipc, { createConnection: connected.createConnection });
  await second.collect([ids[0]]); connected.socket.destroy();
  assert.equal(second.snapshot().phase, "disconnected"); assert.equal(second.snapshot().threads.length, 0);
  assert.equal((await second.collect([ids[0]])).complete, true); assert.equal(connected.snapshotSends, 2); second.dispose();
});

test("incremental lifecycle: a confirmed long tool keeps its current model after 30 seconds and idle release disposes", async () => {
  const h = incrementalHarness(); let now = NOW;
  const collector = createCodexRuntimeCollector({ now: () => now, idleMs: 30, createConnection: h.createConnection,
    readHost: async () => host({ observedAt: iso(now) }),
    readCatalog: async () => ({ observedAt: iso(now), complete: true, ids: [ids[0]] }) });
  assert.equal((await collector.collect()).instances[0].model, "gpt-6.1-sol");
  now += 31_000;
  const unchanged = await collector.collect();
  assert.equal(unchanged.activeSessions, 1); assert.equal(unchanged.instances[0].model, "gpt-6.1-sol");
  assert.equal(h.snapshotSends, 1);
  await new Promise(resolve => setTimeout(resolve, 40));
  assert.equal(h.socket.destroyed, true); assert.equal(collector.diagnostics().sources.length, 0);
  collector.dispose(); assert.equal((await collector.collect()).sourceStatus, "unavailable");
});

test("incremental lifecycle: connected socket with owner lookup timeout withdraws cached running/model until a new baseline", async () => {
  const h = incrementalHarness(); let now = NOW;
  const observer = new CodexIpcObserver(ipc, { createConnection: h.createConnection, now: () => now, ownerTimeoutMs: 15 });
  try {
    assert.equal((await observer.collect([ids[0]])).threads[0].model, "gpt-6.1-sol");
    h.ignoreDiscoveries = true; now += 31_000;
    const missing = await observer.collect([ids[0]], { timeoutMs: 100 });
    assert.equal(missing.complete, false); assert.equal(missing.phase, "owner_unconfirmed");
    assert.equal(missing.stats.connected, true); assert.equal(missing.threads.length, 0); assert.equal(observer.states.size, 0);
    assert.equal(h.snapshotSends, 1);
    h.ignoreDiscoveries = false; h.snapshotRevision = 3;
    const recovered = await observer.collect([ids[0]]);
    assert.equal(recovered.complete, true); assert.equal(recovered.threads[0].model, "gpt-6.1-sol");
    assert.equal(h.snapshotSends, 2);
  } finally { observer.dispose(); }
});

test("incremental lifecycle: pre-aborted production signal cannot create a default observer", async () => {
  const controller = new AbortController(); controller.abort();
  const value = await collectCodexRuntime({ signal: controller.signal });
  assert.equal(value.sourceStatus, "unavailable"); assert.equal(value.activeSessions, null);
});
