import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { diagnosticHandler, probeLaunchPipe } from '../plugin/diagnostic-server.mjs';

const address = () => process.platform === 'win32'
  ? `\\\\.\\pipe\\baton-diagnostic-${randomUUID()}-测试`
  : path.join(os.tmpdir(), `baton-${randomUUID()}.sock`);
const toolCall = (args = {}) => ({ jsonrpc: '2.0', method: 'tools/call', params: {
  name: 'diagnose_app_tools_pipe', arguments: args,
  _meta: { 'x-codex-turn-metadata': { thread_id: 'diagnostic-test-owner' } },
} });
const decode = result => JSON.parse(result.content[0].text);
async function fixture(t, closePeer = false) {
  const endpoint = address(); let connections = 0, bytes = 0;
  const sockets = new Set();
  const server = net.createServer(socket => {
    connections++; sockets.add(socket); socket.on('data', data => { bytes += data.length; });
    socket.on('error', () => {}); socket.on('close', () => sockets.delete(socket));
    if (closePeer) socket.end();
  });
  server.listen(endpoint); await once(server, 'listening');
  t.after(async () => { for (const socket of sockets) socket.destroy(); await new Promise(resolve => server.close(resolve)); });
  return { endpoint, connections: () => connections, bytes: () => bytes };
}

test('diagnostic reports an absent pipe without a connect attempt', async () => {
  assert.deepEqual(await probeLaunchPipe({}), { pipePresent: false, pathCharacters: 0, pathBytes: 0,
    isSocket: null, statErrorCode: null, connect: { attempted: false, outcome: 'not_attempted', errorCode: null } });
});

test('diagnostic reports a missing endpoint with only lengths and error codes', async () => {
  const endpoint = address(), result = await probeLaunchPipe({ CODEX_APP_TOOLS_PIPE_PATH: endpoint });
  assert.equal(result.pipePresent, true); assert.equal(result.pathBytes, Buffer.byteLength(endpoint));
  assert.equal(result.pathCharacters, [...endpoint].length);
  assert.equal(result.connect.attempted, true); assert.equal(result.connect.outcome, 'connect_error');
  assert.equal(result.connect.errorCode, 'ENOENT'); assert.equal(JSON.stringify(result).includes(endpoint), false);
  assert.equal(result.statErrorCode, process.platform === 'win32' ? 'NOT_APPLICABLE_WIN32' : 'ENOENT');
});

test('diagnostic rejects overrides and missing identity, then connects once with zero protocol bytes', async t => {
  const f = await fixture(t), handle = diagnosticHandler({ CODEX_APP_TOOLS_PIPE_PATH: f.endpoint });
  assert.equal((await handle(toolCall({ pipe: 'override' }))).isError, true);
  const noCaller = toolCall(); delete noCaller.params._meta;
  assert.equal((await handle(noCaller)).isError, true); assert.equal(f.connections(), 0);
  const first = handle(toolCall()), concurrent = await handle(toolCall());
  assert.equal(concurrent.isError, true);
  const result = await first; assert.equal(result.isError, false);
  assert.equal(decode(result).connect.outcome, 'connected_no_close_observed');
  assert.equal(decode(result).isSocket, process.platform === 'win32' ? null : true);
  assert.equal((await handle(toolCall())).isError, true);
  assert.equal(f.connections(), 1); assert.equal(f.bytes(), 0);
  assert.equal(JSON.stringify(result).includes(f.endpoint), false);
  assert.deepEqual(Object.keys(decode(result)).sort(), ['connect', 'isSocket', 'pathBytes', 'pathCharacters', 'pipePresent', 'statErrorCode']);
});

test('diagnostic distinguishes remote close after connect without writing a frame', async t => {
  const f = await fixture(t, true), result = await probeLaunchPipe({ CODEX_APP_TOOLS_PIPE_PATH: f.endpoint });
  assert.equal(result.connect.outcome, 'closed_after_connect'); assert.equal(result.connect.errorCode, null);
  assert.equal(f.connections(), 1); assert.equal(f.bytes(), 0);
});

test('temporary stdio entry exposes only diagnostics and never offers reception', async t => {
  const f = await fixture(t), entry = fileURLToPath(new URL('../plugin/diagnostic-server.mjs', import.meta.url));
  const child = spawn(process.execPath, [entry], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
    env: { ...process.env, CODEX_APP_TOOLS_PIPE_PATH: f.endpoint, CODEX_THREAD_ID: 'diagnostic-test-owner' } });
  let sequence = 0, stderr = ''; const pending = new Map();
  child.stderr.on('data', data => { stderr += data; });
  readline.createInterface({ input: child.stdout }).on('line', line => {
    const message = JSON.parse(line), operation = pending.get(message.id);
    if (operation) { pending.delete(message.id); clearTimeout(operation.timer); operation.resolve(message.result); }
  });
  t.after(async () => { const exited = once(child, 'close'); child.stdin.end(); child.kill(); await exited; });
  const call = request => new Promise((resolve, reject) => {
    const id = ++sequence, timer = setTimeout(() => reject(new Error('Diagnostic stdio test timed out.')), 5000);
    pending.set(id, { resolve, timer }); child.stdin.write(`${JSON.stringify({ ...request, id })}\n`);
  });
  const init = await call({ jsonrpc: '2.0', method: 'initialize' });
  assert.equal(init.serverInfo.name, 'codex-mail-wake-diagnostic');
  const catalog = await call({ jsonrpc: '2.0', method: 'tools/list' });
  assert.deepEqual(catalog.tools.map(tool => tool.name), ['diagnose_app_tools_pipe']);
  const forbidden = toolCall(); forbidden.params.name = 'start_app_mailbox_watch';
  assert.equal((await call(forbidden)).isError, true); assert.equal(f.connections(), 0);
  const result = await call(toolCall()); assert.equal(result.isError, false);
  assert.equal(decode(result).connect.outcome, 'connected_no_close_observed');
  assert.equal((await call(toolCall())).isError, true);
  assert.equal(f.connections(), 1); assert.equal(f.bytes(), 0); assert.equal(stderr, '');
});
