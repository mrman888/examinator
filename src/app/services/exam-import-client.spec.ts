import { afterEach, describe, expect, it, vi } from 'vitest';
import { type ImportRecord, loadCatalog, listImports, parseProgressEvent, updateImport, uploadImport } from './exam-import-client';
import { pdfDocumentUrl } from './pdf-preview.service';

afterEach(() => vi.unstubAllGlobals());

describe('exam import client', () => {
  it('uploads files without overriding the multipart boundary and preserves mutation protections', async () => {
    const fetch = vi.fn().mockResolvedValue(Response.json([]));
    vi.stubGlobal('fetch', fetch);
    const body = new FormData();
    body.append('exam', new File(['%PDF-'], 'exam.pdf', { type: 'application/pdf' }));
    await expect(uploadImport(body, new AbortController().signal)).rejects.toThrow('Geen geldig importantwoord');
    expect(fetch).toHaveBeenCalledWith('/api/imports', expect.objectContaining({
      method: 'POST', body, credentials: 'same-origin', redirect: 'error',
      headers: { 'X-Examinator-Request': '1' },
    }));
  });

  it('sends concurrency revision with draft changes and rejects malformed success responses', async () => {
    const fetch = vi.fn().mockResolvedValue(Response.json({ state: 'review' }));
    vi.stubGlobal('fetch', fetch);
    const body = { revision: '"v1"', topics: [], questions: [] };
    await expect(updateImport('import-1', 'draft', body, new AbortController().signal)).rejects.toThrow();
    expect(fetch).toHaveBeenCalledWith('/api/imports/import-1/draft', expect.objectContaining({
      method: 'PATCH', body: JSON.stringify(body),
      headers: { 'Content-Type': 'application/json', 'X-Examinator-Request': '1' },
    }));
  });

  it('accepts records with and without the step log and rejects a malformed one', async () => {
    const base: ImportRecord = {
      id: 'import-1', state: 'review', revision: '"v1"', createdAt: '2026-11-13T18:00:00Z', examDocumentId: 'e',
      answerDocumentId: 'a', warnings: [], error: null, topics: [], questions: [],
      metadata: { subjectId: 'biology', level: 'havo', year: 2025, timevak: 1, chapterNumber: '', chapterTitle: '', topics: [] },
    };
    const timing = { processingStartedAt: '2026-11-14T14:00:00Z', completedAt: '2026-11-14T14:06:12Z', outcome: 'review',
      processingMilliseconds: 372000, earlierProcessingMilliseconds: 0, earlierRunsUnknown: false,
      runs: [{ number: 1, attempt: 1, startedAt: '2026-11-14T14:00:00Z', endedAt: '2026-11-14T14:06:12Z', outcome: 'review',
        phases: [{ phase: 'tagging', startedAt: '2026-11-14T14:00:00Z', endedAt: '2026-11-14T14:06:12Z', message: 'Vraag 1 van 1',
          step: 1, total: 1, modelCalls: 1, outcome: 'ok', error: null }] }] };
    const current = { ...base, updatedAt: '2026-11-14T14:06:13Z', timing };
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.resolve(Response.json([base, current]))));
    expect(await listImports(new AbortController().signal)).toEqual([base, current]);
    const broken = { ...current, timing: { ...timing, runs: [{ ...timing.runs[0], phases: [{ phase: 'tagging' }] }] } };
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.resolve(Response.json([broken]))));
    await expect(listImports(new AbortController().signal)).rejects.toThrow();

    const event = { importId: 'import-1', state: 'tagging', revision: '"v1"', phase: 'tagging', step: 1, total: 2, unit: 'vraag',
      modelCalls: 1, modelCallBudget: 64, message: 'Vraag 1 van 2', updatedAt: null, heartbeatAt: null,
      serverTime: '2026-11-14T14:00:00Z', stale: false };
    expect(parseProgressEvent(JSON.stringify(event))).not.toBeNull();
    expect(parseProgressEvent(JSON.stringify({ ...event, timing }))?.timing?.runs).toHaveLength(1);
    expect(parseProgressEvent(JSON.stringify({ ...event, timing: { runs: 'nee' } }))).toBeNull();
  });

  it.each([409, 413, 503])('surfaces HTTP %s failures explicitly', async (status) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status })));
    await expect(listImports(new AbortController().signal)).rejects.toThrow();
  });

  it('does not accept sign-in HTML or an external document URL as a catalog', async () => {
    const fetch = vi.fn()
      .mockResolvedValueOnce(new Response('<html>sign in</html>'))
      .mockResolvedValueOnce(Response.json({
        subjects: [], topics: [], questions: [],
        exams: [{ id: 'e', subjectId: 'biology', level: 'havo', year: 2025, timevak: 1,
          examFile: 'https://example.org/exam.pdf', answerFile: '/api/documents/a' }],
      }));
    vi.stubGlobal('fetch', fetch);
    await expect(loadCatalog(new AbortController().signal)).rejects.toThrow('Geen geldig importantwoord');
    await expect(loadCatalog(new AbortController().signal)).rejects.toThrow('Geen geldig importantwoord');
  });

  it('supports bundled and authenticated PDFs without accepting arbitrary URLs or traversal', () => {
    expect(pdfDocumentUrl('2025_havo_tijdvak1_examen.pdf')).toBe('Data/2025_havo_tijdvak1_examen.pdf');
    expect(pdfDocumentUrl('/api/documents/doc-1')).toBe('/api/documents/doc-1');
    expect(() => pdfDocumentUrl('../exam.pdf')).toThrow();
    expect(() => pdfDocumentUrl('https://example.org/exam.pdf')).toThrow();
  });
});
