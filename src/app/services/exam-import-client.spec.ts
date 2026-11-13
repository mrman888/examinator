import { afterEach, describe, expect, it, vi } from 'vitest';
import { loadCatalog, listImports, updateImport, uploadImport } from './exam-import-client';
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
