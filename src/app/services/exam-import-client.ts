export interface TopicDefinition {
  id: string;
  title: string;
  description: string;
  keywords: string[];
  exclusions: string[];
  aliases: string[];
  chapterNumber: string | null;
  paragraphNumber: string | null;
}

export interface ImportMetadata {
  subjectId: string;
  level: string;
  year: number;
  timevak: number;
  chapterNumber: string;
  chapterTitle: string;
  topics: TopicDefinition[];
}

export interface DraftQuestion {
  id: string;
  number: number;
  title: string;
  examPage: number;
  examEndPage: number;
  answerPage: number | null;
  answerEndPage: number | null;
  topicIds: string[];
  reasons: string[];
  needsReview: boolean;
  warnings: string[];
}

export type ImportState = 'queued' | 'extracting' | 'awaitingDefinitions' | 'aligning' |
  'tagging' | 'review' | 'published' | 'failed' | 'cancelled';

export interface ImportRecord {
  id: string;
  state: ImportState;
  metadata: ImportMetadata;
  topics: TopicDefinition[];
  questions: DraftQuestion[];
  warnings: string[];
  error: string | null;
  revision: string;
  examDocumentId: string;
  answerDocumentId: string;
  contextDocumentId?: string | null;
  contextText?: string;
  createdAt: string;
}

export interface DuplicateImport {
  importId: string;
  state: string;
  year: number;
  timevak: number;
  role: string;
  documentId: string;
}

export class ImportRequestError extends Error {
  constructor(message: string, readonly status: number, readonly duplicates: DuplicateImport[] = []) {
    super(message);
  }
}

function duplicate(value: unknown): value is DuplicateImport {
  return object(value) && typeof value['importId'] === 'string' && typeof value['state'] === 'string' &&
    nonNegativeInteger(value['year']) && nonNegativeInteger(value['timevak']) &&
    typeof value['role'] === 'string' && typeof value['documentId'] === 'string';
}

export interface CatalogQuestion {
  id: string;
  topics: string[];
  title: string;
  examFile: string;
  examPage: number;
  answerFile: string;
  answerPage: number;
  year: number;
  timevak: number;
  number: number;
  subjectId: string;
}

export interface CatalogExam {
  id: string;
  subjectId: string;
  level: string;
  year: number;
  timevak: number;
  examFile: string;
  answerFile: string;
}

export interface ExamCatalog {
  subjects: { id: string; title: string }[];
  topics: TopicDefinition[];
  questions: CatalogQuestion[];
  exams: CatalogExam[];
}

function object(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function strings(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

function positiveInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
}

function nonNegativeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
}

function nullablePage(value: unknown): value is number | null {
  return value === null || positiveInteger(value);
}

function topic(value: unknown): value is TopicDefinition {
  return object(value) && typeof value['id'] === 'string' && !!value['id'] &&
    typeof value['title'] === 'string' && typeof value['description'] === 'string' &&
    strings(value['keywords']) && strings(value['exclusions']) && strings(value['aliases']) &&
    (value['chapterNumber'] === null || typeof value['chapterNumber'] === 'string') &&
    (value['paragraphNumber'] === null || typeof value['paragraphNumber'] === 'string');
}

function metadata(value: unknown): value is ImportMetadata {
  if (!object(value)) return false;
  const year = value['year'];
  const timevak = value['timevak'];
  return typeof value['subjectId'] === 'string' && typeof value['level'] === 'string' &&
    nonNegativeInteger(year) && nonNegativeInteger(timevak) &&
    ((year === 0 && timevak === 0) ||
      (year >= 2000 && year <= 2100 && timevak >= 1 && timevak <= 3)) &&
    typeof value['chapterNumber'] === 'string' && typeof value['chapterTitle'] === 'string' &&
    Array.isArray(value['topics']) && value['topics'].every(topic);
}

function question(value: unknown): value is DraftQuestion {
  return object(value) && typeof value['id'] === 'string' && positiveInteger(value['number']) &&
    typeof value['title'] === 'string' && positiveInteger(value['examPage']) &&
    positiveInteger(value['examEndPage']) && nullablePage(value['answerPage']) &&
    nullablePage(value['answerEndPage']) && strings(value['topicIds']) && strings(value['reasons']) &&
    typeof value['needsReview'] === 'boolean' && strings(value['warnings']);
}

