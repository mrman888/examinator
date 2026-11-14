import type { EventSourceLike, ProgressStreamDeps } from './import-progress-stream';

/** A scripted EventSource for specs; nothing touches the network. */
export class FakeEventSource implements EventSourceLike {
  readyState = 0;
  onopen: ((event: Event) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  closed = false;
  private readonly listeners = new Map<string, ((event: MessageEvent) => void)[]>();

  constructor(readonly url: string) {}

  addEventListener(type: string, listener: (event: MessageEvent) => void): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  close(): void {
    this.closed = true;
    this.readyState = 2;
  }

  open(): void {
    this.readyState = 1;
    this.onopen?.(new Event('open'));
  }

  emit(type: string, data: unknown): void {
    const event = new MessageEvent(type, { data: typeof data === 'string' ? data : JSON.stringify(data) });
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }

  fail(closed = false): void {
    this.readyState = closed ? 2 : 0;
    this.onerror?.(new Event('error'));
  }
}

/** Manual clock and timers for the progress stream watcher. */
export function fakeStreamDeps() {
  const sources: FakeEventSource[] = [];
  const timers = new Map<number, { at: number; callback: () => void }>();
  let now = 0;
  let nextId = 0;
  const deps: ProgressStreamDeps = {
    createEventSource: (url) => {
      const source = new FakeEventSource(url);
      sources.push(source);
      return source;
    },
    setTimer: (callback, ms) => {
      timers.set(++nextId, { at: now + ms, callback });
      return nextId;
    },
    clearTimer: (handle) => { timers.delete(handle as number); },
    now: () => now,
  };
  return {
    deps,
    sources,
    latest: () => sources[sources.length - 1],
    advance(ms: number): void {
      const target = now + ms;
      for (;;) {
        const due = [...timers.entries()].filter(([, timer]) => timer.at <= target).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        timers.delete(due[0]);
        now = due[1].at;
        due[1].callback();
      }
      now = target;
    },
  };
}
