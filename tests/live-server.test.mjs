import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { mkdtemp, mkdir, writeFile, unlink, rmdir } from "node:fs/promises";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { APP_ID, createStudioServer, loadConfig, SnapshotStore } from "../server/live-server.mjs";

const axisNames = ["process", "turn", "execution", "transport", "delivery", "callback", "leadHandling", "acceptance", "adoption", "goal"];
function payload(at = new Date().toISOString()) {
  const work = { id: "work:fixture", actorName: "CLI", actorKind: "executor", role: "executor",
    route: "watch:running_active", taskName: "fixture task", summary: "fixture", model: "fixture-model", effort: "xhigh",
    blocker: null, quality: "Fresh", isHistorical: false,
    axes: Object.fromEntries(axisNames.map(axis => [axis, axis === "process" ? "alive" : "unknown"])),
    artifacts: [{ label: "交付", state: "present", path: "DO_NOT_EXPORT" }], evidence: "DO_NOT_EXPORT", commandLine: "DO_NOT_EXPORT" };
  return { schemaVersion: 1, mode: "live", source: { name: "CLI Watch", collectedAt: at, publishedAt: at,
    quality: "Conflicting", stale: false, status: "degraded", error: null, coverage: "本机 CLI 执行状态" },
  projects: [{ id: "fixture", name: "fixture", projectLabel: "fixture", summary: "fixture", phase: "running_active",
    statusLabel: "运行中有活动", statusBasis: "fixture", goal: "fixture", isCurrent: true, quality: "Fresh",
    lastFactAt: at, observedActivityAt: at, works: [work], rawFacts: "DO_NOT_EXPORT" }],
  issues: [{ code: "fixture", detail: "scope=historical", scope: "history", sourceId: "DO_NOT_EXPORT" }],
  rawDocuments: "DO_NOT_EXPORT" };
}
async function fixture(t, runtime = false) {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-moe-studio-test-"));
  const files = [];
  const clientDirectory = path.join(directory, "client");
  await mkdir(clientDirectory);
  const put = async (filename, content) => { await writeFile(filename, content); if (!files.includes(filename)) files.push(filename); };
  await put(path.join(clientDirectory, "index.html"), "<html>fixture studio</html>");
  await put(path.join(clientDirectory, "app.js"), "export const fixture = true;");
  await put(path.join(clientDirectory, "config.json"), '{"private":"DO_NOT_EXPORT"}');
  await put(path.join(directory, "private.txt"), "DO_NOT_EXPORT");
  const snapshotPath = path.join(directory, "snapshot.json");
  const config = { port: 0, cliWatchRuntimeDirectory: path.join(directory, "runtime"), cliWatchConfig: path.join(directory, "cli.json"),
    bridgeExecutable: path.join(directory, "fixture-bridge.exe"), snapshotPath, refreshSeconds: 5, staleAfterSeconds: 60 };
  const spawned = [];
  const spawnProcess = (executable, args, options) => {
    const child = new EventEmitter();
    child.kills = 0; child.kill = () => { child.kills++; return true; };
    spawned.push({ executable, args, options, child }); return child;
  };
  const app = createStudioServer(config, { clientDirectory, spawnProcess, runtime });
  await app.listen();
  t.after(async () => {
    await app.close();
    for (const filename of files.reverse()) await unlink(filename);
    await rmdir(clientDirectory); await rmdir(directory);
  });
  const request = (url, options = {}) => new Promise((resolve, reject) => {
    const req = http.request({ hostname: "127.0.0.1", port: app.server.address().port, path: url,
      method: options.method ?? "GET", agent: false,
      headers: { Host: "127.0.0.1:" + app.server.address().port, ...options.headers } }, res => {
      const parts = []; res.on("data", part => parts.push(part));
      res.on("end", () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(parts).toString() }));
    });
    req.on("error", reject); req.end();
  });
  return { directory, config, snapshotPath, put, app, spawned, request };
}