function importRecord(value: unknown): value is ImportRecord {
  const states: string[] = ['queued', 'extracting', 'awaitingDefinitions', 'aligning', 'tagging',
    'review', 'published', 'failed', 'cancelled'];
  return object(value) && typeof value['id'] === 'string' && !!value['id'] &&
    typeof value['state'] === 'string' && states.includes(value['state']) && metadata(value['metadata']) &&
    Array.isArray(value['topics']) && value['topics'].every(topic) &&
    Array.isArray(value['questions']) && value['questions'].every(question) &&
    strings(value['warnings']) && (value['error'] === null || typeof value['error'] === 'string') &&
    typeof value['revision'] === 'string' && typeof value['examDocumentId'] === 'string' &&
    typeof value['answerDocumentId'] === 'string' && typeof value['createdAt'] === 'string' &&
    (value['contextDocumentId'] === undefined || value['contextDocumentId'] === null || typeof value['contextDocumentId'] === 'string') &&
    (value['contextText'] === undefined || typeof value['contextText'] === 'string');
}

function documentPath(value: unknown): value is string {
  return typeof value === 'string' && /^\/api\/documents\/[a-zA-Z0-9_-]+$/.test(value);
}

function catalog(value: unknown): value is ExamCatalog {
  if (!object(value) || !Array.isArray(value['subjects']) ||
    !value['subjects'].every((item: unknown) => object(item) &&
      typeof item['id'] === 'string' && typeof item['title'] === 'string') ||
    !Array.isArray(value['topics']) || !value['topics'].every(topic) ||
    !Array.isArray(value['questions']) || !Array.isArray(value['exams'])) return false;
  return value['questions'].every((item: unknown) => object(item) &&
    typeof item['id'] === 'string' && strings(item['topics']) && typeof item['title'] === 'string' &&
    documentPath(item['examFile']) && documentPath(item['answerFile']) &&
    positiveInteger(item['examPage']) && positiveInteger(item['answerPage']) &&
    positiveInteger(item['year']) && positiveInteger(item['timevak']) &&
    positiveInteger(item['number']) && typeof item['subjectId'] === 'string') &&
    value['exams'].every((item: unknown) => object(item) && typeof item['id'] === 'string' &&
      typeof item['subjectId'] === 'string' && typeof item['level'] === 'string' &&
      positiveInteger(item['year']) && positiveInteger(item['timevak']) &&
      documentPath(item['examFile']) && documentPath(item['answerFile']));
}

async function request<T>(path: string, validate: (value: unknown) => value is T,
  signal: AbortSignal, method = 'GET', body?: FormData | object): Promise<T> {
  const multipart = body instanceof FormData;
  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    redirect: 'error',
    headers: {
      'X-Examinator-Request': '1',
      ...(!multipart && body ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? multipart ? body : JSON.stringify(body) : undefined,
    signal: AbortSignal.any([signal, AbortSignal.timeout(multipart ? 300_000 : 120_000)]),
  });
  const isJson = response.headers.get('content-type')?.includes('json');
  const result: unknown = isJson ? await response.json() : null;
  if (!response.ok) {
    const defaults: Record<number, string> = {
      401: 'Je sessie is verlopen. Meld je opnieuw aan.',
      403: 'Je hebt geen toegang tot deze functie.',
      409: 'De import is gewijzigd. Vernieuw de gegevens voordat je verdergaat.',
      413: 'De bestanden zijn te groot voor deze import.',
      429: 'Te veel verzoeken. Probeer het later opnieuw.',
      503: 'Examenimport is nog niet beschikbaar. Controleer de Azure-opslagconfiguratie.',
    };
    const detail = object(result) && typeof result['detail'] === 'string' ? result['detail'] : null;
    const title = object(result) && typeof result['title'] === 'string' ? result['title'] : null;
    const duplicates = response.status === 409 && object(result) && Array.isArray(result['duplicates'])
      ? result['duplicates'].filter(duplicate) : [];
    throw new ImportRequestError(
      detail ?? (duplicates.length ? title : null) ?? defaults[response.status] ?? title ?? `Het verzoek is mislukt (HTTP ${response.status}).`,
      response.status, duplicates);
  }
  if (response.status === 204 && method === 'DELETE') return undefined as T;
  if (!isJson || !validate(result)) {
    throw new Error('Geen geldig importantwoord ontvangen. Controleer je sessie en de API-configuratie.');
  }
  return result;
}

export function loadCatalog(signal: AbortSignal): Promise<ExamCatalog> {
  return request('/api/catalog?subjectId=biology', catalog, signal);
}

