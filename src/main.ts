import { AfterViewInit, ChangeDetectorRef, Component, ElementRef, HostListener, inject, OnDestroy, QueryList, ViewChild, ViewChildren } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { GlobalWorkerOptions, type PDFDocumentProxy } from 'pdfjs-dist';
import {
  type DocumentSearchMatch,
  type ExamQuestion,
  type ExamYearGroup,
} from './exam-data';
import { ExamService } from './exam.service';
import { PdfRenderService, type PdfRenderContext } from './pdf-render.service';

GlobalWorkerOptions.workerSrc = new URL('assets/pdf.worker.min.mjs', document.baseURI).toString();

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
})
class AppComponent implements AfterViewInit, OnDestroy {
  private readonly examService = inject(ExamService);
  private readonly changeDetector = inject(ChangeDetectorRef);
  @ViewChild('examDocument') private examDocument?: ElementRef<HTMLDivElement>;
  @ViewChildren('examPageCanvas') private examCanvases!: QueryList<ElementRef<HTMLCanvasElement>>;
  @ViewChild('answerCanvas') private answerCanvas?: ElementRef<HTMLCanvasElement>;
  @ViewChild('previousAnswerCanvas') private previousAnswerCanvas?: ElementRef<HTMLCanvasElement>;

  readonly topics = this.examService.topics;
  readonly questions = this.examService.questions;
  examLoading = true;
  examPageNumbers: number[] = [];
  renderedExamPages = new Set<number>();
  examPageErrors = new Map<number, string>();
  answerLoading = true;
  previousAnswerLoading = true;
  examError = '';
  answerError = '';
  previousAnswerError = '';
  private currentExamFile = '';
  private examRenderVersion = 0;
  private searchTimer?: number;
  private examObserver?: IntersectionObserver;
  private readonly pdfRenderService = inject(PdfRenderService);
  private readonly pdfRenderContext: PdfRenderContext = {
    setPreviewState: (kind, loading, error) => this.setPreviewState(kind, loading, error),
    isCurrentExam: (file) => this.currentExamFile === file,
    isExamPageRendered: (pageNumber) => this.renderedExamPages.has(pageNumber),
    setExamPageRendered: (pageNumber) => {
      this.renderedExamPages = new Set(this.renderedExamPages).add(pageNumber);
      this.examPageErrors.delete(pageNumber);
      this.changeDetector.detectChanges();
    },
    setExamPageError: (pageNumber, error) => {
      this.examPageErrors = new Map(this.examPageErrors).set(pageNumber, error);
      this.changeDetector.detectChanges();
    },
  };

  get activeTopic(): string {
    return this.examService.activeTopic;
  }

  get examOverviewOpen(): boolean {
    return this.examService.examOverviewOpen;
  }

  set examOverviewOpen(isOpen: boolean) {
    this.examService.examOverviewOpen = isOpen;
  }

  get searchQuery(): string {
    return this.examService.searchQuery;
  }

  get searchMatches(): DocumentSearchMatch[] {
    return this.examService.searchMatches;
  }

  get activeSearchMatchIndex(): number {
    return this.examService.activeSearchMatchIndex;
  }

  get activeSearchMatchId(): string {
    return this.examService.activeSearchMatchId;
  }

  get searching(): boolean {
    return this.examService.searching;
  }

  get searchError(): string {
    return this.examService.searchError;
  }

  get questionIndex(): number {
    return this.examService.questionIndex;
  }

  get showAnswer(): boolean {
    return this.examService.showAnswer;
  }

  ngAfterViewInit(): void {
    void this.renderExamDocument();
  }

  ngOnDestroy(): void {
    this.examObserver?.disconnect();
    if (this.searchTimer !== undefined) window.clearTimeout(this.searchTimer);
    this.examService.resetSearch();
    this.cancelRenderTasks();
  }

  get filteredQuestions(): ExamQuestion[] {
    return this.examService.filteredQuestions;
  }

  get examYears(): number[] {
    return this.examService.examYears;
  }

