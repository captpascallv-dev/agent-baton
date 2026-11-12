import http from "node:http";
import { readFile, realpath, stat, lstat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { spawn } from "node:child_process";
import { AgentRuntimeStore, codexCliCandidates, applyCodexCliRuntime } from "./agent-runtime-store.mjs";

export const APP_ID = "ai-moe-studio";
const rootDirectory = fileURLToPath(new URL("../", import.meta.url));
const axisNames = ["process", "turn", "execution", "transport", "delivery", "callback", "leadHandling", "acceptance", "adoption", "goal"];
const statuses = new Set(["starting", "live", "degraded", "offline"]);
const qualities = new Set(["Fresh", "LastKnown", "Unavailable", "Conflicting", "Partial"]);
const mime = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml", ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".webp": "image/webp",
  ".ico": "image/x-icon", ".woff": "font/woff", ".woff2": "font/woff2" };

function text(value, fallback = "") { return typeof value === "string" ? value.slice(0, 1200) : fallback; }
function nullableText(value) { return typeof value === "string" ? text(value) : null; }
function date(value) { return typeof value === "string" && Number.isFinite(Date.parse(value)) ? value : null; }
function within(candidate, root) { const rel = path.relative(root, candidate); return !rel.startsWith("..") && !path.isAbsolute(rel); }
function requiredPath(value, key, base) {
  if (typeof value !== "string" || !value.trim()) throw new Error("Missing config field: " + key);
  return path.resolve(base, value);
}

export async function loadConfig(filename) {
  let raw;
  try { raw = JSON.parse(await readFile(filename, "utf8")); }
  catch { throw new Error("Studio config is missing or invalid."); }
  if (!raw || Array.isArray(raw) || typeof raw !== "object") throw new Error("Studio config must be an object.");
  const base = path.dirname(path.resolve(filename));
  const config = {
    port: raw.port ?? 4173,
    cliWatchRuntimeDirectory: requiredPath(raw.cliWatchRuntimeDirectory, "cliWatchRuntimeDirectory", base),
    cliWatchConfig: requiredPath(raw.cliWatchConfig, "cliWatchConfig", base),
    bridgeExecutable: requiredPath(raw.bridgeExecutable, "bridgeExecutable", base),
    snapshotPath: requiredPath(raw.snapshotPath, "snapshotPath", base),
    refreshSeconds: raw.refreshSeconds ?? 5,
    staleAfterSeconds: raw.staleAfterSeconds ?? 60,
  };
  if (raw.allowedRoot != null) config.allowedRoot = requiredPath(raw.allowedRoot, "allowedRoot", base);
  if (raw.allowedPids != null) {
    if (!Array.isArray(raw.allowedPids) || raw.allowedPids.some(pid => !Number.isSafeInteger(pid) || pid < 1))
      throw new Error("allowedPids must contain positive process IDs.");
    config.allowedPids = raw.allowedPids;
  }
  if (raw.codexCliStateRoot != null || raw.codexCliHome != null) {
    config.codexCliStateRoot = requiredPath(raw.codexCliStateRoot, "codexCliStateRoot", base);
    config.codexCliHome = requiredPath(raw.codexCliHome, "codexCliHome", base);
  }
  if (!Number.isInteger(config.port) || config.port < 1 || config.port > 65535
    || !Number.isInteger(config.refreshSeconds) || config.refreshSeconds < 1 || config.refreshSeconds > 3600
    || !Number.isInteger(config.staleAfterSeconds) || config.staleAfterSeconds < 60)
    throw new Error("Studio port/refreshSeconds/staleAfterSeconds is invalid.");
  if (within(config.snapshotPath, path.join(rootDirectory, "dist/client"))
    || within(config.snapshotPath, config.cliWatchRuntimeDirectory)
    || config.snapshotPath === config.cliWatchConfig || config.snapshotPath === path.resolve(filename))
    throw new Error("snapshotPath must be independent of static files and CLI Watch inputs.");
  return config;
}

