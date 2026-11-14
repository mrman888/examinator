import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { ImportProgressEvent } from '../services/exam-import-client';
import type { StreamConnection } from '../services/import-progress-stream';
import { clockTime, connectionText, modelCallText, percent, phaseIndex, phaseLabel, progressSteps,
  stepText } from './import-progress-logic';
import { formatDuration, isRunning, processingTime } from './import-timing';
import { liveClock } from './live-clock';

/** Live processing progress: a full stepper in the detail view, or a compact line in a list row. */
@Component({
  selector: 'app-import-progress',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './import-progress.component.html',
  styleUrl: './import-progress.component.scss',
  host: { '[class.compact]': 'compact()' },
})
export class ImportProgressComponent {
  readonly progress = input.required<ImportProgressEvent | null>();
  readonly connection = input<StreamConnection | null>(null);
  readonly compact = input(false);
  readonly withDefinitions = input(false);
  readonly reconnect = output<void>();

  protected readonly steps = computed(() => progressSteps(this.withDefinitions()));
  protected readonly current = computed(() => {
    const progress = this.progress();
    return progress ? phaseIndex(progress, this.withDefinitions()) : 0;
  });
  protected readonly label = computed(() => {
    const progress = this.progress();
    return progress ? phaseLabel(progress) : 'Verwerking loopt';
  });
  protected readonly percent = computed(() => {
    const progress = this.progress();
    return progress ? percent(progress) : null;
  });
  protected readonly stepText = computed(() => {
    const progress = this.progress();
    return progress ? stepText(progress) : null;
  });
  protected readonly modelCalls = computed(() => {
    const progress = this.progress();
    return progress ? modelCallText(progress) : null;
  });
  protected readonly updatedAt = computed(() => clockTime(this.progress()?.updatedAt ?? null));
  protected readonly heartbeatAt = computed(() => {
    const progress = this.progress();
    return clockTime(progress?.heartbeatAt ?? progress?.updatedAt ?? null);
  });
  protected readonly connectionText = computed(() => connectionText(this.connection()));
  /** The list row ticks its elapsed processing time; the detail view shows it in the step log instead. */
  private readonly ticking = computed(() => this.compact() && isRunning(this.progress()?.timing));
  private readonly now = liveClock(this.ticking);
  protected readonly elapsed = computed(() => {
    const timing = this.progress()?.timing;
    const ms = timing ? processingTime(timing, this.now()) : null;
    return ms === null ? null : formatDuration(ms);
  });
}
