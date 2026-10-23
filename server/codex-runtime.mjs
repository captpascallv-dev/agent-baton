import { execFile } from "node:child_process";
import { homedir } from "node:os";
import path from "node:path";
import { CodexIpcObserver } from "./codex-ipc.mjs";

const HOST_CACHE_MS = 10_000;
const MAX_AGE_MS = 30_000;
const MAX_CANDIDATES = 128;
const UUID = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
let hostCache;
let hostPending;

// Process/window/pipe ownership metadata only: no captions, arguments, app
// settings, credentials, history, or transcript files are read by this helper.
const HOST_SCRIPT = String.raw`
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class StudioCodexMetadata {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr value);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr value);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
  public static uint[] WindowPids() {
    var result = new List<uint>();
    EnumWindows((hwnd, value) => {
      if (IsWindowVisible(hwnd) && GetWindow(hwnd, 4) == IntPtr.Zero && (GetWindowLong(hwnd, -20) & 0x80) == 0) {
        uint pid; GetWindowThreadProcessId(hwnd, out pid); result.Add(pid);
      }
      return true;
    }, IntPtr.Zero);
    return result.ToArray();
  }
}
'@
$apps = @(Get-CimInstance Win32_Process -Filter "Name='ChatGPT.exe' OR Name='Codex.exe'" | Where-Object {
  $_.ExecutablePath -match '\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(ChatGPT|Codex)\.exe$'
})
$appIds = @($apps | ForEach-Object { [int]$_.ProcessId })
$roots = @($apps | Where-Object { [int]$_.ParentProcessId -notin $appIds })
$hosts = @($roots | ForEach-Object {
  @{ pid=[int]$_.ProcessId; startedAt=$_.CreationDate.ToUniversalTime().ToString('o') }
})
$windows = @([StudioCodexMetadata]::WindowPids() | Where-Object { [int]$_ -in $appIds }).Count
$endpoints = @()
if ($hosts.Count -gt 0) {
  $client = $null
  try {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'codex-ipc', [System.IO.Pipes.PipeDirection]::InOut)
    $client.Connect(150)
    [uint32]$serverPid = 0
    if ([StudioCodexMetadata]::GetNamedPipeServerProcessId($client.SafePipeHandle, [ref]$serverPid)) {
      $owner = @($hosts | Where-Object { $_.pid -eq [int]$serverPid })
      if ($owner.Count -eq 1) {
        $endpoints += @{ path='\\.\pipe\codex-ipc'; hostPid=[int]$serverPid; hostStartedAt=$owner[0].startedAt }
      }
    }
  } catch {} finally { if ($null -ne $client) { $client.Dispose() } }
}
@{ observedAt=[DateTime]::UtcNow.ToString('o'); openWindows=$windows; hosts=$hosts; endpoints=$endpoints } | ConvertTo-Json -Depth 5 -Compress
`;

function fresh(value, now) {
  const time = typeof value === "string" ? Date.parse(value) : NaN;
  return Number.isFinite(time) && now - time >= -5_000 && now - time <= MAX_AGE_MS;
}
function count(value) { return Number.isInteger(value) && value >= 0 ? value : null; }
function validHost(host) {
  return Number.isInteger(host?.pid) && host.pid > 0 && Number.isFinite(Date.parse(host.startedAt));
}
function belongsToHost(endpoint, hosts) {
  return endpoint?.path === "\\\\.\\pipe\\codex-ipc" && hosts.some(host => host.pid === endpoint.hostPid
    && Date.parse(host.startedAt) === Date.parse(endpoint.hostStartedAt));
}
function deadline(operation, ms) {
  let timer;
  return Promise.race([Promise.resolve().then(operation), new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error("metadata_timeout")), ms);
  })]).finally(() => clearTimeout(timer));
}

async function discoverHost(now) {
  if (process.platform !== "win32") throw new Error("unsupported_platform");
  if (hostCache && now - Date.parse(hostCache.observedAt) < HOST_CACHE_MS) return hostCache;
  if (hostPending) return hostPending;
  hostPending = new Promise((resolve, reject) => {
    execFile("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", HOST_SCRIPT],
      { windowsHide: true, timeout: 4_000, maxBuffer: 64 * 1024, encoding: "utf8" }, (error, stdout) => {
        if (error) { reject(new Error("host_metadata_unavailable")); return; }
        try { hostCache = JSON.parse(stdout.replace(/^\uFEFF/, "")); resolve(hostCache); }
        catch { reject(new Error("host_metadata_invalid")); }
      });
  }).finally(() => { hostPending = null; });
  return hostPending;
}