// Mac launch is fixture-scoped. No auto-discovered home or App/session inputs.
export async function validateMacConfig(config, filename) {
  if (!config.allowedRoot || !path.isAbsolute(config.allowedRoot)) throw new Error("Mac requires an explicit allowedRoot.");
  const root = await realpath(config.allowedRoot);
  if (root !== path.resolve(config.allowedRoot) || root === path.parse(root).root)
    throw new Error("allowedRoot must be a canonical task fixture directory.");
  async function checked(candidate) {
    const full = path.resolve(candidate);
    if (!within(full, root) || /(?:^|[/\\])\.codex[/\\]sessions(?:[/\\]|$)/i.test(full))
      throw new Error("Mac fixture boundary rejected.");
    let current = root;
    for (const part of path.relative(root, full).split(path.sep).filter(Boolean)) {
      current = path.join(current, part);
      try { if ((await lstat(current)).isSymbolicLink()) throw new Error("Mac fixture symlink rejected."); }
      catch (error) { if (error.code !== "ENOENT") throw error; }
    }
  }
  await checked(filename); await checked(config.cliWatchConfig); await checked(config.snapshotPath);
  if (config.codexCliStateRoot || config.codexCliHome) throw new Error("Private runtime overlays are disabled on Mac.");
  return config;
}

// A second whitelist prevents unexpected disk fields from exposing raw evidence,
// paths, transcripts or configuration through the public API.
export function publicSnapshot(raw) {
  if (raw?.schemaVersion !== 1 || raw.mode !== "live" || !raw.source || !Array.isArray(raw.projects) || !Array.isArray(raw.issues)
    || !statuses.has(raw.source.status) || !qualities.has(raw.source.quality))
    throw new Error("invalid_snapshot");
  const collectedAt = date(raw.source.collectedAt), publishedAt = date(raw.source.publishedAt);
  if ((!collectedAt || !publishedAt) && raw.projects.length) throw new Error("invalid_snapshot");
  if (["live", "degraded"].includes(raw.source.status) && (!collectedAt || !publishedAt)) throw new Error("invalid_snapshot");
  return {
    schemaVersion: 1, mode: "live",
    source: { name: "CLI Watch", collectedAt, publishedAt, quality: raw.source.quality, stale: raw.source.stale === true,
      status: raw.source.status, error: nullableText(raw.source.error), coverage: "本机 CLI 执行状态" },
    projects: raw.projects.map(project => {
      if (typeof project?.id !== "string" || !Array.isArray(project.works)) throw new Error("invalid_snapshot");
      return { id: project.id, name: text(project.name), projectLabel: text(project.projectLabel), summary: text(project.summary),
        phase: text(project.phase), statusLabel: text(project.statusLabel), statusBasis: text(project.statusBasis),
        goal: nullableText(project.goal), isCurrent: project.isCurrent === true, quality: text(project.quality, "Unavailable"),
        lastFactAt: date(project.lastFactAt), observedActivityAt: date(project.observedActivityAt),
        works: project.works.map(work => {
          if (typeof work?.id !== "string" || !work.axes || !Array.isArray(work.artifacts)) throw new Error("invalid_snapshot");
          return { id: work.id, actorName: nullableText(work.actorName), actorKind: nullableText(work.actorKind),
            role: text(work.role), route: nullableText(work.route), taskName: nullableText(work.taskName), summary: text(work.summary),
            model: nullableText(work.model), effort: nullableText(work.effort), blocker: nullableText(work.blocker),
            quality: text(work.quality, "Unavailable"), isHistorical: work.isHistorical === true,
            axes: Object.fromEntries(axisNames.map(axis => [axis, text(work.axes[axis], "unknown")])),
            artifacts: work.artifacts.map(artifact => ({ label: text(artifact?.label), state: text(artifact?.state, "unknown") })) };
        }) };
    }),
    issues: raw.issues.map(issue => ({ code: text(issue?.code), detail: text(issue?.detail),
      scope: issue?.scope === "history" ? "history" : "current" })),
  };
}

function emptySnapshot() {
  return { schemaVersion: 1, mode: "live", source: { name: "CLI Watch", collectedAt: null, publishedAt: null,
    quality: "Unavailable", stale: true, status: "starting", error: null, coverage: "本机 CLI 执行状态" },
  projects: [], issues: [] };
}

