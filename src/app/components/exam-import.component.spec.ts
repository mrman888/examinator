import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ExamImportComponent } from './exam-import.component';
import { ExamDataService } from '../services/exam-data.service';
import { type ExamCatalog, type ImportProgressEvent, type ImportRecord } from '../services/exam-import-client';
import { PdfPreviewService } from '../services/pdf-preview.service';
import { QuestionSidebarComponent } from './question-sidebar.component';
import { PROGRESS_STREAM_DEPS } from '../services/import-progress-stream';
import { fakeStreamDeps } from '../services/import-progress-fakes';

const record: ImportRecord = {
  id: 'import-1', state: 'review', revision: '"v1"', createdAt: '2026-11-13T18:00:00Z',
  examDocumentId: 'exam-1', answerDocumentId: 'answers-1', warnings: [], error: null,
  metadata: { subjectId: 'biology', level: 'havo', year: 2025, timevak: 1,
    chapterNumber: '2', chapterTitle: 'Cellen', topics: [] },
  topics: [{ id: 'biology-cell-division', title: 'Celdeling', description: 'Mitose en celdeling.',
    keywords: ['mitose'], exclusions: [], aliases: ['mitose'], chapterNumber: '2', paragraphNumber: '2.3' }],
  questions: [{ id: 'question-1', number: 1, title: 'Celdeling', examPage: 2, examEndPage: 2,
    answerPage: 5, answerEndPage: 5, topicIds: ['biology-cell-division'], reasons: ['Mitose wordt getoetst.'],
    needsReview: true, warnings: ['Controleer de koppeling.'] }],
};

