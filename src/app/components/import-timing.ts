import type { ImportPhaseTiming, ImportRunOutcome, ImportRunTiming, ImportStepOutcome,
  ImportTiming } from '../services/exam-import-client';

export const UNKNOWN = 'onbekend';

/** "12 s", "3 min 40 s", "1 u 5 min"; whole seconds, rounded down so a live value never runs ahead. */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) return UNKNOWN;
  const seconds = Math.floor(Math.max(0, ms) / 1000);
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return seconds % 60 ? `${minutes} min ${seconds % 60} s` : `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  return minutes % 60 ? `${hours} u ${minutes % 60} min` : `${hours} u`;
}

function parse(value: string | null | undefined): number | null {
  if (!value) return null;
  const time = Date.parse(value);
  return Number.isNaN(time) ? null : time;
}

const DATE_TIME = new Intl.DateTimeFormat('nl-NL', {
  day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
});
const CLOCK = new Intl.DateTimeFormat('nl-NL', { hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });

/** "14-11-2026 15:02" in local time. */
export function formatDateTime(value: string | null | undefined): string {
  const time = parse(value);
  if (time === null) return UNKNOWN;
  const part = Object.fromEntries(DATE_TIME.formatToParts(time).map((item) => [item.type, item.value]));
  return `${part['day']}-${part['month']}-${part['year']} ${part['hour']}:${part['minute']}`;
}

/** "15:02:07" in local time. */
export function formatClock(value: string | null | undefined): string {
  const time = parse(value);
  return time === null ? UNKNOWN : CLOCK.format(time);
}

/** Milliseconds from start to end, or to `now` while it runs; never negative. */
export function elapsed(item: { startedAt: string; endedAt: string | null }, now: number): number | null {
  const start = parse(item.startedAt);
  if (start === null) return null;
  return Math.max(0, (parse(item.endedAt) ?? now) - start);
}

const STEP_LABELS: Record<string, string> = {
  starting: 'Starten', extracting: 'Uitlezen', definitions: 'Hoofdstukdefinities voorstellen',
  rendering: "Pagina's renderen", inventory: 'Vragen inventariseren', aligning: 'Antwoorden koppelen',
  tagging: 'Onderwerpen toekennen', throttled: 'Wachten op modelcapaciteit', finishing: 'Afronden',
};

export function stepLabel(phase: string): string {
  return STEP_LABELS[phase] ?? phase;
}

const STEP_OUTCOMES: Record<ImportStepOutcome, string> = {
  ok: 'Gelukt', throttled: 'Gewacht', failed: 'Mislukt', cancelled: 'Geannuleerd', interrupted: 'Onderbroken',
};

export function stepOutcomeLabel(outcome: ImportStepOutcome | null): string {
  return outcome ? STEP_OUTCOMES[outcome] : 'Bezig';
}

const RUN_OUTCOMES: Record<ImportRunOutcome, string> = {
  review: 'Geslaagd', awaitingDefinitions: 'Wacht op bevestiging van onderwerpen', failed: 'Mislukt',
  cancelled: 'Geannuleerd', interrupted: 'Onderbroken',
};

export function runOutcomeLabel(outcome: ImportRunOutcome | null): string {
  return outcome ? RUN_OUTCOMES[outcome] : 'Bezig';
}

/** True while the latest run has not ended. */
export function isRunning(timing: ImportTiming | null | undefined): boolean {
  const last = timing?.runs.at(-1);
  return !!timing && !timing.completedAt && !!last && !last.endedAt;
}

/**
 * Summed processing time (waiting between runs excluded): the server total once the latest run completed, or the
 * earlier runs plus the live current run. Null when earlier runs have no step log.
 */
export function processingTime(timing: ImportTiming | null | undefined, now: number): number | null {
  if (!timing) return null;
  if (timing.processingMilliseconds !== null) return timing.processingMilliseconds;
  const current = timing.runs.at(-1);
  if (timing.earlierProcessingMilliseconds === null || !current || current.endedAt) return null;
  const running = elapsed(current, now);
  return running === null ? null : timing.earlierProcessingMilliseconds + running;
}

/** Upload until the latest run completed, including queue and user waits; null while processing. */
export function leadTime(createdAt: string | null | undefined, timing: ImportTiming | null | undefined): number | null {
  const start = parse(createdAt);
  const end = parse(timing?.completedAt);
  return start === null || end === null ? null : Math.max(0, end - start);
}

/** Runs from the record with the live current run (from the stream) replacing or extending them, by run number. */
export function combineTiming(record: ImportTiming | null | undefined, live: ImportTiming | null | undefined): ImportTiming | null {
  if (!live) return record ?? null;
  const runs = new Map<number, ImportRunTiming>();
  for (const run of record?.runs ?? []) runs.set(run.number, run);
  for (const run of live.runs) runs.set(run.number, run);
  return { ...live, runs: [...runs.values()].sort((a, b) => a.number - b.number) };
}

export interface StepRow {
  key: string;
  label: string;
  start: string;
  duration: string;
  details: string;
  modelCalls: number;
  outcome: string;
  failed: boolean;
  running: boolean;
  error: string | null;
}

export interface RunView {
  number: number;
  heading: string;
  failed: boolean;
  steps: StepRow[];
}

function details(step: ImportPhaseTiming): string {
  const counter = step.step !== null && step.total !== null && !step.message?.includes(`${step.step} van ${step.total}`)
    ? ` (${step.step} van ${step.total})` : '';
  return `${step.message ?? ''}${counter}`.trim();
}

/** Newest run first; the heading reads "Poging 2 · 14-11-2026 15:02 · 6 min 12 s · Geslaagd". */
export function runViews(timing: ImportTiming | null | undefined, now: number): RunView[] {
  return [...(timing?.runs ?? [])].reverse().map((run) => ({
    number: run.number,
    heading: [`Poging ${run.number}`, formatDateTime(run.startedAt), formatDuration(elapsed(run, now)),
      runOutcomeLabel(run.outcome)].join(' · '),
    failed: run.outcome === 'failed',
    steps: run.phases.map((step, index) => ({
      key: `${index}-${step.startedAt}`,
      label: stepLabel(step.phase),
      start: formatClock(step.startedAt),
      duration: formatDuration(elapsed(step, now)),
      details: details(step),
      modelCalls: step.modelCalls,
      outcome: stepOutcomeLabel(step.outcome),
      failed: step.outcome === 'failed',
      running: step.outcome === null && !step.endedAt,
      error: step.error,
    })),
  }));
}

const SETTLED_WITHOUT_RUN = ['queued', 'cancelled'];
const PROCESSING = ['queued', 'extracting', 'aligning', 'tagging'];

/**
 * The compact list text once processing has finished ("Verwerkt in 6 min 12 s"), "Verwerkingstijd onbekend" for
 * imports without a step log, and null while processing or for imports that never ran.
 */
export function processedText(state: string, timing: ImportTiming | null | undefined): string | null {
  if (PROCESSING.includes(state)) return null;
  if (!timing) return SETTLED_WITHOUT_RUN.includes(state) ? null : 'Verwerkingstijd onbekend';
  return timing.processingMilliseconds === null ? 'Verwerkingstijd onbekend'
    : `Verwerkt in ${formatDuration(timing.processingMilliseconds)}`;
}