export class SnapshotStore {
  constructor(snapshotPath, staleAfterSeconds = 60, now = Date.now) {
    this.snapshotPath = snapshotPath; this.staleAfterSeconds = staleAfterSeconds; this.now = now;
    this.last = null; this.bridgeFailure = null;
    this.startedAt = now();
  }
  async get() {
    let readError = null;
    try { this.last = publicSnapshot(JSON.parse(await readFile(this.snapshotPath, "utf8"))); }
    catch (error) { if (error.code !== "ENOENT" || this.last) readError = "状态快照暂时不可读，保留上次真实状态。"; }
    const result = structuredClone(this.last ?? emptySnapshot());
    const age = result.source.collectedAt ? this.now() - Date.parse(result.source.collectedAt) : Infinity;
    const expired = result.source.collectedAt ? age > this.staleAfterSeconds * 1000 : this.now() - this.startedAt > this.staleAfterSeconds * 1000;
    const error = this.bridgeFailure ?? readError ?? result.source.error;
    if (error || expired) {
      result.source.stale = true;
      result.source.status = !result.source.collectedAt || expired ? "offline" : "degraded";
      result.source.quality = result.source.collectedAt ? "LastKnown" : "Unavailable";
      result.source.error = error ?? "CLI Watch 状态已过期，等待新的真实采集。";
    }
    return result;
  }
}

export class BridgeSupervisor {
  constructor(config, store, spawnProcess = spawn) {
    this.config = config; this.store = store; this.spawnProcess = spawnProcess;
    this.child = null; this.started = false; this.stopping = false;
  }
  start() {
    if (this.started || this.stopping) return;
    this.started = true;
    try {
      this.child = this.spawnProcess(this.config.bridgeExecutable, [
        "--config", this.config.cliWatchConfig, "--snapshot", this.config.snapshotPath,
        "--refresh-seconds", String(this.config.refreshSeconds), "--parent-pid", String(process.pid),
        ...(this.config.allowedRoot ? ["--allowed-root", this.config.allowedRoot,
          ...(this.config.allowedPids ?? []).flatMap(pid => ["--allowed-pid", String(pid)])] : []),
      ], { cwd: path.dirname(this.config.bridgeExecutable), windowsHide: true, stdio: "ignore" });
      this.child.once("error", () => { this.store.bridgeFailure = "本机状态桥接启动失败，请检查桥接程序。"; });
      this.child.once("exit", () => {
        if (!this.stopping) this.store.bridgeFailure = "本机状态桥接已停止，保留上次真实状态。";
        this.child = null;
      });
    } catch { this.store.bridgeFailure = "本机状态桥接启动失败，请检查桥接程序。"; }
  }
  stop() {
    if (this.stopping) return;
    this.stopping = true;
    // Only this child handle; never enumerate or stop CLI Watch or its observed CLIs.
    if (this.child) this.child.kill();
  }
}

function send(req, res, status, body, type = "text/plain; charset=utf-8") {
  const content = Buffer.from(body);
  res.writeHead(status, { "Content-Type": type, "Content-Length": content.length, "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer" });
  res.end(req.method === "HEAD" ? undefined : content);
}
function sendJson(req, res, status, body) { send(req, res, status, JSON.stringify(body), "application/json; charset=utf-8"); }
function allowedRequest(req, port) {
  const allowedHosts = new Set(["127.0.0.1:" + port, "localhost:" + port, ...(port === 80 ? ["127.0.0.1", "localhost"] : [])]);
  if (!allowedHosts.has(req.headers.host)) return false;
  if (req.headers.origin && req.headers.origin !== "http://" + req.headers.host) return false;
  if (req.headers["sec-fetch-site"] === "cross-site") return false;
  return true;
}
function requestPath(raw) {
  if (!raw?.startsWith("/") || raw.startsWith("//")) return null;
  try {
    const decoded = decodeURIComponent(raw.split("?")[0]);
    if (/[\0\\:]/.test(decoded) || decoded.split("/").some(segment => segment.startsWith("."))) return null;
    return decoded;
  } catch { return null; }
}

