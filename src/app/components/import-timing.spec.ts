import { describe, expect, it } from 'vitest';
import type { ImportRunTiming, ImportTiming } from '../services/exam-import-client';
import { combineTiming, elapsed, formatClock, formatDateTime, formatDuration, isRunning, leadTime, processedText,
  processingTime, runViews, stepLabel } from './import-timing';

const at = (iso: string) => Date.parse(iso);

const finished: ImportRunTiming = {
  number: 1, attempt: 1, startedAt: '2026-11-14T14:00:00Z', endedAt: '2026-11-14T14:00:30Z', outcome: 'awaitingDefinitions',
  phases: [
    { phase: 'starting', startedAt: '2026-11-14T14:00:00Z', endedAt: '2026-11-14T14:00:02Z', message: 'Verwerking gestart (poging 1)',
      step: null, total: null, modelCalls: 0, outcome: 'ok', error: null },
    { phase: 'definitions', startedAt: '2026-11-14T14:00:02Z', endedAt: '2026-11-14T14:00:30Z', message: 'Hoofdstukdefinities voorstellen',
      step: null, total: null, modelCalls: 1, outcome: 'ok', error: null },
  ],
};

const running: ImportRunTiming = {
  number: 2, attempt: 2, startedAt: '2026-11-14T15:00:00Z', endedAt: null, outcome: null,
  phases: [
    { phase: 'tagging', startedAt: '2026-11-14T15:00:00Z', endedAt: null, message: 'Vraag 3 van 9: onderwerpen toekennen',
      step: 3, total: 9, modelCalls: 2, outcome: null, error: null },
  ],
};

const live: ImportTiming = {
  processingStartedAt: '2026-11-14T14:00:00Z', completedAt: null, outcome: null, processingMilliseconds: null,
  earlierProcessingMilliseconds: 30_000, earlierRunsUnknown: false, runs: [running],
};

describe('import timing', () => {
  it('formats durations in Dutch seconds, minutes and hours', () => {
    expect(formatDuration(0)).toBe('0 s');
    expect(formatDuration(12_900)).toBe('12 s');
    expect(formatDuration(60_000)).toBe('1 min');
    expect(formatDuration(220_000)).toBe('3 min 40 s');
    expect(formatDuration(372_000)).toBe('6 min 12 s');
    expect(formatDuration(3_600_000)).toBe('1 u');
    expect(formatDuration(3_900_000)).toBe('1 u 5 min');
    expect(formatDuration(-5)).toBe('0 s');
    expect(formatDuration(null)).toBe('onbekend');
    expect(formatDuration(Number.NaN)).toBe('onbekend');
  });

  it('formats dates and clock times in nl-NL local time', () => {
    const local = new Date(2026, 10, 14, 15, 2, 7).toISOString();
    expect(formatDateTime(local)).toBe('14-11-2026 15:02');
    expect(formatClock(local)).toBe('15:02:07');
    expect(formatDateTime(undefined)).toBe('onbekend');
    expect(formatClock('geen datum')).toBe('onbekend');
  });

  it('measures finished steps to their end and running steps to now', () => {
    expect(elapsed(finished.phases[1], 0)).toBe(28_000);
    expect(elapsed(running.phases[0], at('2026-11-14T15:01:05Z'))).toBe(65_000);
    expect(elapsed(running.phases[0], at('2026-11-14T14:00:00Z'))).toBe(0);
  });

  it('adds the live current run to the earlier runs and prefers the server total once complete', () => {
    expect(isRunning(live)).toBe(true);
    expect(processingTime(live, at('2026-11-14T15:01:00Z'))).toBe(90_000);
    const done: ImportTiming = { ...live, completedAt: '2026-11-14T15:02:00Z', processingMilliseconds: 150_000,
      runs: [{ ...running, endedAt: '2026-11-14T15:02:00Z', outcome: 'review' }] };
    expect(isRunning(done)).toBe(false);
    expect(processingTime(done, at('2026-11-14T18:00:00Z'))).toBe(150_000);
    expect(processingTime({ ...live, earlierProcessingMilliseconds: null }, 0)).toBeNull();
    expect(processingTime(null, 0)).toBeNull();
    expect(leadTime('2026-11-14T13:59:00Z', done)).toBe(3_780_000);
    expect(leadTime('2026-11-14T13:59:00Z', live)).toBeNull();
  });

  it('merges the live current run into the runs of the record by number', () => {
    const record: ImportTiming = { ...live, runs: [finished, { ...running, phases: [] }] };
    const merged = combineTiming(record, live)!;
    expect(merged.runs.map((run) => run.number)).toEqual([1, 2]);
    expect(merged.runs[1].phases).toHaveLength(1);
    expect(combineTiming(record, null)).toBe(record);
    expect(combineTiming(null, null)).toBeNull();
  });

  it('builds run views newest first with step details and outcomes', () => {
    const failed: ImportRunTiming = { ...running, endedAt: '2026-11-14T15:00:12Z', outcome: 'failed',
      phases: [{ ...running.phases[0], endedAt: '2026-11-14T15:00:12Z', outcome: 'failed', error: 'Foundry gaf HTTP 500 terug.' }] };
    const views = runViews({ ...live, runs: [finished, failed] }, 0);
    expect(views.map((view) => view.number)).toEqual([2, 1]);
    expect(views[0].heading).toBe(`Poging 2 · ${formatDateTime(failed.startedAt)} · 12 s · Mislukt`);
    expect(views[0].failed).toBe(true);
    expect(views[0].steps[0]).toMatchObject({ label: 'Onderwerpen toekennen', duration: '12 s', modelCalls: 2,
      details: 'Vraag 3 van 9: onderwerpen toekennen', outcome: 'Mislukt', failed: true, error: 'Foundry gaf HTTP 500 terug.' });
    expect(views[1].heading).toContain('30 s · Wacht op bevestiging van onderwerpen');
    expect(views[1].steps.map((step) => step.label)).toEqual(['Starten', 'Hoofdstukdefinities voorstellen']);
    expect(stepLabel('throttled')).toBe('Wachten op modelcapaciteit');
    expect(stepLabel('nieuw')).toBe('nieuw');
  });

  it('describes the processing time for list rows', () => {
    const done: ImportTiming = { ...live, completedAt: '2026-11-14T15:02:00Z', processingMilliseconds: 372_000, runs: [] };
    expect(processedText('review', done)).toBe('Verwerkt in 6 min 12 s');
    expect(processedText('tagging', live)).toBeNull();
    expect(processedText('published', null)).toBe('Verwerkingstijd onbekend');
    expect(processedText('review', { ...done, processingMilliseconds: null, earlierRunsUnknown: true })).toBe('Verwerkingstijd onbekend');
    expect(processedText('queued', null)).toBeNull();
  });
});
