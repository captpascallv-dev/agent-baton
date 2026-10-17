import { spawn } from 'node:child_process';
import readline from 'node:readline';
import fs from 'node:fs';
import path from 'node:path';

export function officialCacheRootForEnvironment(env) {
  if (env.CODEX_HOME?.trim()) return path.join(env.CODEX_HOME, 'plugins', 'cache', 'openai-bundled', 'codex-app-tools');
  if (env.USERPROFILE?.trim()) return path.join(env.USERPROFILE, '.codex', 'plugins', 'cache', 'openai-bundled', 'codex-app-tools');
  if (env.HOME?.trim()) return path.join(env.HOME, '.codex', 'plugins', 'cache', 'openai-bundled', 'codex-app-tools');
  throw new Error('CODEX_HOME, USERPROFILE or HOME is required for the current user official cache.');
}

export function resolveInstalledOfficialEntry({ appToolsEntry = null, cacheRoot = officialCacheRootForEnvironment(process.env) } = {}) {
  const root = fs.realpathSync(cacheRoot);
  const inside = filename => {
    const relative = path.relative(root, fs.realpathSync(filename));
    return relative && !relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative);
  };
  const candidates = [];
  for (const directory of fs.readdirSync(root, { withFileTypes: true })) {
    if (!directory.isDirectory() || !/^\d+\.\d+\.\d+$/.test(directory.name)) continue;
    const entry = path.join(root, directory.name, 'server.mjs');
    const manifestPath = path.join(root, directory.name, '.codex-plugin', 'plugin.json');
    try {
      if (!inside(entry) || !inside(manifestPath) || !fs.statSync(entry).isFile()) continue;
      const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
      if (manifest.name !== 'codex-app-tools' || manifest.version !== directory.name || manifest.author?.name !== 'OpenAI') continue;
      candidates.push({ entry, version: directory.name });
    } catch { /* Invalid/incomplete installed version is not a usable dependency. */ }
  }
  candidates.sort((a, b) => {
    const left = a.version.split('.').map(Number), right = b.version.split('.').map(Number);
    for (let i = 0; i < 3; i++) if (left[i] !== right[i]) return right[i] - left[i];
    return 0;
  });
  if (appToolsEntry) {
    const override = fs.realpathSync(path.resolve(appToolsEntry));
    const candidate = candidates.find(item => fs.realpathSync(item.entry) === override);
    if (!candidate) throw new Error('appToolsEntry must be a valid server.mjs under the fixed installed official codex-app-tools cache root.');
    return candidate;
  }
  if (!candidates.length) throw new Error('No valid installed official codex-app-tools manifest/server entry is available.');
  return candidates[0];
}

