import test from 'node:test';
import assert from 'node:assert/strict';
import { AGENTS, agentFor, characterFor, flattenSnapshot, currentSummary, selectRows, countsFor, sourceState, axisLabel, parseSnapshot, runtimeFor, externalFor, snapshotRevision, sceneSummary, sceneActivity, sceneCounts, newDeliveryAgents } from '../src/live-data.mjs';

const work = (id, extra = {}) => ({ id, actorName: 'Codex', taskName: '测试任务', model: 'gpt-6.1-sol（已观察）', quality: 'Fresh', axes: { process: 'alive', execution: 'running', delivery: 'unknown', acceptance: 'unknown' }, ...extra });
const project = (id, phase, works, extra = {}) => ({ id, name: `测试项目 / ${id}`, phase, isCurrent: true, quality: 'Fresh', works, ...extra });
const snapshot = (projects = [], runtime = null) => ({ schemaVersion: 1, mode: 'live', source: { publishedAt: '2026-10-22T15:00:00Z', status: 'live', stale: false }, projects, ...(runtime ? { agentRuntime: runtime } : {}) });
const runtimeAgent = (extra = {}) => ({ agentId: 'codex', sourceStatus: 'live', observedAt: '2026-10-22T15:00:00Z', openWindows: 1, openSessions: 3, activeSessions: 1, idleSessions: 2, unknownSessions: 0, instances: [], ...extra });

test('八个 Agent 独立归属，actor 身份优先；模型不参与身份推断', () => {
  assert.equal(AGENTS.length, 8);
  assert.equal(new Set(AGENTS.map(agent => agent.id)).size, 8);
  assert.equal(agentFor(work('pi', { actorName: 'PI', model: 'gpt-6.1-sol' })).id, 'pi');
  assert.equal(agentFor(work('cursor', { actorName: 'Cursor', model: 'grok-4.7-xhigh' })).id, 'cursor');
  assert.equal(agentFor(work('kind', { actorName: 'CLI', actorKind: 'OpenCode', model: 'claude-opus' })).id, 'opencode');
  assert.equal(agentFor(work('claude', { actorName: 'Claude', model: 'unknown-vendor-model' })).id, 'claude');
  assert.equal(agentFor(work('unknown', { actorName: 'CLI', actorKind: 'executor', model: 'gpt-6.1-sol' })).id, null);
  assert.equal(characterFor(work('dsh', { actorName: 'DSH', model: 'anything' })).id, 'deepseek');
});

test('GrokBuild 与 Grok Bot 分开，Bot kind 可细分通用 Grok 名称', () => {
  assert.equal(agentFor(work('grok', { actorName: 'GrokBuild' })).id, 'grok');
  assert.equal(agentFor(work('bot', { actorName: 'Grok Bot' })).id, 'grok-bot');
  assert.equal(agentFor(work('kind', { actorName: 'Grok', actorKind: 'grokbot' })).id, 'grok-bot');
  assert.equal(agentFor(work('kind2', { actorName: null, actorKind: 'grok_bot' })).id, 'grok-bot');
});

test('本机模型只取明确 running 的实例，休眠与未知实例的模型不显示', () => {
  const frame = snapshot([], { agents: [runtimeAgent({ instances: [
    { id: 'opaque-running', state: 'running', kind: 'conversation', model: 'gpt-6.1-sol' },
    { id: 'opaque-idle', state: 'idle', kind: 'conversation', model: 'idle-secret-model' },
    { id: 'opaque-unknown', state: 'unknown', kind: 'window', model: 'unknown-secret-model' },
    { id: 'opaque-running2', state: 'running', kind: 'conversation', model: null },
  ] })] });
  assert.deepEqual(runtimeFor(frame, 'codex').models, ['gpt-6.1-sol', '模型未知']);
  assert.equal('instances' in runtimeFor(frame, 'codex'), false);
  assert.deepEqual(runtimeFor(snapshot([], { agents: [runtimeAgent({ activeSessions: 0, instances: [{ id: 'opaque', state: 'idle', model: 'gpt-6.1-sol' }] })] }), 'codex').models, []);
  assert.deepEqual(runtimeFor(snapshot([], { agents: [runtimeAgent({ activeSessions: 1 })] }), 'codex').models, ['模型未知']);
});

