// Temporary diagnostic entry: no receiver, ledger, official child or tool frames.
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';
import { callerIdentity } from './lib/mcp-service.mjs';

const toolName = 'diagnose_app_tools_pipe';
const errorCode = error => /^[A-Z][A-Z0-9_]{0,63}$/.test(error?.code ?? '') ? error.code : 'UNKNOWN';

export async function probeLaunchPipe(env = process.env) {
  // Match the official adapter's path trimming; never return the path itself.
  const pipe = env.CODEX_APP_TOOLS_PIPE_PATH?.trim() ?? '';
  const result = { pipePresent: Boolean(pipe), pathCharacters: [...pipe].length,
    pathBytes: Buffer.byteLength(pipe, 'utf8'), isSocket: null, statErrorCode: null,
    connect: { attempted: false, outcome: 'not_attempted', errorCode: null } };
  if (!pipe) return result;
  if (process.platform === 'win32') result.statErrorCode = 'NOT_APPLICABLE_WIN32';
  else {
    try { result.isSocket = fs.statSync(pipe).isSocket(); }
    catch (error) { result.statErrorCode = errorCode(error); }
  }
  result.connect = await new Promise(resolve => {
    let socket, settled = false, connected = false, observation = null;
    const finish = (outcome, code = null) => {
      if (settled) return;
      settled = true; clearTimeout(deadline); clearTimeout(observation);
      socket?.destroy();
      resolve({ attempted: true, outcome, errorCode: code });
    };
    const deadline = setTimeout(() => finish('connect_timeout', 'ETIMEDOUT'), 2000);
    try { socket = net.createConnection(pipe); }
    catch (error) { finish('connect_error', errorCode(error)); return; }
    socket.once('connect', () => {
      connected = true; clearTimeout(deadline);
      observation = setTimeout(() => finish('connected_no_close_observed'), 250);
    });
    socket.on('data', () => {}); // Discard any peer bytes; never write application data.
    socket.once('error', error => finish(connected ? 'error_after_connect' : 'connect_error', errorCode(error)));
    socket.once('end', () => finish(connected ? 'closed_after_connect' : 'closed_before_connect'));
    socket.once('close', () => finish(connected ? 'closed_after_connect' : 'closed_before_connect'));
  });
  return result;
}

export function diagnosticHandler(env = process.env) {
  let used = false;
  return async request => {
    if (!request || request.jsonrpc !== '2.0' || typeof request.method !== 'string') throw new Error('Invalid diagnostic request.');
    if (request.method === 'initialize') return { protocolVersion: '2024-11-05', capabilities: { tools: {} },
      serverInfo: { name: 'codex-mail-wake-diagnostic', version: '0.5.1' },
      instructions: 'Temporary connect-only diagnostic. No reception runs. Invoke the diagnostic once in the existing owner chat; never copy a pipe or supply identity overrides.' };
    if (request.method === 'tools/list') return { tools: [{ name: toolName,
      description: 'Once per process, inspect only this host launch pipe and attempt one connection without sending protocol data. Returns lengths, socket type and sanitized connection status. Does not start reception.',
      inputSchema: { type: 'object', properties: {}, additionalProperties: false },
      annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false } }] };
    if (request.method === 'ping') return {};
    if (request.method.startsWith('notifications/')) return null;
    if (request.method !== 'tools/call') throw new Error('Unknown diagnostic method.');
    const failure = text => ({ content: [{ type: 'text', text }], isError: true });
    const params = request.params ?? {}, args = params.arguments ?? {};
    if (params.name !== toolName) return failure('Only the launch-pipe diagnostic is available in this temporary entry.');
    if (!args || typeof args !== 'object' || Array.isArray(args) || Object.keys(args).length) return failure('This diagnostic accepts no arguments or overrides.');
    try { callerIdentity(params, env); }
    catch { return failure('Actual host caller identity is unavailable; no connection attempted.'); }
    if (used) return failure('Diagnostic already requested in this process; no second connection attempted.');
    used = true;
    try { return { content: [{ type: 'text', text: JSON.stringify(await probeLaunchPipe(env)) }], isError: false }; }
    catch { return failure('Diagnostic failed; details suppressed to protect the launch path. No automatic retry.'); }
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const handle = diagnosticHandler();
  readline.createInterface({ input: process.stdin, crlfDelay: Infinity }).on('line', line => {
    let request;
    try { if (Buffer.byteLength(line) > 1024 * 1024) throw new Error(); request = JSON.parse(line); }
    catch { process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Invalid diagnostic JSON.' } })}\n`); return; }
    void handle(request).then(result => {
      if (Object.hasOwn(request, 'id')) process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id: request.id, result })}\n`);
    }, () => {
      if (request && Object.hasOwn(request, 'id')) process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: -32600, message: 'Invalid diagnostic request.' } })}\n`);
    });
  });
}