  get examOverview(): ExamYearGroup[] {
    return this.examService.examOverview;
  }

  get selectedQuestion(): ExamQuestion | undefined {
    return this.examService.selectedQuestion;
  }

  get previousAnswerPage(): number {
    return this.examService.previousAnswerPage;
  }

  get activeTopicTitle(): string {
    return this.examService.activeTopicTitle;
  }

  get completedCount(): number {
    return this.examService.completedCount;
  }

  get progressPercent(): number {
    return this.examService.progressPercent;
  }

  countFor(topicId: string): number {
    return this.examService.countFor(topicId);
  }

  searchMatchesForPage(pageNumber: number): DocumentSearchMatch[] {
    return this.examService.searchMatchesForPage(pageNumber);
  }

  updateSearchQuery(query: string): void {
    const version = this.examService.beginSearch(query);
    if (this.searchTimer !== undefined) window.clearTimeout(this.searchTimer);

    const trimmedQuery = query.trim();
    if (!trimmedQuery) return;

    this.searchTimer = window.setTimeout(() => {
      this.searchTimer = undefined;
      const file = this.currentExamFile;
      if (!file) {
        this.examService.stopSearch(version);
        return;
      }
      void this.findDocumentMatches(file, trimmedQuery, version);
    }, 220);
  }

  moveSearchMatch(direction: number): void {
    if (!this.searchMatches.length) return;
    const nextIndex = (this.activeSearchMatchIndex + direction + this.searchMatches.length) % this.searchMatches.length;
    const match = this.searchMatches[nextIndex];
    this.examService.activeSearchMatchIndex = nextIndex;
    this.examService.activeSearchMatchId = match.id;
    this.scrollToExamPage(match.pageNumber);
    this.renderExamPage(match.pageNumber);
    this.changeDetector.detectChanges();
  }

  isDone(questionId: string): boolean {
    return this.examService.isDone(questionId);
  }

  selectTopic(topicId: string): void {
    this.examService.selectTopic(topicId);
    void this.renderExamDocument();
  }

  selectQuestion(question: ExamQuestion): void {
    this.examService.selectQuestion(question);
    void this.renderExamDocument();
  }

  selectExamOverviewQuestion(question: ExamQuestion): void {
    this.examService.selectExamOverviewQuestion(question);
    void this.renderExamDocument();
  }

  moveQuestion(direction: number): void {
    this.examService.moveQuestion(direction);
    void this.renderExamDocument();
  }

  toggleAnswer(): void {
    this.examService.toggleAnswer();
    const question = this.selectedQuestion;
    if (this.showAnswer && question) {
      this.pdfRenderService.renderAnswerPage(
        question,
        this.answerCanvas?.nativeElement,
        this.previousAnswerCanvas?.nativeElement,
        this.previousAnswerPage,
        this.pdfRenderContext,
      );
    }
  }

  closeAnswer(): void {
    this.examService.closeAnswer();
  }

  @HostListener('document:keydown.escape')
  closeAnswerOnEscape(): void {
    this.examService.closeAnswer();
    this.examService.closeExamOverview();
  }

  @HostListener('document:click')
  closeExamOverviewOnOutsideClick(): void {
    this.examService.closeExamOverview();
  }

  toggleDone(questionId: string): void {
    this.examService.toggleDone(questionId);
  }

