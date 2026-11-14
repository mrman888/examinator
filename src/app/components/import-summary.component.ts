import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { DraftQuestion, TopicDefinition } from '../services/exam-import-client';
import { questionCount, summarizeImport } from './import-summary-logic';

/** Summary of the questions found per topic, and the questions that match none of the topics. */
@Component({
  selector: 'app-import-summary',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './import-summary.component.html',
  styleUrl: './import-summary.component.scss',
})
export class ImportSummaryComponent {
  readonly topics = input.required<readonly TopicDefinition[]>();
  readonly questions = input.required<readonly DraftQuestion[]>();
  readonly questionSelected = output<string>();

  protected readonly summary = computed(() => summarizeImport(this.topics(), this.questions()));
  protected readonly count = questionCount;
}
