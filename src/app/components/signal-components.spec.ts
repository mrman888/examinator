import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppComponent } from '../app.component';
import { ExamDataService, type ExamQuestion } from '../services/exam-data.service';
import { PdfPreviewService } from '../services/pdf-preview.service';
import { AnswerDrawerComponent } from './answer-drawer.component';
import { ExamViewerComponent } from './exam-viewer.component';
import { QuestionSidebarComponent } from './question-sidebar.component';

describe('signal-based exam components', () => {
  const loadDocument = vi.fn<(file: string) => Promise<{ numPages: number }>>();
  const renderPage = vi.fn<(file: string, page: number, canvas: HTMLCanvasElement) => Promise<boolean>>();
  const cancelRender = vi.fn();
  let data: ExamDataService;
  let question: ExamQuestion;

  beforeEach(() => {
    localStorage.clear();
    loadDocument.mockReset().mockResolvedValue({ numPages: 20 });
    renderPage.mockReset().mockResolvedValue(true);
    cancelRender.mockReset();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ subjects: [{ id: 'biology', title: 'Biologie' }], topics: [], questions: [], exams: [] })));
    vi.spyOn(HTMLElement.prototype, 'scrollTo').mockImplementation(() => {});
    TestBed.configureTestingModule({
      providers: [{
        provide: PdfPreviewService,
        useValue: { loadDocument, renderPage, cancelRender },
      }],
    });
    data = TestBed.inject(ExamDataService);
    question = data.questions()[0];
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('updates sidebar input filtering, outputs, and shared completion without manual change detection', async () => {
    const fixture = TestBed.createComponent(QuestionSidebarComponent);
    fixture.componentRef.setInput('activeTopic', '2.1');
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('.question-link')).toHaveLength(data.getQuestionsForTopic('2.1').length);

    fixture.componentRef.setInput('selectedExamFile', question.examFile);
    await fixture.whenStable();
    expect(root.querySelectorAll('.question-link')).toHaveLength(data.getQuestionsForTopic('2.1', question.examFile).length);

    const selected = vi.fn();
    fixture.componentInstance.questionSelected.subscribe(selected);
    root.querySelector<HTMLButtonElement>('.question-link')!.click();
    expect(selected).toHaveBeenCalledWith(question);

    data.toggleDone(question.id);
    await fixture.whenStable();
    expect(root.querySelector('.progress-copy strong')?.textContent).toBe('1');
    expect(root.querySelector('.done-dot')).not.toBeNull();
    expect(data.completedCount()).toBe(1);
    expect(data.progressPercent()).toBe(100 / data.questions().length);
    data.toggleDone(question.id);
    await fixture.whenStable();
    expect(root.querySelector('.progress-copy strong')?.textContent).toBe('0');
    expect(root.querySelector('.done-dot')).toBeNull();
  });

  describe.each(['import', 'AI test'] as const)('returning from the %s panel', (panel) => {
    it.each(['question', 'topic', 'exam'] as const)('restores the document preview when selecting a %s', async (selection) => {
      const fixture = TestBed.createComponent(AppComponent);
      await fixture.whenStable();
      const root: HTMLElement = fixture.nativeElement;
      const panelSelector = panel === 'import' ? 'app-exam-import' : 'app-ai-test';
      const panelButton = root.querySelectorAll<HTMLButtonElement>('.topbar > button')[panel === 'import' ? 0 : 1];
      if (panel === 'import') vi.mocked(fetch).mockResolvedValueOnce(Response.json([]));
      panelButton.click();
      await fixture.whenStable();
      expect(root.querySelector(panelSelector)).not.toBeNull();
      expect(root.querySelector('app-exam-viewer')).toBeNull();
      loadDocument.mockClear();
      renderPage.mockClear();

      if (selection === 'question') {
        root.querySelector<HTMLButtonElement>('.question-link')!.click();
      } else if (selection === 'topic') {
        root.querySelector<HTMLButtonElement>('.topic-button')!.click();
      } else {
        root.querySelector<HTMLButtonElement>('[aria-label="Selecteer examen"]')!.click();
        await fixture.whenStable();
        root.querySelector<HTMLButtonElement>('.year-option')!.click();
      }
      await fixture.whenStable();

      expect(root.querySelector(panelSelector)).toBeNull();
      expect(panelButton.getAttribute('aria-pressed')).toBe('false');
      expect(root.querySelector('app-exam-viewer h1')?.textContent).toBe(question.title);
      expect(loadDocument).toHaveBeenCalledWith(question.examFile);
      expect(renderPage).toHaveBeenCalledWith(question.examFile, question.examPage, expect.any(HTMLCanvasElement));
      expect(root.querySelector(`app-exam-viewer canvas[data-page="${question.examPage}"]`)?.classList.contains('is-hidden')).toBe(false);
    });
  });

  it('resets navigation and answer state when the topic or exam changes, including empty results', async () => {
    const fixture = TestBed.createComponent(AppComponent);
    await fixture.whenStable();
    const app = fixture.componentInstance;
    app.moveQuestion(1);
    app.toggleAnswer();
    app.selectTopic('2.4');
    await fixture.whenStable();
    expect(app.questionIndex()).toBe(0);
    expect(app.showAnswer()).toBe(false);
    expect(app.selectedQuestion()?.topics).toContain('2.4');

    app.selectExam(data.exams().find((exam) => exam.year === 2024)!);
    await fixture.whenStable();
    expect(app.selectedQuestion()).toBeUndefined();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('.empty-state h1')?.textContent).toBe('Geen vragen gevonden');
    app.toggleYearMenu();
    await fixture.whenStable();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await fixture.whenStable();
    expect(app.yearMenuOpen()).toBe(false);
    app.toggleYearMenu();
    await fixture.whenStable();
    root.querySelector<HTMLElement>('.empty-state')!.click();
    await fixture.whenStable();
    expect(app.yearMenuOpen()).toBe(false);
  });

  it('renders signal-query canvases, reuses the exam document, and emits navigation events', async () => {
    const fixture = TestBed.createComponent(ExamViewerComponent);
    fixture.componentRef.setInput('question', question);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('canvas')).toHaveLength(20);
    expect(renderPage).toHaveBeenCalledWith(question.examFile, question.examPage, expect.any(HTMLCanvasElement));
    expect(root.querySelector(`canvas[data-page="${question.examPage}"]`)?.classList.contains('is-hidden')).toBe(false);

    const next = vi.fn();
    fixture.componentRef.setInput('questionCount', 8);
    fixture.componentInstance.nextQuestion.subscribe(next);
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('[aria-label="Volgende vraag"]')!.click();
    expect(next).toHaveBeenCalledOnce();

    const sameFileQuestion = data.questions().find((item) => item.examFile === question.examFile && item.examPage !== question.examPage)!;
    fixture.componentRef.setInput('question', sameFileQuestion);
    await fixture.whenStable();
    expect(loadDocument).toHaveBeenCalledTimes(1);
    expect(root.querySelector('h1')?.textContent).toBe(sameFileQuestion.title);
    data.toggleDone(sameFileQuestion.id);
    await fixture.whenStable();
    expect(root.querySelector('.done-button')?.classList.contains('is-done')).toBe(true);
    fixture.destroy();
    expect(cancelRender).toHaveBeenCalledTimes(20);
  });

  it('ignores obsolete document loads after the exam changes or the viewer is destroyed', async () => {
    let resolveOld!: (document: { numPages: number }) => void;
    loadDocument.mockImplementationOnce(() => new Promise((resolve) => { resolveOld = resolve; }));
    const fixture = TestBed.createComponent(ExamViewerComponent);
    fixture.componentRef.setInput('question', question);
    await fixture.whenStable();
    const otherQuestion = data.questions().find((item) => item.examFile !== question.examFile)!;
    fixture.componentRef.setInput('question', otherQuestion);
    await fixture.whenStable();
    resolveOld({ numPages: 99 });
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('canvas')).toHaveLength(20);
    expect(root.querySelector('h1')?.textContent).toBe(otherQuestion.title);

    loadDocument.mockImplementationOnce(() => new Promise((resolve) => { resolveOld = resolve; }));
    fixture.componentRef.setInput('question', question);
    await fixture.whenStable();
    const renderCount = renderPage.mock.calls.length;
    fixture.destroy();
    resolveOld({ numPages: 99 });
    await Promise.resolve();
    expect(renderPage).toHaveBeenCalledTimes(renderCount);
  });

  it('loads answers only while open, renders queries, emits close, and cancels on close', async () => {
    const fixture = TestBed.createComponent(AnswerDrawerComponent);
    fixture.componentRef.setInput('question', question);
    await fixture.whenStable();
    expect(loadDocument).not.toHaveBeenCalled();
    fixture.componentRef.setInput('open', true);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('canvas:not(.is-hidden)')).toHaveLength(20);
    expect(renderPage).toHaveBeenCalledWith(question.answerFile, question.answerPage, expect.any(HTMLCanvasElement));
    const closed = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);
    root.querySelector<HTMLButtonElement>('.drawer-close')!.click();
    expect(closed).toHaveBeenCalledOnce();

    fixture.componentRef.setInput('open', false);
    await fixture.whenStable();
    expect(cancelRender).toHaveBeenCalledTimes(20);
    expect(root.querySelector('aside')?.getAttribute('aria-hidden')).toBe('true');
    fixture.componentRef.setInput('open', true);
    await fixture.whenStable();
    expect(root.querySelectorAll('canvas:not(.is-hidden)')).toHaveLength(20);
  });

  it('ignores pending answer loads when closed', async () => {
    let resolveDocument!: (document: { numPages: number }) => void;
    loadDocument.mockImplementationOnce(() => new Promise((resolve) => { resolveDocument = resolve; }));
    const fixture = TestBed.createComponent(AnswerDrawerComponent);
    fixture.componentRef.setInput('question', question);
    fixture.componentRef.setInput('open', true);
    await fixture.whenStable();
    fixture.componentRef.setInput('open', false);
    await fixture.whenStable();
    resolveDocument({ numPages: 20 });
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('canvas')).toHaveLength(0);
    expect(renderPage).not.toHaveBeenCalled();
  });

  it('ignores stale answer render results after closing and reopening', async () => {
    const pendingRenders: Array<(rendered: boolean) => void> = [];
    renderPage.mockImplementation(() => new Promise((resolve) => { pendingRenders.push(resolve); }));
    const fixture = TestBed.createComponent(AnswerDrawerComponent);
    fixture.componentRef.setInput('question', question);
    fixture.componentRef.setInput('open', true);
    await fixture.whenStable();
    expect(pendingRenders).toHaveLength(20);
    fixture.componentRef.setInput('open', false);
    await fixture.whenStable();
    renderPage.mockResolvedValue(true);
    fixture.componentRef.setInput('open', true);
    await fixture.whenStable();
    pendingRenders.forEach((resolve) => resolve(false));
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('canvas:not(.is-hidden)')).toHaveLength(20);
    expect(root.querySelector('.pdf-error')).toBeNull();
  });

  it('surfaces document and page failures through reactive error state', async () => {
    loadDocument.mockRejectedValueOnce(new Error('unavailable'));
    const viewer = TestBed.createComponent(ExamViewerComponent);
    viewer.componentRef.setInput('question', question);
    await viewer.whenStable();
    const viewerRoot: HTMLElement = viewer.nativeElement;
    expect(viewerRoot.querySelector('.pdf-error')?.textContent).toContain('Het examendocument kon niet worden geladen.');

    renderPage.mockRejectedValue(new Error('render failed'));
    const drawer = TestBed.createComponent(AnswerDrawerComponent);
    drawer.componentRef.setInput('question', question);
    drawer.componentRef.setInput('open', true);
    await drawer.whenStable();
    const drawerRoot: HTMLElement = drawer.nativeElement;
    expect(drawerRoot.querySelectorAll('.pdf-error')).toHaveLength(20);
  });
});