test('未知数量保留 null，窗口数不能冒充会话数，缺来源不伪造空闲', () => {
  const result = runtimeFor(snapshot([], { agents: [runtimeAgent({ openWindows: 4, openSessions: null, activeSessions: null, idleSessions: null, unknownSessions: null })] }), 'codex');
  assert.equal(result.openWindows, 4);
  assert.equal(result.openSessions, null);
  assert.equal(result.activeSessions, null);
  const missing = runtimeFor(snapshot(), 'dsh');
  assert.equal(missing.openWindows, null);
  assert.equal(missing.openSessions, null);
  assert.equal(missing.activeSessions, null);
  assert.deepEqual(missing.models, []);
  const unavailable = runtimeFor(snapshot([], { agents: [runtimeAgent({ sourceStatus: 'unavailable', activeSessions: 1, instances: [{ state: 'running', model: 'old-model' }] })] }), 'codex');
  assert.equal(unavailable.activeSessions, null);
  assert.deepEqual(unavailable.models, []);
});

test('本机运行发布时间能独立推动刷新，不依赖 CLI Watch 帧变化', () => {
  const first = snapshot([], { observedAt: '2026-10-22T15:00:00Z', agents: [] });
  const next = { ...first, agentRuntime: { ...first.agentRuntime, observedAt: '2026-10-22T15:00:03Z' } };
  assert.notEqual(snapshotRevision(first), snapshotRevision(next));
  assert.equal(snapshotRevision(first), snapshotRevision(structuredClone(first)));
  const agentOnly = snapshot([], { agents: [runtimeAgent()] });
  const changed = snapshot([], { agents: [runtimeAgent({ observedAt: '2026-10-22T15:00:06Z' })] });
  assert.notEqual(snapshotRevision(agentOnly), snapshotRevision(changed));
});

test('零窗口简明显示未打开，但不能遮住后台会话、终端实例或失联', () => {
  const closed = { openWindows: 0, openSessions: null, activeSessions: null, idleSessions: null, unknownSessions: null, instances: [] };
  const read = patch => runtimeFor(snapshot([], { agents: [runtimeAgent({ ...closed, ...patch })] }), 'codex');
  assert.equal(read({}).windowClosed, true);
  assert.equal(read({ openWindows: 1 }).windowClosed, false);
  assert.equal(read({ openSessions: 2, activeSessions: 1, idleSessions: 1 }).windowClosed, false);
  assert.equal(read({ instances: [{ state: 'unknown', kind: 'conversation', model: null }] }).windowClosed, false);
  assert.equal(read({ sourceStatus: 'unavailable' }).windowClosed, false);
});

test('外部任务排除历史与未关联进程，不和本机窗口重复计数', () => {
  const frame = snapshot([
    project('one', 'running_unknown', [work('one')]),
    project('two', 'waiting', [work('two')]),
    project('old', 'running_active', [work('old')], { isCurrent: false }),
    project('unbound', 'unbound', ['a', 'b', 'c', 'd'].map(id => work(id))),
  ], { agents: [runtimeAgent({ openWindows: 1 })] });
  const rows = flattenSnapshot(frame), external = externalFor(rows, 'codex');
  assert.equal(external.count, 2);
  assert.deepEqual(external.tasks.map(row => row.task), ['测试任务', '测试任务']);
  assert.equal(external.activeCount, null);
  assert.equal(runtimeFor(frame, 'codex').openWindows, 1);
  assert.deepEqual(currentSummary(rows), { tasks: 2, unbound: 4, history: 1 });
  assert.equal(selectRows(rows).length, 2);
  assert.equal(selectRows(rows, { history: true }).length, 1);
});

