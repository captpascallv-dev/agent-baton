// Read-only structure, syntax and accidental-machine-data check for this subtree.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { handleRequest, TOOLS } from '../plugin/lib/mcp-service.mjs';
const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const files = [];
function walk(directory) {
  for (const entry of fs.readdirSync(path.join(root, directory), { withFileTypes: true })) {
    const relative = path.join(directory, entry.name);
    if (entry.isDirectory()) walk(relative); else files.push(relative);
  }
}
walk('');
const read = filename => fs.readFileSync(path.join(root, filename), 'utf8');
assert.equal(JSON.parse(read('plugin/package.json')).version, '0.5.0');
assert.equal(JSON.parse(read('plugin/.codex-plugin/plugin.json')).version, '0.5.0');
assert.equal((await handleRequest({ jsonrpc: '2.0', method: 'initialize' }, null)).serverInfo.version, '0.5.0');
assert.equal(TOOLS.length, 6);
assert.deepEqual(TOOLS.map(tool => tool.name), ['start_app_mailbox_watch', 'app_mailbox_status', 'stop_app_mailbox_watch', 'ack_app_mailbox_delivery', 'grant_app_mailbox_delegation', 'revoke_app_mailbox_delegation']);
const mcp = JSON.parse(read('plugin/.mcp.json')).mcpServers.codex_mail_wake;
assert.deepEqual(mcp.args, ['./server.mjs']); assert.equal(mcp.cwd, '.');
assert.ok(mcp.env_vars.includes('MAIL_WAKE_CONFIG'));
for (const name of ['start_app_mailbox_watch', 'stop_app_mailbox_watch', 'grant_app_mailbox_delegation', 'revoke_app_mailbox_delegation']) assert.equal(mcp.tools[name].approval_mode, 'prompt');
assert.deepEqual(TOOLS[3].inputSchema.required, ['deliveryIds']);
assert.equal(TOOLS[3].inputSchema.properties.deliveryIds.maxItems, 32);
for (const tool of TOOLS.slice(4)) assert.deepEqual(tool.inputSchema, { type: 'object', additionalProperties: false, properties: {} });
let nodeChecks = 0;
for (const filename of files) {
  const text = read(filename);
  assert.equal(/[A-Za-z]:[\\/]Users[\\/]/i.test(text), false, `${filename}: machine user path`);
  assert.equal(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i.test(text), false, `${filename}: literal session ID`);
  if (filename.endsWith('.mjs')) {
    const result = spawnSync(process.execPath, ['--check', path.join(root, filename)], { windowsHide: true, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr); nodeChecks++;
  }
  if (filename.endsWith('.json')) JSON.parse(text);
}
const python = [process.env.MAIL_WAKE_TEST_PYTHON, 'python3', 'python'].filter(Boolean).find(command =>
  spawnSync(command, ['-c', 'import sys; sys.exit(0 if sys.version_info >= (3,10) else 1)'], { windowsHide: true }).status === 0);
assert.ok(python, 'Python 3.10+ required');
const sources = files.filter(filename => filename.endsWith('.py')).map(filename => read(filename));
const result = spawnSync(python, ['-B', '-X', 'utf8', '-c', 'import ast, json, sys; [ast.parse(text) for text in json.load(sys.stdin)]; print("Python AST OK")'], {
  input: JSON.stringify(sources), windowsHide: true, encoding: 'utf8',
});
assert.equal(result.status, 0, result.stderr);
const pythonVersion = spawnSync(python, ['--version'], { windowsHide: true, encoding: 'utf8' }).stdout.trim();
console.log(JSON.stringify({ version: '0.5.0', node: process.version, python: pythonVersion, nodeSyntaxChecks: nodeChecks, pythonASTChecks: sources.length,
  sixReceptionTools: true, noLiteralUserPathsOrSessionIDs: true, explicitConfig: true, runtimeDependenciesBundled: true }, null, 2));
