import { AfterViewInit, ChangeDetectorRef, Component, ElementRef, EventEmitter, Input, OnChanges, OnDestroy, Output, QueryList, SimpleChanges, ViewChild, ViewChildren, inject } from '@angular/core';
import { type ExamQuestion } from '../services/exam-data.service';
import { PdfPreviewService } from '../services/pdf-preview.service';

@Component({
  selector: 'app-answer-drawer',
  standalone: true,
  templateUrl: './answer-drawer.component.html',
})
export class AnswerDrawerComponent implements AfterViewInit, OnChanges, OnDestroy {
  private readonly changeDetector = inject(ChangeDetectorRef);
  private readonly pdfPreview = inject(PdfPreviewService);

  @ViewChild('answerDocument') private answerDocument?: ElementRef<HTMLDivElement>;
  @ViewChildren('answerPageCanvas') private answerCanvases!: QueryList<ElementRef<HTMLCanvasElement>>;

  @Input({ required: true }) question!: ExamQuestion;
  @Input() open = false;
  @Output() closed = new EventEmitter<void>();

  answerLoading = true;
  answerError = '';
  answerPageNumbers: number[] = [];
  renderedAnswerPages = new Set<number>();
  answerPageErrors = new Map<number, string>();

  private renderVersion = 0;
  private viewReady = false;

  ngAfterViewInit(): void {
    this.viewReady = true;
    if (this.open) void this.renderAnswerDocument();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (!this.viewReady) return;
    if (changes['open'] && !this.open) this.cancelAnswerRenders();
    if (this.open && (changes['open'] || changes['question'])) void this.renderAnswerDocument();
  }

  ngOnDestroy(): void {
    this.cancelAnswerRenders();
  }

  private async renderAnswerDocument(): Promise<void> {
    const renderVersion = ++this.renderVersion;
    this.answerPageNumbers = [];
    this.renderedAnswerPages = new Set();
    this.answerPageErrors = new Map();
    this.answerLoading = true;
    this.answerError = '';
    this.changeDetector.detectChanges();

    try {
      const document = await this.pdfPreview.loadDocument(this.question.answerFile);
      if (renderVersion !== this.renderVersion) return;

      this.answerPageNumbers = Array.from({ length: document.numPages }, (_, index) => index + 1);
      this.answerLoading = false;
      this.changeDetector.detectChanges();
      if (renderVersion !== this.renderVersion) return;

      await Promise.all(this.answerCanvases.map((canvas) => this.renderAnswerPage(
        this.question.answerFile,
        Number(canvas.nativeElement.dataset['page']),
        canvas.nativeElement,
        renderVersion,
      )));
      if (renderVersion === this.renderVersion) this.scrollToAnswerPage(this.question.answerPage);
    } catch {
      if (renderVersion !== this.renderVersion) return;
      this.answerLoading = false;
      this.answerError = 'Het correctievoorschrift kon niet worden geladen.';
      this.changeDetector.detectChanges();
    }
  }

  private async renderAnswerPage(file: string, pageNumber: number, canvas: HTMLCanvasElement, version: number): Promise<void> {
    try {
      const rendered = await this.pdfPreview.renderPage(file, pageNumber, canvas);
      if (!rendered || version !== this.renderVersion) return;
      this.renderedAnswerPages = new Set(this.renderedAnswerPages).add(pageNumber);
      this.answerPageErrors.delete(pageNumber);
      this.changeDetector.detectChanges();
    } catch {
      if (version !== this.renderVersion) return;
      this.answerPageErrors = new Map(this.answerPageErrors).set(pageNumber, 'De PDF-pagina kon niet worden geladen. Probeer de vraag opnieuw.');
      this.changeDetector.detectChanges();
    }
  }

  private scrollToAnswerPage(pageNumber: number): void {
    const container = this.answerDocument?.nativeElement;
    const page = container?.querySelector<HTMLElement>(`.answer-sheet[data-page="${pageNumber}"]`);
    if (!container || !page) return;
    const top = page.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop;
    container.scrollTo({ top, behavior: 'smooth' });
  }

  private cancelAnswerRenders(): void {
    this.renderVersion++;
    for (const canvas of this.answerCanvases ?? []) this.pdfPreview.cancelRender(canvas.nativeElement);
  }
}
