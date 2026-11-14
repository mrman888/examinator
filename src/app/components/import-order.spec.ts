import { describe, expect, it } from 'vitest';
import { type ImportRecord } from '../services/exam-import-client';
import { removeImport, sortImports, upsertImport } from './import-order';

function item(id: string, createdAt: string, state: ImportRecord['state'] = 'queued'): ImportRecord {
  return {
    id, state, revision: '"v1"', createdAt, examDocumentId: `exam-${id}`, answerDocumentId: `answers-${id}`,
    warnings: [], error: null, topics: [], questions: [],
    metadata: { subjectId: 'biology', level: 'havo', year: 2025, timevak: 1, chapterNumber: '', chapterTitle: '', topics: [] },
  };
}

describe('import list order', () => {
  const a = item('a', '2026-11-13T10:00:00Z');
  const b = item('b', '2026-11-13T12:00:00Z');
  const c = item('c', '2026-11-13T12:00:00Z');

  it('sorts newest first by creation time with the id as tiebreaker', () => {
    expect(sortImports([a, c, b]).map((record) => record.id)).toEqual(['b', 'c', 'a']);
    expect(sortImports([b, a, c]).map((record) => record.id)).toEqual(['b', 'c', 'a']);
  });

  it('replaces an updated record in place without moving it', () => {
    const list = sortImports([a, b, c]);
    const updated = upsertImport(list, { ...a, state: 'review', revision: '"v2"' });
    expect(updated.map((record) => record.id)).toEqual(['b', 'c', 'a']);
    expect(updated[2].state).toBe('review');
    expect(list[2].state).toBe('queued');
  });

  it('inserts a new record at its sorted position and removes records by id', () => {
    const newest = item('d', '2026-11-14T08:00:00Z');
    const oldest = item('e', '2026-11-01T08:00:00Z');
    const list = upsertImport(upsertImport(sortImports([a, b, c]), oldest), newest);
    expect(list.map((record) => record.id)).toEqual(['d', 'b', 'c', 'a', 'e']);
    expect(removeImport(list, 'b').map((record) => record.id)).toEqual(['d', 'c', 'a', 'e']);
  });
});
