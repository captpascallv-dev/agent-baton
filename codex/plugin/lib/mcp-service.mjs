import readline from 'node:readline';

const schema = { type: 'object', additionalProperties: false, properties: { project: { type: 'string', minLength: 1, maxLength: 40 } } };
const noArguments = { type: 'object', additionalProperties: false, properties: {} };
export const GRANT_DELEGATION_TOOL = 'grant_app_mailbox_delegation';
export const REVOKE_DELEGATION_TOOL = 'revoke_app_mailbox_delegation';
export const TOOLS = [
  { name: 'start_app_mailbox_watch', description: 'Enable explicitly authorized continuous reception of a registered owned project. Only the configured coordinator may omit project to enable all active routes.', inputSchema: schema,
    annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: true } },
  { name: 'app_mailbox_status', description: 'Read owned reception and transport problems. A sent receipt proves notification acceptance, not actual Lead continuation.', inputSchema: schema,
    annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false } },
  { name: 'stop_app_mailbox_watch', description: 'Stop an owned project without deleting evidence. Only the configured coordinator may omit project to stop all routes.', inputSchema: schema,
    annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: false } },
  { name: 'ack_app_mailbox_delivery', description: 'The exact original delivery owner, still the current route owner, records notifications already read/handled. Suppress only pending notifications without a committed send. Never records business acceptance or rewrites transport uncertainty; no coordinator override.',
    inputSchema: { type: 'object', additionalProperties: false, required: ['deliveryIds'], properties: {
      deliveryIds: { type: 'array', minItems: 1, maxItems: 32, uniqueItems: true, items: { type: 'string', minLength: 1, maxLength: 221 } },
    } }, annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: false } },
  { name: GRANT_DELEGATION_TOOL, description: 'Configured coordinator only, identified by this call metadata/executor environment: explicitly grant revocable background reception after service reload. Requires enabled all-watch; binds coordinator, roots and current epoch. Never enables watch. Accepts no arguments.', inputSchema: noArguments,
    annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: false } },
  { name: REVOKE_DELEGATION_TOOL, description: 'Configured coordinator only: revoke background delegation. Stop new delegated claims/sends, retain submitted sends and receipts. Startup/status never restore the grant. Accepts no arguments.', inputSchema: noArguments,
    annotations: { readOnlyHint: false, destructiveHint: false, openWorldHint: false } },
];
export function callerIdentity(params, env = process.env) {
  let metadata = params?._meta?.['x-codex-turn-metadata'];
  if (metadata !== undefined) {
    if (typeof metadata === 'string') metadata = JSON.parse(metadata);
    if (!metadata || typeof metadata.thread_id !== 'string' || !metadata.thread_id.trim()) throw new Error('Actual caller metadata requires thread_id.');
    return { owner: metadata.thread_id, source: 'turn_metadata' };
  }
  if (env.CODEX_THREAD_ID?.trim()) return { owner: env.CODEX_THREAD_ID, source: 'executor_environment' };
  throw new Error('Actual caller identity is unavailable. No target override is accepted.');
}
export async function handleRequest(message, engine, env = process.env) {
  if (!message || message.jsonrpc !== '2.0') throw new Error('Invalid JSON-RPC request.');
  if (message.method === 'initialize') return { protocolVersion: '2024-11-05', capabilities: { tools: {} }, serverInfo: { name: 'codex-mail-wake', version: '0.5.0' },
    instructions: 'Start only after explicit continuous-reception authorization. Receipts do not prove actual Lead continuation. Existing intents are never automatically resent.' };
  if (message.method === 'tools/list') return { tools: TOOLS };
  if (message.method === 'ping') return {};
  if (message.method.startsWith('notifications/')) return null;
  if (message.method !== 'tools/call') throw new Error('Unknown MCP method.');
  try {
    const params = message.params ?? {}; const args = params.arguments ?? {};
    if (!args || typeof args !== 'object' || Array.isArray(args)) throw new Error('Arguments must be an object.');
    const delegationTool = [GRANT_DELEGATION_TOOL, REVOKE_DELEGATION_TOOL].includes(params.name);
    const acknowledgment = params.name === 'ack_app_mailbox_delivery';
    for (const name of Object.keys(args)) if (delegationTool || name !== (acknowledgment ? 'deliveryIds' : 'project')) throw new Error(`Unsupported argument: ${name}.`);
    const identity = callerIdentity(params, env); engine.bindActualCaller(identity.owner, identity.source);
    let value;
    if (params.name === 'start_app_mailbox_watch') value = engine.startWatch(identity.owner, args);
    else if (params.name === 'stop_app_mailbox_watch') value = engine.stopWatch(identity.owner, args);
    else if (params.name === 'app_mailbox_status') value = engine.getStatus(identity.owner, args.project ?? null);
    else if (acknowledgment) value = await engine.acknowledgeDeliveries(identity.owner, args, identity.source);
    else if (params.name === GRANT_DELEGATION_TOOL) value = await engine.grantDelegation(identity.owner, identity.source);
    else if (params.name === REVOKE_DELEGATION_TOOL) value = await engine.revokeDelegation(identity.owner, identity.source);
    else throw new Error('Unknown reception tool.');
    return { content: [{ type: 'text', text: JSON.stringify(value) }], isError: false };
  } catch (error) { return { content: [{ type: 'text', text: String(error) }], isError: true }; }
}
export function serveStdio(engine, { input = process.stdin, output = process.stdout, env = process.env } = {}) {
  const lines = readline.createInterface({ input, crlfDelay: Infinity });
  lines.on('line', line => {
    let request;
    try { if (Buffer.byteLength(line) > 1024 * 1024) throw new Error(); request = JSON.parse(line); }
    catch { output.write(`${JSON.stringify({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Invalid JSON request.' } })}\n`); return; }
    void handleRequest(request, engine, env).then(result => {
      if (Object.hasOwn(request, 'id')) output.write(`${JSON.stringify({ jsonrpc: '2.0', id: request.id, result })}\n`);
    }, error => {
      if (Object.hasOwn(request, 'id')) output.write(`${JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: -32601, message: String(error) } })}\n`);
    });
  });
  lines.on('close', () => engine.close()); engine.start(); return lines;
}
