import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, linkedSignal, output, signal, viewChild } from '@angular/core';
import { ExamDataService } from '../services/exam-data.service';
import { type DraftQuestion, type ImportProgressEvent, type ImportRecord, type ImportSettledEvent, type ImportState,
  type TopicDefinition, type DuplicateImport, ImportRequestError, deleteImport, listImports, loadImport, reprocessImport,
  updateImport, uploadImport } from '../services/exam-import-client';
import { PROGRESS_STREAM_DEPS, type ProgressStreamHandle, type StreamConnection,
  watchImportProgress } from '../services/import-progress-stream';
import { approveAll as approveQuestions, attentionSummary } from './import-approval';
import { removeImport, sortImports, upsertImport } from './import-order';
import { type AnnouncementInput, announcement } from './import-progress-logic';
import { type PdfSourceMode, applyPdfSource, pdfSourceError } from './import-source';
import { ImportPdfPreviewComponent } from './import-pdf-preview.component';
import { ImportProgressComponent } from './import-progress.component';
import { ImportStepsComponent } from './import-steps.component';
import { combineTiming, formatDateTime, processedText } from './import-timing';
import { ImportSummaryComponent } from './import-summary.component';
import { TopicEditorComponent } from './topic-editor.component';

const STATE_LABELS: Record<ImportState, string> = {
  queued: 'In wachtrij', extracting: 'Documenten uitlezen', awaitingDefinitions: 'Hoofdstuk controleren',
  aligning: 'Antwoorden koppelen', tagging: 'Onderwerpen toekennen', review: 'Klaar voor beoordeling',
  published: 'Gepubliceerd', failed: 'Mislukt', cancelled: 'Geannuleerd',
};

function deletable(record: ImportRecord): boolean {
  return !['published', 'extracting', 'aligning', 'tagging'].includes(record.state);
}

function processing(record: ImportRecord | null): boolean {
  return !!record && ['queued', 'extracting', 'aligning', 'tagging'].includes(record.state);
}