async function readCatalog({ now, codexHome }) {
  const { DatabaseSync } = await import("node:sqlite");
  const filename = path.join(codexHome ?? path.join(homedir(), ".codex"), "sqlite", "codex-dev.db");
  const db = new DatabaseSync(filename, { readOnly: true });
  try {
    // The App metadata catalog supplies opaque IDs, never activity or models.
    // No title, preview, cwd, payload, rollout or global-state field is selected.
    db.exec("BEGIN");
    const host = db.prepare("SELECT host_kind FROM local_thread_catalog_hosts WHERE host_id = ?").get("local");
    const sync = db.prepare("SELECT initial_build_complete FROM local_thread_catalog_sync_state WHERE host_id = ?").get("local");
    const rows = db.prepare("SELECT thread_id FROM local_thread_catalog WHERE host_id = ? LIMIT ?").all("local", MAX_CANDIDATES + 1);
    db.exec("ROLLBACK");
    const ids = rows.slice(0, MAX_CANDIDATES).map(row => row.thread_id).filter(id => UUID.test(id ?? ""));
    return { observedAt: new Date(now).toISOString(), ids,
      complete: host?.host_kind === "local" && sync?.initial_build_complete === 1
        && rows.length <= MAX_CANDIDATES && ids.length === rows.length };
  } finally { db.close(); }
}

function sessionState(thread) {
  const status = typeof thread.status === "object" ? thread.status?.type : thread.status;
  if (status === "idle") return "idle";
  if (status !== "active") return "unknown";
  const flags = typeof thread.status === "object" ? thread.status.activeFlags : thread.activeFlags;
  if (!Array.isArray(flags)) return "unknown";
  if (flags.some(flag => flag === "waitingOnUserInput" || flag === "waitingOnApproval")) return "idle";
  return flags.length === 0 ? "running" : "unknown";
}
function activeModel(thread, state, now) {
  if (state !== "running" || thread.modelSource !== "activeTurn" || !fresh(thread.modelObservedAt, now)) return null;
  const model = thread.model;
  return typeof model === "string" && /^[a-z0-9][a-z0-9._:/-]{0,95}$/i.test(model)
    && !/^(sk-|sess-|eyJ|bearer)/i.test(model) ? model : null;
}

/** Independent read-only collector; no executor/thread context is used.
 * readHost/readCatalog/readMetadata/createConnection are injectable for fixtures.
 * Runtime metadata must match the desktop host PID plus creation time. */
