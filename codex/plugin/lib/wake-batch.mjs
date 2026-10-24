import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { createJsonOnce, readJson } from './store.mjs';
import { exactSendReceipt } from './official-tools.mjs';

// One host send, exact per-item identities. A durable batch receipt closes the
// crash window before its per-item fanout; recovery only finishes bookkeeping.
export class WakeBatchJournal {
  constructor(root) { this.root = root; }
  allocate() { return randomUUID(); }
  path(id, filename) {
    if (typeof id !== 'string' || !/^[a-f0-9-]{36}$/.test(id)) throw new Error('Invalid wake batch ID.');
    return path.join(this.root, id, filename);
  }
  publish(id, ownerThreadId, members, atUtc) {
    if (!members.length || new Set(members).size !== members.length) throw new Error('Batch requires unique members.');
    if (!createJsonOnce(this.path(id, 'manifest.json'), { schemaVersion: 1, id, ownerThreadId, members, atUtc })) {
      throw new Error('Batch manifest already exists; never resend.');
    }
  }
  saveReceipt(id, receipt) { createJsonOnce(this.path(id, 'receipt.json'), receipt); }
  receipt(intent, itemId, ownerThreadId) {
    if (typeof intent?.batchId !== 'string' || !/^[a-f0-9-]{36}$/.test(intent.batchId)
      || intent.deliveryId !== itemId || intent.ownerThreadId !== ownerThreadId) return null;
    const manifest = readJson(this.path(intent.batchId, 'manifest.json'));
    if (!manifest || manifest._unreadable || manifest.schemaVersion !== 1 || manifest.id !== intent.batchId
      || manifest.ownerThreadId !== ownerThreadId || !Array.isArray(manifest.members) || !manifest.members.includes(itemId)) return null;
    const receipt = readJson(this.path(intent.batchId, 'receipt.json'));
    return exactSendReceipt(receipt, ownerThreadId) ? receipt : null;
  }
}

export const MAX_WAKE_PROMPT_BYTES = 256 * 1024;
const prefix = (count, label) => `${label} (${count} items). Each notification retains its original authorization and handling boundaries.\n\n`;

export function selectPromptBatch(items, getPrompt, label) {
  const selected = []; let contentBytes = 0;
  for (const item of items) {
    const prompt = getPrompt(item);
    if (typeof prompt !== 'string') throw new Error('Original prompt is unavailable for bounded batch selection.');
    const nextBytes = Buffer.byteLength(prompt, 'utf8'); const count = selected.length + 1;
    const total = count === 1 ? nextBytes : contentBytes + nextBytes + 2 * (count - 1) + Buffer.byteLength(prefix(count, label), 'utf8');
    if (selected.length && total > MAX_WAKE_PROMPT_BYTES) break;
    selected.push(item); contentBytes += nextBytes;
    // Preserve the original oversized single-item contract, without adding claims.
    if (total > MAX_WAKE_PROMPT_BYTES) break;
  }
  return selected;
}

export function combinedPrompt(prompts, label) {
  if (prompts.length === 1) return prompts[0];
  return `${prefix(prompts.length, label)}${prompts.join('\n\n')}`;
}
