import { describe, expect, it } from 'vitest';
import type { ImportProgressEvent } from '../services/exam-import-client';
import { announcement, clockTime, connectionText, modelCallText, percent, phaseIndex, phaseLabel, progressSteps,
  stepText } from './import-progress-logic';

const base: ImportProgressEvent = {
  importId: 'import-1', state: 'tagging', revision: '"v1"', phase: 'tagging', step: 7, total: 32, unit: 'vraag',
  modelCalls: 12, modelCallBudget: 64, message: 'Vraag 7 van 32: onderwerpen toekennen',
  updatedAt: '2026-11-14T13:32:05Z', heartbeatAt: '2026-11-14T13:32:05Z', serverTime: '2026-11-14T13:32:06Z', stale: false,
};

describe('import progress logic', () => {
  it('only shows the chapter step for imports with chapter context', () => {
    expect(progressSteps(false).map((step) => step.label)).toEqual(
      ['Wachtrij', 'Uitlezen', "Pagina's en inventaris", 'Koppelen', 'Onderwerpen toekennen', 'Beoordelen']);
    expect(progressSteps(true).map((step) => step.key)).toContain('definitions');
  });

  it('maps every phase to a stepper position', () => {
    const at = (phase: ImportProgressEvent['phase'], state: ImportProgressEvent['state'] = 'aligning', chapter = false) =>
      progressSteps(chapter)[phaseIndex({ phase, state }, chapter)].key;
    expect(at('queued', 'queued')).toBe('queued');
    expect(at('starting', 'queued')).toBe('extracting');
    expect(at('extracting', 'extracting')).toBe('extracting');
    expect(at('definitions', 'extracting', true)).toBe('definitions');
    expect(at('rendering')).toBe('pages');
    expect(at('inventory')).toBe('pages');
    expect(at('aligning')).toBe('aligning');
    expect(at('tagging', 'tagging')).toBe('tagging');
    expect(at('throttled', 'tagging')).toBe('tagging');
    expect(at('throttled', 'extracting', true)).toBe('definitions');
    expect(at('finishing', 'tagging')).toBe('review');
    expect(phaseLabel({ phase: 'throttled', state: 'tagging' })).toBe('Wachten op modelcapaciteit');
    expect(phaseLabel({ phase: 'inventory', state: 'aligning' })).toBe("Pagina's en inventaris");
  });

  it('formats steps, percentages, model calls, times and the connection', () => {
    expect(percent(base)).toBe(22);
    expect(percent({ step: null, total: null })).toBeNull();
    expect(stepText(base)).toBe('Vraag 7 van 32');
    expect(stepText({ step: 1, total: 2, unit: 'document' })).toBe('Document 1 van 2');
    expect(modelCallText(base)).toBe('Modelaanroepen: 12 van 64');
    expect(modelCallText({ modelCalls: null, modelCallBudget: null })).toBeNull();
    expect(clockTime('2026-11-14T13:32:05Z')).toMatch(/^\d\d:\d\d:05$/);
    expect(clockTime('not a date')).toBeNull();
    expect(connectionText('live')).toBe('Live');
    expect(connectionText('reconnecting')).toBe('Verbinding herstellen…');
    expect(connectionText('polling')).toContain('elke 5 seconden');
  });

  it('announces phase changes, every 5 questions, stale flips and live loss, and nothing else', () => {
    const live = 'live' as const;
    const at = (step: number, extra: Partial<ImportProgressEvent> = {}) => ({ progress: { ...base, step, ...extra }, connection: live });
    expect(announcement(null, at(7))).toBe('Onderwerpen toekennen: Vraag 7 van 32: onderwerpen toekennen');
    expect(announcement(at(7), at(8))).toBeNull();
    expect(announcement(at(9), at(10, { message: 'Vraag 10 van 32: onderwerpen toekennen' })))
      .toBe('Vraag 10 van 32: onderwerpen toekennen');
    expect(announcement(at(10), at(10, { stale: true }))).toBe('De achtergrondtaak reageert niet meer.');
    expect(announcement(at(10, { stale: true }), at(10, { stale: true }))).toBeNull();
    expect(announcement(at(10), { ...at(10), connection: 'polling' })).toContain('Live updates niet beschikbaar');
    expect(announcement({ ...at(10), connection: 'polling' }, { ...at(10), connection: 'polling' })).toBeNull();
    expect(announcement(at(10), at(1, { phase: 'finishing', step: null, total: null, message: 'Resultaten opslaan voor beoordeling' })))
      .toBe('Beoordelen: Resultaten opslaan voor beoordeling');
  });
});