export function createStudioServer(config, options = {}) {
  const demo = options.mode === "demo";
  const clientDirectory = options.clientDirectory ?? path.join(rootDirectory, "dist/client");
  const store = demo ? null : options.store ?? new SnapshotStore(config.snapshotPath, config.staleAfterSeconds);
  const supervisor = demo ? null : new BridgeSupervisor(config, store, options.spawnProcess);
  // Mac observes only the explicit mailbox bridge. Never infer private App or
  // session-store coverage from the host user's home directory.
  const runtime = demo || options.runtime === false || (process.platform === "darwin" && options.runtime == null)
    ? null : options.runtime ?? new AgentRuntimeStore({
    collectOptions: { dshStatusDirectory: path.join(path.dirname(config.snapshotPath), 'dsh-status'),
      claudeStatusDirectory: path.join(path.dirname(config.snapshotPath), 'claude-status'),
      codexCliStateRoot: config.codexCliStateRoot, codexCliHome: config.codexCliHome,
      readExternalCandidates: async () => codexCliCandidates(await store.get()) },
  });
  const server = http.createServer(async (req, res) => {
    const port = server.address()?.port;
    if (!allowedRequest(req, port)) return send(req, res, 403, "Forbidden");
    if (!["GET", "HEAD"].includes(req.method)) return send(req, res, 405, "Read-only server");
    const pathname = requestPath(req.url);
    if (pathname === null) return send(req, res, 400, "Invalid path");
    try {
      if (pathname === "/health") return sendJson(req, res, 200, { app: APP_ID, schemaVersion: 1, mode: demo ? "demo" : "live" });
      if (pathname === "/api/snapshot") {
        if (demo) return sendJson(req, res, 200, options.demoSnapshot);
        const snapshot = await store.get(), agentRuntime = runtime?.get();
        return sendJson(req, res, 200, { ...applyCodexCliRuntime(snapshot, agentRuntime), ...(agentRuntime ? { agentRuntime } : {}) });
      }
      if (pathname.startsWith("/api/")) return send(req, res, 404, "Not found");
      const root = await realpath(clientDirectory);
      let filename = path.resolve(root, "." + pathname);
      if (!within(filename, root)) return send(req, res, 403, "Forbidden");
      let info;
      try { info = await stat(filename); } catch { /* May be a client route. */ }
      if (!info?.isFile()) {
        if (pathname === "/" || (!path.extname(pathname) && req.headers.accept?.includes("text/html")))
          filename = path.join(root, "index.html");
        else return send(req, res, 404, "Not found");
      }
      const extension = path.extname(filename).toLowerCase();
      if (!mime[extension] || !within(await realpath(filename), root)) return send(req, res, 404, "Not found");
      send(req, res, 200, await readFile(filename), mime[extension]);
    } catch { send(req, res, 503, "Local content temporarily unavailable"); }
  });
  server.on("close", () => { supervisor?.stop(); runtime?.stop(); });
  return {
    server, store, supervisor, runtime,
    async listen() {
      await new Promise((resolve, reject) => {
        const onError = error => { server.off("listening", onListen); reject(error); };
        const onListen = () => { server.off("error", onError); resolve(); };
        server.once("error", onError); server.once("listening", onListen);
        server.listen(config.port, "127.0.0.1");
      });
      // Binding first prevents duplicate/failed servers from creating a collector child.
      if (!demo) {
        supervisor.start();
        runtime?.start();
      }
      return server.address();
    },
    async close() {
      supervisor?.stop();
      await runtime?.stop();
      await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
    },
  };
}

function demoPort(argv) {
  if (argv.length === 3 && argv[2] === "--demo") return 4173;
  if (argv.length === 5 && argv[2] === "--demo" && argv[3] === "--port") {
    const port = Number(argv[4]);
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Demo port is invalid.");
    return port;
  }
  return null;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    const port = demoPort(process.argv);
    const demo = port !== null;
    if (!demo && (process.argv.length !== 4 || process.argv[2] !== "--config"))
      throw new Error("Usage: node server/live-server.mjs --config <external JSON> | --demo [--port N]");
    const demoSnapshot = demo ? JSON.parse(await readFile(path.join(rootDirectory, "demo", "snapshot.json"), "utf8")) : null;
    if (demo && (demoSnapshot?.schemaVersion !== 1 || demoSnapshot.mode !== "demo" || !Array.isArray(demoSnapshot.projects) || !demoSnapshot.agentRuntime))
      throw new Error("Demo snapshot is invalid.");
    const liveConfig = demo ? null : await loadConfig(process.argv[3]);
    if (!demo && process.platform === "darwin") await validateMacConfig(liveConfig, process.argv[3]);
    const app = demo
      ? createStudioServer({ port }, { mode: "demo", demoSnapshot })
      : createStudioServer(liveConfig);
    const address = await app.listen();
    console.log((demo ? "AI Moe Studio demo: http://127.0.0.1:" : "AI Moe Studio live: http://127.0.0.1:") + address.port);
    let stopping = false;
    for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, async () => {
      if (stopping) return; stopping = true;
      await app.close(); process.exit(0);
    });
    process.on("exit", () => app.supervisor?.stop());
  } catch (error) {
    console.error(error.code === "EADDRINUSE" ? "Studio port is already in use." : text(error.message, "Studio startup failed."));
    process.exitCode = 1;
  }
}
