export const AGENTS = [
  { id: 'claude', name: 'Claude', art: 'claude', mark: 'C', color: '#c77c43', x: 48, bottom: 35, width: 27 },
  { id: 'codex', name: 'Codex', art: 'gpt', mark: 'CX', color: '#9683bf', x: 22, bottom: 57, width: 25 },
  { id: 'dsh', name: 'DSH', art: 'deepseek', mark: 'D', color: '#4e78ae', x: 22, bottom: 12, width: 29 },
  { id: 'grok', name: 'Grok / GrokBuild', art: 'grok', mark: 'G', color: '#a97b68', x: 81, bottom: 1, width: 34 },
  { id: 'grok-bot', name: 'Grok Bot', mark: 'GB', color: '#879d99' },
  { id: 'cursor', name: 'Cursor', mark: 'CU', color: '#868ca8' },
  { id: 'pi', name: 'PI', mark: 'PI', color: '#9d8bb1' },
  { id: 'opencode', name: 'OpenCode', mark: 'OC', color: '#7d9baf' },
];
// Art assets and Agent identity are separate; the other existing art files remain on disk.
export const CHARACTERS = AGENTS.filter(agent => agent.art || agent.botArt);
const PHASES = {
  running_active: ['activity', '有新活动'], running_quiet: ['idle', '运行情况未知'], running_unknown: ['idle', '运行情况未知'],
  waiting: ['queued', '等待中'], registered: ['queued', '刚派出'], start_unconfirmed: ['queued', '正在启动'],
  delivered: ['review', '已交卷'], reported_failure: ['review', '交回了难题'], process_gone: ['idle', '进程已结束'],
  transport_failed: ['review', '交接未完成'], executor_finished: ['idle', '这轮结束了'], failed: ['idle', '本轮已结束'],
  retired: ['idle', '已换新一轮'], unknown: ['idle', '运行情况未知'], unbound: ['unbound', '未关联进程记录'],
};
const PHASE_NOTES = {
  running_active: 'CLI Watch 看到了新活动；目前没有直接运行信号，无法确认是否正在生成或跑工具。',
  running_quiet: '暂时没有新活动；这不能区分正在工作和等待输入。',
  running_unknown: '有进行中的任务记录，实际运行情况暂时未知。',
  waiting: '任务记录显示正在等待，实际会话状态暂时未知。', registered: '任务刚派出，等待开始的记录。',
  start_unconfirmed: '任务正在启动，尚未确认实际运行。', delivered: '已经交卷，等待负责人检查。',
  reported_failure: '遇到的难题已交回负责人处理。', process_gone: '观察到进程结束，尚未看到交卷记录。',
  transport_failed: '记录显示交接尚未完成，可查看交卷与负责人检查的状态。',
  executor_finished: '这一轮已经结束，结果可查看交卷与负责人消息。',
  failed: '记录显示这一轮因错误结束，交卷与检查状态另列。', retired: '这一轮已经由后续的新一轮接手。',
  unknown: '当前信息不足，实际运行情况未知。', unbound: '这是一条尚未关联任务的进程记录，不作为窗口或会话数量。',
};
export const FILTERS = [
  { id: 'running', label: '进行中记录' }, { id: 'queued', label: '准备 / 等待' },
  { id: 'review', label: '已交回' }, { id: 'ended', label: '已结束' },
];
export function phaseInfo(phase) {
  const [tone, label] = PHASES[phase] || PHASES.unknown;
  return { tone, label, basis: PHASE_NOTES[phase] || PHASE_NOTES.unknown };
}
function plainName(value) {
  const text = String(value || '').replace(/^[^·/]{1,12}发起\s*·\s*/, '').trim();
  return /^[a-z]/.test(text) ? text[0].toUpperCase() + text.slice(1) : text;
}
function plainModel(value) {
  const model = String(value || '').replace(/（[^）]*）/g, '').trim();
  return !model || /^(?:unknown|未知|未观察|未提供|未标明)(?:模型)?$/i.test(model) ? null : model;
}
function identity(value) {
  const name = plainName(value).toLowerCase();
  if (/grok[\s_-]*bot/.test(name)) return 'grok-bot';
  if (/\bopen[\s_-]*code\b/.test(name)) return 'opencode';
  if (/\bcursor\b/.test(name)) return 'cursor';
  if (/\bpi\b/.test(name)) return 'pi';
  if (/\b(?:dsh|deepseek)\b/.test(name)) return 'dsh';
  if (/\bcodex\b/.test(name)) return 'codex';
  if (/\bclaude\b/.test(name)) return 'claude';
  if (/\bgrok(?:build)?\b/.test(name)) return 'grok';
  return null;
}
export function normalizeAgentId(value) { return identity(value); }
export function agentFor(work) {
  const name = identity(work.actorName), kind = identity(work.actorKind);
  const id = name === 'grok' && kind === 'grok-bot' ? kind : name || kind;
  return { id, basis: id ? '按记录中的 Agent 身份归组' : 'Agent 身份未知，保留未分类' };
}
export function characterFor(work) {
  const agent = agentFor(work);
  return { id: AGENTS.find(item => item.id === agent.id)?.art || null, basis: agent.basis };
}
export function parseSnapshot(value) {
  if (!value || value.schemaVersion !== 1 || (value.mode !== 'live' && value.mode !== 'demo') || !value.source || !Array.isArray(value.projects)) throw new Error('研究室读到的数据格式不对。');
  if (!value.projects.every(project => project && typeof project.id === 'string' && Array.isArray(project.works))) throw new Error('研究室读到的任务记录不完整。');
  return value;
}
export function snapshotRevision(snapshot) {
  if (!snapshot) return '';
  return JSON.stringify([snapshot.source.publishedAt, snapshot.source.status, snapshot.source.stale,
    snapshot.agentRuntime?.observedAt, snapshot.agentRuntime?.publishedAt,
    snapshot.agentRuntime?.agents?.map(agent => [agent.agentId, agent.observedAt, agent.sourceStatus])]);
}
function projectName(project) { return plainName(project.projectLabel || String(project.name || '').split(' / ')[0]) || '没写项目'; }
export function flattenSnapshot(snapshot) {
  if (!snapshot) return [];
  return snapshot.projects.flatMap(project => project.works.map(work => {
    const agent = agentFor(work);
    const phase = String(work.route || '').startsWith('watch:') ? work.route.slice(6) : project.phase;
    const unbound = phase === 'unbound';
    const runtimeState = ['running', 'idle', 'unknown'].includes(work.runtimeState) ? work.runtimeState : 'unknown';
    return {
      id: `${project.id}\u001f${work.id}`, sourceProjectId: project.id,
      project: unbound ? '未关联进程记录' : projectName(project), projectFullName: project.name,
      task: unbound ? `${plainName(work.actorName) || 'AI'} 进程记录` : work.taskName || project.goal || '没写任务名',
      actor: plainName(work.actorName) || '身份未知', agentId: agent.id,
      model: plainModel(work.model), effort: plainModel(work.effort),
      runtimeState, runtimeModel: runtimeState === 'running' ? plainModel(work.runtimeModel) : null,
      characterId: AGENTS.find(item => item.id === agent.id)?.art || null, characterBasis: agent.basis,
      phase, ...phaseInfo(phase),
      isCurrent: Boolean(project.isCurrent) && !work.isHistorical && work.quality !== 'LastKnown', isUnbound: unbound,
      axes: work.axes || {}, quality: work.quality || project.quality || 'Unknown', summary: work.summary || project.summary || '',
      note: work.blocker || '', artifacts: Array.isArray(work.artifacts) ? work.artifacts : [],
      lastFactAt: project.lastFactAt || null, observedActivityAt: project.observedActivityAt || null,
    };
  })).sort((a, b) => Number(b.isCurrent) - Number(a.isCurrent) || (Date.parse(b.lastFactAt) || 0) - (Date.parse(a.lastFactAt) || 0) || a.id.localeCompare(b.id));
}
const RUNNING_PHASES = ['running_active', 'running_quiet', 'running_unknown'];
const QUEUED_PHASES = ['waiting', 'registered', 'start_unconfirmed'];
const ENDED_PHASES = ['process_gone', 'executor_finished', 'failed', 'retired'];
export function matchesFilter(row, filter) {
  if (!filter) return true;
  if (filter === 'running') return RUNNING_PHASES.includes(row.phase);
  if (filter === 'queued') return QUEUED_PHASES.includes(row.phase);
  if (filter === 'review') return ['delivered', 'reported_failure', 'transport_failed'].includes(row.phase);
  if (filter === 'ended') return ENDED_PHASES.includes(row.phase);
  return true;
}
export function selectRows(rows, { history = false, project = null, filter = null, includeUnbound = false } = {}) {
  return rows.filter(row => (includeUnbound || !row.isUnbound) && row.isCurrent !== history && (!project || row.project === project) && matchesFilter(row, filter));
}
export function countsFor(rows) { return Object.fromEntries(FILTERS.map(filter => [filter.id, rows.filter(row => !row.isUnbound && matchesFilter(row, filter.id)).length])); }
export function currentSummary(rows) {
  const current = rows.filter(row => row.isCurrent);
  return { tasks: current.filter(row => !row.isUnbound).length, unbound: current.filter(row => row.isUnbound).length, history: rows.filter(row => !row.isCurrent && !row.isUnbound).length };
}
function count(value) { return Number.isInteger(value) && value >= 0 ? value : null; }
export function countLabel(value) { return value === null ? '未知' : String(value); }
export function runtimeFor(snapshot, agentId) {
  const runtime = snapshot?.agentRuntime;
  const agent = runtime?.agents?.find(item => normalizeAgentId(item.agentId) === agentId);
  const status = ['live', 'partial'].includes(agent?.sourceStatus) ? agent.sourceStatus : 'unavailable';
  const available = status !== 'unavailable';
  const instances = available && Array.isArray(agent?.instances) ? agent.instances : [];
  const activeSessions = available ? count(agent.activeSessions) : null;
  const windowClosed = available && count(agent.openWindows) === 0 && instances.length === 0
    && ![agent.openSessions, agent.activeSessions, agent.idleSessions, agent.unknownSessions].some(value => count(value) > 0);
  const backgroundOnlyUnknown = available && count(agent.openWindows) === 0 && instances.length > 0
    && instances.every(instance => instance.state === 'unknown')
    && ![agent.openSessions, agent.activeSessions, agent.idleSessions].some(value => count(value) > 0);
  const running = instances.filter(instance => instance.state === 'running');
  const models = [...new Set(running.map(instance => plainModel(instance.model) || '模型未知'))];
  if (activeSessions > 0 && !models.length) models.push('模型未知');
  return {
    sourceStatus: status, observedAt: agent?.observedAt || runtime?.observedAt || null, windowClosed, backgroundOnlyUnknown,
    openWindows: available ? count(agent.openWindows) : null, openSessions: available ? count(agent.openSessions) : null,
    activeSessions, idleSessions: available ? count(agent.idleSessions) : null, unknownSessions: available ? count(agent.unknownSessions) : null,
    observedActive: running.length, observedIdle: instances.filter(instance => instance.state === 'idle').length,
    models, coverage: agent?.coverage || '还没有可读取的窗口和会话状态。',
  };
}
export function externalFor(rows, agentId) {
  const tasks = rows.filter(row => row.agentId === agentId && row.isCurrent && !row.isUnbound);
  const running = tasks.filter(row => row.runtimeState === 'running');
  const uncertain = tasks.some(row => row.runtimeState === 'unknown' && [...RUNNING_PHASES, ...QUEUED_PHASES, 'unknown'].includes(row.phase));
  return { tasks, count: tasks.length, activeCount: uncertain ? null : running.length,
    models: [...new Set(running.map(row => row.runtimeModel || '模型未知'))], hasActivity: tasks.some(row => row.phase === 'running_active') };
}
export function sceneSummary(runtime, external, hasFrame = true) {
  const knownRunning = runtime.activeSessions ?? runtime.observedActive ?? 0;
  const appRunning = knownRunning > 0, externalRunning = external.activeCount > 0;
  let label;
  if (appRunning) label = `${runtime.activeSessions === null ? '已见 ' : ''}${knownRunning} 个对话运行`;
  else if (externalRunning) label = `外部 ${external.activeCount} 个运行`;
  else if (hasFrame && external.count > 0) label = `${external.count} 个外部任务`;
  else if (runtime.windowClosed) label = '没有打开窗口';
  else if (runtime.openWindows > 0) label = `${runtime.openWindows} 个窗口打开`;
  else if (runtime.idleSessions > 0) label = `${runtime.idleSessions} 个对话休眠`;
  else label = '运行情况未知';
  if (hasFrame && external.count > 0) {
    if (appRunning || externalRunning) label += ` · 外部 ${external.count} 任务`;
    else if (external.activeCount === null) label += external.hasActivity ? ' · 有新活动' : ' · 运行未知';
  }
  const state = appRunning || externalRunning ? 'running' : sceneActivity(runtime, external) === 'sleeping' ? 'sleeping'
    : external.hasActivity ? 'activity' : runtime.observedIdle > 0 ? 'partial-sleep' : runtime.windowClosed && !external.count ? 'closed' : 'unknown';
  const stateLabel = state === 'running' ? `运行 ${appRunning ? knownRunning : external.activeCount}`
    : state === 'partial-sleep' ? `已见 ${runtime.observedIdle} 休眠`
    : { sleeping: 'zz 休眠', activity: '有新活动', closed: '未打开', unknown: '状态未知' }[state];
  const meta = hasFrame && external.count > 0 ? `外部 ${external.count}`
    : runtime.openWindows > 0 ? `${runtime.openWindows} 窗口` : '';
  return { label, tone: appRunning || externalRunning ? 'running' : external.hasActivity ? 'activity' : 'idle', state, stateLabel, meta };
}
export function sceneActivity(runtime, external) {
  if (runtime.activeSessions > 0 || runtime.observedActive > 0 || external.activeCount > 0) return 'working';
  if (runtime.activeSessions === 0 && runtime.idleSessions > 0 && runtime.unknownSessions === 0
    && (external.count === 0 || external.activeCount === 0)) return 'sleeping';
  return 'neutral';
}
export function sceneCounts(agents) {
  return {
    running: agents.reduce((total, agent) => total + (agent.runtime.activeSessions ?? agent.runtime.observedActive ?? 0), 0),
    sleeping: agents.reduce((total, agent) => total + (agent.runtime.idleSessions ?? agent.runtime.observedIdle ?? 0), 0),
    partial: agents.some(agent => !agent.runtime.windowClosed && (agent.runtime.activeSessions === null || agent.runtime.unknownSessions > 0)),
  };
}
export function newDeliveryAgents(previous, next) {
  if (!previous) return [];
  const before = new Map(previous.filter(row => row.isCurrent && !row.isUnbound).map(row => [row.id, row]));
  return [...new Set(next.filter(row => row.agentId && !row.isUnbound && row.axes.delivery === 'complete'
    && before.has(row.id) && before.get(row.id).axes.delivery !== 'complete').map(row => row.agentId))];
}
export function sourceState(snapshot, networkError) {
  if (networkError) return { status: 'offline', label: '连接断了', stale: true, description: snapshot ? '暂时连不上，下面是上次看到的情况。' : '暂时连不上研究室，还没有可显示的内容。' };
  if (snapshot?.mode === 'demo') return { status: 'live', label: '演示数据', stale: false, description: '这是仓库里的演示数据，没有读取本机窗口或任务目录。' };
  if (!snapshot || snapshot.source.status === 'starting') return { status: 'starting', label: '正在读取', stale: true, description: '正在读取本机状态，首次打开需要一点时间。' };
  const source = snapshot.source;
  if (source.status === 'offline') return { status: 'offline', label: '暂时读不到', stale: true, description: '暂时读不到 CLI Watch 最新记录，保留上次看到的情况；各 Agent 本机状态另列。' };
  if (source.stale) return { status: 'degraded', label: '记录待更新', stale: true, description: 'CLI Watch 记录暂未更新，保留上次看到的情况；各 Agent 本机状态另列。' };
  if (source.status === 'degraded') return { status: 'degraded', label: '实时', stale: false, description: '有些情况还没看全，看到的会继续更新。' };
  return { status: 'live', label: '实时', stale: false, description: '每 3 秒自动刷新本机窗口、会话状态与外部任务。' };
}
const AXES = {
  process: { alive: '进程存在', wrapper_only: '正在启动', dead: '已结束', unknown: '未知' },
  execution: { running: '任务进行中，实际运行未知', returned: '这轮结束了', failed: '本轮因错误结束', registered: '刚派出', unknown: '未知' },
  transport: { complete: '已交接', failed: '交接未完成', unknown: '尚未确认' },
  delivery: { complete: '已交卷', pending: '还没交', unknown: '还没看到' },
  acceptance: { pending: '等负责人检查', unknown: '看负责人消息' },
  turn: { active: '有新活动', quiet: '暂时安静', waiting: '在等', unknown: '未知' },
};
export function axisLabel(axis, value) { return AXES[axis]?.[value] || '未知'; }
export function timeLabel(value) {
  if (!value || !Number.isFinite(Date.parse(value))) return '未读取';
  return new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Taipei', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }).format(new Date(value));
}
export function agoLabel(value, now = Date.now()) {
  if (!value || !Number.isFinite(Date.parse(value))) return '未知';
  const seconds = Math.max(0, Math.floor((now - Date.parse(value)) / 1000));
  if (seconds < 60) return `${seconds} 秒前`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分钟前`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} 小时前`;
  return `${Math.floor(seconds / 86400)} 天前`;
}
