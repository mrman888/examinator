import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { AnswerDrawerComponent } from './components/answer-drawer.component';
import { AiTestComponent } from './components/ai-test.component';
import { ExamViewerComponent } from './components/exam-viewer.component';
import { QuestionSidebarComponent } from './components/question-sidebar.component';
import { ExamDataService, type ExamQuestion, type ExamSitting } from './services/exam-data.service';

@Component({
  selector: 'app-root',
  imports: [QuestionSidebarComponent, ExamViewerComponent, AnswerDrawerComponent, AiTestComponent],
  templateUrl: './app.component.html',
  host: {
    '(document:click)': 'closeYearMenuOnOutsideClick($event)',
    '(document:keydown.escape)': 'closeYearMenuOnEscape()',
  },
})
export class AppComponent {
  private readonly examData = inject(ExamDataService);
  private readonly yearPicker = viewChild<ElementRef<HTMLElement>>('yearPicker');

  readonly topics = this.examData.topics;
  readonly years = this.examData.years;
  readonly exams = this.examData.exams;

  readonly activeTopic = signal(this.topics[0].id);
  readonly activeExamFile = signal<string | null>(null);
  readonly questionIndex = signal(0);
  readonly showAnswer = signal(false);
  readonly yearMenuOpen = signal(false);
  readonly showAiTest = signal(false);

  readonly filteredQuestions = computed(() =>
    this.examData.getQuestionsForTopic(this.activeTopic(), this.activeExamFile()));

  readonly selectedQuestion = computed<ExamQuestion | undefined>(() => this.filteredQuestions()[this.questionIndex()]);

  readonly activeTopicTitle = computed(() =>
    this.topics.find((topic) => topic.id === this.activeTopic())?.title ?? '');

  readonly examMenuLabel = computed(() => {
    const selectedExam = this.exams.find((exam) => exam.examFile === this.activeExamFile());
    if (selectedExam) return `HAVO ${selectedExam.year} · tijdvak ${selectedExam.timevak}`;

    const firstYear = Math.min(...this.years);
    const lastYear = Math.max(...this.years);
    return `Examenjaren ${firstYear}-${lastYear}`;
  });

  selectTopic(topicId: string): void {
    this.activeTopic.set(topicId);
    this.questionIndex.set(0);
    this.showAnswer.set(false);
  }

  selectExam(exam: ExamSitting | null): void {
    this.activeExamFile.set(exam?.examFile ?? null);
    this.questionIndex.set(0);
    this.showAnswer.set(false);
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
