import { describe, expect, it, vi } from 'vitest';
import type { ImportProgressEvent } from './exam-import-client';
import { fakeStreamDeps } from './import-progress-fakes';
import { type StreamConnection, ERROR_WINDOW_MS, POLL_INTERVAL_MS, WATCHDOG_MS, watchImportProgress } from './import-progress-stream';

const progress: ImportProgressEvent = {
  importId: 'import-1', state: 'tagging', revision: '"v3"', phase: 'tagging', step: 7, total: 32, unit: 'vraag',
  modelCalls: 12, modelCallBudget: 64, message: 'Vraag 7 van 32: onderwerpen toekennen',
  updatedAt: '2026-11-14T13:32:05Z', heartbeatAt: '2026-11-14T13:32:05Z', serverTime: '2026-11-14T13:32:06Z', stale: false,
};

function watch() {
  const fake = fakeStreamDeps();
  const states: StreamConnection[] = [];
  const handlers = {
    onEvent: vi.fn(), onSettled: vi.fn(), onPoll: vi.fn(),
    onConnection: (state: StreamConnection) => { states.push(state); },
  };
  const handle = watchImportProgress(handlers, fake.deps);
  return { fake, states, handlers, handle };
}

describe('import progress stream', () => {
  it('opens one EventSource on the events URL and delivers valid progress and settled events', () => {
    const { fake, states, handlers } = watch();
    expect(fake.sources).toHaveLength(1);
    expect(fake.latest().url).toBe('/api/imports/events');
    expect(states).toEqual(['connecting']);
    fake.latest().open();
    fake.latest().emit('progress', progress);
    fake.latest().emit('settled', { importId: 'import-1', state: 'review', revision: '"v4"' });
    expect(states).toEqual(['connecting', 'live']);
    expect(handlers.onEvent).toHaveBeenCalledWith(progress);
    expect(handlers.onSettled).toHaveBeenCalledWith({ importId: 'import-1', state: 'review', revision: '"v4"' });
  });

  it('ignores invalid payloads', () => {
    const { fake, handlers } = watch();
    fake.latest().emit('progress', 'not json');
    fake.latest().emit('progress', { ...progress, phase: 'unknown' });
    fake.latest().emit('progress', { ...progress, step: -1 });
    fake.latest().emit('settled', { importId: 'import-1', state: 'weird', revision: null });
    expect(handlers.onEvent).not.toHaveBeenCalled();
    expect(handlers.onSettled).not.toHaveBeenCalled();
  });

  it('shows reconnecting while EventSource retries and goes live again on a message', () => {
    const { fake, states } = watch();
    fake.latest().open();
    fake.latest().fail();
    expect(states.at(-1)).toBe('reconnecting');
    fake.latest().emit('ping', { serverTime: 'x' });
    expect(states.at(-1)).toBe('live');
    expect(fake.latest().closed).toBe(false);
  });

  it('falls back to polling when the server closes the stream (401, 403, 404, 503 or a redirect)', () => {
    const { fake, states, handlers } = watch();
    fake.latest().fail(true);
    expect(states.at(-1)).toBe('polling');
    expect(fake.latest().closed).toBe(true);
    fake.advance(POLL_INTERVAL_MS);
    fake.advance(POLL_INTERVAL_MS);
    expect(handlers.onPoll).toHaveBeenCalledTimes(2);
  });

  it('falls back to polling after three errors without a message within 30 seconds', () => {
    const { fake, states } = watch();
    fake.latest().fail();
    fake.advance(1_000);
    fake.latest().open();
    fake.latest().fail();
    expect(states.at(-1)).toBe('reconnecting');
    fake.advance(1_000);
    fake.latest().fail();
    expect(states.at(-1)).toBe('polling');
  });

  it('forgets errors older than the window', () => {
    const { fake, states } = watch();
    fake.latest().fail();
    fake.latest().fail();
    fake.advance(ERROR_WINDOW_MS + 1);
    fake.latest().fail();
    expect(states.at(-1)).toBe('reconnecting');
  });

  it('falls back to polling when nothing arrives for 45 seconds', () => {
    const { fake, states } = watch();
    fake.latest().open();
    fake.advance(30_000);
    fake.latest().emit('ping', {});
    fake.advance(30_000);
    expect(states.at(-1)).toBe('live');
    fake.advance(WATCHDOG_MS);
    expect(states.at(-1)).toBe('polling');
    expect(fake.latest().closed).toBe(true);
  });

  it('reconnects live on request and stops polling', () => {
    const { fake, states, handle, handlers } = watch();
    fake.latest().fail(true);
    handle.reconnectLive();
    expect(fake.sources).toHaveLength(2);
    expect(states.at(-1)).toBe('reconnecting');
    fake.advance(POLL_INTERVAL_MS * 2);
    expect(handlers.onPoll).not.toHaveBeenCalled();
  });

  it('closes the connection and all timers on dispose', () => {
    const { fake, states, handle, handlers } = watch();
    handle.dispose();
    expect(fake.latest().closed).toBe(true);
    expect(states.at(-1)).toBe('closed');
    fake.latest().emit('progress', progress);
    fake.advance(WATCHDOG_MS * 2);
    expect(handlers.onEvent).not.toHaveBeenCalled();
    expect(states.at(-1)).toBe('closed');
  });

  it('polls when the browser has no EventSource', () => {
    const fake = fakeStreamDeps();
    const onPoll = vi.fn();
    const states: StreamConnection[] = [];
    watchImportProgress({ onEvent: vi.fn(), onSettled: vi.fn(), onPoll, onConnection: (s) => { states.push(s); } },
      { ...fake.deps, createEventSource: () => null });
    expect(states.at(-1)).toBe('polling');
    fake.advance(POLL_INTERVAL_MS);
    expect(onPoll).toHaveBeenCalledOnce();
  });
});
