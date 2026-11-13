import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ExamImportComponent } from './exam-import.component';
import { ExamDataService } from '../services/exam-data.service';
import { type ExamCatalog, type ImportRecord } from '../services/exam-import-client';
import { PdfPreviewService } from '../services/pdf-preview.service';
import { QuestionSidebarComponent } from './question-sidebar.component';

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
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [{
      provide: PdfPreviewService,
      useValue: { renderPage: vi.fn().mockResolvedValue(true), cancelRender: vi.fn() },
    }] });
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
});