async function collectRuntime(options, context) {
  const now = (options.now ?? Date.now)();
  const result = { agentId: "codex", observedAt: new Date(now).toISOString(), sourceStatus: "unavailable",
    openWindows: null, openSessions: null, activeSessions: null, idleSessions: null, unknownSessions: null,
    instances: [], coverage: "暂时无法读取 Codex App 的本机状态。" };
  let host;
  try { host = await deadline(() => (options.readHost ?? discoverHost)(now), 4_500); }
  catch { return result; }
  if (context.disposed) return result;
  if (!fresh(host?.observedAt, now) || !Array.isArray(host.hosts) || !host.hosts.every(validHost)
    || !Array.isArray(host.endpoints) || count(host.openWindows) === null) return result;
  result.openWindows = host.openWindows;
  result.sourceStatus = "partial";
  if (host.hosts.length === 0) {
    if (host.openWindows !== 0) return result;
    return { ...result, sourceStatus: "live", openSessions: 0, activeSessions: 0, idleSessions: 0, unknownSessions: 0,
      coverage: "Codex App 当前未打开。" };
  }
  result.coverage = "已读取 Codex App 窗口；会话运行状态暂时不可读。";
  const endpoints = host.endpoints.filter(endpoint => belongsToHost(endpoint, host.hosts));
  const endpointKeys = new Set(endpoints.map(endpoint => `${endpoint.hostPid}:${endpoint.hostStartedAt}`));
  for (const [key, observer] of context.observers) if (!endpointKeys.has(key)) { observer.dispose(); context.observers.delete(key); }
  if (!endpoints.length) return result;
  let catalog;
  if (!options.readMetadata) {
    try { catalog = await deadline(() => (options.readCatalog ?? readCatalog)({ now, codexHome: options.codexHome }), 1_000); }
    catch { return result; }
    if (!fresh(catalog?.observedAt, now) || !Array.isArray(catalog.ids)
      || catalog.ids.length > MAX_CANDIDATES || !catalog.ids.every(id => UUID.test(id ?? ""))) return result;
  }
  if (context.disposed) return result;
  const signals = await Promise.all(endpoints.slice(0, 4).map(async endpoint => {
    try {
      const signal = await deadline(async () => {
        if (options.readMetadata) return options.readMetadata(endpoint, { now });
        const key = `${endpoint.hostPid}:${endpoint.hostStartedAt}`;
        let observer = context.observers.get(key);
        if (!observer) {
          observer = new CodexIpcObserver(endpoint, { now: options.now ?? Date.now, createConnection: options.createConnection });
          context.observers.set(key, observer);
        }
        const observed = await observer.collect(catalog.ids, { timeoutMs: options.timeoutMs ?? 10_000 });
        return { ...observed, observedAt: new Date((options.now ?? Date.now)()).toISOString(), hostPid: endpoint.hostPid,
          hostStartedAt: endpoint.hostStartedAt, complete: observed.complete && catalog.complete === true };
      }, options.readMetadata ? 2_000 : 10_500);
      return signal && fresh(signal.observedAt, (options.now ?? Date.now)()) && signal.hostPid === endpoint.hostPid
        && Date.parse(signal.hostStartedAt) === Date.parse(endpoint.hostStartedAt) && Array.isArray(signal.threads) ? signal : null;
    } catch { return null; }
  }));
  const observedNow = (options.now ?? Date.now)();
  result.observedAt = new Date(observedNow).toISOString();
  if (context.disposed) { result.sourceStatus = "unavailable"; result.coverage = "Codex 状态采集已停止。"; return result; }
  const byId = new Map();
  let invalid = false;
  for (const signal of signals) {
    if (!signal) continue;
    for (const thread of signal.threads) {
      if (!UUID.test(thread?.id ?? "")) { invalid = true; continue; }
      const status = typeof thread.status === "object" ? thread.status?.type : thread.status;
      if (status === "notLoaded") continue;
      const state = sessionState(thread);
      const instance = { id: `codex:${thread.id}`, state, model: activeModel(thread, state, observedNow), kind: "conversation" };
      const previous = byId.get(instance.id);
      if (previous && previous.state !== state) { instance.state = "unknown"; instance.model = null; }
      byId.set(instance.id, instance);
    }
  }
  result.instances = [...byId.values()];
  const complete = !invalid && endpoints.length === host.endpoints.length && endpoints.length <= 4
    && new Set(endpoints.map(endpoint => endpoint.hostPid)).size === host.hosts.length
    && signals.every(signal => signal?.complete === true);
  if (!complete) {
    const phase = signals.find(signal => signal?.phase && signal.phase !== "ready")?.phase;
    const reasons = { loading: "正在读取会话初始状态，尚未收齐。", disconnected: "本机会话状态连接已断开。",
      revision_gap: "会话增量版本不连续，等待重新取得状态。", frame_limit: "会话状态超过读取上限，已暂停重复读取以减少 Codex 开销；会话运行状态暂时未知。",
      owner_unconfirmed: "会话连接未重新确认，旧运行状态已撤下。",
      invalid_metadata: "当前会话状态格式暂时无法解析。" };
    result.coverage = reasons[phase] ?? "已读取部分 Codex App 状态；会话来源可能不完整，数量暂时未知。";
    return result;
  }
  result.openSessions = result.instances.length;
  result.idleSessions = result.instances.filter(instance => instance.state === "idle").length;
  result.unknownSessions = result.instances.filter(instance => instance.state === "unknown").length;
  result.activeSessions = result.unknownSessions ? null : result.instances.filter(instance => instance.state === "running").length;
  const missingModel = result.instances.some(instance => instance.state === "running" && instance.model === null);
  result.sourceStatus = result.unknownSessions || missingModel ? "partial" : "live";
  result.coverage = result.unknownSessions
    ? "已读取本机已加载会话；部分会话的执行或等待状态暂时未知。"
    : missingModel ? "已读取本机会话运行状态；部分正在运行的会话尚无当前模型信息。"
      : "已读取 Codex App 的本机窗口和已加载会话状态。";
  return result;
}

export function createCodexRuntimeCollector(options = {}) {
  const context = { observers: new Map(), disposed: false };
  let idleTimer;
  const release = () => { for (const observer of context.observers.values()) observer.dispose(); context.observers.clear(); };
  return {
    collect() {
      if (context.disposed) return Promise.resolve({ agentId: "codex", observedAt: new Date().toISOString(), sourceStatus: "unavailable",
        openWindows: null, openSessions: null, activeSessions: null, idleSessions: null, unknownSessions: null, instances: [], coverage: "Codex 状态采集已停止。" });
      clearTimeout(idleTimer); idleTimer = setTimeout(release, options.idleMs ?? 20_000); idleTimer.unref?.();
      return collectRuntime(options, context);
    },
    diagnostics() { return { sources: [...context.observers.values()].map(observer => observer.diagnostics()) }; },
    dispose() { context.disposed = true; clearTimeout(idleTimer); release(); },
  };
}
let defaultCollector;
export function collectCodexRuntime(options = {}) {
  const fixtureKeys = ["readHost", "readCatalog", "readMetadata", "createConnection", "now", "timeoutMs", "idleMs", "codexHome"];
  if (fixtureKeys.some(key => Object.hasOwn(options, key))) {
    const collector = createCodexRuntimeCollector(options);
    return collector.collect().finally(() => collector.dispose());
  }
  if (options.signal?.aborted) return Promise.resolve({ agentId: "codex", observedAt: new Date().toISOString(), sourceStatus: "unavailable",
    openWindows: null, openSessions: null, activeSessions: null, idleSessions: null, unknownSessions: null, instances: [], coverage: "Codex 状态采集已停止。" });
  defaultCollector ??= createCodexRuntimeCollector();
  return defaultCollector.collect();
}
export function disposeCodexRuntime() { defaultCollector?.dispose(); defaultCollector = null; }
export function getCodexRuntimeDiagnostics() { return defaultCollector?.diagnostics() ?? { sources: [] }; }