  private async renderExamDocument(): Promise<void> {
    const question = this.selectedQuestion;
    if (!question) return;

    if (this.currentExamFile === question.examFile && this.examPageNumbers.length > 0) {
      this.scrollToExamPage(question.examPage);
      this.renderExamPage(question.examPage);
      return;
    }

    const renderVersion = ++this.examRenderVersion;
    this.examObserver?.disconnect();
    this.cancelRenderTasks();
    this.currentExamFile = question.examFile;
    if (this.searchTimer !== undefined) window.clearTimeout(this.searchTimer);
    this.searchTimer = undefined;
    this.examService.resetSearch();
    this.examPageNumbers = [];
    this.renderedExamPages = new Set();
    this.examPageErrors = new Map();
    this.examLoading = true;
    this.examError = '';
    this.changeDetector.detectChanges();

    try {
      const document = await this.loadDocument(question.examFile);
      if (renderVersion !== this.examRenderVersion) return;

      this.examPageNumbers = Array.from({ length: document.numPages }, (_, index) => index + 1);
      this.examLoading = false;
      this.changeDetector.detectChanges();
      if (renderVersion !== this.examRenderVersion) return;
      this.observeExamPages(question.examFile, renderVersion);
      this.scrollToExamPage(question.examPage);
      this.renderExamPage(question.examPage);
      if (this.searchQuery.trim()) this.updateSearchQuery(this.searchQuery);
    } catch {
      if (renderVersion !== this.examRenderVersion) return;
      this.examLoading = false;
      this.examError = 'Het examendocument kon niet worden geladen.';
      this.changeDetector.detectChanges();
    }
  }

  private observeExamPages(file: string, version: number): void {
    const container = this.examDocument?.nativeElement;
    if (!container) return;

    this.examObserver?.disconnect();
    if (typeof IntersectionObserver === 'undefined') {
      for (const page of this.examPageNumbers) this.renderExamPage(page);
      return;
    }

    this.examObserver = new IntersectionObserver((entries) => {
      if (version !== this.examRenderVersion) return;
      for (const entry of entries) {
        if (entry.isIntersecting) {
          this.renderExamPage(Number((entry.target as HTMLElement).dataset['page']));
        }
      }
    }, { root: container, rootMargin: '420px 0px' });

    container.querySelectorAll<HTMLElement>('.exam-page-sheet').forEach((page) => this.examObserver?.observe(page));
  }

  private renderExamPage(pageNumber: number): void {
    const file = this.currentExamFile;
    const canvas = this.examCanvases.find((item) => Number(item.nativeElement.dataset.page) === pageNumber)?.nativeElement;
    if (!file || !canvas || this.renderedExamPages.has(pageNumber) || this.examPageErrors.has(pageNumber)) return;
    void this.pdfRenderService.renderPage(file, pageNumber, canvas, 'exam', this.pdfRenderContext);
  }

  private scrollToExamPage(pageNumber: number): void {
    const container = this.examDocument?.nativeElement;
    const page = container?.querySelector<HTMLElement>(`.exam-page-sheet[data-page="${pageNumber}"]`);
    if (!container || !page) return;
    const top = page.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop;
    container.scrollTo({ top, behavior: 'smooth' });
  }

  private async findDocumentMatches(file: string, query: string, version: number): Promise<void> {
    try {
      const results = await this.examService.findDocumentMatches(file, query, version, (pageNumber) => {
        const canvas = this.examCanvases.find((item) => Number(item.nativeElement.dataset.page) === pageNumber)?.nativeElement;
        return canvas?.parentElement?.clientWidth;
      });
      if (!results || file !== this.currentExamFile || !this.examService.setSearchResults(version, results)) return;
      this.changeDetector.detectChanges();
      if (results.matches.length) this.moveSearchMatch(1);
    } catch {
      if (!this.examService.isCurrentSearch(version) || file !== this.currentExamFile) return;
      this.examService.setSearchError(version);
      this.changeDetector.detectChanges();
    }
  }

  private loadDocument(file: string): Promise<PDFDocumentProxy> {
    return this.examService.loadDocument(file);
  }

  private cancelRenderTasks(): void {
    this.pdfRenderService.cancelAll();
  }

  private setPreviewState(kind: 'exam' | 'answer' | 'previousAnswer', loading: boolean, error: string): void {
    if (kind === 'exam') {
      this.examLoading = loading;
      this.examError = error;
    } else if (kind === 'answer') {
      this.answerLoading = loading;
      this.answerError = error;
    } else {
      this.previousAnswerLoading = loading;
      this.previousAnswerError = error;
    }
    if (!loading) {
      this.changeDetector.detectChanges();
    }
  }
}

bootstrapApplication(AppComponent).catch(console.error);