import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, inject, linkedSignal, signal, viewChild } from '@angular/core';
import { AnswerDrawerComponent } from './components/answer-drawer.component';
import { AiTestComponent } from './components/ai-test.component';
import { ExamViewerComponent } from './components/exam-viewer.component';
import { QuestionSidebarComponent } from './components/question-sidebar.component';
import { ExamDataService, type ExamQuestion, type ExamSitting } from './services/exam-data.service';
import { ExamImportComponent } from './components/exam-import.component';
import { ThemeService, type ThemePreference } from './services/theme.service';

const THEME_LABELS: Record<ThemePreference, string> = { system: 'Systeem', light: 'Licht', dark: 'Donker' };
const THEME_ICONS: Record<ThemePreference, string> = { system: '◐', light: '☀', dark: '☾' };
const NEXT_THEME: Record<ThemePreference, ThemePreference> = { system: 'light', light: 'dark', dark: 'system' };

@Component({
  selector: 'app-root',
  imports: [QuestionSidebarComponent, ExamViewerComponent, AnswerDrawerComponent, AiTestComponent, ExamImportComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './app.component.html',
  host: {
    '(document:click)': 'closeYearMenuOnOutsideClick($event)',
    '(document:keydown.escape)': 'closeYearMenuOnEscape()',
  },
})
export class AppComponent {
  private readonly examData = inject(ExamDataService);
  private readonly theme = inject(ThemeService);
  private readonly yearPicker = viewChild<ElementRef<HTMLElement>>('yearPicker');

  readonly topics = this.examData.topics;
  readonly years = this.examData.years;
  readonly exams = this.examData.exams;

  readonly activeTopic = signal(this.topics()[0].id);
  readonly activeExamFile = linkedSignal<ExamSitting[], string | null>({
    source: this.exams,
    computation: (exams, previous) => {
      const selectedFile = previous?.value;
      if (!selectedFile) return null;
      if (exams.some((exam) => exam.examFile === selectedFile)) return selectedFile;
      const oldExam = previous.source.find((exam) => exam.examFile === selectedFile);
      if (!oldExam) return null;
      return exams.find((exam) => exam.year === oldExam.year && exam.timevak === oldExam.timevak)?.examFile ?? null;
    },
  });
  readonly questionIndex = signal(0);
  readonly showAnswer = signal(false);
  readonly yearMenuOpen = signal(false);
  readonly showAiTest = signal(false);
  readonly showImport = signal(false);
  protected readonly themeLabel = computed(() => THEME_LABELS[this.theme.preference()]);
  protected readonly themeToggleLabel = computed(() =>
    `Thema: ${this.themeLabel()}. Wissel naar ${THEME_LABELS[NEXT_THEME[this.theme.preference()]]}`);
  protected readonly themeIcon = computed(() => THEME_ICONS[this.theme.preference()]);
  readonly catalogError = this.examData.catalogError;
  readonly catalogLoading = this.examData.catalogLoading;
  private catalogRequest?: AbortController;

  readonly filteredQuestions = computed(() =>
    this.examData.getQuestionsForTopic(this.activeTopic(), this.activeExamFile()));

  readonly selectedQuestion = computed<ExamQuestion | undefined>(() => this.filteredQuestions()[this.questionIndex()]);

  readonly activeTopicTitle = computed(() =>
    this.topics().find((topic) => topic.id === this.activeTopic())?.title ?? '');

  readonly examMenuLabel = computed(() => {
    const selectedExam = this.exams().find((exam) => exam.examFile === this.activeExamFile());
    if (selectedExam) return `HAVO ${selectedExam.year} · tijdvak ${selectedExam.timevak}`;

    const firstYear = Math.min(...this.years());
    const lastYear = Math.max(...this.years());
    return `Examenjaren ${firstYear}-${lastYear}`;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.catalogRequest?.abort());
    this.refreshCatalog();
  }

  protected cycleTheme(): void {
    this.theme.cycle();
  }

  refreshCatalog(): void {
    this.catalogRequest?.abort();
    this.catalogRequest = new AbortController();
    void this.examData.refreshCatalog(this.catalogRequest.signal);
  }

  selectTopic(topicId: string): void {
    this.activeTopic.set(topicId);
    this.questionIndex.set(0);
    this.returnToTrainer();
  }

  selectExam(exam: ExamSitting | null): void {
    this.activeExamFile.set(exam?.examFile ?? null);
    this.questionIndex.set(0);
    this.returnToTrainer();
    this.yearMenuOpen.set(false);
  }

  examsForYear(year: number): ExamSitting[] {
    return this.examData.getExamsForYear(year);
  }

  toggleYearMenu(): void {
    this.yearMenuOpen.update((open) => !open);
  }

  closeYearMenuOnOutsideClick(event: MouseEvent): void {
    if (this.yearMenuOpen() && event.target instanceof Node && !this.yearPicker()?.nativeElement.contains(event.target)) {
      this.yearMenuOpen.set(false);
    }
  }

  closeYearMenuOnEscape(): void {
    this.yearMenuOpen.set(false);
  }

  selectQuestion(question: ExamQuestion): void {
    this.questionIndex.set(this.filteredQuestions().findIndex((item) => item.id === question.id));
    this.returnToTrainer();
  }

  private returnToTrainer(): void {
    this.showImport.set(false);
    this.showAiTest.set(false);
    this.showAnswer.set(false);
  }

  moveQuestion(direction: number): void {
    this.questionIndex.update((index) => Math.max(0, Math.min(this.filteredQuestions().length - 1, index + direction)));
    this.showAnswer.set(false);
  }

  toggleDone(questionId: string): void {
    this.examData.toggleDone(questionId);
  }

  toggleAnswer(): void {
    this.showAnswer.update((open) => !open);
  }

  closeAnswer(): void {
    this.showAnswer.set(false);
  }
}