test('CLI Watch 活动不证明生成，声明模型不用作当前模型；显式运行字段才有效', () => {
  const rows = flattenSnapshot(snapshot([
    project('activity', 'running_active', [work('activity')]),
    project('quiet', 'running_quiet', [work('quiet', { actorName: 'PI', model: 'claude-opus' })]),
    project('actual', 'running_active', [work('actual', { actorName: 'Cursor', model: 'declared-model', runtimeState: 'running', runtimeModel: 'observed-model' })]),
    project('idle', 'waiting', [work('idle', { actorName: 'Cursor', runtimeState: 'idle', runtimeModel: 'must-not-show' })]),
  ]));
  const codex = externalFor(rows, 'codex');
  assert.equal(codex.activeCount, null);
  assert.equal(codex.hasActivity, true);
  assert.deepEqual(codex.models, []);
  assert.equal(rows.find(row => row.agentId === 'codex').label, '有新活动');
  assert.equal(externalFor(rows, 'pi').activeCount, null);
  const cursor = externalFor(rows, 'cursor');
  assert.equal(cursor.activeCount, 1);
  assert.deepEqual(cursor.models, ['observed-model']);
  assert.equal(rows.find(row => row.runtimeState === 'idle').runtimeModel, null);
});

test('历史交卷与负责人检查分列，结束记录不做停摆警报', () => {
  const rows = flattenSnapshot(snapshot([
    project('ended', 'delivered', [work('a', { axes: { process: 'dead', execution: 'returned', delivery: 'complete', acceptance: 'pending' } })], { isCurrent: false }),
    project('gone', 'process_gone', [work('b')]),
    project('failed', 'failed', [work('c')]),
  ]));
  assert.equal(selectRows(rows, { history: true, filter: 'review' }).length, 1);
  assert.equal(axisLabel('delivery', rows[0].axes.delivery), '还没看到');
  const delivered = rows.find(row => row.phase === 'delivered');
  assert.equal(axisLabel('delivery', delivered.axes.delivery), '已交卷');
  assert.equal(axisLabel('acceptance', delivered.axes.acceptance), '等负责人检查');
  assert.equal(axisLabel('acceptance', 'unknown'), '看负责人消息');
  assert.ok(rows.filter(row => ['failed', 'process_gone'].includes(row.phase)).every(row => row.tone === 'idle'));
  assert.deepEqual(countsFor(rows), { running: 0, queued: 0, review: 1, ended: 2 });
});

test('LastKnown 与历史不进入当前；项目与记录筛选继续生效', () => {
  const rows = flattenSnapshot(snapshot([
    project('first', 'running_active', [work('live'), work('old', { isHistorical: true }), work('last', { quality: 'LastKnown' })]),
    project('second', 'delivered', [work('ready')], { name: '另一个项目 / second' }),
  ]));
  assert.equal(selectRows(rows).length, 2);
  assert.equal(selectRows(rows, { history: true }).length, 2);
  assert.equal(selectRows(rows, { project: '测试项目', filter: 'review' }).length, 0);
  assert.equal(selectRows(rows, { project: '另一个项目', filter: 'review' }).length, 1);
});

test('来源失联保留旧帧，首次读取不伪造空闲，格式继续检查', () => {
  assert.equal(sourceState(null, null).status, 'starting');
  const frame = snapshot();
  assert.equal(sourceState(frame, 'fetch failed').stale, true);
  assert.match(sourceState(frame, 'fetch failed').description, /上次/);
  assert.equal(sourceState({ ...frame, source: { status: 'live', stale: true } }, null).label, '记录待更新');
  assert.equal(sourceState({ ...frame, source: { status: 'degraded', stale: false } }, null).stale, false);
  assert.throws(() => parseSnapshot({ projects: [] }), /格式/);
  assert.throws(() => parseSnapshot({ ...frame, projects: [{}] }), /不完整/);
});

