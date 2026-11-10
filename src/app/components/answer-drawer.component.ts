import { Component, ElementRef, afterRenderEffect, effect, inject, input, output, signal, untracked, viewChild, viewChildren } from '@angular/core';
import { type ExamQuestion } from '../services/exam-data.service';
import { PdfPreviewService } from '../services/pdf-preview.service';

@Component({
  selector: 'app-answer-drawer',
  templateUrl: './answer-drawer.component.html',
})
export class AnswerDrawerComponent {
  private readonly pdfPreview = inject(PdfPreviewService);

  private readonly answerDocument = viewChild<ElementRef<HTMLDivElement>>('answerDocument');
  private readonly answerCanvases = viewChildren<ElementRef<HTMLCanvasElement>>('answerPageCanvas');

  readonly question = input.required<ExamQuestion>();
  readonly open = input(false);
  readonly closed = output<void>();

  protected readonly answerLoading = signal(true);
  protected readonly answerError = signal('');
  protected readonly answerPageNumbers = signal<number[]>([]);
  protected readonly renderedAnswerPages = signal(new Set<number>());
  protected readonly answerPageErrors = signal(new Map<number, string>());

  private renderVersion = 0;
  constructor() {
    effect((onCleanup) => {
      const open = this.open();
      const question = this.question();
      untracked(() => {
        if (open) void this.loadAnswerDocument(question);
      });
      onCleanup(() => this.cancelAnswerRenders());
    });
    afterRenderEffect(() => {
      const canvases = this.answerCanvases();
      const open = this.open();
      const question = this.question();
      untracked(() => {
        if (!open || !canvases.length) return;
        const version = this.renderVersion;
        void Promise.all(canvases.map((canvas) => this.renderAnswerPage(
          question.answerFile,
          Number(canvas.nativeElement.dataset['page']),
          canvas.nativeElement,
          version,
        ))).then(() => {
          if (version === this.renderVersion) this.scrollToAnswerPage(question.answerPage);
        });
      });
    });
  }

  private async loadAnswerDocument(question: ExamQuestion): Promise<void> {
    const renderVersion = ++this.renderVersion;
    this.answerPageNumbers.set([]);
    this.renderedAnswerPages.set(new Set());
    this.answerPageErrors.set(new Map());
    this.answerLoading.set(true);
    this.answerError.set('');

    try {
      const document = await this.pdfPreview.loadDocument(question.answerFile);
      if (renderVersion !== this.renderVersion) return;

      this.answerPageNumbers.set(Array.from({ length: document.numPages }, (_, index) => index + 1));
      this.answerLoading.set(false);
    } catch {
      if (renderVersion !== this.renderVersion) return;
      this.answerLoading.set(false);
      this.answerError.set('Het correctievoorschrift kon niet worden geladen.');
    }
  }

  private async renderAnswerPage(file: string, pageNumber: number, canvas: HTMLCanvasElement, version: number): Promise<void> {
    try {
      const rendered = await this.pdfPreview.renderPage(file, pageNumber, canvas);
      if (!rendered || version !== this.renderVersion) return;
      this.renderedAnswerPages.update((pages) => new Set(pages).add(pageNumber));
    } catch {
      if (version !== this.renderVersion) return;
      this.answerPageErrors.update((errors) => new Map(errors).set(pageNumber, 'De PDF-pagina kon niet worden geladen. Probeer de vraag opnieuw.'));
    }
  }

  private scrollToAnswerPage(pageNumber: number): void {
    const container = this.answerDocument()?.nativeElement;
    const page = container?.querySelector<HTMLElement>(`.answer-sheet[data-page="${pageNumber}"]`);
    if (!container || !page) return;
    const top = page.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop;
    container.scrollTo({ top, behavior: 'smooth' });
  }

  private cancelAnswerRenders(): void {
    this.renderVersion++;
    for (const canvas of this.answerCanvases()) this.pdfPreview.cancelRender(canvas.nativeElement);
  }
}
