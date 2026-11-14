import type { ImportRecord } from '../services/exam-import-client';

function time(record: ImportRecord): number {
  const value = Date.parse(record.createdAt);
  return Number.isNaN(value) ? 0 : value;
}

/** Newest first by creation time, with the id as a stable tiebreaker. */
export function compareImports(a: ImportRecord, b: ImportRecord): number {
  return time(b) - time(a) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
}

export function sortImports(records: readonly ImportRecord[]): ImportRecord[] {
  return [...records].sort(compareImports);
}

/** Replaces a known record in place; a new record is inserted at its sorted position. */
export function upsertImport(records: readonly ImportRecord[], record: ImportRecord): ImportRecord[] {
  return records.some((item) => item.id === record.id)
    ? records.map((item) => (item.id === record.id ? record : item))
    : sortImports([...records, record]);
}

export function removeImport(records: readonly ImportRecord[], id: string): ImportRecord[] {
  return records.filter((item) => item.id !== id);
}