test("loopback service identifies live mode, serves built client, and publishes only allowed fields", async t => {
  const f = await fixture(t);
  assert.equal(f.app.server.address().address, "127.0.0.1");
  const health = await f.request("/health");
  assert.deepEqual(JSON.parse(health.body), { app: APP_ID, schemaVersion: 1, mode: "live" });
  assert.match((await f.request("/")).body, /fixture studio/);
  assert.match((await f.request("/tasks/fixture", { headers: { Accept: "text/html" } })).body, /fixture studio/);
  const staticResponse = await f.request("/app.js");
  assert.equal(staticResponse.status, 200);
  assert.match(staticResponse.headers["content-type"], /javascript/);
  const starting = JSON.parse((await f.request("/api/snapshot")).body);
  assert.equal(starting.source.status, "starting");
  assert.equal(starting.source.collectedAt, null);
  await f.put(f.snapshotPath, JSON.stringify(payload()));
  const response = await f.request("/api/snapshot");
  const api = JSON.parse(response.body);
  assert.equal(api.projects[0].quality, "Fresh");
  assert.equal(api.source.quality, "Conflicting");
  assert.equal(api.projects[0].works[0].axes.acceptance, "unknown");
  assert.equal(api.issues[0].scope, "history");
  assert.equal(api.projects[0].projectLabel, "fixture");
  assert.equal(api.projects[0].works[0].isHistorical, false);
  assert.deepEqual(api.projects[0].works[0].artifacts[0], { label: "交付", state: "present" });
  assert.doesNotMatch(response.body, /DO_NOT_EXPORT|rawFacts|commandLine|evidence/);
  assert.equal(response.headers["access-control-allow-origin"], undefined);
  const head = await f.request("/api/snapshot", { method: "HEAD" });
  assert.equal(head.status, 200); assert.equal(head.body, "");
});

test("invalid/deleted snapshots and bridge failures retain real data without advancing freshness", async t => {
  const f = await fixture(t);
  const firstAt = new Date().toISOString();
  await f.put(f.snapshotPath, JSON.stringify(payload(firstAt)));
  const first = await f.app.store.get();
  await f.put(f.snapshotPath, "{broken");
  const invalid = await f.app.store.get();
  assert.equal(invalid.source.status, "degraded");
  assert.equal(invalid.source.stale, true);
  assert.equal(invalid.source.collectedAt, firstAt);
  assert.equal(invalid.source.publishedAt, firstAt);
  assert.equal(invalid.projects.length, first.projects.length);
  await unlink(f.snapshotPath);
  const missing = await f.app.store.get();
  assert.equal(missing.source.status, "degraded");
  assert.equal(missing.source.collectedAt, firstAt);
  await f.put(f.snapshotPath, JSON.stringify({ ...payload(), source: { ...payload().source, collectedAt: null } }));
  assert.equal((await f.app.store.get()).source.collectedAt, firstAt);
  f.spawned[0].child.emit("exit", 1);
  const stopped = await f.app.store.get();
  assert.equal(stopped.source.status, "degraded");
  assert.match(stopped.source.error, /桥接已停止/);
});

test("agent runtime refresh publishes independently while the CLI frame is unchanged", async t => {
  let started = 0, stopped = 0;
  let observedAt = "2026-10-22T15:00:00Z";
  const runtime = { start: () => { started++; }, stop: () => { stopped++; }, get: () => ({ observedAt, agents: [] }) };
  const f = await fixture(t, runtime);
  await f.put(f.snapshotPath, JSON.stringify(payload()));
  const first = JSON.parse((await f.request("/api/snapshot")).body);
  observedAt = "2026-10-22T15:00:05Z";
  const next = JSON.parse((await f.request("/api/snapshot")).body);
  assert.equal(started, 1);
  assert.equal(first.source.publishedAt, next.source.publishedAt);
  assert.notEqual(first.agentRuntime.observedAt, next.agentRuntime.observedAt);
  assert.equal(stopped, 0);
});

test("Codex CLI native state reaches the HTTP snapshot without changing App session counts", async t => {
  const key = '11111111-1111-7111-8111-111111111111--1';
  const runtime = { start() {}, stop() {}, get: () => ({ observedAt: new Date().toISOString(), agents: [{
    agentId: 'codex', observedAt: new Date().toISOString(), sourceStatus: 'live', activeSessions: 2, openSessions: 2,
    instances: [], externalObservedAt: new Date().toISOString(), externalJobs: [{ jobKey: key, state: 'running', model: null }],
  }] }) };
  const f = await fixture(t, runtime);
  const frame = payload(); frame.projects[0].id = key; frame.projects[0].works[0].actorName = 'Codex';
  await f.put(f.snapshotPath, JSON.stringify(frame));
  const api = JSON.parse((await f.request('/api/snapshot')).body);
  assert.equal(api.projects[0].works[0].runtimeState, 'running');
  assert.equal(api.projects[0].works[0].runtimeModel, null);
  assert.equal(api.agentRuntime.agents[0].activeSessions, 2);
});