export function listImports(signal: AbortSignal): Promise<ImportRecord[]> {
  return request('/api/imports', (value): value is ImportRecord[] =>
    Array.isArray(value) && value.every(importRecord), signal);
}

export function loadImport(id: string, signal: AbortSignal): Promise<ImportRecord> {
  return request(`/api/imports/${encodeURIComponent(id)}`, importRecord, signal);
}

export function uploadImport(data: FormData, signal: AbortSignal): Promise<ImportRecord> {
  return request('/api/imports', importRecord, signal, 'POST', data);
}

export function reprocessImport(id: string, data: FormData, signal: AbortSignal): Promise<ImportRecord> {
  return request(`/api/imports/${encodeURIComponent(id)}/reprocess`, importRecord, signal, 'POST', data);
}

export function updateImport(id: string, action: 'draft' | 'confirm-definitions' | 'publish' | 'cancel' | 'retry',
  body: object, signal: AbortSignal): Promise<ImportRecord> {
  return request(`/api/imports/${encodeURIComponent(id)}/${action}`, importRecord, signal,
    action === 'draft' ? 'PATCH' : 'POST', body);
}

export function deleteImport(id: string, signal: AbortSignal): Promise<void> {
  return request(`/api/imports/${encodeURIComponent(id)}`, (value): value is void => true, signal, 'DELETE');
}

/** Server-sent live progress for the import list; see server/README.md for the event contract. */
export const IMPORT_EVENTS_URL = '/api/imports/events';

export type ImportProgressPhase = 'queued' | 'starting' | 'extracting' | 'definitions' | 'rendering' |
  'inventory' | 'aligning' | 'tagging' | 'throttled' | 'finishing';

export interface ImportProgressEvent {
  importId: string;
  state: ImportState;
  revision: string;
  phase: ImportProgressPhase;
  step: number | null;
  total: number | null;
  unit: string | null;
  modelCalls: number | null;
  modelCallBudget: number | null;
  message: string;
  updatedAt: string | null;
  heartbeatAt: string | null;
  serverTime: string;
  stale: boolean;
}

export interface ImportSettledEvent {
  importId: string;
  state: ImportState | 'deleted';
  revision: string | null;
}

const IMPORT_STATES: readonly string[] = ['queued', 'extracting', 'awaitingDefinitions', 'aligning', 'tagging',
  'review', 'published', 'failed', 'cancelled'];
const PROGRESS_PHASES: readonly string[] = ['queued', 'starting', 'extracting', 'definitions', 'rendering',
  'inventory', 'aligning', 'tagging', 'throttled', 'finishing'];

function nullableCount(value: unknown): value is number | null {
  return value === null || nonNegativeInteger(value);
}

function nullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function progressEvent(value: unknown): value is ImportProgressEvent {
  return object(value) && typeof value['importId'] === 'string' && !!value['importId'] &&
    typeof value['state'] === 'string' && IMPORT_STATES.includes(value['state']) &&
    typeof value['revision'] === 'string' && typeof value['phase'] === 'string' && PROGRESS_PHASES.includes(value['phase']) &&
    nullableCount(value['step']) && nullableCount(value['total']) && nullableString(value['unit']) &&
    nullableCount(value['modelCalls']) && nullableCount(value['modelCallBudget']) &&
    typeof value['message'] === 'string' && nullableString(value['updatedAt']) && nullableString(value['heartbeatAt']) &&
    typeof value['serverTime'] === 'string' && typeof value['stale'] === 'boolean';
}

function settledEvent(value: unknown): value is ImportSettledEvent {
  return object(value) && typeof value['importId'] === 'string' && !!value['importId'] &&
    typeof value['state'] === 'string' && (value['state'] === 'deleted' || IMPORT_STATES.includes(value['state'])) &&
    nullableString(value['revision']);
}

function parse<T>(data: unknown, validate: (value: unknown) => value is T): T | null {
  if (typeof data !== 'string') return null;
  try {
    const value: unknown = JSON.parse(data);
    return validate(value) ? value : null;
  } catch {
    return null;
  }
}

/** Returns null for anything that is not a valid `progress` event payload. */
export function parseProgressEvent(data: unknown): ImportProgressEvent | null {
  return parse(data, progressEvent);
}

/** Returns null for anything that is not a valid `settled` event payload. */
export function parseSettledEvent(data: unknown): ImportSettledEvent | null {
  return parse(data, settledEvent);
}
