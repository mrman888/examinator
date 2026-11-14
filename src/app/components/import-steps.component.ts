import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { ImportTiming } from '../services/exam-import-client';
import { formatDuration, isRunning, leadTime, processingTime, runViews } from './import-timing';
import { liveClock } from './live-clock';

/**
 * "Verwerkingsstappen": the step log of every processing run of an import, newest run expanded. While the import
 * processes, the running step and the totals tick every second; nothing here is announced by a live region.
 */
@Component({
  selector: 'app-import-steps',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './import-steps.component.html',
  styleUrl: './import-steps.component.scss',
})
export class ImportStepsComponent {
  readonly timing = input<ImportTiming | null>(null);
  readonly createdAt = input<string | null>(null);
  /** True while the record processes; the clock never ticks for a settled import. */
  readonly live = input(false);

  protected readonly running = computed(() => this.live() && isRunning(this.timing()));
  private readonly now = liveClock(this.running);
  protected readonly runs = computed(() => runViews(this.timing(), this.now()));
  protected readonly total = computed(() => formatDuration(processingTime(this.timing(), this.now())));
  /** Shown only when it reads differently from the processing time. */
  protected readonly lead = computed(() => {
    const lead = formatDuration(leadTime(this.createdAt(), this.timing()));
    return this.running() || lead === 'onbekend' || lead === this.total() ? null : lead;
  });
}
