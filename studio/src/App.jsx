import { useEffect, useMemo, useRef, useState } from 'react';
import { DiamondsFourIcon, FolderSimpleIcon, PlayCircleIcon, HourglassIcon, FileTextIcon, CaretRightIcon, XIcon, MinusIcon, PlusIcon, InfoIcon, MonitorIcon, PauseCircleIcon, ClockIcon, SquaresFourIcon, PowerIcon, ArrowClockwiseIcon } from '@phosphor-icons/react';
import { AGENTS, FILTERS, parseSnapshot, snapshotRevision, flattenSnapshot, selectRows, countsFor, currentSummary, sourceState, axisLabel, timeLabel, agoLabel, runtimeFor, externalFor, sceneSummary, sceneActivity, sceneCounts, newDeliveryAgents } from './live-data.mjs';
import { startSkits } from './room-skits.mjs';

const ICONS = { running: PlayCircleIcon, activity: ClockIcon, queued: HourglassIcon, review: FileTextIcon, ended: PowerIcon, idle: PowerIcon, unbound: InfoIcon };
const QUALITY = { Fresh: '刚刷新', Complete: '刚刷新', Partial: '部分可见', LastKnown: '上次记录', Conflicting: '记录有出入', Unavailable: '暂时读不到', Unknown: '未知' };
const ISSUE_LABELS = { scan_budget_exhausted: '这次还有记录未读完', schema_error: '有几份记录暂时读不懂', ready_identity_conflict: '有交卷记录尚未对上派单', optional_missing: '有几份旧记录未找到', grok_session_missing: '部分 Grok 活动记录未找到' };
// Room companions are a cast, not a list of status sources. Gemini and Kimi stay
// in the room without receiving invented Agent cards or runtime observations.
const ROOM_CAST = [
  { key: 'gpt', name: 'GPT', agentId: 'codex', art: 'gpt-chibi', x: 19, bottom: 53, height: 41, ratio: .75, layer: 2, cue: [.74, .2] },
  { key: 'gemini', name: 'Gemini', art: 'gemini-chibi', x: 49, bottom: 54, height: 40, ratio: .75, layer: 2, cue: [.68, .22] },
  { key: 'kimi', name: 'Kimi', art: 'kimi-chibi', x: 83, bottom: 41, height: 43, ratio: .75, layer: 4, cue: [.68, .08], walk: [-32], turn: true },
  { key: 'claude', name: 'Claude', agentId: 'claude', art: 'claude-chibi', x: 47, bottom: 28, height: 48, ratio: .75, layer: 4, cue: [.24, .13, 'left'] },
  { key: 'deepseek', name: 'DeepSeek', agentId: 'dsh', art: 'deepseek-chibi', x: 19, bottom: 9, height: 42, ratio: .889, layer: 8, cue: [.45, .06] },
  { key: 'grok', name: 'Grok', agentId: 'grok', art: 'grok-chibi', x: 81, bottom: 0, height: 48, ratio: .853, layer: 8, cue: [.66, .1] },
];
// cue: where head-top state cues (typing dots, z's) attach, as fractions of the character box; 'left'
// hangs them off the left of the head where the right side would sit on a neighbour.
// walk: spots a cast member moves to and back from, as percent of its own width. Kimi strolls along the aisle
// behind the table so the table keeps hiding her feet (past its right end they would float over the stool).
// Only full-body standing art walks: the seated girls need standing poses first. turn: flip to face the way
// she walks. A walking agent's status pin follows it.
// Room pins hang off their character's box so they follow the cast at any window shape or zoom.
// ax/ay are fractions of that box (0 = left/top edge, 1 = right/bottom), picked beside a head
// where the art is transparent; side is where the tag sits from that point. align/above only
// steer the hover card. Phones keep the fixed mobileX/mobileY layout.
// Agents without a room figure use the same unanchored top-bar chip as Grok Bot.
const SCENE_PINS = [
  { id: 'codex', ax: .2, ay: .24, side: 'left', mobileX: 2, mobileY: 36, align: 'left' },
  { id: 'claude', ax: .17, ay: .26, side: 'left', mobileX: 45, mobileY: 66, align: 'right', above: true },
  { id: 'dsh', ax: .15, ay: .17, side: 'left', mobileX: 2, mobileY: 82, align: 'left', above: true },
  { id: 'grok', ax: .88, ay: .12, side: 'right', mobileX: 73, mobileY: 82, align: 'right', above: true },
  { id: 'cursor', mobileX: 28, mobileY: 3, align: 'right' },
  { id: 'pi', mobileX: 46, mobileY: 3, align: 'right' },
  { id: 'opencode', mobileX: 64, mobileY: 3, align: 'right' },
  { id: 'grok-bot', mobileX: 73, mobileY: 3, align: 'right' },
];
// Small in-place actions for cast members that are neither working nor asleep (see data-idle in styles.css).
const IDLE_ACTIONS = ['hop', 'look', 'wiggle', 'stretch', 'bounce'];
const WALK_MS = 2000;

