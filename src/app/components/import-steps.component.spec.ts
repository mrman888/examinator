import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ImportRunTiming, ImportTiming } from '../services/exam-import-client';
import { ImportStepsComponent } from './import-steps.component';

const earlier: ImportRunTiming = {
  number: 1, attempt: 1, startedAt: '2026-11-14T14:00:00Z', endedAt: '2026-11-14T14:00:30Z', outcome: 'failed',
  phases: [
    { phase: 'starting', startedAt: '2026-11-14T14:00:00Z', endedAt: '2026-11-14T14:00:02Z', message: 'Verwerking gestart (poging 1)',
      step: null, total: null, modelCalls: 0, outcome: 'ok', error: null },
    { phase: 'inventory', startedAt: '2026-11-14T14:00:02Z', endedAt: '2026-11-14T14:00:30Z', message: 'Vragen inventariseren in het examen',
      step: 1, total: 2, modelCalls: 1, outcome: 'failed', error: 'Foundry gaf HTTP 500 terug voor de importaanvraag.' },
  ],
};

const current: ImportRunTiming = {
  number: 2, attempt: 1, startedAt: '2026-11-14T15:00:00Z', endedAt: null, outcome: null,
  phases: [
    { phase: 'starting', startedAt: '2026-11-14T15:00:00Z', endedAt: '2026-11-14T15:00:01Z', message: 'Verwerking gestart (poging 1)',
      step: null, total: null, modelCalls: 0, outcome: 'ok', error: null },
    { phase: 'tagging', startedAt: '2026-11-14T15:00:01Z', endedAt: null, message: 'Vraag 3 van 9: onderwerpen toekennen',
      step: 3, total: 9, modelCalls: 3, outcome: null, error: null },
  ],
};

const live: ImportTiming = {
  processingStartedAt: earlier.startedAt, completedAt: null, outcome: null, processingMilliseconds: null,
  earlierProcessingMilliseconds: 30_000, earlierRunsUnknown: false, runs: [earlier, current],
};

const done: ImportTiming = {
  ...live, completedAt: '2026-11-14T15:05:42Z', outcome: 'review', processingMilliseconds: 372_000,
  runs: [earlier, { ...current, endedAt: '2026-11-14T15:05:42Z', outcome: 'review',
    phases: [current.phases[0], { ...current.phases[1], endedAt: '2026-11-14T15:05:42Z', outcome: 'ok' }] }],
};

function setVisibility(state: DocumentVisibilityState) {
  Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
  document.dispatchEvent(new Event('visibilitychange'));
}

describe('ImportStepsComponent', () => {
  afterEach(() => {
    vi.useRealTimers();
    setVisibility('visible');
    TestBed.resetTestingModule();
  });

  async function render(timing: ImportTiming | null, isLive = false, createdAt = '2026-11-14T13:59:00Z') {
    const fixture = TestBed.createComponent(ImportStepsComponent);
    fixture.componentRef.setInput('timing', timing);
    fixture.componentRef.setInput('createdAt', createdAt);
    fixture.componentRef.setInput('live', isLive);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  const text = (root: HTMLElement) => root.textContent!.replace(/\s+/g, ' ');

  it('says when an import has no step history', async () => {
    const { root } = await render(null);
    expect(text(root)).toContain('Verwerkingsstappen');
    expect(text(root)).toContain('Geen stapgeschiedenis beschikbaar voor deze import');
    expect(root.querySelector('table')).toBeNull();
  });

  it('shows every run with the latest expanded, the failed step highlighted and the totals', async () => {
    const { root } = await render(done);
    const runs = [...root.querySelectorAll<HTMLDetailsElement>('details.run')];
    expect(runs.map((run) => run.open)).toEqual([true, false]);
    expect(runs[0].querySelector('summary')!.textContent).toContain('Poging 2');
    expect(runs[0].querySelector('summary')!.textContent).toContain('5 min 42 s · Geslaagd');
    expect(runs[1].querySelector('summary')!.textContent).toContain('Poging 1');
    expect(runs[1].querySelector('summary')!.textContent).toContain('30 s · Mislukt');
    const failed = runs[1].querySelector('tr.failed')!;
    expect(failed.querySelector('th')!.textContent).toBe('Vragen inventariseren');
    expect(failed.textContent).toContain('Fout: Foundry gaf HTTP 500 terug voor de importaanvraag.');
    expect(failed.textContent).toContain('28 s');
    const headers = [...runs[0].querySelectorAll('thead th')].map((cell) => cell.textContent);
    expect(headers).toEqual(['Stap', 'Start', 'Duur', 'Details', 'Modelaanroepen', 'Resultaat']);
    const tagging = runs[0].querySelectorAll('tbody tr')[1];
    expect(tagging.textContent).toContain('Onderwerpen toekennen');
    expect(tagging.textContent).toContain('5 min 41 s');
    expect(text(root)).toContain('Totale verwerkingstijd: 6 min 12 s');
    expect(text(root)).toContain('Doorlooptijd sinds upload: 1 u 6 min');
    expect(root.querySelector('[aria-live]')).toBeNull();
  });

  it('ticks the running step every second and stops when the import settles', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });
    vi.setSystemTime(new Date('2026-11-14T15:01:01Z'));
    const { fixture, root } = await render(live, true);
    expect(text(root)).toContain('Verwerkingstijd tot nu toe: 1 min 31 s');
    expect(root.querySelector('tr.running')!.textContent).toContain('1 min');
    expect(vi.getTimerCount()).toBe(1);

    vi.advanceTimersByTime(3_000);
    await fixture.whenStable();
    expect(text(root)).toContain('Verwerkingstijd tot nu toe: 1 min 34 s');
    expect(root.querySelector('tr.running')!.textContent).toContain('1 min 3 s');

    fixture.componentRef.setInput('live', false);
    fixture.componentRef.setInput('timing', done);
    await fixture.whenStable();
    expect(vi.getTimerCount()).toBe(0);
    expect(text(root)).toContain('Totale verwerkingstijd: 6 min 12 s');
  });

  it('does not tick on a hidden tab and clears the timer on destroy', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });
    vi.setSystemTime(new Date('2026-11-14T15:01:01Z'));
    const { fixture } = await render(live, true);
    expect(vi.getTimerCount()).toBe(1);
    setVisibility('hidden');
    await fixture.whenStable();
    expect(vi.getTimerCount()).toBe(0);
    setVisibility('visible');
    await fixture.whenStable();
    expect(vi.getTimerCount()).toBe(1);
    fixture.destroy();
    expect(vi.getTimerCount()).toBe(0);
  });
});
