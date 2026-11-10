import { Component, computed, inject, input, output } from '@angular/core';
import { ExamDataService, type ExamQuestion } from '../services/exam-data.service';

@Component({
  selector: 'app-question-sidebar',
  templateUrl: './question-sidebar.component.html',
})
export class QuestionSidebarComponent {
  private readonly examData = inject(ExamDataService);
  readonly topics = this.examData.topics;

  readonly activeTopic = input.required<string>();
  readonly selectedExamFile = input<string | null>(null);
  readonly selectedQuestionId = input('');

  readonly topicSelected = output<string>();
  readonly questionSelected = output<ExamQuestion>();
  readonly completionToggled = output<string>();

  readonly filteredQuestions = computed(() =>
    this.examData.getQuestionsForTopic(this.activeTopic(), this.selectedExamFile()));
  readonly completedCount = this.examData.completedCount;
  readonly questionCount = this.examData.questions.length;
  readonly progressPercent = this.examData.progressPercent;

  countFor(topicId: string): number {
    return this.examData.countFor(topicId, this.selectedExamFile());
  }

  isDone(questionId: string): boolean {
    return this.examData.isDone(questionId);
  }
}
