import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { ExamDataService, type ExamQuestion } from '../services/exam-data.service';

@Component({
  selector: 'app-question-sidebar',
  templateUrl: './question-sidebar.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuestionSidebarComponent {
  private readonly examData = inject(ExamDataService);
  readonly topics = this.examData.topics;
  protected readonly topicSearch = signal('');
  protected readonly matchingTopics = computed(() => {
    const search = this.topicSearch().trim().toLocaleLowerCase('nl');
    return this.topics().filter((topic) => !search ||
      [topic.title, ...(topic.aliases ?? [])].some((value) => value.toLocaleLowerCase('nl').includes(search)));
  });

  readonly activeTopic = input.required<string>();
  readonly selectedExamFile = input<string | null>(null);
  readonly selectedQuestionId = input('');

  readonly topicSelected = output<string>();
  readonly questionSelected = output<ExamQuestion>();
  readonly completionToggled = output<string>();

  readonly filteredQuestions = computed(() =>
    this.examData.getQuestionsForTopic(this.activeTopic(), this.selectedExamFile()));
  readonly completedCount = this.examData.completedCount;
  readonly questionCount = computed(() => this.examData.questions().length);
  readonly progressPercent = this.examData.progressPercent;

  countFor(topicId: string): number {
    return this.examData.countFor(topicId, this.selectedExamFile());
  }

  isDone(questionId: string): boolean {
    return this.examData.isDone(questionId);
  }
}