describe('import review and catalog integration', () => {
  let stream: ReturnType<typeof fakeStreamDeps>;
  beforeEach(() => {
    localStorage.clear();
    stream = fakeStreamDeps();
    TestBed.configureTestingModule({ providers: [{
      provide: PdfPreviewService,
      useValue: { renderPage: vi.fn().mockResolvedValue(true), cancelRender: vi.fn() },
    }, { provide: PROGRESS_STREAM_DEPS, useValue: stream.deps }] });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  async function review(state: ImportRecord = record) {
    const fetch = vi.fn().mockImplementation((_url: string, options?: RequestInit) =>
      Promise.resolve(Response.json(options?.method === 'PATCH'
        ? { ...state, revision: '"v2"', ...JSON.parse(String(options.body)) }
        : _url === '/api/imports' ? [state] : state)));
    vi.stubGlobal('fetch', fetch);
    const fixture = TestBed.createComponent(ExamImportComponent);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    root.querySelector<HTMLButtonElement>('.import-item')!.click();
    await fixture.whenStable();
    return { fixture, root, fetch };
  }

  it('does not ask for year or timevak when starting an import', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json([])));
    const fixture = TestBed.createComponent(ExamImportComponent);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    root.querySelector<HTMLButtonElement>('.import-panel header button')!.click();
    await fixture.whenStable();
    const dialog = root.querySelector('dialog')!;
    expect(dialog.textContent).toContain(
      'Jaar en tijdvak worden automatisch uit het examen en correctievoorschrift gehaald.');
    const labels = [...dialog.querySelectorAll('label')].map((label) => label.textContent).join(' ');
    expect(labels).not.toContain('Jaar');
    expect(labels).not.toContain('Tijdvak');
  });

  it('submits PDF links instead of files when the link source is chosen', async () => {
    const fetch = vi.fn().mockImplementation((url: string, options?: RequestInit) =>
      Promise.resolve(Response.json(options?.method === 'POST' ? record : [])));
    vi.stubGlobal('fetch', fetch);
    const fixture = TestBed.createComponent(ExamImportComponent);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    root.querySelector<HTMLButtonElement>('.import-panel header button')!.click();
    await fixture.whenStable();
    const form = root.querySelector('form')!;
    expect(form.querySelector('input[name="exam"]')).not.toBeNull();
    expect(form.querySelector('input[name="examUrl"]')).toBeNull();
    for (const field of ['exam', 'answers']) {
      const radio = form.querySelector<HTMLInputElement>(`input[name="${field}Mode"][value="url"]`)!;
      radio.click();
      await fixture.whenStable();
      expect(form.querySelector(`input[name="${field}"]`)).toBeNull();
      const url = form.querySelector<HTMLInputElement>(`input[name="${field}Url"]`)!;
      url.value = `https://www.alleexamens.nl/${field}.pdf`;
    }
    expect(form.querySelector('input[name="permission"]')).toBeNull();
    form.requestSubmit();
    await fixture.whenStable();
    const post = fetch.mock.calls.find(([, options]) => (options as RequestInit | undefined)?.method === 'POST')!;
    const body = (post[1] as RequestInit).body as FormData;
    expect(post[0]).toBe('/api/imports');
    expect(body.get('examUrl')).toBe('https://www.alleexamens.nl/exam.pdf');
    expect(body.get('answersUrl')).toBe('https://www.alleexamens.nl/answers.pdf');
    expect(body.has('exam')).toBe(false);
    expect(body.has('answers')).toBe(false);
    expect(body.has('examMode')).toBe(false);
  });

  const duplicateProblem = {
    status: 409, title: 'Dit document is al eerder verwerkt.',
    duplicates: [{ importId: 'import-1', state: 'review', year: 2025, timevak: 1, role: 'exam', documentId: 'exam-1' }],
  };

  async function openDialog(fetch: ReturnType<typeof vi.fn>) {
    vi.stubGlobal('fetch', fetch);
    const fixture = TestBed.createComponent(ExamImportComponent);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    root.querySelector<HTMLButtonElement>('.import-panel header button')!.click();
    await fixture.whenStable();
    return { fixture, root, form: root.querySelector('form')! };
  }

  function button(root: HTMLElement, text: string): HTMLButtonElement {
    return [...root.querySelectorAll<HTMLButtonElement>('button')].find((item) => item.textContent?.includes(text))!;
  }

  it('shows a Dutch duplicate message with open and reprocess actions', async () => {
    const fetch = vi.fn().mockImplementation((url: string, options?: RequestInit) => {
      if (options?.method === 'POST' && url === '/api/imports') {
        return Promise.resolve(Response.json(duplicateProblem, { status: 409, headers: { 'content-type': 'application/problem+json' } }));
      }
      if (options?.method === 'POST' && url === '/api/imports/import-1/reprocess') {
        return Promise.resolve(Response.json({ ...record, id: 'import-2', state: 'queued', questions: [] }));
      }
      return Promise.resolve(Response.json(url === '/api/imports' ? [record] : record));
    });
    const { fixture, root, form } = await openDialog(fetch);
    for (const field of ['exam', 'answers']) {
      form.querySelector<HTMLInputElement>(`input[name="${field}Mode"][value="url"]`)!.click();
      await fixture.whenStable();
      form.querySelector<HTMLInputElement>(`input[name="${field}Url"]`)!.value = `https://www.alleexamens.nl/${field}.pdf`;
    }
    form.requestSubmit();
    await fixture.whenStable();
    const message = root.querySelector('.duplicates')!;
    expect(message.textContent).toContain('Dit document is al eerder verwerkt.');
    expect(message.textContent).toContain('2025, tijdvak 1');
    expect(message.textContent).toContain('Klaar voor beoordeling');

    button(root, 'Opnieuw verwerken met andere onderwerpen').click();
    await fixture.whenStable();
    const call = fetch.mock.calls.find(([url]) => url === '/api/imports/import-1/reprocess')!;
    const body = (call[1] as RequestInit).body as FormData;
    expect(JSON.parse(String(body.get('metadata'))).level).toBe('havo');
    expect(body.has('exam')).toBe(false);
    expect(body.has('examUrl')).toBe(false);
    expect(root.querySelector('.duplicates')).toBeNull();
  });

  it('opens the earlier import from the duplicate message', async () => {
    const fetch = vi.fn().mockImplementation((url: string, options?: RequestInit) =>
      Promise.resolve(options?.method === 'POST'
        ? Response.json(duplicateProblem, { status: 409, headers: { 'content-type': 'application/problem+json' } })
        : Response.json(url === '/api/imports' ? [] : record)));
    const { fixture, root, form } = await openDialog(fetch);
    for (const field of ['exam', 'answers']) {
      form.querySelector<HTMLInputElement>(`input[name="${field}Mode"][value="url"]`)!.click();
      await fixture.whenStable();
      form.querySelector<HTMLInputElement>(`input[name="${field}Url"]`)!.value = `https://www.alleexamens.nl/${field}.pdf`;
    }
    form.requestSubmit();
    await fixture.whenStable();
    button(root, 'Bestaande import openen').click();
    await fixture.whenStable();
    expect(fetch).toHaveBeenCalledWith('/api/imports/import-1', expect.anything());
    expect(root.querySelector('.review')).not.toBeNull();
  });

  it('reprocesses an existing import without file inputs and keeps its topics', async () => {
    const queued = { ...record, id: 'import-2', state: 'queued' as const, questions: [] };
    const { fetch, fixture, root } = await review();
    fetch.mockImplementation((url: string, options?: RequestInit) =>
      Promise.resolve(Response.json(options?.method === 'POST' ? queued : url === '/api/imports' ? [record] : record)));
    button(root, 'Opnieuw verwerken met andere onderwerpen').click();
    await fixture.whenStable();
    const form = root.querySelector('form')!;
    expect(form.querySelector('input[type="file"][name="exam"]')).toBeNull();
    expect(form.querySelector('input[name="examUrl"]')).toBeNull();
    expect(form.textContent).toContain('Celdeling');
    form.requestSubmit();
    await fixture.whenStable();
    const call = fetch.mock.calls.find(([url]) => url === '/api/imports/import-1/reprocess')!;
    const metadata = JSON.parse(String(((call[1] as RequestInit).body as FormData).get('metadata')));
    expect(metadata.topics[0].id).toBe('biology-cell-division');
    expect(root.textContent).toContain('niet opnieuw geupload');
  });

  it('requires review and saved changes before publication, and saves the revision with corrections', async () => {
    const { fixture, root, fetch } = await review();
    const publish = () => [...root.querySelectorAll<HTMLButtonElement>('button')]
      .find((button) => button.textContent?.includes('Goedkeuren en publiceren'))!;
    expect(publish().disabled).toBe(true);
    const checked = root.querySelector<HTMLInputElement>('.review > fieldset .review-check input')!;
    checked.checked = true;
    checked.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(publish().disabled).toBe(true);
    [...root.querySelectorAll<HTMLButtonElement>('button')].find((button) =>
      button.textContent === 'Concept opslaan')!.click();
    await fixture.whenStable();
    expect(fetch).toHaveBeenCalledWith('/api/imports/import-1/draft', expect.objectContaining({
      method: 'PATCH', body: expect.stringContaining('"revision":"\\"v1\\""'),
    }));
    expect(publish().disabled).toBe(false);
    expect(TestBed.inject(PdfPreviewService).renderPage).toHaveBeenCalledWith('/api/documents/exam-1', 2, expect.any(HTMLCanvasElement));
  });

  describe('bulk approval', () => {
    const base = record.questions[0];
    const many: ImportRecord = { ...record, questions: [
      { ...base, id: 'question-1', number: 1 },
      { ...base, id: 'question-2', number: 2 },
      { ...base, id: 'question-3', number: 3, answerPage: null, answerEndPage: null },
    ] };
    const button = (root: HTMLElement, text: string) => [...root.querySelectorAll<HTMLButtonElement>('button')]
      .find((item) => item.textContent?.trim() === text)!;
    const patches = (fetch: ReturnType<typeof vi.fn>) => fetch.mock.calls.filter(([, o]) => (o as RequestInit | undefined)?.method === 'PATCH');

    it('renders the summary above the bulk-approve row', async () => {
      const { root } = await review(many);
      const summary = root.querySelector('app-import-summary')!;
      const bulk = root.querySelector('.bulk-approve')!;
      expect(summary.compareDocumentPosition(bulk) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    });

    it('does not list unchecked questions for a fresh import', async () => {
      const { root } = await review({ ...many, questions: many.questions.slice(0, 2) });
      expect(root.querySelector('.attention')).toBeNull();
      expect(root.textContent).not.toContain('nog niet gecontroleerd');
    });

    it('lists real issues only, with the unchecked count', async () => {
      const { root } = await review(many);
      const text = root.querySelector('.attention')!.textContent!;
      expect(text).toContain('Vraag 3: antwoordpagina ontbreekt');
      expect(text).not.toContain('Vraag 1');
      expect(text).toContain('Nog 2 vragen niet gecontroleerd');
    });

    it('shows the first 8 issues and puts the rest in a details element', async () => {
      const broken = Array.from({ length: 10 }, (_, index) => ({
        ...base, id: `question-${index + 1}`, number: index + 1, answerPage: null, answerEndPage: null }));
      const { root } = await review({ ...many, questions: broken });
      const direct = root.querySelectorAll('.attention > ul > li');
      expect(direct).toHaveLength(8);
      const more = root.querySelector('.attention-more')!;
      expect(more.querySelector('summary')!.textContent).toContain('Toon alle 10 vragen');
      expect(more.querySelectorAll('li')).toHaveLength(2);
      expect(more.textContent).toContain('Vraag 10: antwoordpagina ontbreekt');
    });

    it('asks for confirmation and sends nothing when cancelled', async () => {
      const { fixture, root, fetch } = await review(many);
      button(root, 'Alles goedkeuren').click();
      await fixture.whenStable();
      expect(root.textContent).toContain('goedkeuren zoals ze zijn voorgesteld?');
      button(root, 'Annuleren').click();
      await fixture.whenStable();
      expect(patches(fetch)).toHaveLength(0);
      expect(root.textContent).toContain('0 van 3 vragen goedgekeurd');
    });

    it('approves valid questions, skips invalid ones, lists them and saves with the revision', async () => {
      const { fixture, root, fetch } = await review(many);
      button(root, 'Alles goedkeuren').click();
      await fixture.whenStable();
      button(root, 'Ja, alles goedkeuren').click();
      await fixture.whenStable();
      const [call] = patches(fetch);
      const body = JSON.parse(String((call[1] as RequestInit).body));
      expect(body.revision).toBe('"v1"');
      expect(body.questions.map((q: { needsReview: boolean }) => q.needsReview)).toEqual([false, false, true]);
      expect(root.textContent).toContain('2 van 3 vragen goedgekeurd');
      expect(root.querySelector('.attention')!.textContent).toContain('Vraag 3: antwoordpagina ontbreekt');
      expect(button(root, 'Goedkeuren en publiceren').disabled).toBe(true);
    });

    it('enables publishing straight after approving everything and allows un-approving one question', async () => {
      const { fixture, root } = await review({ ...many, questions: many.questions.slice(0, 2) });
      button(root, 'Alles goedkeuren').click();
      await fixture.whenStable();
      button(root, 'Ja, alles goedkeuren').click();
      await fixture.whenStable();
      expect(root.textContent).toContain('2 van 2 vragen goedgekeurd');
      expect(root.querySelector('.attention')).toBeNull();
      expect(button(root, 'Goedkeuren en publiceren').disabled).toBe(false);
      const check = root.querySelector<HTMLInputElement>('.review > fieldset .review-check input')!;
      check.checked = false;
      check.dispatchEvent(new Event('change'));
      await fixture.whenStable();
      expect(root.textContent).toContain('1 van 2 vragen goedgekeurd');
      expect(button(root, 'Goedkeuren en publiceren').disabled).toBe(true);
    });

    it('shows the stale-revision message on 409 and keeps the draft unapproved on the server', async () => {
      const fetch = vi.fn().mockImplementation((url: string, options?: RequestInit) =>
        Promise.resolve(options?.method === 'PATCH' ? new Response(null, { status: 409 })
          : Response.json(url === '/api/imports' ? [many] : many)));
      vi.stubGlobal('fetch', fetch);
      const fixture = TestBed.createComponent(ExamImportComponent);
      await fixture.whenStable();
      const root: HTMLElement = fixture.nativeElement;
      root.querySelector<HTMLButtonElement>('.import-item')!.click();
      await fixture.whenStable();
      button(root, 'Alles goedkeuren').click();
      await fixture.whenStable();
      button(root, 'Ja, alles goedkeuren').click();
      await fixture.whenStable();
      expect(root.querySelector('.error')!.textContent).toContain('De import is gewijzigd');
      expect(root.textContent).not.toContain('vragen zijn goedgekeurd');
    });
  });

  it('pauses screenshot-derived definition processing for explicit confirmation', async () => {
    const { fixture, root, fetch } = await review({ ...record, state: 'awaitingDefinitions', questions: [] });
    expect(root.querySelector('.previews')).toBeNull();
    expect(fetch.mock.calls.some(([url]) => String(url).endsWith('confirm-definitions'))).toBe(false);
    [...root.querySelectorAll<HTMLButtonElement>('button')].find((button) =>
      button.textContent?.includes('Onderwerpen bevestigen en verwerken'))!.click();
    await fixture.whenStable();
    expect(fetch).toHaveBeenCalledWith('/api/imports/import-1/confirm-definitions', expect.objectContaining({ method: 'POST' }));
  });

  it('merges published canonical topics into the legacy navigation and preserves completion IDs', async () => {
    const data = TestBed.inject(ExamDataService);
    const existingId = data.questions()[0].id;
    data.toggleDone(existingId);
    const catalog: ExamCatalog = {
      subjects: [{ id: 'biology', title: 'Biologie' }], topics: record.topics,
      exams: [{ id: 'e1', subjectId: 'biology', level: 'havo', year: 2026, timevak: 1,
        examFile: '/api/documents/exam-1', answerFile: '/api/documents/answers-1' }],
      questions: [{ id: 'new-q1', topics: ['biology-cell-division'], title: 'Mitose',
        examFile: '/api/documents/exam-1', examPage: 2, answerFile: '/api/documents/answers-1',
        answerPage: 5, year: 2026, timevak: 1, number: 1, subjectId: 'biology' }],
    };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json(catalog)));
    await data.refreshCatalog(new AbortController().signal);
    expect(data.getQuestionsForTopic('2.3').some((question) => question.id === 'new-q1')).toBe(true);
    expect(data.topics().filter((topic) => topic.id === '2.3')).toHaveLength(1);
    expect(data.years()).toContain(2026);
    expect(data.isDone(existingId)).toBe(true);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status: 503 })));
    await data.refreshCatalog(new AbortController().signal);
    expect(data.catalogError()).toContain('niet beschikbaar');
    expect(data.getQuestionsForTopic('2.3').some((question) => question.id === 'new-q1')).toBe(true);
  });

  it('replaces a published bundled sitting without duplicate exams or loss of existing question completion', async () => {
    const data = TestBed.inject(ExamDataService);
    const oldQuestion = data.questions()[0];
    data.toggleDone(oldQuestion.id);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({
      subjects: [{ id: 'biology', title: 'Biologie' }], topics: [],
      exams: [{ id: 'replacement', subjectId: 'biology', level: 'havo', year: oldQuestion.year, timevak: oldQuestion.timevak,
        examFile: '/api/documents/replacement', answerFile: '/api/documents/correction' }],
      questions: [{ ...oldQuestion, id: 'server-question-id', subjectId: 'biology',
        topics: ['biology-cells'], examFile: '/api/documents/replacement', answerFile: '/api/documents/correction' }],
    })));
    await data.refreshCatalog(new AbortController().signal);
    expect(data.exams().filter((exam) => exam.year === oldQuestion.year && exam.timevak === oldQuestion.timevak)).toHaveLength(1);
    const current = data.getQuestionsForTopic('2.1', '/api/documents/replacement')[0];
    expect(current.id).toBe(oldQuestion.id);
    expect(data.isDone(current.id)).toBe(true);
    expect(data.questions().some((question) => question.examFile === oldQuestion.examFile)).toBe(false);
  });

  it('searches topic aliases independently of chapter metadata in the rendered navigation', async () => {
    const fixture = TestBed.createComponent(QuestionSidebarComponent);
    fixture.componentRef.setInput('activeTopic', '2.1');
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    const input = root.querySelector<HTMLInputElement>('input[type="search"]')!;
    input.value = 'mitose';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(root.querySelectorAll('.topic-button')).toHaveLength(1);
    expect(root.querySelector('.topic-button')?.textContent).toContain('Celdeling en kanker');
  });

  describe('list order and deletion', () => {
    const older: ImportRecord = { ...record, id: 'import-old', createdAt: '2026-11-10T09:00:00Z', state: 'failed', questions: [] };
    const newer: ImportRecord = { ...record, id: 'import-new', createdAt: '2026-11-14T09:00:00Z', state: 'review' };
    const published: ImportRecord = { ...record, id: 'import-pub', createdAt: '2026-11-12T09:00:00Z', state: 'published' };
    const busyRecord: ImportRecord = { ...record, id: 'import-busy', createdAt: '2026-11-11T09:00:00Z', state: 'tagging' };
    const all = [older, newer, published, busyRecord];
    type Overrides = Record<string, (options?: RequestInit) => Response>;

    function stub(listed: ImportRecord[], overrides: Overrides) {
      const fetch = vi.fn().mockImplementation((url: string, options?: RequestInit) => {
        const handler = overrides[`${options?.method ?? 'GET'} ${url}`];
        if (handler) return Promise.resolve(handler(options));
        if (url === '/api/imports') return Promise.resolve(Response.json(listed));
        return Promise.resolve(Response.json(listed.find((item) => url === `/api/imports/${item.id}`) ?? listed[0]));
      });
      vi.stubGlobal('fetch', fetch);
      return fetch;
    }

    function ids(root: HTMLElement): string[] {
      return [...root.querySelectorAll('.import-item small')].map((item) => item.textContent!.split(' · ')[1]);
    }

    async function open(listed: ImportRecord[], overrides: Overrides = {}) {
      const fetch = stub(listed, overrides);
      const fixture = TestBed.createComponent(ExamImportComponent);
      await fixture.whenStable();
      return { fixture, root: fixture.nativeElement as HTMLElement, fetch };
    }

    const noContent = () => new Response(null, { status: 204 });

    it('shows newest first with the id as tiebreaker, whatever order the server returns', async () => {
      const tie: ImportRecord = { ...older, id: 'import-aaa', createdAt: older.createdAt };
      const { root } = await open([older, tie, published, newer, busyRecord]);
      expect(ids(root)).toEqual(['import-n', 'import-p', 'import-b', 'import-a', 'import-o']);
    });

    it('keeps the order after selecting, refreshing the selected item and refreshing the list', async () => {
      const { fixture, root, fetch } = await open(all);
      const before = ids(root);
      expect(before).toEqual(['import-n', 'import-p', 'import-b', 'import-o']);
      root.querySelectorAll<HTMLButtonElement>('.import-item')[3].click();
      await fixture.whenStable();
      expect(ids(root)).toEqual(before);
      expect(root.querySelector('.import-item.active small')?.textContent).toContain('import-o');
      button(root, 'Status vernieuwen').click();
      await fixture.whenStable();
      expect(ids(root)).toEqual(before);
      root.querySelectorAll<HTMLButtonElement>('.import-item')[0].click();
      await fixture.whenStable();
      expect(ids(root)).toEqual(before);
      button(root, 'Lijst vernieuwen').click();
      await fixture.whenStable();
      expect(ids(root)).toEqual(before);
      expect(fetch).toHaveBeenCalledWith('/api/imports/import-old', expect.anything());
    });

    it('replaces an updated record in place', async () => {
      const { fixture, root } = await open(all, {
        'POST /api/imports/import-old/retry': () => Response.json({ ...older, state: 'queued', revision: '"v2"' }),
      });
      const before = ids(root);
      root.querySelectorAll<HTMLButtonElement>('.import-item')[3].click();
      await fixture.whenStable();
      button(root, 'Opnieuw verwerken').click();
      await fixture.whenStable();
      expect(ids(root)).toEqual(before);
      expect(root.querySelectorAll('.import-item span:not(.row-times)')[3].textContent).toBe('In wachtrij');
    });

    it('asks for confirmation before deleting and cancels without a request', async () => {
      const { fixture, root, fetch } = await open(all);
      root.querySelector<HTMLButtonElement>('.delete-button')!.click();
      await fixture.whenStable();
      expect(root.querySelector('.delete-confirm')!.textContent).toContain('definitief verwijderen');
      expect(fetch.mock.calls.some(([, options]) => (options as RequestInit | undefined)?.method === 'DELETE')).toBe(false);
      button(root, 'Annuleren').click();
      await fixture.whenStable();
      expect(root.querySelector('.delete-confirm')).toBeNull();
      expect(ids(root)).toHaveLength(4);
    });

    it('labels the delete buttons and disables them for published and processing imports', async () => {
      const { root } = await open(all);
      const buttons = [...root.querySelectorAll<HTMLButtonElement>('.delete-button')];
      expect(buttons.map((item) => item.getAttribute('aria-label'))).toEqual([
        'Import import-n verwijderen', 'Import import-p verwijderen',
        'Import import-b verwijderen', 'Import import-o verwijderen']);
      expect(buttons.map((item) => item.disabled)).toEqual([false, true, true, false]);
    });

    it('deletes an import from the list in place and keeps the order of the others', async () => {
      const { fixture, root, fetch } = await open(all, { 'DELETE /api/imports/import-old': noContent });
      root.querySelectorAll<HTMLButtonElement>('.delete-button')[3].click();
      await fixture.whenStable();
      button(root, 'Ja, verwijderen').click();
      await fixture.whenStable();
      expect(fetch).toHaveBeenCalledWith('/api/imports/import-old', expect.objectContaining({
        method: 'DELETE', headers: expect.objectContaining({ 'X-Examinator-Request': '1' }),
      }));
      expect(ids(root)).toEqual(['import-n', 'import-p', 'import-b']);
      expect(root.textContent).toContain('De import is verwijderd.');
    });

    it('closes the detail view when the open import is deleted', async () => {
      const { fixture, root } = await open(all, { 'DELETE /api/imports/import-new': noContent });
      root.querySelectorAll<HTMLButtonElement>('.import-item')[0].click();
      await fixture.whenStable();
      expect(root.querySelector('.review h2')).not.toBeNull();
      root.querySelectorAll<HTMLButtonElement>('.delete-button')[0].click();
      await fixture.whenStable();
      button(root, 'Ja, verwijderen').click();
      await fixture.whenStable();
      expect(root.querySelector('.review h2')).toBeNull();
      expect(root.querySelector('.review')!.textContent).toContain('Selecteer een import');
    });

    it('keeps the list and the open item when the server refuses the deletion', async () => {
      const { fixture, root } = await open(all, {
        'DELETE /api/imports/import-new': () => Response.json(
          { status: 409, title: 'Conflict', detail: 'Deze import wordt verwerkt en kan nu niet worden verwijderd.' },
          { status: 409, headers: { 'content-type': 'application/problem+json' } }),
      });
      root.querySelectorAll<HTMLButtonElement>('.import-item')[0].click();
      await fixture.whenStable();
      root.querySelectorAll<HTMLButtonElement>('.delete-button')[0].click();
      await fixture.whenStable();
      button(root, 'Ja, verwijderen').click();
      await fixture.whenStable();
      expect(root.querySelector('.error')!.textContent).toContain('wordt verwerkt');
      expect(ids(root)).toHaveLength(4);
      expect(root.querySelector('.review h2')).not.toBeNull();
      expect(root.querySelector('.delete-confirm')).toBeNull();
    });
  });

  describe('summary', () => {
    const base = record.questions[0];
    const withSecond: ImportRecord = { ...record, questions: [
      { ...base, id: 'question-1', number: 1 },
      { ...base, id: 'question-2', number: 2, title: 'Andere vraag', topicIds: [] },
    ] };

    it('summarises topics and not relevant questions and follows topic edits', async () => {
      const { fixture, root } = await review(withSecond);
      const summary = () => root.querySelector('app-import-summary')!;
      expect(summary().querySelector('h3')!.textContent).toBe('Samenvatting');
      expect(summary().querySelector('.topics li')!.textContent!.replace(/\s+/g, ' ')).toContain('Celdeling — 1 vraag: 1');
      expect(summary().querySelector('.not-relevant')!.textContent).toContain('1 vraag');
      const tags = root.querySelectorAll<HTMLInputElement>('.review > fieldset .tags input');
      tags[0].checked = false;
      tags[0].dispatchEvent(new Event('change'));
      await fixture.whenStable();
      expect(summary().querySelector('.topics li')!.textContent).toContain('Geen vragen gevonden');
      expect(summary().querySelector('.not-relevant')!.textContent).toContain('2 vragen');
      expect(summary().querySelector('.total')!.textContent).toContain('0 vragen gevonden in totaal');
    });

    it('selects a question from the summary', async () => {
      const { fixture, root } = await review(withSecond);
      const open = root.querySelector<HTMLElement>('app-import-summary details')!;
      expect(root.querySelector('.review > fieldset legend')!.textContent).toBe('Vraag 1');
      open.querySelector<HTMLButtonElement>('.number')!.click();
      await fixture.whenStable();
      expect(root.querySelector('.review > fieldset legend')!.textContent).toBe('Vraag 2');
    });

    it('shows the summary for a published import', async () => {
      const { root } = await review({ ...record, state: 'published' });
      expect(root.querySelector('app-import-summary .total')!.textContent).toContain('1 vragen gevonden in totaal');
    });
  });

  describe('live progress', () => {
    const tagging: ImportRecord = { ...record, id: 'import-busy', state: 'tagging', revision: '"v3"', questions: [] };
    const done: ImportRecord = { ...record, id: 'import-done', createdAt: '2026-11-12T09:00:00Z', state: 'review' };
    const event = (overrides: Partial<ImportProgressEvent> = {}): ImportProgressEvent => ({
      importId: 'import-busy', state: 'tagging', revision: '"v3"', phase: 'tagging', step: 7, total: 32, unit: 'vraag',
      modelCalls: 12, modelCallBudget: 64, message: 'Vraag 7 van 32: onderwerpen toekennen',
      updatedAt: '2026-11-14T13:32:05Z', heartbeatAt: '2026-11-14T13:32:05Z', serverTime: '2026-11-14T13:32:06Z',
      stale: false, ...overrides,
    });

    async function open(listed: ImportRecord[], records: Record<string, ImportRecord> = {}) {
      const fetch = vi.fn().mockImplementation((url: string) => {
        if (url === '/api/imports') return Promise.resolve(Response.json(listed));
        const id = url.replace('/api/imports/', '');
        const found = records[id] ?? listed.find((item) => item.id === id);
        return Promise.resolve(found ? Response.json(found)
          : Response.json({ status: 404, title: 'Niet gevonden' }, { status: 404, headers: { 'content-type': 'application/problem+json' } }));
      });
      vi.stubGlobal('fetch', fetch);
      const fixture = TestBed.createComponent(ExamImportComponent);
      await fixture.whenStable();
      const root: HTMLElement = fixture.nativeElement;
      const loads = (id: string) => fetch.mock.calls.filter(([url]) => url === `/api/imports/${id}`).length;
      return { fixture, root, fetch, loads };
    }

    it('opens no stream when nothing is processing', async () => {
      await open([done]);
      expect(stream.sources).toHaveLength(0);
    });

    it('shows compact progress in every processing row from one stream', async () => {
      const second: ImportRecord = { ...tagging, id: 'import-queued', state: 'queued', createdAt: '2026-11-13T09:00:00Z' };
      const { fixture, root } = await open([tagging, second, done]);
      expect(stream.sources).toHaveLength(1);
      stream.latest().open();
      stream.latest().emit('progress', event());
      stream.latest().emit('progress', event({ importId: 'import-queued', state: 'queued', phase: 'queued', step: null,
        total: null, unit: null, modelCalls: null, modelCallBudget: null, revision: second.revision,
        message: 'Wachten op de achtergrondtaak (kan tot een minuut duren)' }));
      await fixture.whenStable();
      const rows = [...root.querySelectorAll('.import-row')];
      expect(rows[0].querySelector('app-import-progress')!.textContent).toContain('Vraag 7 van 32');
      expect(rows[0].querySelector('progress')!.getAttribute('max')).toBe('32');
      expect(rows[1].querySelector('app-import-progress')!.textContent).toContain('Wachtrij');
      expect(rows[2].querySelector('app-import-progress')).toBeNull();
      expect(stream.sources).toHaveLength(1);
    });

    const run = (number: number, outcome: 'review' | 'failed' | 'awaitingDefinitions', startedAt: string, endedAt: string) => ({
      number, attempt: 1, startedAt, endedAt, outcome, phases: [{ phase: 'starting', startedAt, endedAt, message: 'Verwerking gestart',
        step: null, total: null, modelCalls: 0, outcome: outcome === 'failed' ? 'failed' as const : 'ok' as const,
        error: outcome === 'failed' ? 'Foundry gaf HTTP 500 terug.' : null }],
    });
    const timed: ImportRecord = { ...done, id: 'import-timed', createdAt: '2026-11-13T12:00:00Z',
      updatedAt: new Date(2026, 10, 14, 15, 2).toISOString(),
      timing: { processingStartedAt: '2026-11-14T14:00:00Z', completedAt: '2026-11-14T14:56:12Z', outcome: 'review',
        processingMilliseconds: 372_000, earlierProcessingMilliseconds: 30_000, earlierRunsUnknown: false, runs: [] } };
    const old: ImportRecord = { ...record, id: 'import-old', state: 'published', createdAt: '2026-11-10T09:00:00Z' };

    it('shows the last update and the processing time in list rows, and onbekend for older imports', async () => {
      const { fixture, root } = await open([tagging, timed, old]);
      stream.latest().open();
      stream.latest().emit('progress', event({ timing: { processingStartedAt: '2026-11-14T13:30:00Z', completedAt: null,
        outcome: null, processingMilliseconds: null, earlierProcessingMilliseconds: 0, earlierRunsUnknown: false,
        runs: [{ ...run(1, 'review', '2026-11-14T13:30:00Z', ''), endedAt: null, outcome: null }] } }));
      await fixture.whenStable();
      const rows = [...root.querySelectorAll('.import-row')];
      expect(rows[0].querySelector('app-import-progress')!.textContent).toContain('verstreken');
      expect(rows[0].querySelector('.row-times')!.textContent).not.toContain('Verwerkt in');
      expect(rows[1].querySelector('.row-times')!.textContent!.replace(/\s+/g, ' ').trim())
        .toBe('Bijgewerkt: 14-11-2026 15:02 · Verwerkt in 6 min 12 s');
      expect(rows[2].querySelector('.row-times')!.textContent!.replace(/\s+/g, ' ').trim())
        .toBe('Bijgewerkt: onbekend · Verwerkingstijd onbekend');
    });

    it('lets every settled import show its processing steps, newest run expanded', async () => {
      const detail: ImportRecord = { ...timed, timing: { ...timed.timing!, runs: [
        run(1, 'failed', '2026-11-14T14:00:00Z', '2026-11-14T14:00:30Z'),
        run(2, 'review', '2026-11-14T14:50:30Z', '2026-11-14T14:56:12Z')] } };
      const { fixture, root } = await open([timed, old], { 'import-timed': detail });
      root.querySelector<HTMLButtonElement>('.import-item')!.click();
      await fixture.whenStable();
      const steps = root.querySelector('.review app-import-steps')!;
      expect(steps.textContent).toContain('Verwerkingsstappen');
      expect(steps.textContent).toContain('Totale verwerkingstijd: 6 min 12 s');
      const runs = [...steps.querySelectorAll<HTMLDetailsElement>('details.run')];
      expect(runs.map((item) => item.open)).toEqual([true, false]);
      expect(runs[1].querySelector('tr.failed')!.textContent).toContain('Fout: Foundry gaf HTTP 500 terug.');

      root.querySelectorAll<HTMLButtonElement>('.import-item')[1].click();
      await fixture.whenStable();
      expect(root.querySelector('.review app-import-steps')!.textContent).toContain('Geen stapgeschiedenis beschikbaar voor deze import');
    });

    it('shows the detail stepper, counts, connection and a polite announcement for the selected import', async () => {
      const { fixture, root } = await open([tagging]);
      root.querySelector<HTMLButtonElement>('.import-item')!.click();
      await fixture.whenStable();
      stream.latest().open();
      stream.latest().emit('progress', event());
      await fixture.whenStable();
      const detail = root.querySelector('.review app-import-progress')!;
      expect(detail.querySelector('[aria-current="step"]')!.textContent).toBe('Onderwerpen toekennen');
      expect(detail.textContent).toContain('Vraag 7 van 32');
      expect(detail.textContent).toContain('Modelaanroepen: 12 van 64');
      expect(detail.textContent).toMatch(/Laatst bijgewerkt om \d\d:\d\d:\d\d/);
      expect(detail.querySelector('.connection')!.textContent).toContain('Live');
      const live = root.querySelector('.visually-hidden[aria-live="polite"]')!;
      expect(live.textContent).toContain('Onderwerpen toekennen: Vraag 7 van 32');
      stream.latest().emit('progress', event({ step: 8, message: 'Vraag 8 van 32: onderwerpen toekennen' }));
      await fixture.whenStable();
      expect(detail.textContent).toContain('Vraag 8 van 32');
      expect(live.textContent).toContain('Vraag 7 van 32');
    });

    it('marks a silent worker as stale and offers to reconnect after live updates fail', async () => {
      const { fixture, root } = await open([tagging]);
      root.querySelector<HTMLButtonElement>('.import-item')!.click();
      await fixture.whenStable();
      stream.latest().emit('progress', event({ stale: true }));
      await fixture.whenStable();
      expect(root.querySelector('.review .stale')!.textContent).toContain('reageert niet');
      expect(root.querySelector('.import-row .stale')!.textContent).toContain('Geen reactie');
      stream.latest().fail(true);
      await fixture.whenStable();
      expect(root.querySelector('.review .connection')!.textContent).toContain('elke 5 seconden');
      button(root, 'Opnieuw live verbinden').click();
      await fixture.whenStable();
      expect(stream.sources).toHaveLength(2);
    });

    it('loads a record exactly once when an event carries a newer revision', async () => {
      const updated = { ...tagging, revision: '"v4"', questions: record.questions };
      const { fixture, loads } = await open([tagging], { 'import-busy': updated });
      stream.latest().emit('progress', event({ revision: '"v4"' }));
      await fixture.whenStable();
      stream.latest().emit('progress', event({ revision: '"v4"', step: 8 }));
      await fixture.whenStable();
      expect(loads('import-busy')).toBe(1);
    });

    it('refreshes a settled import in place and closes the stream when nothing processes any more', async () => {
      const reviewed = { ...tagging, state: 'review' as const, revision: '"v9"', questions: record.questions };
      const { fixture, root, loads } = await open([tagging, done], { 'import-busy': reviewed });
      stream.latest().emit('progress', event());
      stream.latest().emit('settled', { importId: 'import-busy', state: 'review', revision: '"v9"' });
      await fixture.whenStable();
      expect(loads('import-busy')).toBe(1);
      expect(root.querySelectorAll('.import-item span')[0].textContent).toBe('Klaar voor beoordeling');
      expect(root.querySelector('.import-row app-import-progress')).toBeNull();
      expect(stream.latest().closed).toBe(true);
    });

    it('shows the summary immediately when the selected import settles into review', async () => {
      const reviewed = { ...tagging, state: 'review' as const, revision: '"v9"', questions: record.questions };
      const { fixture, root } = await open([tagging], { 'import-busy': tagging });
      root.querySelector<HTMLButtonElement>('.import-item')!.click();
      await fixture.whenStable();
      expect(root.querySelector('app-import-summary')).toBeNull();
      const fetch = vi.mocked(globalThis.fetch);
      fetch.mockImplementation(() => Promise.resolve(Response.json(reviewed)));
      stream.latest().emit('settled', { importId: 'import-busy', state: 'review', revision: '"v9"' });
      await fixture.whenStable();
      expect(root.querySelector('app-import-summary .total')!.textContent).toContain('1 vragen gevonden in totaal');
    });

    it('removes a row whose import was deleted elsewhere', async () => {
      const queued = { ...tagging, state: 'queued' as const };
      const { fixture, root } = await open([queued, done]);
      stream.latest().emit('settled', { importId: 'import-busy', state: 'deleted', revision: null });
      await fixture.whenStable();
      expect(root.querySelectorAll('.import-item')).toHaveLength(1);
    });

    it('closes the stream while the tab is hidden and reopens it when visible', async () => {
      const { fixture } = await open([tagging]);
      const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
      document.dispatchEvent(new Event('visibilitychange'));
      await fixture.whenStable();
      expect(stream.latest().closed).toBe(true);
      visibility.mockReturnValue('visible');
      document.dispatchEvent(new Event('visibilitychange'));
      await fixture.whenStable();
      expect(stream.sources).toHaveLength(2);
      expect(stream.latest().closed).toBe(false);
      visibility.mockRestore();
    });

    it('closes the stream when the component is destroyed', async () => {
      const { fixture } = await open([tagging]);
      fixture.destroy();
      expect(stream.latest().closed).toBe(true);
    });
  });
});
