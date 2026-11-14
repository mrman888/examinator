import { DOCUMENT } from '@angular/common';
import { DestroyRef, type Signal, effect, inject, signal } from '@angular/core';

export const LIVE_CLOCK_INTERVAL_MS = 1_000;

/**
 * The current time as a signal that ticks every second while `active()` is true and the tab is visible. The timer
 * stops when `active()` turns false, on a hidden tab and on destroy. Call it in an injection context.
 */
export function liveClock(active: () => boolean): Signal<number> {
  const document = inject(DOCUMENT);
  const now = signal(Date.now());
  const visible = signal(document.visibilityState !== 'hidden');
  const onVisibility = () => visible.set(document.visibilityState !== 'hidden');
  document.addEventListener('visibilitychange', onVisibility);
  inject(DestroyRef).onDestroy(() => document.removeEventListener('visibilitychange', onVisibility));
  // A browser timer is an imperative side effect; the effect's cleanup clears it.
  effect((onCleanup) => {
    if (!active() || !visible()) return;
    now.set(Date.now());
    const handle = setInterval(() => now.set(Date.now()), LIVE_CLOCK_INTERVAL_MS);
    onCleanup(() => clearInterval(handle));
  });
  return now.asReadonly();
}