function Badge({ tone = 'idle', children, small = false }) {
  const Icon = ICONS[tone] || InfoIcon;
  return <span className={`badge ${tone}${small ? ' small' : ''}`}><Icon weight="fill" />{children}</span>;
}
function useLiveSnapshot() {
  const [snapshot, setSnapshot] = useState(null), [error, setError] = useState(null), [refresh, setRefresh] = useState(0);
  useEffect(() => {
    let disposed = false, timer, controller;
    async function read() {
      controller = new AbortController();
      const deadline = setTimeout(() => controller.abort(), 8000);
      try {
        const response = await fetch('/api/snapshot', { cache: 'no-store', signal: controller.signal });
        if (!response.ok) throw new Error('snapshot_unavailable');
        const value = parseSnapshot(await response.json());
        if (!disposed) { setSnapshot(previous => snapshotRevision(previous) === snapshotRevision(value) ? previous : value); setError(null); }
      } catch { if (!disposed) setError('暂时连不上'); }
      finally { clearTimeout(deadline); if (!disposed) timer = setTimeout(read, 3000); }
    }
    read();
    return () => { disposed = true; clearTimeout(timer); controller?.abort(); };
  }, [refresh]);
  return { snapshot, error, refresh: () => setRefresh(value => value + 1) };
}
function TaskList({ rows, selectedId, onSelect, empty = '这里没有任务记录。' }) {
  if (!rows.length) return <p className="list-empty">{empty}</p>;
  return <div className="compact-list real-task-list">{rows.map(row => <button key={row.id} className={row.id === selectedId ? 'selected' : ''} onClick={() => onSelect(row)}><span><strong>{row.task}</strong><small>{row.actor} · {row.project}</small></span><Badge tone={row.tone} small>{row.label}</Badge></button>)}</div>;
}
function ModelLine({ models }) { return models.length ? <span className="runtime-model">{models.join(' · ')}</span> : null; }
function OpenedStatus({ runtime, detail = false }) {
  if (runtime.windowClosed || runtime.backgroundOnlyUnknown) return <><p className="runtime-counts">没有打开的窗口</p>{detail && <>{runtime.backgroundOnlyUnknown && <p className="runtime-coverage">另检测到后台实例，是否由你手动启动、是否正在运行暂未确认。</p>}<small className="runtime-time">读取 · {timeLabel(runtime.observedAt)}</small></>}</>;
  return <>
    <p className="runtime-counts">{runtime.openWindows === null ? '窗口数未知' : <><strong>{runtime.openWindows}</strong> 个窗口</>} <span>·</span> {runtime.openSessions === null ? '对话数未知' : <><strong>{runtime.openSessions}</strong> 个对话</>}</p>
    <p className={`runtime-running ${runtime.activeSessions > 0 || runtime.observedActive > 0 ? 'is-running' : ''}`}>{runtime.activeSessions === null ? runtime.observedActive > 0 ? `已见 ${runtime.observedActive} 个运行 · 总数未知` : '运行数量未知' : `${runtime.activeSessions} 个运行`}{runtime.idleSessions !== null ? <span> · {runtime.idleSessions} 个休眠</span> : runtime.observedIdle > 0 && <span> · 已见 {runtime.observedIdle} 个休眠</span>}</p>
    <ModelLine models={runtime.models} />
    {detail && <><p className="runtime-coverage">{runtime.coverage}</p>{runtime.unknownSessions > 0 && <p className="runtime-coverage">另有 {runtime.unknownSessions} 个会话状态未知</p>}<small className="runtime-time">读取 · {timeLabel(runtime.observedAt)}</small></>}
  </>;
}
function ExternalStatus({ external, hasFrame, compact = false }) {
  return <>
    <p className="external-count">{hasFrame ? <><strong>{external.count}</strong> 个当前任务</> : '正在读取任务'}</p>
    {hasFrame && external.count > 0 && <p className="external-running">{external.activeCount === null ? '运行情况未知' : `${external.activeCount} 个运行`}{external.hasActivity && external.activeCount === null && <span> · 有新活动</span>}</p>}
    <ModelLine models={external.models} />
    {compact && external.tasks.length > 0 && <div className="agent-task-names">{external.tasks.slice(0, 2).map(row => <span key={row.id} title={row.task}>{row.task}</span>)}{external.tasks.length > 2 && <small>还有 {external.tasks.length - 2} 件 · 点开查看</small>}</div>}
    {hasFrame && !external.count && <p className="agent-no-tasks">暂无当前任务</p>}
  </>;
}
function SceneAgentStatus({ agent, pin, hasFrame, selected, onSelect, inline = false }) {
  const [open, setOpen] = useState(false);
  const summary = sceneSummary(agent.runtime, agent.external, hasFrame);
  const cast = !inline && pin.side && ROOM_CAST.find(person => person.agentId === agent.id);
  const anchor = cast ? { '--c-x': cast.x, '--c-bottom': cast.bottom, '--c-h': cast.height, '--c-ratio': cast.ratio, '--a-x': pin.ax, '--a-y': pin.ay } : {};
  return <div data-agent={agent.id} className={`scene-agent-pin ${pin.align}${pin.above ? ' opens-above' : ''}${cast ? ` anchored side-${pin.side}` : ''}${summary.state === 'closed' ? ' closed' : ''}${open ? ' expanded' : ''}${selected ? ' selected' : ''}${inline ? ' in-overview' : ''}`} style={{ ...anchor, '--pin-mobile-x': pin.mobileX + '%', '--pin-mobile-y': pin.mobileY + '%', '--pin-accent': agent.color }} onMouseEnter={() => setOpen(true)} onMouseLeave={event => { if (!event.currentTarget.contains(document.activeElement)) setOpen(false); }} onFocus={() => setOpen(true)} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) setOpen(false); }} onKeyDown={event => { if (event.key === 'Escape') { event.stopPropagation(); setOpen(false); } }}>
    <button className={`scene-pin-button ${summary.tone}`} aria-expanded={open} aria-controls={`scene-details-${agent.id}`} aria-label={`${agent.name}：${summary.label}`} title={summary.label} onClick={() => { onSelect(agent); setOpen(true); }}><strong><span className="scene-state-dot" />{agent.id === 'grok' ? 'Grok' : agent.name}<CaretRightIcon /></strong><span className="scene-state-line"><b className={`scene-state-pill ${summary.state}`}>{summary.stateLabel}</b>{summary.meta && <small>{summary.meta}</small>}</span></button>
    {open && <div className="scene-pin-popover" id={`scene-details-${agent.id}`}><div className="scene-popover-card"><div className="scene-popover-title"><strong>{agent.name}</strong><button aria-label={`关闭 ${agent.name} 状态`} onClick={() => setOpen(false)}><XIcon /></button></div><section><h3><MonitorIcon />你打开的</h3><OpenedStatus runtime={agent.runtime} /></section><section><h3><FolderSimpleIcon />外部执行</h3><ExternalStatus external={agent.external} hasFrame={hasFrame} compact /></section><button className="scene-detail-link" onClick={() => { onSelect(agent); setOpen(false); }}>查看完整详情<CaretRightIcon /></button></div></div>}
  </div>;
}
function stateRows(row) {
  const list = [['进度', row.runtimeState === 'running' && row.isCurrent ? '正在生成或跑工具' : axisLabel('execution', row.axes.execution)]];
  if (row.axes.delivery && row.axes.delivery !== 'unknown') list.push(['交卷', axisLabel('delivery', row.axes.delivery)]);
  if (row.axes.acceptance === 'pending') list.push(['检查', axisLabel('acceptance', row.axes.acceptance)]);
  return list;
}

