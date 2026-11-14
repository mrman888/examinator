import type { ImportProgressEvent } from '../services/exam-import-client';
import type { StreamConnection } from '../services/import-progress-stream';

export type ProgressStepKey = 'queued' | 'extracting' | 'definitions' | 'pages' | 'aligning' | 'tagging' | 'review';

export interface ProgressStep {
  key: ProgressStepKey;
  label: string;
}

const ALL_STEPS: readonly ProgressStep[] = [
  { key: 'queued', label: 'Wachtrij' },
  { key: 'extracting', label: 'Uitlezen' },
  { key: 'definitions', label: 'Hoofdstuk' },
  { key: 'pages', label: "Pagina's en inventaris" },
  { key: 'aligning', label: 'Koppelen' },
  { key: 'tagging', label: 'Onderwerpen toekennen' },
  { key: 'review', label: 'Beoordelen' },
];

/** The stepper; the chapter step only exists for imports with chapter context. */
export function progressSteps(withDefinitions: boolean): ProgressStep[] {
  return ALL_STEPS.filter((step) => withDefinitions || step.key !== 'definitions');
}

/** Phases that have no step of their own (starting, throttled) follow the record state. */
export function stepKey(progress: Pick<ImportProgressEvent, 'phase' | 'state'>, withDefinitions: boolean): ProgressStepKey {
  switch (progress.phase) {
    case 'queued': return 'queued';
    case 'extracting': return 'extracting';
    case 'definitions': return 'definitions';
    case 'rendering':
    case 'inventory': return 'pages';
    case 'aligning': return 'aligning';
    case 'tagging': return 'tagging';
    case 'finishing': return 'review';
    case 'starting':
      return progress.state === 'queued' || progress.state === 'extracting' ? 'extracting'
        : progress.state === 'tagging' ? 'tagging' : 'pages';
    case 'throttled':
      return progress.state === 'tagging' ? 'tagging' : progress.state === 'aligning' ? 'pages'
        : withDefinitions ? 'definitions' : 'extracting';
  }
}

export function phaseIndex(progress: Pick<ImportProgressEvent, 'phase' | 'state'>, withDefinitions: boolean): number {
  const key = stepKey(progress, withDefinitions);
  return progressSteps(withDefinitions).findIndex((step) => step.key === key);
}

export function phaseLabel(progress: Pick<ImportProgressEvent, 'phase' | 'state'>): string {
  if (progress.phase === 'throttled') return 'Wachten op modelcapaciteit';
  if (progress.phase === 'starting') return 'Verwerking gestart';
  return ALL_STEPS.find((step) => step.key === stepKey(progress, true))!.label;
}

/** Rounded percentage of the current step, or null when the phase has no countable steps. */
export function percent(progress: Pick<ImportProgressEvent, 'step' | 'total'>): number | null {
  const { step, total } = progress;
  if (step === null || total === null || total < 1) return null;
  return Math.max(0, Math.min(100, Math.round((step / total) * 100)));
}

/** "Vraag 7 van 32" for questions, "Document 1 van 2" otherwise. */
export function stepText(progress: Pick<ImportProgressEvent, 'step' | 'total' | 'unit'>): string | null {
  if (progress.step === null || progress.total === null) return null;
  const unit = progress.unit === 'vraag' ? 'Vraag' : 'Document';
  return `${unit} ${progress.step} van ${progress.total}`;
}

export function modelCallText(progress: Pick<ImportProgressEvent, 'modelCalls' | 'modelCallBudget'>): string | null {
  if (progress.modelCalls === null) return null;
  return progress.modelCallBudget === null ? `Modelaanroepen: ${progress.modelCalls}`
    : `Modelaanroepen: ${progress.modelCalls} van ${progress.modelCallBudget}`;
}

const TIME = new Intl.DateTimeFormat('nl-NL', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

export function clockTime(value: string | null): string | null {
  if (!value) return null;
  const time = Date.parse(value);
  return Number.isNaN(time) ? null : TIME.format(time);
}

export function connectionText(connection: StreamConnection | null): string | null {
  switch (connection) {
    case 'live': return 'Live';
    case 'connecting':
    case 'reconnecting': return 'Verbinding herstellen…';
    case 'polling': return 'Live updates niet beschikbaar; de status wordt elke 5 seconden ververst';
    default: return null;
  }
}

export interface AnnouncementInput {
  progress: ImportProgressEvent | null;
  connection: StreamConnection | null;
}

function bucket(progress: ImportProgressEvent): number {
  if (progress.step === null || progress.total === null) return -1;
  return progress.unit === 'vraag'
    ? Math.max(Math.floor(progress.step / 5), Math.floor((percent(progress) ?? 0) / 25))
    : Math.floor((percent(progress) ?? 0) / 25);
}

/**
 * What the polite live region should say for this change, or null to stay silent. It speaks on a phase change,
 * every 5 questions or 25 %, when the worker goes silent and when live updates are lost.
 */
export function announcement(previous: AnnouncementInput | null, next: AnnouncementInput): string | null {
  if (next.connection === 'polling' && previous?.connection !== 'polling') {
    return 'Live updates niet beschikbaar; de status wordt elke 5 seconden ververst.';
  }
  const before = previous?.progress?.importId === next.progress?.importId ? previous?.progress ?? null : null;
  const progress = next.progress;
  if (!progress) return null;
  if (progress.stale && !before?.stale) {
    return progress.phase === 'queued' ? `${progress.message}.` : 'De achtergrondtaak reageert niet meer.';
  }
  if (!before || before.phase !== progress.phase) return `${phaseLabel(progress)}: ${progress.message}`;
  if (bucket(progress) > bucket(before)) return progress.message;
  return null;
}
