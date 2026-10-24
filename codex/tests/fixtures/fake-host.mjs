// Fake MCP host used only by the portable tests. It is not official server code.
import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
const { key } = await import(process.env.TEST_KEY_MODULE);
const respond = (id, result) => process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id, result })}\n`);
readline.createInterface({ input: process.stdin }).on('line', line => {
  const request = JSON.parse(line); if (!Object.hasOwn(request, 'id')) return;
  fs.appendFileSync(process.env.TEST_LOG, `${JSON.stringify({ method: request.method, params: request.params, pipe: process.env.CODEX_APP_TOOLS_PIPE_PATH })}\n`);
  if (request.method === 'initialize') return respond(request.id, { protocolVersion: '2024-11-05', capabilities: { tools: {} }, serverInfo: { name: 'test-host', version: '0.0.0' } });
  if (request.method === 'tools/list') return respond(request.id, { tools: [{ name: 'read_thread' }, { name: 'send_message_to_thread' }, { name: 'list_threads' }] });
  const control = JSON.parse(fs.readFileSync(process.env.TEST_CONTROL, 'utf8')); const params = request.params;
  if (params.name === 'read_thread') {
    if (control.readReject) return respond(request.id, { isError: true, content: [{ type: 'text', text: 'Test read refusal' }] });
    if (control.readLegacy) return respond(request.id, { isError: true, content: [{ type: 'text', text: 'thread/items/list is not supported yet' }] });
    const result = { isError: false, content: [{ type: 'text', text: JSON.stringify({ thread: {
      id: control.wrongActor ?? params.arguments.threadId, status: { type: control.ownerStatus ?? 'idle' },
    } }) }] };
    if (params.arguments.threadId === control.delayedReadThread) return setTimeout(() => respond(request.id, result), control.readDelayMs);
    return respond(request.id, result);
  }
  if (params.name === 'list_threads') {
    return respond(request.id, { isError: Boolean(control.listReject), content: [{ type: 'text', text: JSON.stringify({
      pinnedThreads: control.listedPinned ?? [], threads: control.listedThreads ?? [],
    }) }] });
  }
  if (params.name === 'send_message_to_thread') {
    const id = params.arguments.prompt.match(/^\[app-mailbox:([^\]]+)\]/)?.[1];
    const fault = params.arguments.prompt.match(/^\[app-mailbox-transport-fault:([^\]]+)\]/)?.[1];
    if (id === 'batch') {
      const members = [...params.arguments.prompt.matchAll(/^\[app-mailbox:([^\]]+)\]/gm)].map(match => match[1]).filter(value => value !== 'batch');
      if (members.length < 2) throw new Error('Batch must contain original notifications');
      const intents = members.map(member => JSON.parse(fs.readFileSync(path.join(process.env.TEST_STATE, 'app-mailbox', 'deliveries', key(member), 'intent.json'), 'utf8')));
      const manifest = JSON.parse(fs.readFileSync(path.join(process.env.TEST_STATE, 'app-mailbox', 'wake-batches', intents[0].batchId, 'manifest.json'), 'utf8'));
      if (JSON.stringify(manifest.members) !== JSON.stringify(members) || manifest.ownerThreadId !== params.arguments.threadId
        || intents.some((intent, index) => intent.batchId !== manifest.id || intent.deliveryId !== members[index]
          || intent.ownerThreadId !== manifest.ownerThreadId)) throw new Error('Batch send must follow all exact durable member intents');
    } else {
      const intentPath = id ? path.join(process.env.TEST_STATE, 'app-mailbox', 'deliveries', key(id), 'intent.json')
        : path.join(process.env.TEST_STATE, 'app-mailbox', 'faults', fault, 'intent.json');
      if (!fs.existsSync(intentPath)) throw new Error('Send happened before durable intent publication');
    }
    if (control.sendMode === 'timeout') return;
    const result = { isError: control.sendMode === 'reject', content: [{ type: 'text', text: JSON.stringify({ threadId: params.arguments.threadId }) }] };
    if (control.sendDelayMs) return setTimeout(() => respond(request.id, result), control.sendDelayMs);
    return respond(request.id, result);
  }
});