export function App() {
  const live = useLiveSnapshot();
  const [selectedAgentId, setSelectedAgentId] = useState('codex'), [selectedRowId, setSelectedRowId] = useState(null);
  const [project, setProject] = useState(null), [filter, setFilter] = useState(null), [history, setHistory] = useState(false);
  const [motion, setMotion] = useState(() => !window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const [pageVisible, setPageVisible] = useState(() => !document.hidden), [deliveredAgents, setDeliveredAgents] = useState([]);
  const [zoom, setZoom] = useState(1), [modal, setModal] = useState(null), [ready, setReady] = useState(false), [portraitReady, setPortraitReady] = useState(false), [now, setNow] = useState(Date.now());
  const world = useRef(null), close = useRef(null), previousFocus = useRef(null), previousRows = useRef(null), deliveryTimer = useRef(null);
  const rows = useMemo(() => flattenSnapshot(live.snapshot), [live.snapshot]);
  const health = sourceState(live.snapshot, live.error), summary = currentSummary(rows);
  const scopeRows = useMemo(() => selectRows(rows, { history }), [rows, history]);
  const baseRows = useMemo(() => selectRows(rows, { history, project }), [rows, history, project]);
  const visibleRows = useMemo(() => selectRows(rows, { history, project, filter }), [rows, history, project, filter]);
  const projects = useMemo(() => [...new Set(scopeRows.map(row => row.project))], [scopeRows]);
  const totals = countsFor(baseRows), hasFrame = Boolean(live.snapshot?.source.collectedAt);
  const agents = useMemo(() => AGENTS.map(agent => ({ ...agent, runtime: runtimeFor(live.snapshot, agent.id), external: externalFor(rows, agent.id) })), [live.snapshot, rows]);
  const observed = sceneCounts(agents);
  const agent = agents.find(item => item.id === selectedAgentId) || null;
  const selected = selectedRowId ? rows.find(row => row.id === selectedRowId) || null : null;
  const related = visibleRows.filter(row => row.agentId === selectedAgentId), otherRows = visibleRows.filter(row => !row.agentId);
  const currentIssues = (live.snapshot?.issues || []).filter(issue => issue.scope !== 'history');
  const historicalIssues = (live.snapshot?.issues || []).filter(issue => issue.scope === 'history');
  useEffect(() => { setPortraitReady(false); }, [selectedAgentId]);
  useEffect(() => {
    const onVisibility = () => setPageVisible(!document.hidden);
    const preference = window.matchMedia('(prefers-reduced-motion: reduce)');
    const onPreference = () => { if (preference.matches) setMotion(false); };
    document.addEventListener('visibilitychange', onVisibility); preference.addEventListener('change', onPreference);
    return () => { document.removeEventListener('visibilitychange', onVisibility); preference.removeEventListener('change', onPreference); clearTimeout(deliveryTimer.current); };
  }, []);
  useEffect(() => {
    const delivered = newDeliveryAgents(previousRows.current, rows);
    previousRows.current = rows;
    if (delivered.length) { clearTimeout(deliveryTimer.current); setDeliveredAgents(delivered); deliveryTimer.current = setTimeout(() => setDeliveredAgents([]), 8000); }
  }, [rows]);
  useEffect(() => { const timer = setInterval(() => setNow(Date.now()), 15000); return () => clearInterval(timer); }, []);
  // Every few seconds one idle cast member does a small action. It is set straight on the DOM so the
  // room does not re-render, never touches working/sleeping members, and stops with motion or a hidden tab.
  useEffect(() => {
    if (!motion || !pageVisible) return;
    let next, done;
    const act = () => {
      const pool = [...(world.current?.querySelectorAll('.room-cast-member:not(.working):not(.sleeping):not(.delivered):not([data-walk]):not([data-skit])') ?? [])];
      const member = pool[Math.floor(Math.random() * pool.length)];
      if (member) {
        member.dataset.idle = IDLE_ACTIONS[Math.floor(Math.random() * IDLE_ACTIONS.length)];
        done = setTimeout(() => { delete member.dataset.idle; }, 1900);
      }
      next = setTimeout(act, 3500 + Math.random() * 5500);
    };
    next = setTimeout(act, 1500 + Math.random() * 2500);
    const room = world.current;
    return () => { clearTimeout(next); clearTimeout(done); room?.querySelectorAll('[data-idle]').forEach(member => { delete member.dataset.idle; }); };
  }, [motion, pageVisible]);
  // Now and then a walker (Kimi) moves to one of its spots, lingers, and comes back; an agent's status
  // pin would slide along with it. Also DOM-only; turning motion off or hiding the tab puts
  // everyone straight back home.
  useEffect(() => {
    const walkers = ROOM_CAST.filter(person => person.walk);
    if (!motion || !pageVisible || !walkers.length) return;
    const timers = new Set();
    const later = (run, ms) => { const id = setTimeout(() => { timers.delete(id); run(); }, ms); timers.add(id); };
    const room = world.current, stage = room?.closest('.agent-room-stage');
    const parts = person => [room?.querySelector(`.room-cast-member.${person.key}`),
      person.agentId ? stage?.querySelector(`.scene-agent-pin[data-agent="${person.agentId}"]`) : null];
    const set = (person, walk, x = 0, face = 1) => {
      const [member, pin] = parts(person);
      if (member && walk) {
        member.dataset.walk = walk;
        member.style.setProperty('--walk-x', x + '%');
        member.style.setProperty('--walk-face', person.turn ? face : 1);
      } else if (member) {
        delete member.dataset.walk; delete member.dataset.idle;
        member.style.removeProperty('--walk-x'); member.style.removeProperty('--walk-face');
      }
      if (pin && walk) { pin.dataset.walk = walk; pin.style.setProperty('--walk-dx', x / 100); }
      else if (pin) { delete pin.dataset.walk; pin.style.removeProperty('--walk-dx'); }
    };
    // Agents get twice the turns of room companions: the room is mostly about them.
    const turns = walkers.flatMap(person => person.agentId ? [person, person] : [person]);
    const stroll = () => {
      const person = turns[Math.floor(Math.random() * turns.length)];
      const [member] = parts(person);
      if (member && !member.matches('.working,.sleeping,.delivered,[data-idle],[data-walk],[data-skit]')) {
        const to = person.walk[Math.floor(Math.random() * person.walk.length)];
        // Kimi faces the way she walks (her art looks left by default), then the room while she lingers.
        set(person, 'moving', to, to > 0 ? -1 : 1);
        later(() => {
          set(person, 'away', to);
          if (Math.random() < .6) later(() => {
            member.dataset.idle = IDLE_ACTIONS[Math.floor(Math.random() * IDLE_ACTIONS.length)];
            later(() => { delete member.dataset.idle; }, 1900);
          }, 800);
          later(() => {
            set(person, 'moving', 0, to > 0 ? 1 : -1);
            later(() => set(person, null), WALK_MS);
          }, 3500 + Math.random() * 3000);
        }, WALK_MS);
      }
      later(stroll, 12000 + Math.random() * 13000);
    };
    later(stroll, 5000 + Math.random() * 5000);
    return () => { timers.forEach(clearTimeout); walkers.forEach(person => set(person, null)); };
  }, [motion, pageVisible]);
  // Every half minute or so the AI girls play a little comic skit (room-skits.mjs); same motion boundaries.
  useEffect(() => {
    if (!motion || !pageVisible || !world.current) return;
    return startSkits(world.current);
  }, [motion, pageVisible]);
  useEffect(() => {
    const onKey = event => {
      if (event.key === 'Escape') setModal(null);
      if (event.key === 'Tab' && modal) {
        const controls = [...document.querySelectorAll('.modal button, .modal a')].filter(element => !element.disabled);
        const first = controls[0], last = controls.at(-1);
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
      }
    };
    window.addEventListener('keydown', onKey); return () => window.removeEventListener('keydown', onKey);
  }, [modal]);
  useEffect(() => { if (modal) { previousFocus.current = document.activeElement; close.current?.focus(); } else previousFocus.current?.focus?.(); }, [modal]);
  function chooseRow(row) { setSelectedRowId(row.id); setSelectedAgentId(row.agentId); }
  function chooseAgent(person) { setSelectedAgentId(person.id); setSelectedRowId(null); }
  function resetFilters() { setProject(null); setFilter(null); setSelectedRowId(null); }
  function chooseProject(name) { setProject(project === name ? null : name); setFilter(null); setSelectedRowId(null); }
  function chooseFilter(id) { setFilter(filter === id ? null : id); setSelectedRowId(null); }
  function changeScope(next) { setHistory(next); resetFilters(); }
  function move(event) {
    if (!motion || !pageVisible || !world.current) return;
    const rectangle = event.currentTarget.getBoundingClientRect();
    world.current.style.setProperty('--px', ((event.clientX - rectangle.left) / rectangle.width - .5) * 8 + 'px');
    world.current.style.setProperty('--py', ((event.clientY - rectangle.top) / rectangle.height - .5) * 6 + 'px');
  }
  function resetMove() { world.current?.style.setProperty('--px', '0px'); world.current?.style.setProperty('--py', '0px'); }

  return <div className={`studio-app real-studio agent-studio ${motion && pageVisible ? 'motion-on' : 'motion-off'}${health.stale ? ' source-stale' : ''}`}>
    <header className="app-header"><div className="brand"><DiamondsFourIcon weight="duotone" /><span>本机 <i>/</i> 共创中</span></div><h1>AI娘研究室</h1><button className={`preview-label live-label ${health.status}`} onClick={() => setModal('source')} title={health.description}><span className="live-dot" />{health.label}</button><button className="light-window" onClick={() => setModal('list')}><MonitorIcon />任务记录</button></header>
    <div className="workspace">
      <aside className="project-rail">
        <div className="scope-switch" aria-label="记录范围"><button aria-pressed={!history} className={!history ? 'selected' : ''} onClick={() => changeScope(false)}>当前工作</button><button aria-pressed={history} className={history ? 'selected' : ''} onClick={() => changeScope(true)}>历史记录</button></div>
        <p className="work-count">{hasFrame ? `${summary.tasks} 个当前外部任务` : '正在读取任务记录'}</p>
        <div className="rail-heading"><FolderSimpleIcon weight="fill" /><span>任务项目</span><button aria-label="显示全部项目" title="显示全部项目" onClick={resetFilters}><SquaresFourIcon /></button></div>
        <nav aria-label="项目筛选">{projects.map(name => <button key={name} className={`project-item ${project === name ? 'selected' : ''}`} aria-pressed={project === name} onClick={() => chooseProject(name)} title={name}><FolderSimpleIcon weight="fill" /><span><strong>{name}</strong><small>{scopeRows.filter(row => row.project === name).length} 条任务记录</small></span></button>)}{hasFrame && !projects.length && <p className="rail-empty">{history ? '还没有历史记录' : '暂无当前外部任务'}</p>}</nav>
        <div className="rail-spacer" />
        <button className="rail-source" onClick={() => setModal('source')}><span>任务采集 · {timeLabel(live.snapshot?.source.collectedAt)}</span><small>本机 · 台北时间</small></button>
      </aside>
      <main className="room-area" aria-label="研究室总览">
        <div className="agent-room-stage" onPointerMove={move} onPointerLeave={resetMove}>
          {!ready && <div className="scene-loading"><DiamondsFourIcon weight="duotone" />研究室正在打开…</div>}
          <div className="scene-overview"><div className="scene-totals"><span className="scene-running-total"><PlayCircleIcon weight="fill" /><span>{observed.running > 0 || !observed.partial ? <>已见 <strong>{observed.running}</strong> 个对话运行</> : '运行数量未知'}</span></span><button onClick={() => setModal('list')}><FolderSimpleIcon weight="fill" /><span>{health.stale && hasFrame ? '上次外部' : '外部任务'} <strong>{hasFrame ? summary.tasks : '—'}</strong></span></button><span className="scene-sleep-total"><PauseCircleIcon /><span>{observed.sleeping > 0 || !observed.partial ? <>已见 <strong>{observed.sleeping}</strong> 个休眠</> : '休眠数量未知'}</span></span>{observed.partial && <small>有些状态未知</small>}</div><div className="scene-header-agents">{SCENE_PINS.filter(pin => !pin.side).map(pin => <SceneAgentStatus key={pin.id} agent={agents.find(person => person.id === pin.id)} pin={pin} hasFrame={hasFrame} selected={selectedAgentId === pin.id} onSelect={chooseAgent} inline />)}</div></div>
          <div className="room-world" ref={world} style={{ '--zoom': zoom }}><img className="room-background" src="/assets/scene/room-background.png" alt="暖色书桌、书架和阅读角组成的共创研究室" onLoad={() => setReady(true)} />
            {ROOM_CAST.map((person, index) => {
              const status = agents.find(item => item.id === person.agentId);
              const Element = status ? 'button' : 'div';
              const activity = status ? sceneActivity(status.runtime, status.external) : 'neutral';
              const delivered = Boolean(status && deliveredAgents.includes(status.id));
              return <Element key={person.key} className={`character room-cast-member ${person.key} ${activity}${status?.botArt ? ' bot-character' : ''}${status && selectedAgentId === status.id ? ' chosen' : ''}${status ? '' : ' room-companion'}${delivered ? ' delivered' : ''}${person.cue[2] === 'left' ? ' cue-left' : ''}`} style={{ '--cast-x': person.x + '%', '--cue-x': person.cue[0] * 100 + '%', '--cue-y': person.cue[1] * 100 + '%', '--cast-bottom': person.bottom + '%', '--cast-height': person.height + '%', '--cast-ratio': person.ratio, '--cast-layer': person.layer, '--delay': index * -.7 + 's' }} {...(status ? { onClick: () => chooseAgent(status), 'aria-label': `查看 ${status.name}`, 'aria-pressed': selectedAgentId === status.id } : { title: `${person.name} · 研究室伙伴` })}><img className="character-art" src={`/assets/characters/${person.art || status.botArt}.png`} alt={status ? '' : `${person.name}，研究室伙伴`} draggable="false" />{activity === 'working' && <><span className="cast-work-glow" aria-hidden="true" /><span className="cast-typing" aria-hidden="true"><i /><i /><i /></span></>}{activity === 'sleeping' && <span className="cast-sleep" aria-hidden="true"><i>z</i><i>z</i><i>Z</i></span>}{delivered && <><span className="cast-sparkles" aria-hidden="true"><i>✦</i><i>✧</i><i>✦</i><i>✧</i></span><span className="cast-delivery" aria-hidden="true">✧ 刚交卷</span></>}</Element>;
            })}<div className="desk-occlusion" aria-hidden="true"><img src="/assets/scene/room-background.png" alt="" /></div><div className="table-occlusion" aria-hidden="true"><img src="/assets/scene/room-background.png" alt="" /></div>
          </div>
          <div className="scene-status-layer" aria-label="房间里的 Agent 状态" style={{ '--zoom': zoom }}>{SCENE_PINS.filter(pin => pin.side).map(pin => <SceneAgentStatus key={pin.id} agent={agents.find(person => person.id === pin.id)} pin={pin} hasFrame={hasFrame} selected={selectedAgentId === pin.id} onSelect={chooseAgent} />)}</div>
        </div>
          <div className="scene-tools"><button className="tool-button" onClick={() => { setMotion(!motion); resetMove(); }} aria-pressed={motion} title={motion ? '暂停场景动态' : '开启场景动态'}>{motion ? <PauseCircleIcon /> : <PlayCircleIcon />}<span>{motion ? '动态已开启' : '动态已暂停'}</span></button><div className="zoom-tools"><button aria-label="缩小场景" disabled={zoom <= 1} onClick={() => setZoom(value => Math.max(1, +(value - .1).toFixed(1)))}><MinusIcon /></button><button aria-label="重置场景缩放" onClick={() => setZoom(1)}>{Math.round(zoom * 100)}%</button><button aria-label="放大场景" disabled={zoom >= 1.3} onClick={() => setZoom(value => Math.min(1.3, +(value + .1).toFixed(1)))}><PlusIcon /></button></div><button className="tool-button" onClick={() => setModal('about')}><InfoIcon /><span>关于</span></button></div>
        <div className="agent-dock-heading"><h2>我的 Agent</h2><span>窗口 / 会话与外部任务分开看</span></div>
        {live.error && <div className="agent-source-note"><InfoIcon /><span>连接暂时断开，以下保留上次读到的状态。</span><button onClick={live.refresh}>重试</button></div>}
        <div className="agent-dock" aria-label="Agent 状态卡片">{agents.map(person => <button key={person.id} className={`agent-card ${selectedAgentId === person.id ? 'chosen' : ''}`} style={{ '--agent-color': person.color }} onClick={() => chooseAgent(person)} aria-label={`查看 ${person.name} 的窗口、会话与外部任务`} aria-pressed={selectedAgentId === person.id}>
          <div className="agent-card-heading"><span className={`agent-avatar ${person.botArt ? 'has-art bot-avatar' : person.art ? 'has-art' : ''}`}>{person.art || person.botArt ? <img src={`/assets/characters/${person.botArt || `${person.art}-chibi`}.png`} alt="" /> : <span>{person.mark}</span>}</span><strong>{person.name}</strong><CaretRightIcon /></div>
          <section className="agent-opened"><h3><MonitorIcon />你打开的<small>{person.runtime.sourceStatus === 'live' ? '已读取' : person.runtime.sourceStatus === 'partial' ? '部分可见' : '暂未读到'}</small></h3><OpenedStatus runtime={person.runtime} /></section>
          <section className="agent-external"><h3><FolderSimpleIcon />外部执行<small>后台任务</small></h3><ExternalStatus external={person.external} hasFrame={hasFrame} compact /></section>
        </button>)}</div>
        <div className="task-record-toolbar"><strong>{history ? '历史任务记录' : '当前任务记录'}</strong><button onClick={() => setModal('list')}>查看全部<CaretRightIcon /></button></div>
        <div className="status-summary" aria-label="任务记录筛选">{FILTERS.map(item => { const Icon = ICONS[item.id]; return <button key={item.id} className={item.id + (filter === item.id ? ' active' : '')} aria-pressed={filter === item.id} onClick={() => chooseFilter(item.id)}><Icon weight="fill" /><span>{item.label}</span><strong>{hasFrame ? totals[item.id] : '—'}</strong></button>; })}</div>
        {(project || filter || history) && <div className="filter-caption"><span>{history ? '历史' : '当前'} · {project || '全部项目'}{filter ? ' · ' + FILTERS.find(item => item.id === filter)?.label : ''} · {visibleRows.length} 条</span><button onClick={resetFilters}>清除筛选<XIcon /></button></div>}
        {otherRows.length > 0 && <button className="agent-unclassified" onClick={() => setModal('other')}><InfoIcon />身份未分类 · {otherRows.length} 条任务记录<CaretRightIcon /></button>}
      </main>
      <aside className="inspector" aria-label="选中 Agent 与任务详情">
        <div className={`portrait-panel ${portraitReady ? 'loaded' : ''}${agent?.art || agent?.botArt ? '' : ' unidentified'}${agent?.botArt ? ' bot-portrait' : ''}`} style={{ '--portrait-accent': agent?.color || '#9683bf' }}><div className="portrait-heading"><span className="portrait-eyebrow">我的 Agent</span><h2>{agent?.name || selected?.actor || '身份未分类'}</h2></div>{agent?.art || agent?.botArt ? <img key={agent.id} src={`/assets/characters/${agent.botArt || `${agent.art}-portrait`}.png`} alt={`${agent.name}角色立绘`} onLoad={() => setPortraitReady(true)} /> : <div className="neutral-portrait" style={{ '--agent-color': agent?.color }}><span>{agent?.mark || '?'}</span><small>{agent ? 'Agent' : '身份未分类'}</small></div>}</div>
        <div className="inspector-info">
          {agent && <><section className="inspector-runtime"><h3><MonitorIcon />你打开的</h3><OpenedStatus runtime={agent.runtime} detail /></section><section className="inspector-external"><h3><FolderSimpleIcon />外部执行</h3><ExternalStatus external={agent.external} hasFrame={hasFrame} /><p className="external-explanation">后台执行的任务会列在这里。</p></section></>}
          <section className="related-tasks"><h3>{history ? '历史任务' : '当前任务'}{project && <span> · {project}</span>}</h3><TaskList rows={agent ? related : otherRows} selectedId={selectedRowId} onSelect={chooseRow} empty={history ? '这个 Agent 在当前筛选下没有历史任务。' : '这个 Agent 在当前筛选下没有外部任务。'} /></section>
          {selected && <div className="selected-task-detail"><div className="project-fact"><span>项目</span><strong title={selected.projectFullName}><FolderSimpleIcon weight="fill" />{selected.project}</strong></div><h4 className="real-task-title">{selected.task}</h4><p className="actor-fact">{selected.actor}<Badge tone={selected.tone} small>{selected.label}</Badge></p>{selected.isCurrent && selected.runtimeState === 'running' && <ModelLine models={[selected.runtimeModel || '模型未知']} />}{health.stale && <p className="stale-detail">任务记录为上次采集的情况</p>}<dl className="state-facts">{stateRows(selected).map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}<div><dt>上次活动</dt><dd title={timeLabel(selected.observedActivityAt)}>{agoLabel(selected.observedActivityAt, now)}</dd></div></dl><p className="task-caption">{selected.basis}</p><button className="detail-button" onClick={() => setModal('task')}><FileTextIcon />查看任务详情<CaretRightIcon /></button><span className="inspector-footer">{selected.isCurrent ? QUALITY[selected.quality] || selected.quality : '历史记录'}</span></div>}
          {selectedRowId && !selected && <p className="list-empty">这条任务记录刚刚离开列表，可选另一条查看。</p>}
        </div>
      </aside>
    </div>
    {modal && <div className="modal-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) setModal(null); }}><section className={`modal ${['list', 'other'].includes(modal) ? 'wide-modal' : ''}`} role="dialog" aria-modal="true" aria-labelledby="modal-title"><button ref={close} className="modal-close" aria-label="关闭弹窗" onClick={() => setModal(null)}><XIcon /></button>
      {modal === 'task' && selected && <><div className="modal-eyebrow">{selected.isCurrent ? health.stale ? '上次采集' : '当前任务' : '历史记录'} · {selected.actor}</div><h2 id="modal-title">{selected.task}</h2><Badge tone={selected.tone}>{selected.label}</Badge><p className="modal-summary">{selected.basis}</p><dl className="observation-facts"><div><dt>项目</dt><dd>{selected.project}</dd></div><div><dt>Agent</dt><dd>{selected.actor}</dd></div>{selected.isCurrent && selected.runtimeState === 'running' && <div><dt>当前模型</dt><dd>{selected.runtimeModel || '模型未知'}</dd></div>}{stateRows(selected).map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}<div><dt>上次活动</dt><dd>{timeLabel(selected.observedActivityAt)}</dd></div></dl>{selected.artifacts.length > 0 && <details className="artifact-details"><summary>相关文件（{selected.artifacts.length} 个）</summary><div className="artifact-labels">{selected.artifacts.map((artifact, index) => <span key={index}><FileTextIcon />{artifact.label}</span>)}</div></details>}<p className="modal-footnote">{health.stale ? '这是上次采集的任务记录。' : '任务记录自动刷新。'}交卷与负责人检查分别列出。</p></>}
      {modal === 'task' && !selected && <><h2 id="modal-title">这条任务记录已离开列表</h2><p className="modal-summary">关掉详情，选择其他任务记录即可。</p></>}
      {(modal === 'list' || modal === 'other') && <><div className="modal-eyebrow">{history ? '历史记录' : '当前任务'} · {project || '全部项目'}</div><h2 id="modal-title">{modal === 'other' ? '身份未分类的任务' : '外部任务记录'}</h2><p className="modal-summary">{health.stale ? health.description : '选择任务，查看执行、交卷与负责人检查的记录。'}</p><TaskList rows={modal === 'other' ? otherRows : visibleRows} selectedId={selectedRowId} onSelect={row => { chooseRow(row); setModal(null); }} /><p className="modal-footnote">卡片上的窗口和会话来自本机 Agent 状态；这些任务记录来自 CLI Watch。</p></>}
      {modal === 'source' && <><div className="modal-eyebrow">本机只读状态</div><h2 id="modal-title">数据从哪来</h2><p className="modal-summary">{health.description}</p><dl className="observation-facts"><div><dt>你打开的</dt><dd>各 Agent 可读的窗口与会话状态，读取范围见 Agent 详情。</dd></div><div><dt>外部执行</dt><dd>CLI Watch 的派单与交卷记录；有活动不等于正在生成。</dd></div><div><dt>任务采集</dt><dd>{timeLabel(live.snapshot?.source.collectedAt)}（台北）</dd></div><div><dt>本机状态</dt><dd>{timeLabel(live.snapshot?.agentRuntime?.observedAt)}（台北）</dd></div><div><dt>任务记录</dt><dd>{hasFrame ? `${summary.tasks} 个当前外部任务` : '正在读取'}；{summary.unbound} 条未关联进程记录</dd></div><div><dt>记录提示</dt><dd>当前 {currentIssues.length} 条，历史 {historicalIssues.length} 条</dd></div></dl>{currentIssues.length > 0 && <details className="source-issues"><summary>查看采集说明</summary>{[...new Set(currentIssues.map(issue => issue.code))].map(code => <p key={code}><InfoIcon />{ISSUE_LABELS[code] || code}</p>)}</details>}<button className="detail-button" onClick={live.refresh}><ArrowClockwiseIcon />马上刷新</button><p className="modal-footnote">这里只展示状态。私人聊天的内容和主题不会出现在页面里。</p></>}
      {modal === 'about' && <><div className="modal-eyebrow">我的本机共创空间</div><h2 id="modal-title">AI娘研究室</h2><p className="modal-summary">每个 Agent 一张卡，点开就能分别看你打开的会话和后台外部任务。</p><p>运行表示正在生成或跑工具，等待输入算休眠。只有明确运行时才展示当前模型；读不到的数量会写“未知”。</p><p>项目与历史筛选用于任务记录，Agent 卡片始终展示当前状态。原来的“本机 CLI 执行窗”继续保留。</p><button className="detail-button" onClick={() => setModal(null)}>返回研究室<CaretRightIcon /></button></>}
    </section></div>}
  </div>;
}
