import { Component, DestroyRef, ElementRef, afterRenderEffect, computed, effect, inject, input, output, signal, untracked, viewChild, viewChildren } from '@angular/core';
import { ExamDataService, type ExamQuestion } from '../services/exam-data.service';
import { PdfPreviewService } from '../services/pdf-preview.service';

@Component({
  selector: 'app-exam-viewer',
  templateUrl: './exam-viewer.component.html',
})
export class ExamViewerComponent {
  private readonly examData = inject(ExamDataService);
  private readonly pdfPreview = inject(PdfPreviewService);

  private readonly examDocument = viewChild<ElementRef<HTMLDivElement>>('examDocument');
  private readonly examCanvases = viewChildren<ElementRef<HTMLCanvasElement>>('examPageCanvas');

  readonly question = input.required<ExamQuestion>();
  readonly activeTopicTitle = input('');
  readonly questionIndex = input(0);
  readonly questionCount = input(0);
  readonly answerOpen = input(false);

  readonly answerToggled = output<void>();
  readonly completionToggled = output<void>();
  readonly previousQuestion = output<void>();
  readonly nextQuestion = output<void>();

  protected readonly examLoading = signal(true);
  protected readonly examPageNumbers = signal<number[]>([]);
  protected readonly renderedExamPages = signal(new Set<number>());
  protected readonly examPageErrors = signal(new Map<number, string>());
  protected readonly examError = signal('');

  private currentExamFile = '';
  private renderVersion = 0;
  private examObserver?: IntersectionObserver;
  private renderingExamPages = new Set<string>();

  protected readonly isDone = computed(() => this.examData.isDone(this.question().id));

  constructor() {
    effect(() => {
      const question = this.question();
      untracked(() => { void this.loadExamDocument(question); });
    });
    afterRenderEffect(() => {
      const canvases = this.examCanvases();
      const question = this.question();
      untracked(() => {
        if (!canvases.length || this.currentExamFile !== question.examFile) return;
        this.observeExamPages(this.renderVersion);
        this.scrollToExamPage(question.examPage);
        this.renderExamPage(question.examPage);
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.renderVersion++;
      this.examObserver?.disconnect();
      for (const canvas of this.examCanvases()) this.pdfPreview.cancelRender(canvas.nativeElement);
    });
  }

  private async loadExamDocument(question: ExamQuestion): Promise<void> {
    if (this.currentExamFile === question.examFile && this.examPageNumbers().length > 0) return;

    const renderVersion = ++this.renderVersion;
    this.examObserver?.disconnect();
    for (const canvas of this.examCanvases()) this.pdfPreview.cancelRender(canvas.nativeElement);
    this.currentExamFile = question.examFile;
    this.examPageNumbers.set([]);
    this.renderedExamPages.set(new Set());
    this.examPageErrors.set(new Map());
    this.renderingExamPages.clear();
    this.examLoading.set(true);
    this.examError.set('');

    try {
      const document = await this.pdfPreview.loadDocument(question.examFile);
      if (renderVersion !== this.renderVersion) return;

      this.examPageNumbers.set(Array.from({ length: document.numPages }, (_, index) => index + 1));
      this.examLoading.set(false);
    } catch {
      if (renderVersion !== this.renderVersion) return;
      this.examLoading.set(false);
      this.examError.set('Het examendocument kon niet worden geladen.');
    }
  }

  private observeExamPages(version: number): void {
    const container = this.examDocument()?.nativeElement;
    if (!container) return;

    this.examObserver?.disconnect();
    if (typeof IntersectionObserver === 'undefined') {
      for (const page of this.examPageNumbers()) this.renderExamPage(page);
      return;
    }

    this.examObserver = new IntersectionObserver((entries) => {
      if (version !== this.renderVersion) return;
      for (const entry of entries) {
        if (entry.isIntersecting) this.renderExamPage(Number((entry.target as HTMLElement).dataset['page']));
      }
    }, { root: container, rootMargin: '420px 0px' });

    container.querySelectorAll<HTMLElement>('.exam-page-sheet').forEach((page) => this.examObserver?.observe(page));
  }

  private renderExamPage(pageNumber: number): void {
    const canvas = this.examCanvases().find((item) => Number(item.nativeElement.dataset['page']) === pageNumber)?.nativeElement;
    const renderKey = `${this.currentExamFile}:${pageNumber}`;
    if (!canvas || this.renderedExamPages().has(pageNumber) || this.examPageErrors().has(pageNumber) || this.renderingExamPages.has(renderKey)) return;
    this.renderingExamPages.add(renderKey);
    void this.renderPage(pageNumber, canvas, renderKey);
  }

  private async renderPage(pageNumber: number, canvas: HTMLCanvasElement, renderKey: string): Promise<void> {
    const file = this.currentExamFile;
    const version = this.renderVersion;
    try {
      const rendered = await this.pdfPreview.renderPage(file, pageNumber, canvas);
      if (!rendered || version !== this.renderVersion || file !== this.currentExamFile) return;
      this.renderedExamPages.update((pages) => new Set(pages).add(pageNumber));
    } catch {
      if (version !== this.renderVersion || file !== this.currentExamFile) return;
      this.examPageErrors.update((errors) => new Map(errors).set(pageNumber, 'De PDF-pagina kon niet worden geladen. Probeer de vraag opnieuw.'));
    } finally {
      if (version === this.renderVersion) this.renderingExamPages.delete(renderKey);
    }
  }

  private scrollToExamPage(pageNumber: number): void {
    const container = this.examDocument()?.nativeElement;
    const page = container?.querySelector<HTMLElement>(`.exam-page-sheet[data-page="${pageNumber}"]`);
    if (!container || !page) return;
    const top = page.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop;
    container.scrollTo({ top, behavior: 'smooth' });
  }
}
