import { AfterViewInit, ChangeDetectorRef, Component, ElementRef, EventEmitter, Input, OnChanges, OnDestroy, Output, QueryList, SimpleChanges, ViewChild, ViewChildren, inject } from '@angular/core';
import { ExamDataService, type ExamQuestion } from '../services/exam-data.service';
import { PdfPreviewService } from '../services/pdf-preview.service';

@Component({
  selector: 'app-exam-viewer',
  standalone: true,
  templateUrl: './exam-viewer.component.html',
})
export class ExamViewerComponent implements AfterViewInit, OnChanges, OnDestroy {
  private readonly changeDetector = inject(ChangeDetectorRef);
  private readonly examData = inject(ExamDataService);
  private readonly pdfPreview = inject(PdfPreviewService);

  @ViewChild('examDocument') private examDocument?: ElementRef<HTMLDivElement>;
  @ViewChildren('examPageCanvas') private examCanvases!: QueryList<ElementRef<HTMLCanvasElement>>;

  @Input({ required: true }) question!: ExamQuestion;
  @Input() activeTopicTitle = '';
  @Input() questionIndex = 0;
  @Input() questionCount = 0;
  @Input() answerOpen = false;

  @Output() answerToggled = new EventEmitter<void>();
  @Output() completionToggled = new EventEmitter<void>();
  @Output() previousQuestion = new EventEmitter<void>();
  @Output() nextQuestion = new EventEmitter<void>();

  examLoading = true;
  examPageNumbers: number[] = [];
  renderedExamPages = new Set<number>();
  examPageErrors = new Map<number, string>();
  examError = '';

  private currentExamFile = '';
  private renderVersion = 0;
  private viewReady = false;
  private examObserver?: IntersectionObserver;
  private renderingExamPages = new Set<string>();

  get isDone(): boolean {
    return this.examData.isDone(this.question.id);
  }

  ngAfterViewInit(): void {
    this.viewReady = true;
    void this.loadExamDocument();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (this.viewReady && changes['question']) void this.loadExamDocument();
  }

  ngOnDestroy(): void {
    this.examObserver?.disconnect();
    for (const canvas of this.examCanvases) this.pdfPreview.cancelRender(canvas.nativeElement);
  }

  private async loadExamDocument(): Promise<void> {
    if (this.currentExamFile === this.question.examFile && this.examPageNumbers.length > 0) {
      this.scrollToExamPage(this.question.examPage);
      this.renderExamPage(this.question.examPage);
      return;
    }

    const renderVersion = ++this.renderVersion;
    this.examObserver?.disconnect();
    for (const canvas of this.examCanvases) this.pdfPreview.cancelRender(canvas.nativeElement);
    this.currentExamFile = this.question.examFile;
    this.examPageNumbers = [];
    this.renderedExamPages = new Set();
    this.examPageErrors = new Map();
    this.renderingExamPages.clear();
    this.examLoading = true;
    this.examError = '';
    this.changeDetector.detectChanges();

    try {
      const document = await this.pdfPreview.loadDocument(this.question.examFile);
      if (renderVersion !== this.renderVersion) return;

      this.examPageNumbers = Array.from({ length: document.numPages }, (_, index) => index + 1);
      this.examLoading = false;
      this.changeDetector.detectChanges();
      if (renderVersion !== this.renderVersion) return;
      this.observeExamPages(renderVersion);
      this.scrollToExamPage(this.question.examPage);
      this.renderExamPage(this.question.examPage);
    } catch {
      if (renderVersion !== this.renderVersion) return;
      this.examLoading = false;
      this.examError = 'Het examendocument kon niet worden geladen.';
      this.changeDetector.detectChanges();
    }
  }

  private observeExamPages(version: number): void {
    const container = this.examDocument?.nativeElement;
    if (!container) return;

    this.examObserver?.disconnect();
    if (typeof IntersectionObserver === 'undefined') {
      for (const page of this.examPageNumbers) this.renderExamPage(page);
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
    const canvas = this.examCanvases.find((item) => Number(item.nativeElement.dataset['page']) === pageNumber)?.nativeElement;
    const renderKey = `${this.currentExamFile}:${pageNumber}`;
    if (!canvas || this.renderedExamPages.has(pageNumber) || this.examPageErrors.has(pageNumber) || this.renderingExamPages.has(renderKey)) return;
    this.renderingExamPages.add(renderKey);
    void this.renderPage(pageNumber, canvas, renderKey);
  }

  private async renderPage(pageNumber: number, canvas: HTMLCanvasElement, renderKey: string): Promise<void> {
    const file = this.currentExamFile;
    const version = this.renderVersion;
    try {
      const rendered = await this.pdfPreview.renderPage(file, pageNumber, canvas);
      if (!rendered || version !== this.renderVersion || file !== this.currentExamFile) return;
      this.renderedExamPages = new Set(this.renderedExamPages).add(pageNumber);
      this.examPageErrors.delete(pageNumber);
      this.changeDetector.detectChanges();
    } catch {
      if (version !== this.renderVersion || file !== this.currentExamFile) return;
      this.examPageErrors = new Map(this.examPageErrors).set(pageNumber, 'De PDF-pagina kon niet worden geladen. Probeer de vraag opnieuw.');
      this.changeDetector.detectChanges();
    } finally {
      this.renderingExamPages.delete(renderKey);
    }
  }

  private scrollToExamPage(pageNumber: number): void {
    const container = this.examDocument?.nativeElement;
    const page = container?.querySelector<HTMLElement>(`.exam-page-sheet[data-page="${pageNumber}"]`);
    if (!container || !page) return;
    const top = page.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop;
    container.scrollTo({ top, behavior: 'smooth' });
  }
}