export class OfficialTools {
  constructor({ env = process.env, appToolsEntry = null, timeoutMs,
    sendTimeoutMs = timeoutMs === undefined ? 60000 : timeoutMs,
    entryResolver = () => resolveInstalledOfficialEntry({ appToolsEntry, cacheRoot: officialCacheRootForEnvironment(env) }) } = {}) {
    // Each NEW process receives its own environment. No pipe is read from state.
    this.env = { ...env };
    this.entryResolver = entryResolver;
    this.adapterStatus = { state: 'not_connected', version: null };
    this.timeoutMs = timeoutMs === undefined ? 20000 : timeoutMs;
    this.sendTimeoutMs = sendTimeoutMs;
    this.pipePresent = Boolean(this.env.CODEX_APP_TOOLS_PIPE_PATH?.trim());
    if (!this.pipePresent) this.adapterStatus.state = 'missing_launch_pipe';
    this.sequence = 0;
    this.pending = new Map();
    this.child = null;
    this.ready = null;
  }
  safeError(error) {
    const value = String(error); const pipe = this.env.CODEX_APP_TOOLS_PIPE_PATH;
    return pipe ? value.replaceAll(JSON.stringify(pipe).slice(1, -1), '[launch-pipe]').replaceAll(pipe, '[launch-pipe]') : value;
  }
  safeResult(value) {
    if (typeof value === 'string') return this.safeError(value);
    if (Array.isArray(value)) return value.map(item => this.safeResult(item));
    if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([name, item]) => [this.safeError(name), this.safeResult(item)]));
    return value;
  }
  rejectPending(error) {
    for (const operation of this.pending.values()) {
      clearTimeout(operation.timer);
      operation.reject(error);
    }
    this.pending.clear();
  }
  transportFailed(error) {
    this.adapterStatus = { ...this.adapterStatus, state: 'unavailable', reason: this.safeError(error) };
    this.rejectPending(error);
    // Optional receiver callback distinguishes a confirmed transport failure
    // from an ordinary native tool refusal. It never changes host approval.
    this.onTransportFailure?.(error);
  }
  async connect() {
    if (!this.pipePresent) throw new Error('No CODEX_APP_TOOLS_PIPE_PATH in this service launch; preserving pending state.');
    if (this.ready) return this.ready;
    let entry;
    try { entry = this.entryResolver(); }
    catch (error) { this.adapterStatus = { state: 'unavailable', reason: this.safeError(error), version: null }; throw error; }
    this.adapterStatus = { state: 'connecting', version: entry.version };
    this.child = spawn(process.execPath, [entry.entry], {
      env: this.env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true,
    });
    const child = this.child;
    readline.createInterface({ input: child.stdout }).on('line', line => {
      let response;
      try { response = JSON.parse(line); } catch { return; }
      const operation = this.pending.get(response.id);
      if (!operation) return;
      this.pending.delete(response.id);
      clearTimeout(operation.timer);
      if (response.error) operation.reject(new Error(this.safeError(`Official MCP rejection: ${JSON.stringify(response.error)}`)));
      else operation.resolve(response.result);
    });
    // Drain stderr; do not write host output or pipe details to protocol stdout.
    child.stderr.on('data', () => {});
    child.stdin.on('error', error => { if (this.child === child) this.transportFailed(error); });
    child.on('error', error => { if (this.child === child) this.transportFailed(error); });
    child.on('exit', code => {
      if (this.child === child) {
        this.transportFailed(new Error(`Official MCP process exited (${code}).`));
        this.child = null; this.ready = null;
      }
    });
    this.ready = (async () => {
      await this.request('initialize', { protocolVersion: '2024-11-05', capabilities: {},
        clientInfo: { name: 'codex-mail-wake', version: '0.4.1' } });
      child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' })}\n`);
      const catalog = await this.request('tools/list', {});
      for (const name of ['read_thread', 'send_message_to_thread']) {
        if (!catalog.tools?.some(tool => tool.name === name)) throw new Error(`Official MCP did not advertise ${name}.`);
      }
      this.adapterStatus = { state: 'connected', version: entry.version };
    })();
    try { await this.ready; }
    catch (error) { this.close(); this.adapterStatus = { state: 'unavailable', reason: this.safeError(error), version: entry.version }; throw error; }
  }
  request(method, params) {
    const id = ++this.sequence;
    const timeoutMs = method === 'tools/call' && params?.name === 'send_message_to_thread'
      ? this.sendTimeoutMs : this.timeoutMs;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        const error = new Error(`Official MCP timed out (${method}); a send is never automatically repeated.`);
        // A single deadline does not prove a process/pipe failure. In particular,
        // keep other submitted sends alive until their own original result.
        error.code = 'MCP_REQUEST_TIMEOUT';
        reject(error);
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      if (!this.child || this.child.stdin.destroyed) {
        clearTimeout(timer); this.pending.delete(id); reject(new Error('Official MCP input is closed.')); return;
      }
      this.child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`, error => {
        if (!error || !this.pending.has(id)) return;
        clearTimeout(timer); this.pending.delete(id); reject(error);
      });
    });
  }
  async call(name, args, ownerThreadId, authorized = null) {
    await this.connect();
    // connect/handshake may yield while another instance revokes authority.
    // A request already submitted is never cancelled or automatically resent.
    if (authorized && !authorized()) throw new Error('Receiver authorization ended before official request submission.');
    const result = await this.request('tools/call', { name, arguments: args,
      _meta: { 'x-codex-turn-metadata': { thread_id: ownerThreadId } } });
    // Retain native success/refusal and exact target fields, without saving pipe
    // strings that may appear in diagnostic text. Never use a new/guessed pipe.
    return this.safeResult(result);
  }
  async readThread(ownerThreadId, callerThreadId = ownerThreadId, authorized = null) {
    const result = await this.call('read_thread', { threadId: ownerThreadId, turnLimit: 1,
      includeOutputs: false, maxOutputCharsPerItem: 0 }, callerThreadId, authorized);
    if (result.isError && result.content?.some(item => item.type === 'text'
      && item.text.includes('thread/items/list is not supported yet'))) {
      // Some old local threads cannot enumerate history. Reception only needs
      // live status: use the official visible list, never local history files.
      const listed = await this.call('list_threads', { limit: 50 }, callerThreadId, authorized);
      if (listed.isError) throw new Error('Official live thread status is unavailable.');
      const decoded = decodeText(listed);
      const matches = [...(decoded?.pinnedThreads ?? []), ...(decoded?.threads ?? [])]
        .filter(thread => thread.id === ownerThreadId && thread.kind === 'codex' && thread.hostId === 'local');
      const statuses = new Set(['idle', 'notLoaded', 'active', 'systemError']);
      if (!matches.length || matches.some(thread => !statuses.has(thread.status) || thread.status !== matches[0].status)) {
        throw new Error('Exact local thread has no unambiguous official live status.');
      }
      if (authorized && !authorized()) throw new Error('Receiver authorization ended during status lookup.');
      return { thread: { id: ownerThreadId, kind: 'codex', hostId: 'local', status: { type: matches[0].status } }, turns: [] };
    }
    if (result.isError) throw new Error('Official read_thread rejected the read; native refusal retained.');
    const decoded = decodeText(result);
    if (decoded?.thread?.id !== ownerThreadId) throw new Error('Official read_thread returned a different owner identity.');
    return decoded;
  }
  send(ownerThreadId, prompt, callerThreadId = ownerThreadId, authorized = null) {
    // Deliberately no model, thinking, effort, service tier, host or turn override.
    return this.call('send_message_to_thread', { threadId: ownerThreadId, prompt }, callerThreadId, authorized);
  }
  close() {
    const child = this.child;
    this.child = null; this.ready = null;
    this.rejectPending(new Error('Official MCP client closed.'));
    if (child) { child.stdin.end(); child.kill(); }
  }
}

export function decodeText(result) {
  try { return JSON.parse(result.content.filter(item => item.type === 'text').map(item => item.text).join('\n')); }
  catch { return null; }
}

export function exactSendReceipt(result, ownerThreadId) {
  if (!result || result.isError) return false;
  let evidence = result.structuredContent && typeof result.structuredContent === 'object' ? result.structuredContent : null;
  for (const item of result.content ?? []) {
    if (item.type !== 'text') continue;
    try { const decoded = JSON.parse(item.text); if (decoded && typeof decoded === 'object' && decoded.threadId) evidence = decoded; } catch {}
  }
  return evidence?.threadId === ownerThreadId;
}
