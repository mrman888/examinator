import { Component, EventEmitter, Input, Output, inject } from '@angular/core';
import { ExamDataService, type ExamQuestion } from '../services/exam-data.service';

@Component({
  selector: 'app-question-sidebar',
  standalone: true,
  templateUrl: './question-sidebar.component.html',
})
export class QuestionSidebarComponent {
  private readonly examData = inject(ExamDataService);
  readonly topics = this.examData.topics;

  @Input({ required: true }) activeTopic!: string;
  @Input() selectedExamFile: string | null = null;
  @Input() selectedQuestionId = '';

  @Output() topicSelected = new EventEmitter<string>();
  @Output() questionSelected = new EventEmitter<ExamQuestion>();
  @Output() completionToggled = new EventEmitter<string>();

  get filteredQuestions(): ExamQuestion[] {
    return this.examData.getQuestionsForTopic(this.activeTopic, this.selectedExamFile);
  }

  get completedCount(): number {
    return this.examData.completedCount;
  }

  get questionCount(): number {
    return this.examData.questions.length;
  }

  get progressPercent(): number {
    return this.examData.progressPercent;
  }

  countFor(topicId: string): number {
    return this.examData.countFor(topicId, this.selectedExamFile);
  }

  isDone(questionId: string): boolean {
    return this.examData.isDone(questionId);
  }
}
