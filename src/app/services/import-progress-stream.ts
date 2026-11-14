import { InjectionToken } from '@angular/core';
import { type ImportProgressEvent, type ImportSettledEvent, IMPORT_EVENTS_URL, parseProgressEvent,
  parseSettledEvent } from './exam-import-client';

/** `polling`: live updates failed and the caller refreshes every {@link POLL_INTERVAL_MS}. */
export type StreamConnection = 'connecting' | 'live' | 'reconnecting' | 'polling' | 'closed';

export const POLL_INTERVAL_MS = 5_000;
/** Without any event (pings arrive every 15 s) the stream is considered dead. */
export const WATCHDOG_MS = 45_000;
export const ERROR_WINDOW_MS = 30_000;
export const MAX_ERRORS = 3;

/** The parts of EventSource the watcher uses, so tests can provide a fake. */
export interface EventSourceLike {
  readonly readyState: number;
  onopen: ((event: Event) => void) | null;
  onerror: ((event: Event) => void) | null;
  addEventListener(type: string, listener: (event: MessageEvent) => void): void;
  close(): void;
}

export interface ProgressStreamDeps {
  createEventSource(url: string): EventSourceLike | null;
  setTimer(callback: () => void, ms: number): unknown;
  clearTimer(handle: unknown): void;
  now(): number;
}

export interface ProgressStreamHandlers {
  onEvent(event: ImportProgressEvent): void;
  onSettled(event: ImportSettledEvent): void;
  onConnection(state: StreamConnection): void;
  /** Called every {@link POLL_INTERVAL_MS} while live updates are unavailable. */
  onPoll(): void;
}

export interface ProgressStreamHandle {
  /** Leaves polling mode and opens a new live connection. */
  reconnectLive(): void;
  dispose(): void;
}

const CLOSED = 2;

export const PROGRESS_STREAM_DEPS = new InjectionToken<ProgressStreamDeps>('PROGRESS_STREAM_DEPS', {
  providedIn: 'root',
  factory: () => ({
    // A browser without EventSource falls back to polling.
    createEventSource: (url) => typeof EventSource === 'undefined' ? null : new EventSource(url),
    setTimer: (callback, ms) => setTimeout(callback, ms),
    clearTimer: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
    now: () => Date.now(),
  }),
});

/**
 * Watches the list-level progress stream. Reconnects are left to EventSource; repeated errors, a closed stream
 * (401/403/404/503 or a redirect) or a silent connection switch to polling until {@link ProgressStreamHandle.reconnectLive}.
 */
export function watchImportProgress(handlers: ProgressStreamHandlers, deps: ProgressStreamDeps): ProgressStreamHandle {
  let source: EventSourceLike | null = null;
  let watchdog: unknown = null;
  let poller: unknown = null;
  let errors: number[] = [];
  let lastActivity = 0;
  let state: StreamConnection | null = null;
  let disposed = false;

  const set = (next: StreamConnection) => {
    if (state === next) return;
    state = next;
    handlers.onConnection(next);
  };

  const stopTimers = () => {
    if (watchdog !== null) deps.clearTimer(watchdog);
    if (poller !== null) deps.clearTimer(poller);
    watchdog = poller = null;
  };

  const closeSource = () => {
    if (!source) return;
    source.onopen = source.onerror = null;
    source.close();
    source = null;
  };

  const poll = () => {
    poller = deps.setTimer(() => {
      if (disposed || state !== 'polling') return;
      handlers.onPoll();
      poll();
    }, POLL_INTERVAL_MS);
  };

  const fallBack = () => {
    closeSource();
    stopTimers();
    set('polling');
    poll();
  };

  const armWatchdog = () => {
    if (watchdog !== null) deps.clearTimer(watchdog);
    watchdog = deps.setTimer(() => {
      watchdog = null;
      if (disposed || !source) return;
      if (deps.now() - lastActivity >= WATCHDOG_MS) fallBack();
      else armWatchdog();
    }, WATCHDOG_MS);
  };

  const activity = () => {
    lastActivity = deps.now();
    errors = [];
    set('live');
  };

  const listen = <T>(name: string, parse: (data: unknown) => T | null, handle: (value: T) => void) => {
    source!.addEventListener(name, (event: MessageEvent) => {
      if (disposed) return;
      activity();
      const value = parse(event.data);
      if (value) handle(value);
    });
  };

  const connect = () => {
    stopTimers();
    closeSource();
    errors = [];
    set(state === null ? 'connecting' : 'reconnecting');
    source = deps.createEventSource(IMPORT_EVENTS_URL);
    if (!source) {
      fallBack();
      return;
    }
    lastActivity = deps.now();
    // Only messages reset the error count: an endpoint that accepts and closes at once must still fall back.
    source.onopen = () => { if (!disposed) set('live'); };
    source.onerror = () => {
      if (disposed || !source) return;
      const now = deps.now();
      errors = [...errors.filter((time) => now - time < ERROR_WINDOW_MS), now];
      if (source.readyState === CLOSED || errors.length >= MAX_ERRORS) fallBack();
      else set('reconnecting');
    };
    listen('progress', parseProgressEvent, handlers.onEvent);
    listen('settled', parseSettledEvent, handlers.onSettled);
    listen('ping', () => null, () => undefined);
    armWatchdog();
  };

  connect();
  return {
    reconnectLive: () => {
      if (!disposed) connect();
    },
    dispose: () => {
      if (disposed) return;
      disposed = true;
      stopTimers();
      closeSource();
      set('closed');
    },
  };
}
