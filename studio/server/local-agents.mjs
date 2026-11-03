import { execFile } from 'node:child_process';
import { createHmac, randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { collectClaudeRuntime } from './claude-runtime.mjs';
import { collectDshRuntime } from './dsh-runtime.mjs';

const run = promisify(execFile);
const salt = randomBytes(32);
const agentIds = ['dsh', 'grok-bot', 'claude', 'grok', 'cursor', 'pi', 'opencode'];
const helperPath = fileURLToPath(new URL('./local-agent-process.ps1', import.meta.url));
const opaqueId = value => createHmac('sha256', salt).update(String(value)).digest('hex').slice(0, 20);

export async function readProcessSnapshot() {
  if (process.platform !== 'win32') return { available: false };
  try {
    const { stdout } = await run('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', helperPath],
      { windowsHide: true, timeout: 12000, maxBuffer: 512 * 1024, encoding: 'utf8' });
    const value = JSON.parse(stdout.replace(/^\uFEFF/, '').trim());
    return value?.available === true && Array.isArray(value.processes) ? value : { available: false };
  } catch { return { available: false }; }
}

function hasAncestor(process, processes, predicate) {
  const seen = new Set([process.pid]);
  let parent = processes.get(process.parentPid);
  while (parent && !seen.has(parent.pid)) {
    if (predicate(parent)) return true;
    seen.add(parent.pid);
    parent = processes.get(parent.parentPid);
  }
  return false;
}

export function projectLocalAgents(snapshot, { observedAt = new Date().toISOString() } = {}) {
  const available = snapshot?.available === true && Array.isArray(snapshot.processes);
  const processes = available ? snapshot.processes.filter(row => Number.isInteger(row.pid) && agentIds.includes(row.agentId)) : [];
  const parents = Array.isArray(snapshot?.parents) ? snapshot.parents.filter(row => Number.isInteger(row.pid)) : [];
  const byPid = new Map([...parents, ...processes].map(row => [row.pid, row]));
  return agentIds.map(agentId => {
    const rows = processes.filter(row => row.agentId === agentId);
    const handles = new Set(rows.flatMap(row => Array.isArray(row.windows) ? row.windows.filter(handle => typeof handle === 'string') : []));
    const instances = [...handles].map(handle => ({ id: opaqueId(`${agentId}:window:${handle}`), state: 'unknown', model: null, kind: 'window' }));
    const standaloneCli = rows.filter(row => row.type === 'cli' && (!Array.isArray(row.windows) || row.windows.length === 0)
      && !hasAncestor(row, byPid, parent => parent.agentId === agentId));
    for (const row of standaloneCli) instances.push({ id: opaqueId(`${agentId}:cli:${row.pid}:${row.startedAt ?? ''}`), state: 'unknown', model: null, kind: 'conversation' });
    let coverage = '本机窗口已核对；暂时看不到里面有多少对话正在工作，也读不到当下模型。';
    if (!available) coverage = '本机进程与原生窗口来源暂时不可用，数量与运行状态未知。';
    else if (agentId === 'dsh') {
      coverage = '能看到 DSH 窗口是否开着；暂时看不到里面的对话是否正在工作，以及当下使用的模型。';
    } else if (agentId === 'grok-bot') coverage = '能看到 Grok Bot 窗口是否开着；里面各个 Bot 是否正在工作、使用哪个模型，暂时看不到。';
    return { agentId, observedAt, sourceStatus: available ? 'partial' : 'unavailable',
      openWindows: available && snapshot.windowsAvailable === true ? handles.size : null,
      openSessions: null, activeSessions: null, idleSessions: null, unknownSessions: null, instances, coverage };
  });
}

// One bounded collection per caller refresh; no timers, watches or background loop.
export async function collectLocalAgents(options = {}) {
  const snapshot = await (options.readProcessSnapshot ?? readProcessSnapshot)();
  const now = options.now?.() ?? new Date();
  const rows = projectLocalAgents(snapshot, { observedAt: now.toISOString() });
  const runtime = await collectDshRuntime(options.dshStatusDirectory, snapshot, { now: options.now ? now.getTime() : undefined });
  if (runtime) {
    const dsh = rows.find(row => row.agentId === 'dsh');
    const coveredCliIds = new Set(snapshot.processes.filter(row => row.agentId === 'dsh' && runtime.coveredProcessIds.has(row.pid))
      .map(row => opaqueId(`dsh:cli:${row.pid}:${row.startedAt ?? ''}`)));
    dsh.instances = [...dsh.instances.filter(instance => !coveredCliIds.has(instance.id)), ...runtime.instances.map(instance => ({
      id: opaqueId(`dsh:runtime:${instance.id}`), state: instance.state, model: instance.model, kind: 'conversation',
    }))];
    dsh.sourceStatus = 'partial';
    dsh.coverage = `已读到部分 DSH 对话状态（${runtime.runtimeCount} 个运行环境、${runtime.instances.length} 个会话）；其他运行环境是否完整可见仍未知，暂不报全机对话总数。`;
  }
  const hooks = await collectClaudeRuntime(options.claudeStatusDirectory, snapshot, { now: options.now ? now.getTime() : undefined });
  if (hooks) {
    const claude = rows.find(row => row.agentId === 'claude');
    const coveredCliIds = new Set(snapshot.processes.filter(row => row.agentId === 'claude' && hooks.coveredProcessIds.has(row.pid))
      .map(row => opaqueId(`claude:cli:${row.pid}:${row.startedAt ?? ''}`)));
    claude.instances = [...claude.instances.filter(instance => !coveredCliIds.has(instance.id)), ...hooks.instances.map(instance => ({
      id: opaqueId(`claude:hook:${instance.id}`), state: instance.state, model: instance.model, kind: 'conversation',
    }))];
    claude.sourceStatus = 'partial';
    const tally = state => hooks.instances.filter(instance => instance.state === state).length;
    if (hooks.complete) {
      Object.assign(claude, { openSessions: hooks.sessionCount, activeSessions: tally('running'), idleSessions: tally('idle'), unknownSessions: tally('unknown') });
      claude.coverage = `已通过 Claude Code 钩子读到全部 ${hooks.sessionCount} 个会话的运行/休眠状态；桌面应用里的普通聊天是否在生成仍看不到。`;
    } else {
      claude.coverage = `已通过 Claude Code 钩子读到 ${hooks.instances.length} 个会话；另有 ${hooks.sessionCount - hooks.instances.length} 个会话还没报过状态（下次收到消息或跑工具时接上），暂不报总数。`;
    }
  }
  return rows;
}
