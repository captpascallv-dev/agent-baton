import { StatusState, HEARTBEAT_MS } from './state.mjs';
import { createPublisher } from './publisher.mjs';

export const name = 'studio-status-observer';
export const inject = ['agents'];

// Only public Cordis lifecycle APIs are used. No Agent is created/resumed.
export function attachObserver(ctx, { state = new StatusState(), publish, heartbeatMs = HEARTBEAT_MS } = {}) {
  let stopped = false, dirty = false, draining = null, timer;
  const disposers = [];
  const signalCleanups = new Set();
  const safe = callback => { try { return callback(); } catch { state.faulted = true; } };
  const current = () => safe(() => state.reconcile(ctx.agents.list()));
  async function flush() {
    dirty = true;
    if (draining) return draining;
    draining = (async () => {
      while (dirty && !stopped) {
        dirty = false; current();
        try { await publish(state.snapshot()); } catch { /* Old output expires; never fail a business event. */ }
      }
    })();
    try { await draining; } finally { draining = null; }
  }
  const changed = callback => { safe(callback); void flush(); };
  const on = (event, callback, options = {}) => disposers.push(ctx.on(event, callback, { global: true, ...options }));
  on('agent/created', ({ agent }) => { changed(() => state.ensure(agent)); });
  on('agent/disposed', ({ agent }) => { changed(() => state.remove(agent)); });
  on('agent/status', ({ agent, status }) => { changed(() => state.status(agent, status)); });
  on('agent/assistant-stream', ({ agent, frame }) => {
    if (frame.type === 'start' || frame.type === 'end') changed(() => state.stream(agent, frame));
  });
  function around(kind, callId) {
    return async (request, next) => {
      const done = safe(() => request.agent ? state.begin(request.agent, kind, callId(request),
        kind === 'tool' ? request.rootCallId : undefined) : undefined);
      // ApprovalService may finish its abort race before an answerer's next()
      // settles. End the observed wait on that same signal without answering it.
      const signal = kind === 'tool' ? undefined : safe(() => request.signal);
      const releaseWait = () => { safe(() => done?.()); void flush(); };
      if (signal?.aborted) releaseWait();
      else signal?.addEventListener('abort', releaseWait, { once: true });
      const cleanupSignal = () => signal?.removeEventListener('abort', releaseWait);
      if (signal) signalCleanups.add(cleanupSignal);
      void flush();
      try { return await next(); }
      finally { cleanupSignal(); signalCleanups.delete(cleanupSignal); safe(() => done?.()); void flush(); }
    };
  }
  on('tools/execute', around('tool', request => request.callId), { prepend: true });
  on('approval/request', around('approval', request => request.callId), { prepend: true });
  on('user-questions/request', around('input', request => request.wait?.callId), { prepend: true });
  function schedule() {
    if (stopped) return;
    timer = setTimeout(async () => { await flush(); schedule(); }, heartbeatMs);
    timer.unref?.();
  }
  const ready = flush(); schedule();
  return { state, ready, flush, async stop() {
    if (stopped) return;
    stopped = true; clearTimeout(timer);
    for (const dispose of disposers) dispose();
    for (const cleanup of signalCleanups) cleanup();
    signalCleanups.clear();
    await draining;
    try { await publish(state.snapshot(false)); } catch { /* Crash/failed final output still expires. */ }
  } };
}

export async function apply(ctx, config) {
  if (typeof config?.outputDirectory !== 'string' || Object.keys(config).some(key => key !== 'outputDirectory'))
    throw new Error('Only outputDirectory is accepted');
  const state = new StatusState();
  const publisher = await createPublisher(config.outputDirectory, state.producerId);
  const observer = attachObserver(ctx, { state, publish: frame => publisher.publish(frame) });
  ctx.effect(() => () => observer.stop(), 'studio-status-observer: lifecycle');
  await observer.ready;
}
