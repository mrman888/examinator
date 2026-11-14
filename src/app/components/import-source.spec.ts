import { describe, expect, it } from 'vitest';
import { applyPdfSource, normalizePdfUrl, pdfSourceError } from './import-source';

describe('import PDF sources', () => {
  it('accepts plain https links and trims them', () => {
    expect(normalizePdfUrl('  https://www.alleexamens.nl/a.pdf ')).toBe('https://www.alleexamens.nl/a.pdf');
  });

  it.each([
    '', 'not a url', 'http://www.alleexamens.nl/a.pdf', 'ftp://host/a.pdf', 'javascript:alert(1)',
    'https://user:pw@www.alleexamens.nl/a.pdf', 'https://www.alleexamens.nl:8443/a.pdf',
    `https://example.nl/${'a'.repeat(2100)}`,
  ])('rejects %s', (value) => {
    expect(normalizePdfUrl(value)).toBeNull();
  });

  it('validates a file or a link per field', () => {
    const pdf = new File(['%PDF-1.4'], 'a.pdf', { type: 'application/pdf' });
    expect(pdfSourceError('het examen', 'file', pdf, null)).toBeNull();
    expect(pdfSourceError('het examen', 'file', new File([], 'a.pdf'), null)).toContain('Kies een bestand');
    expect(pdfSourceError('het examen', 'file', null, null)).toContain('Kies een bestand');
    expect(pdfSourceError('het examen', 'url', null, 'https://www.alleexamens.nl/a.pdf')).toBeNull();
    expect(pdfSourceError('het examen', 'url', pdf, 'http://www.alleexamens.nl/a.pdf')).toContain('https-link');
    expect(pdfSourceError('het examen', 'url', null, '')).toContain('https-link');
  });

  it('keeps only the chosen source in the request body', () => {
    const data = new FormData();
    data.set('exam', new File(['x'], 'e.pdf'));
    data.set('examUrl', ' https://www.alleexamens.nl/e.pdf ');
    data.set('answers', new File(['x'], 'a.pdf'));
    data.set('answersUrl', 'https://www.alleexamens.nl/a.pdf');
    applyPdfSource(data, 'exam', 'url');
    applyPdfSource(data, 'answers', 'file');
    expect(data.has('exam')).toBe(false);
    expect(data.get('examUrl')).toBe('https://www.alleexamens.nl/e.pdf');
    expect(data.get('answers')).toBeInstanceOf(File);
    expect(data.has('answersUrl')).toBe(false);
  });
});
