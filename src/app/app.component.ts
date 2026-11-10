import { Component, ElementRef, HostListener, ViewChild, inject } from '@angular/core';
import { AnswerDrawerComponent } from './components/answer-drawer.component';
import { AiTestComponent } from './components/ai-test.component';
import { ExamViewerComponent } from './components/exam-viewer.component';
import { QuestionSidebarComponent } from './components/question-sidebar.component';
import { ExamDataService, type ExamQuestion, type ExamSitting } from './services/exam-data.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [QuestionSidebarComponent, ExamViewerComponent, AnswerDrawerComponent, AiTestComponent],
  templateUrl: './app.component.html',
})
export class AppComponent {
  private readonly examData = inject(ExamDataService);
  @ViewChild('yearPicker') private yearPicker?: ElementRef<HTMLElement>;

  readonly topics = this.examData.topics;
  readonly years = this.examData.years;
  readonly exams = this.examData.exams;

  activeTopic = this.topics[0].id;
  activeExamFile: string | null = null;
  questionIndex = 0;
  showAnswer = false;
  yearMenuOpen = false;
  showAiTest = false;

  get filteredQuestions(): ExamQuestion[] {
    return this.examData.getQuestionsForTopic(this.activeTopic, this.activeExamFile);
  }

  get selectedQuestion(): ExamQuestion | undefined {
    return this.filteredQuestions[this.questionIndex];
  }

  get activeTopicTitle(): string {
    return this.topics.find((topic) => topic.id === this.activeTopic)?.title ?? '';
  }

  get examMenuLabel(): string {
    const selectedExam = this.exams.find((exam) => exam.examFile === this.activeExamFile);
    if (selectedExam) return `HAVO ${selectedExam.year} · tijdvak ${selectedExam.timevak}`;

    const firstYear = Math.min(...this.years);
    const lastYear = Math.max(...this.years);
    return `Examenjaren ${firstYear}-${lastYear}`;
  }

  selectTopic(topicId: string): void {
    this.activeTopic = topicId;
    this.questionIndex = 0;
    this.showAnswer = false;
  }

  selectExam(exam: ExamSitting | null): void {
    this.activeExamFile = exam?.examFile ?? null;
    this.questionIndex = 0;
    this.showAnswer = false;
    this.yearMenuOpen = false;
  }

  examsForYear(year: number): ExamSitting[] {
    return this.examData.getExamsForYear(year);
  }

  toggleYearMenu(): void {
    this.yearMenuOpen = !this.yearMenuOpen;
  }

  @HostListener('document:click', ['$event'])
  closeYearMenuOnOutsideClick(event: MouseEvent): void {
    if (this.yearMenuOpen && !this.yearPicker?.nativeElement.contains(event.target as Node)) {
      this.yearMenuOpen = false;
    }
  }

  @HostListener('document:keydown.escape')
  closeYearMenuOnEscape(): void {
    this.yearMenuOpen = false;
  }

  selectQuestion(question: ExamQuestion): void {
    this.questionIndex = this.filteredQuestions.findIndex((item) => item.id === question.id);
    this.showAnswer = false;
  }

  moveQuestion(direction: number): void {
    this.questionIndex = Math.max(0, Math.min(this.filteredQuestions.length - 1, this.questionIndex + direction));
    this.showAnswer = false;
  }

  toggleDone(questionId: string): void {
    this.examData.toggleDone(questionId);
  }

  toggleAnswer(): void {
    this.showAnswer = !this.showAnswer;
  }

  closeAnswer(): void {
    this.showAnswer = false;
  }
}
