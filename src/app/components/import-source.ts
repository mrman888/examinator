export type PdfSourceMode = 'file' | 'url';

export const MAX_PDF_BYTES = 20 * 1024 * 1024;

/** Returns the trimmed https URL, or null when the link is not acceptable for a server-side download. */
export function normalizePdfUrl(value: string): string | null {
  const text = value.trim();
  if (!text || text.length > 2048) return null;
  let url: URL;
  try {
    url = new URL(text);
  } catch {
    return null;
  }
  if (url.protocol !== 'https:' || url.username || url.password || url.port || !url.hostname) return null;
  return text;
}

/** Validates one PDF field of the import form and returns a Dutch error message, or null when it is valid. */
export function pdfSourceError(label: string, mode: PdfSourceMode, file: FormDataEntryValue | null,
  url: FormDataEntryValue | null): string | null {
  if (mode === 'url') {
    return typeof url === 'string' && normalizePdfUrl(url) !== null ? null
      : `Geef bij ${label} een geldige https-link naar een PDF op (zonder poortnummer of inloggegevens).`;
  }
  if (!(file instanceof File) || !file.size) return `Kies een bestand voor ${label}.`;
  return file.size > MAX_PDF_BYTES ? 'Maximaal 20 MiB per PDF en 5 MiB voor de hoofdstukscreenshot.' : null;
}

/** Keeps only the chosen source of a PDF field in the multipart body. */
export function applyPdfSource(data: FormData, field: 'exam' | 'answers', mode: PdfSourceMode): void {
  const urlField = `${field}Url`;
  if (mode === 'url') {
    data.delete(field);
    data.set(urlField, normalizePdfUrl(String(data.get(urlField) ?? '')) ?? '');
  } else {
    data.delete(urlField);
  }
}