test('名称去掉派单通道，项目归并继续有效；声明模型只留数据不做当前状态', () => {
  const rows = flattenSnapshot(snapshot([
    project('a', 'running_active', [work('a', { actorName: '值班发起 · Codex', effort: 'xhigh（已观察）' })], { name: '值班发起 · todo-app / a' }),
    project('b', 'delivered', [work('b')], { projectLabel: 'Todo-app' }),
  ]));
  assert.deepEqual([...new Set(rows.map(row => row.project))], ['Todo-app']);
  const live = rows.find(row => row.phase === 'running_active');
  assert.equal(live.actor, 'Codex');
  assert.equal(live.agentId, 'codex');
  assert.equal(live.model, 'gpt-6.1-sol');
  assert.equal(live.effort, 'xhigh');
  assert.equal(live.runtimeModel, null);
});

test('房间标签在零窗口时仍显示外部任务，不把有活动写成运行', () => {
  const runtime = { windowClosed: true, activeSessions: null, openWindows: 0, idleSessions: null };
  const external = { count: 1, activeCount: null, hasActivity: true };
  const summary = sceneSummary(runtime, external);
  assert.equal(summary.label, '1 个外部任务 · 有新活动');
  assert.equal(summary.tone, 'activity');
  assert.equal(sceneSummary(runtime, { count: 0, activeCount: 0, hasActivity: false }).label, '没有打开窗口');
});

test('房间标签同时呈现真实对话运行与外部任务，未知只显示可见窗口', () => {
  const external = { count: 3, activeCount: null, hasActivity: false };
  const summary = sceneSummary({ activeSessions: 2, openWindows: 1 }, external);
  assert.equal(summary.label, '2 个对话运行 · 外部 3 任务');
  assert.equal(summary.tone, 'running');
  assert.equal(sceneSummary({ activeSessions: null, openWindows: 2 }, { count: 0, activeCount: 0, hasActivity: false }).label, '2 个窗口打开');
  assert.equal(sceneSummary({ activeSessions: null, openWindows: null }, { count: 0, activeCount: 0, hasActivity: false }).label, '运行情况未知');
});

test('动作只映射明确运行或完整休眠信号，未知 Agent 保持中性', () => {
  const noTask = { count: 0, activeCount: 0 };
  assert.equal(sceneActivity({ activeSessions: null, idleSessions: null, unknownSessions: null }, noTask), 'neutral');
  assert.equal(sceneActivity({ activeSessions: 0, idleSessions: 2, unknownSessions: 0 }, noTask), 'sleeping');
  assert.equal(sceneActivity({ activeSessions: 0, idleSessions: 2, unknownSessions: 1 }, noTask), 'neutral');
  assert.equal(sceneActivity({ activeSessions: 0, idleSessions: 2, unknownSessions: 0 }, { count: 1, activeCount: null, hasActivity: true }), 'neutral');
  assert.equal(sceneActivity({ windowClosed: true, activeSessions: null }, { count: 1, activeCount: 1 }), 'working');
  assert.equal(sceneActivity({ activeSessions: 1 }, { count: 1, activeCount: null }), 'working');
});

test('房间顶部仅累计明确观察数，保留部分未知提示', () => {
  assert.deepEqual(sceneCounts([
    { runtime: { activeSessions: 1, idleSessions: 3, unknownSessions: 0 } },
    { runtime: { activeSessions: null, idleSessions: null, unknownSessions: null } },
    { runtime: { windowClosed: true, activeSessions: null, idleSessions: null, unknownSessions: null } },
  ]), { running: 1, sleeping: 3, partial: true });
});