test("Codex CLI config requires the state root and native home together", async t => {
  const f = await fixture(t);
  const filename = path.join(f.directory, 'config.json');
  const raw = { ...f.config, port: 4173, codexCliStateRoot: 'native-jobs' };
  await f.put(filename, JSON.stringify(raw));
  await assert.rejects(loadConfig(filename), /codexCliHome/);
  await f.put(filename, JSON.stringify({ ...raw, codexCliHome: 'native-home' }));
  const loaded = await loadConfig(filename);
  assert.equal(loaded.codexCliStateRoot, path.join(f.directory, 'native-jobs'));
  assert.equal(loaded.codexCliHome, path.join(f.directory, 'native-home'));
});

test("missing/invalid initial frames and expired frames are explicit starting/offline", async t => {
  const f = await fixture(t);
  let now = Date.parse("2026-10-22T01:00:00Z");
  const store = new SnapshotStore(f.snapshotPath, 60, () => now);
  assert.equal((await store.get()).source.status, "starting");
  now += 61000;
  assert.equal((await store.get()).source.status, "offline");
  await f.put(f.snapshotPath, "{broken");
  const invalid = await store.get();
  assert.equal(invalid.source.status, "offline"); assert.equal(invalid.projects.length, 0);
  const oldAt = "2026-10-22T01:00:00Z";
  await f.put(f.snapshotPath, JSON.stringify(payload(oldAt)));
  const expired = await store.get();
  assert.equal(expired.source.status, "offline");
  assert.equal(expired.source.stale, true);
  assert.equal(expired.source.collectedAt, oldAt);
  assert.equal(expired.projects.length, 1);
});

test("rejects foreign hosts/origins, write methods, traversal and source/config paths", async t => {
  const f = await fixture(t);
  for (const headers of [{ Host: "evil.example" }, { Origin: "https://evil.example" }, { Origin: "null" }, { "Sec-Fetch-Site": "cross-site" }])
    assert.equal((await f.request("/api/snapshot", { headers })).status, 403);
  for (const method of ["POST", "PUT", "DELETE", "OPTIONS"])
    assert.equal((await f.request("/api/snapshot", { method })).status, 405);
  for (const url of ["/../private.txt", "/%2e%2e/private.txt", "/%2e%2e%5cprivate.txt", "//private.txt", "/C:/private.txt", "/.git/config"])
    assert.equal((await f.request(url)).status, 400, url);
  for (const url of ["/config.json", "/snapshot.json", "/server/live-server.mjs", "/src/App.jsx", "/app.js.map", "/api/missing"])
    assert.equal((await f.request(url)).status, 404, url);
  const ownOrigin = "http://127.0.0.1:" + f.app.server.address().port;
  assert.equal((await f.request("/health", { headers: { Origin: ownOrigin } })).status, 200);
});

test("concurrent requests and duplicate starts use one hidden collector; occupied ports start none", async t => {
  const f = await fixture(t);
  f.app.supervisor.start(); f.app.supervisor.start();
  await Promise.all(Array.from({ length: 15 }, () => f.request("/api/snapshot")));
  assert.equal(f.spawned.length, 1);
  assert.equal(f.spawned[0].options.windowsHide, true);
  assert.ok(f.spawned[0].args.includes("--parent-pid"));
  assert.equal(f.spawned[0].options.stdio, "ignore");
  let additionalSpawns = 0;
  const duplicate = createStudioServer({ ...f.config, port: f.app.server.address().port }, { spawnProcess: () => { additionalSpawns++; } });
  await assert.rejects(duplicate.listen(), { code: "EADDRINUSE" });
  assert.equal(additionalSpawns, 0);
  f.app.supervisor.stop(); f.app.supervisor.stop();
  assert.equal(f.spawned[0].child.kills, 1);
});

test("external config validates read-only server values and keeps snapshots outside static/runtime inputs", async t => {
  const f = await fixture(t);
  const configPath = path.join(f.directory, "studio.json");
  await f.put(configPath, "{broken");
  await assert.rejects(loadConfig(configPath), /missing or invalid/);
  const valid = { ...f.config, port: 4173 };
  await f.put(configPath, JSON.stringify(valid));
  assert.equal((await loadConfig(configPath)).port, 4173);
  for (const patch of [{ port: 0 }, { staleAfterSeconds: 5 }, { refreshSeconds: 0 }, { snapshotPath: f.config.cliWatchConfig },
    { snapshotPath: path.join(f.config.cliWatchRuntimeDirectory, "snapshot.json") }]) {
    await f.put(configPath, JSON.stringify({ ...valid, ...patch }));
    await assert.rejects(loadConfig(configPath));
  }
});