@Component({
  selector: 'app-exam-import',
  imports: [TopicEditorComponent, ImportPdfPreviewComponent, ImportProgressComponent, ImportSummaryComponent,
    ImportStepsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './exam-import.component.html',
  styleUrl: './exam-import.component.scss',
})
export class ExamImportComponent {
  readonly published = output<void>();
  private readonly data = inject(ExamDataService);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('uploadDialog');
  private readonly importList = viewChild.required<ElementRef<HTMLElement>>('importList');
  private readonly uploadForm = viewChild.required<ElementRef<HTMLFormElement>>('uploadForm');
  private readonly document = inject(DOCUMENT);
  private readonly streamDeps = inject(PROGRESS_STREAM_DEPS);
  private request?: AbortController;
  private destroyed = false;
  private stream: ProgressStreamHandle | null = null;
  /** One background record load per import; a newer event during a load schedules exactly one more. */
  private readonly refreshing = new Map<string, AbortController>();
  private readonly refreshAgain = new Set<string>();
  private listPoll: AbortController | null = null;
  private readonly progress = signal<ReadonlyMap<string, ImportProgressEvent>>(new Map());
  private readonly visible = signal(true);
  protected readonly progressById = this.progress.asReadonly();
  protected readonly connection = signal<StreamConnection | null>(null);
  protected readonly records = signal<ImportRecord[]>([]);
  protected readonly selected = signal<ImportRecord | null>(null);
  protected readonly deleteCandidate = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly chapterNumber = signal('');
  protected readonly chapterTitle = signal('');
  protected readonly contextText = signal('');
  protected readonly examMode = signal<PdfSourceMode>('file');
  protected readonly answersMode = signal<PdfSourceMode>('file');
  protected readonly uploadTopics = signal<TopicDefinition[]>([]);
  protected readonly duplicates = signal<DuplicateImport[]>([]);
  protected readonly approveAllPending = signal(false);
  protected readonly reprocessSource = signal<ImportRecord | null>(null);
  protected readonly availableTopics = this.data.topicDefinitions;
  protected readonly draftTopics = linkedSignal(() => structuredClone(this.selected()?.topics ?? []));
  protected readonly draftQuestions = linkedSignal(() => structuredClone(this.selected()?.questions ?? []));
  protected readonly selectedQuestionId = signal('');
  protected readonly activeQuestion = computed<DraftQuestion | undefined>(() =>
    this.draftQuestions().find((question) => question.id === this.selectedQuestionId()) ?? this.draftQuestions()[0]);
  protected readonly dirty = computed(() => {
    const selected = this.selected();
    return !!selected && (JSON.stringify(this.draftTopics()) !== JSON.stringify(selected.topics) ||
      JSON.stringify(this.draftQuestions()) !== JSON.stringify(selected.questions));
  });
  protected readonly canPublish = computed(() => !this.dirty() && this.selected()?.state === 'review' &&
    this.draftQuestions().length > 0 && this.draftQuestions().every((question) =>
      !question.needsReview && question.answerPage !== null && question.answerEndPage !== null));
  protected readonly approvedCount = computed(() => this.draftQuestions().filter((question) => !question.needsReview).length);
  /** Real validation issues of unconfirmed questions, derived so it follows single checkbox changes too. */
  protected readonly attention = computed(() => attentionSummary(this.draftQuestions(), this.draftTopics()));
  protected readonly attentionLimit = 8;
  /** A boolean, so record updates do not reopen the stream; it only runs while something processes and the tab is visible. */
  private readonly watching = computed(() => this.visible() && this.records().some(processing));
  protected readonly selectedProgress = computed(() => {
    const selected = this.selected();
    return selected && processing(selected) ? this.progressById().get(selected.id) ?? null : null;
  });
  /** The record's step log of all runs, with the live current run from the stream while it processes. */
  protected readonly selectedTiming = computed(() => {
    const selected = this.selected();
    return selected ? combineTiming(selected.timing, this.selectedProgress()?.timing) : null;
  });
  protected readonly selectedProcessing = computed(() => processing(this.selected()));
  /** "Bijgewerkt" and, once processing has finished, "Verwerkt in" per list row. */
  protected readonly rowTimes = computed(() => new Map(this.records().map((record) => [record.id, {
    updated: formatDateTime(record.updatedAt),
    processed: processedText(record.state, record.timing),
  }])));
  /** Spoken for the selected import only, and only on meaningful changes (see `announcement`). */
  protected readonly announcement = linkedSignal<AnnouncementInput, string>({
    source: () => ({ progress: this.selectedProgress(), connection: this.connection() }),
    computation: (next, previous) => announcement(previous?.source ?? null, next) ?? previous?.value ?? '',
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    const onVisibility = () => this.visible.set(this.document.visibilityState !== 'hidden');
    onVisibility();
    this.document.addEventListener('visibilitychange', onVisibility);
    destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.request?.abort();
      this.listPoll?.abort();
      this.refreshing.forEach((controller) => controller.abort());
      this.document.removeEventListener('visibilitychange', onVisibility);
    });
    // EventSource is an imperative browser API; the stream closes on hidden tabs, destroy and when nothing processes.
    effect((onCleanup) => {
      if (!this.watching()) return;
      const handle = watchImportProgress({
        onEvent: (event) => this.onProgress(event),
        onSettled: (event) => this.onSettled(event),
        onConnection: (state) => this.connection.set(state === 'closed' ? null : state),
        onPoll: () => this.pollList(),
      }, this.streamDeps);
      this.stream = handle;
      onCleanup(() => {
        handle.dispose();
        if (this.stream === handle) this.stream = null;
      });
    });
    void this.refreshList();
  }

  protected reconnectLive(): void {
    this.stream?.reconnectLive();
  }

  protected withDefinitions(record: ImportRecord): boolean {
    return !!record.contextDocumentId || !!record.contextText?.trim();
  }

  private onProgress(event: ImportProgressEvent): void {
    this.progress.update((map) => new Map(map).set(event.importId, event));
    const row = this.records().find((record) => record.id === event.importId);
    if (!row || row.revision !== event.revision) this.refreshRecord(event.importId);
  }

  private onSettled(event: ImportSettledEvent): void {
    this.dropProgress(event.importId);
    if (event.state === 'deleted') {
      this.forget(event.importId);
      return;
    }
    const row = this.records().find((record) => record.id === event.importId);
    if (!row || row.revision !== event.revision || row.state !== event.state) this.refreshRecord(event.importId);
  }

  private refreshRecord(id: string): void {
    if (this.destroyed) return;
    if (this.refreshing.has(id)) {
      this.refreshAgain.add(id);
      return;
    }
    const controller = new AbortController();
    this.refreshing.set(id, controller);
    void loadImport(id, controller.signal).then((record) => {
      if (!controller.signal.aborted && !this.destroyed) this.applyRecord(record);
    }, (error: unknown) => {
      if (!controller.signal.aborted && !this.destroyed && error instanceof ImportRequestError && error.status === 404) {
        this.forget(id);
      }
    }).finally(() => {
      this.refreshing.delete(id);
      if (this.refreshAgain.delete(id) && !controller.signal.aborted) this.refreshRecord(id);
    });
  }

  /** Fallback while live updates are unavailable: reload the list every 5 seconds. */
  private pollList(): void {
    if (this.listPoll || this.destroyed) return;
    const controller = new AbortController();
    this.listPoll = controller;
    void listImports(controller.signal).then((records) => {
      if (controller.signal.aborted || this.destroyed) return;
      this.records.set(sortImports(records));
      const selected = this.selected();
      const fresh = selected && records.find((record) => record.id === selected.id);
      // List rows carry no step log; the selected record is reloaded in full.
      if (fresh && fresh.revision !== selected.revision && !this.dirty()) this.refreshRecord(fresh.id);
      for (const record of records) if (!processing(record)) this.dropProgress(record.id);
    }, () => undefined).finally(() => {
      if (this.listPoll === controller) this.listPoll = null;
    });
  }

  /** Replaces a background-refreshed record in place without changing the selection or unsaved edits. */
  private applyRecord(record: ImportRecord): void {
    this.records.update((records) => upsertImport(records, record));
    const selected = this.selected();
    if (selected?.id === record.id && selected.revision !== record.revision && !this.dirty()) this.selected.set(record);
    if (!processing(record)) this.dropProgress(record.id);
  }

  private dropProgress(id: string): void {
    if (!this.progress().has(id)) return;
    this.progress.update((map) => {
      const next = new Map(map);
      next.delete(id);
      return next;
    });
  }

  private forget(id: string): void {
    this.dropProgress(id);
    this.records.update((records) => removeImport(records, id));
    if (this.selected()?.id === id && !this.dirty()) {
      this.selected.set(null);
      this.selectedQuestionId.set('');
    }
  }

  protected stateLabel(record: ImportRecord): string {
    return STATE_LABELS[record.state];
  }

  protected documentUrl(id: string): string {
    return `/api/documents/${encodeURIComponent(id)}`;
  }

  protected openUpload(): void {
    if (this.dirty()) {
      this.error.set('Sla je wijzigingen op of herstel het concept voordat je een nieuwe import begint.');
      return;
    }
    this.error.set('');
    this.notice.set('');
    this.duplicates.set([]);
    this.reprocessSource.set(null);
    this.uploadTopics.set(structuredClone(this.data.topicDefinitions()));
    this.dialog().nativeElement.showModal();
  }

  protected openReprocess(source: ImportRecord): void {
    this.openUpload();
    if (this.dialog().nativeElement.open) {
      this.reprocessSource.set(source);
      this.uploadTopics.set(structuredClone(source.topics));
      this.chapterNumber.set(source.metadata.chapterNumber);
      this.chapterTitle.set(source.metadata.chapterTitle);
      this.contextText.set(source.contextText ?? '');
    }
  }

  protected async openDuplicate(duplicate: DuplicateImport): Promise<void> {
    this.dialog().nativeElement.close();
    await this.run(async (signal) => {
      this.accept(await loadImport(duplicate.importId, signal));
      this.selectedQuestionId.set('');
    });
  }

  protected async reprocessDuplicate(duplicate: DuplicateImport): Promise<void> {
    if (!this.validTopics(this.uploadTopics())) return;
    await this.reprocess(duplicate.importId, new FormData(this.uploadForm().nativeElement));
  }

  protected duplicateLabel(duplicate: DuplicateImport): string {
    const sitting = duplicate.year ? `${duplicate.year}, tijdvak ${duplicate.timevak}` : 'tijdvak nog onbekend';
    const state = (STATE_LABELS as Record<string, string>)[duplicate.state] ?? 'Onbekende status';
    return `${duplicate.role === 'exam' ? 'Examen' : 'Antwoorddocument'} in eerdere import (${sitting}, ${state})`;
  }

  protected closeUpload(): void {
    if (!this.busy()) this.dialog().nativeElement.close();
  }

  protected preventBusyClose(event: Event): void {
    if (this.busy()) event.preventDefault();
  }

  protected addExistingTopic(id: string): void {
    const topic = this.availableTopics().find((item) => item.id === id);
    if (topic && !this.uploadTopics().some((item) => item.id === id)) {
      this.uploadTopics.update((topics) => [...topics, structuredClone(topic)]);
    }
  }

  protected async refreshList(): Promise<void> {
    await this.run(async (signal) => { this.records.set(sortImports(await listImports(signal))); });
  }

  protected async select(record: ImportRecord): Promise<void> {
    if (this.dirty()) {
      this.error.set('Sla je wijzigingen op of herstel het concept voordat je een andere import opent.');
      return;
    }
    await this.run(async (signal) => {
      this.accept(await loadImport(record.id, signal));
      this.selectedQuestionId.set('');
    });
  }

  /** Selects a question from the summary and moves focus to its review form. */
  protected showQuestion(id: string): void {
    this.selectedQuestionId.set(id);
    this.document.getElementById('question-review')?.focus();
  }

  protected async refreshSelected(): Promise<void> {
    const selected = this.selected();
    if (!selected) return;
    await this.select(selected);
  }

  protected resetDraft(): void {
    this.draftTopics.set(structuredClone(this.selected()?.topics ?? []));
    this.draftQuestions.set(structuredClone(this.selected()?.questions ?? []));
    this.error.set('');
  }

  protected async upload(event: Event): Promise<void> {
    event.preventDefault();
    const form = this.uploadForm().nativeElement;
    if (!form.reportValidity()) return;
    const data = new FormData(form);
    const source = this.reprocessSource();
    if (source) {
      if (!this.validTopics(this.uploadTopics())) return;
      await this.reprocess(source.id, data);
      return;
    }
    const context = data.get('context');
    const sourceError = pdfSourceError('het examen', this.examMode(), data.get('exam'), data.get('examUrl')) ??
      pdfSourceError('het antwoorddocument', this.answersMode(), data.get('answers'), data.get('answersUrl'));
    if (sourceError) {
      this.error.set(sourceError);
      return;
    }
    if (context instanceof File && context.size > 5 * 1024 * 1024) {
      this.error.set('Maximaal 20 MiB per PDF en 5 MiB voor de hoofdstukscreenshot.');
      return;
    }
    if (!this.validTopics(this.uploadTopics())) return;
    if (context instanceof File && !context.size) data.delete('context');
    applyPdfSource(data, 'exam', this.examMode());
    applyPdfSource(data, 'answers', this.answersMode());
    data.delete('examMode');
    data.delete('answersMode');
    data.set('metadata', JSON.stringify({
      subjectId: 'biology', level: 'havo', year: 0, timevak: 0,
      chapterNumber: this.chapterNumber().trim(), chapterTitle: this.chapterTitle().trim(),
      topics: this.uploadTopics(),
    }));
    data.set('contextText', this.contextText().trim());
    this.duplicates.set([]);
    await this.run(async (signal) => {
      let record: ImportRecord;
      try {
        record = await uploadImport(data, signal);
      } catch (error: unknown) {
        if (error instanceof ImportRequestError && error.duplicates.length) this.duplicates.set(error.duplicates);
        throw error;
      }
      if (signal.aborted) return;
      this.accept(record);
      this.dialog().nativeElement.close();
      form.querySelectorAll<HTMLInputElement>('input[type="file"], input[type="url"]').forEach((input) => { input.value = ''; });
      this.examMode.set('file');
      this.answersMode.set('file');
      this.notice.set('De documenten zijn opgeslagen. De verwerking loopt op de achtergrond; publicatie volgt pas na goedkeuring.');
    });
  }

  private async reprocess(sourceId: string, form: FormData): Promise<void> {
    const data = new FormData();
    const context = form.get('context');
    if (context instanceof File && context.size > 5 * 1024 * 1024) {
      this.error.set('Maximaal 5 MiB voor de hoofdstukscreenshot.');
      return;
    }
    if (context instanceof File && context.size) data.set('context', context);
    data.set('metadata', JSON.stringify({
      subjectId: 'biology', level: 'havo', year: 0, timevak: 0,
      chapterNumber: this.chapterNumber().trim(), chapterTitle: this.chapterTitle().trim(),
      topics: this.uploadTopics(),
    }));
    data.set('contextText', this.contextText().trim());
    this.duplicates.set([]);
    await this.run(async (signal) => {
      const record = await reprocessImport(sourceId, data, signal);
      if (signal.aborted) return;
      this.accept(record);
      this.dialog().nativeElement.close();
      this.reprocessSource.set(null);
      this.notice.set('De import wordt opnieuw verwerkt met de nieuwe onderwerpen. Het originele document is niet opnieuw geupload; publicatie volgt pas na goedkeuring.');
    });
  }

  protected editQuestion(id: string, field: 'title' | 'examPage' | 'examEndPage' | 'answerPage' | 'answerEndPage',
    value: string): void {
    const next = field === 'title' ? value : value.trim() ? Number(value) : null;
    if (field !== 'title' && next !== null && (typeof next !== 'number' || !Number.isSafeInteger(next) || next < 1)) {
      this.error.set('Een PDF-paginanummer moet een positief geheel getal zijn.');
      return;
    }
    if ((field === 'examPage' || field === 'examEndPage') && next === null) {
      this.error.set('Het examenpaginanummer is verplicht.');
      return;
    }
    this.draftQuestions.update((questions) => questions.map((question) =>
      question.id === id ? { ...question, [field]: next, needsReview: true } : question));
  }

  protected toggleTopic(questionId: string, topicId: string, checked: boolean): void {
    this.draftQuestions.update((questions) => questions.map((question) => question.id === questionId
      ? { ...question, needsReview: true, topicIds: checked ? [...new Set([...question.topicIds, topicId])]
        : question.topicIds.filter((id) => id !== topicId) } : question));
  }

  protected markReviewed(id: string, checked: boolean): void {
    this.draftQuestions.update((questions) => questions.map((question) =>
      question.id === id ? { ...question, needsReview: !checked } : question));
  }

  protected changeDraftTopics(topics: TopicDefinition[]): void {
    this.draftTopics.set(topics);
    const allowed = new Set(topics.map((topic) => topic.id));
    this.draftQuestions.update((questions) => questions.map((question) => ({
      ...question, needsReview: true, topicIds: question.topicIds.filter((id) => allowed.has(id)),
    })));
  }

  protected askApproveAll(): void {
    this.approveAllPending.set(true);
  }

  protected cancelApproveAll(): void {
    this.approveAllPending.set(false);
  }

  /** Confirms every valid question with its proposed values and saves with the current revision (409 on a stale one). */
  protected async approveAll(): Promise<void> {
    const selected = this.selected();
    this.approveAllPending.set(false);
    if (!selected || selected.state !== 'review' || !this.validTopics(this.draftTopics())) return;
    const { questions, approved } = approveQuestions(this.draftQuestions(), this.draftTopics());
    const skipped = questions.filter((question) => question.needsReview).length;
    await this.mutate('draft', { revision: selected.revision, topics: this.draftTopics(), questions });
    if (!this.error()) {
      this.notice.set(skipped
        ? `${approved} vragen goedgekeurd. ${skipped} ${skipped === 1 ? 'vraag heeft' : 'vragen hebben'} nog aandacht nodig.`
        : `Alle ${approved} vragen zijn goedgekeurd en opgeslagen. Je kunt nu publiceren.`);
    }
  }

  protected async saveDraft(): Promise<void> {
    const selected = this.selected();
    if (!selected || !this.validTopics(this.draftTopics())) return;
    await this.mutate('draft', { revision: selected.revision, topics: this.draftTopics(), questions: this.draftQuestions() });
  }

  protected async confirmDefinitions(): Promise<void> {
    const selected = this.selected();
    if (!selected || !this.validTopics(this.draftTopics())) return;
    await this.mutate('confirm-definitions', { revision: selected.revision, topics: this.draftTopics() });
  }

  protected async action(action: 'publish' | 'cancel' | 'retry'): Promise<void> {
    const selected = this.selected();
    if (!selected) return;
    if (action === 'publish' && !this.canPublish()) {
      this.error.set('Sla je wijzigingen op en controleer alle antwoordkoppelingen voordat je publiceert.');
      return;
    }
    await this.mutate(action, { revision: selected.revision });
  }

  private validTopics(topics: TopicDefinition[]): boolean {
    if (!topics.length || topics.some((topic) => !topic.title.trim() || !topic.description.trim())) {
      this.error.set('Geef ten minste een onderwerp op, met een titel en een duidelijke leerinhoud.');
      return false;
    }
    return true;
  }

  private async mutate(action: 'draft' | 'confirm-definitions' | 'publish' | 'cancel' | 'retry', body: object): Promise<void> {
    const selected = this.selected();
    if (!selected) return;
    await this.run(async (signal) => {
      const record = await updateImport(selected.id, action, body, signal);
      this.accept(record);
      this.notice.set(action === 'publish' ? 'Het examen is gepubliceerd in de trainer.'
        : action === 'retry' ? 'De verwerking is opnieuw ingepland. Dit kan modelkosten veroorzaken.'
          : action === 'confirm-definitions' ? 'De onderwerpen zijn bevestigd. De examenverwerking wordt vervolgd.'
            : action === 'cancel' ? 'Annulering is aangevraagd.' : 'Het concept is opgeslagen.');
      if (record.state === 'published') this.published.emit();
    });
  }

  private accept(record: ImportRecord): void {
    if (this.destroyed) return;
    this.selected.set(record);
    this.records.update((records) => upsertImport(records, record));
  }

  protected canDelete(record: ImportRecord): boolean {
    return deletable(record);
  }

  protected askDelete(record: ImportRecord): void {
    this.deleteCandidate.set(record.id);
  }

  protected cancelDelete(): void {
    this.deleteCandidate.set(null);
  }

  protected async confirmDelete(record: ImportRecord): Promise<void> {
    let removed = false;
    await this.run(async (signal) => {
      try {
        await deleteImport(record.id, signal);
      } catch (error: unknown) {
        this.deleteCandidate.set(null);
        throw error;
      }
      if (signal.aborted || this.destroyed) return;
      this.deleteCandidate.set(null);
      this.records.update((records) => removeImport(records, record.id));
      if (this.selected()?.id === record.id) {
        this.selected.set(null);
        this.selectedQuestionId.set('');
      }
      this.notice.set('De import is verwijderd.');
      removed = true;
    });
    if (removed && !this.destroyed) {
      this.importList().nativeElement.focus();
    }
  }

  private message(error: unknown): string {
    return error instanceof Error ? error.message : 'Het importverzoek is mislukt.';
  }

  private async run(operation: (signal: AbortSignal) => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    this.request = new AbortController();
    try {
      await operation(this.request.signal);
    } catch (error: unknown) {
      if (!this.destroyed) this.error.set(this.message(error));
    } finally {
      if (!this.destroyed) this.busy.set(false);
    }
  }
}