test('房间部分会话可见时保留已见运行数，总数继续未知', () => {
  const runtime = runtimeFor({ agentRuntime: { agents: [{ agentId: 'codex', sourceStatus: 'partial', openWindows: 1, activeSessions: null, idleSessions: null, unknownSessions: null,
    instances: [{ state: 'running', model: 'gpt-6-astra' }, { state: 'idle', model: null }] }] } }, 'codex');
  const external = { count: 0, activeCount: 0, hasActivity: false };
  assert.equal(runtime.activeSessions, null);
  assert.equal(sceneSummary(runtime, external).label, '已见 1 个对话运行');
  assert.equal(sceneActivity(runtime, external), 'working');
  assert.deepEqual(sceneCounts([{ runtime }]), { running: 1, sleeping: 1, partial: true });
  const unavailable = runtimeFor({ agentRuntime: { agents: [{ agentId: 'codex', sourceStatus: 'unavailable', instances: [{ state: 'running', model: 'old' }] }] } }, 'codex');
  assert.equal(unavailable.observedActive, 0);
  assert.equal(sceneActivity(unavailable, external), 'neutral');
});

test('房间状态胶囊区分运行休眠未知，零窗口未知后台实例另列', () => {
  const noTask = { count: 0, activeCount: 0, hasActivity: false };
  assert.equal(sceneSummary({ activeSessions: 2 }, noTask).stateLabel, '运行 2');
  assert.equal(sceneSummary({ activeSessions: 0, idleSessions: 2, unknownSessions: 0 }, noTask).state, 'sleeping');
  assert.equal(sceneSummary({ activeSessions: null, openWindows: 1 }, noTask).state, 'unknown');
  assert.equal(sceneSummary({ activeSessions: null, openWindows: 0 }, { count: 1, activeCount: null, hasActivity: true }).state, 'activity');
  const runtime = runtimeFor({ agentRuntime: { agents: [{ agentId: 'cursor', sourceStatus: 'partial', openWindows: 0, openSessions: null, activeSessions: null, idleSessions: null,
    instances: [{ kind: 'conversation', state: 'unknown', model: null }] }] } }, 'cursor');
  assert.equal(runtime.windowClosed, false);
  assert.equal(runtime.backgroundOnlyUnknown, true);
  const running = runtimeFor({ agentRuntime: { agents: [{ agentId: 'cursor', sourceStatus: 'partial', openWindows: 0, activeSessions: null, instances: [{ state: 'running', model: 'observed' }] }] } }, 'cursor');
  assert.equal(running.backgroundOnlyUnknown, false);
  assert.equal(sceneActivity(running, noTask), 'working');
});

test('房间部分休眠实例显示已见数量，未知总数不触发整个人物睡眠', () => {
  const runtime = runtimeFor({ agentRuntime: { agents: [{ agentId: 'codex', sourceStatus: 'partial', openWindows: 1, activeSessions: null, idleSessions: null,
    instances: [{ state: 'idle' }, { state: 'idle' }] }] } }, 'codex');
  const external = { count: 0, activeCount: 0, hasActivity: false };
  assert.equal(sceneSummary(runtime, external).stateLabel, '已见 2 休眠');
  assert.equal(sceneSummary(runtime, external).state, 'partial-sleep');
  assert.equal(sceneActivity(runtime, external), 'neutral');
  assert.equal(runtime.idleSessions, null);
});

test('交卷气泡只认当前同一任务交卷的变化，初帧历史和新出现的完成记录不闪', () => {
  const before = flattenSnapshot(snapshot([project('job', 'running_active', [work('a', { axes: { delivery: 'pending' } })])]));
  const complete = flattenSnapshot(snapshot([project('job', 'delivered', [work('a', { axes: { delivery: 'complete' } })], { isCurrent: false })]));
  assert.deepEqual(newDeliveryAgents(null, complete), []);
  assert.deepEqual(newDeliveryAgents([], complete), []);
  assert.deepEqual(newDeliveryAgents(before, complete), ['codex']);
  assert.deepEqual(newDeliveryAgents(complete, complete), []);
  assert.deepEqual(newDeliveryAgents(before.map(row => ({ ...row, isCurrent: false })), complete), []);
});
