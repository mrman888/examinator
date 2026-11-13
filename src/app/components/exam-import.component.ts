import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, linkedSignal, output, signal, viewChild } from '@angular/core';
import { ExamDataService } from '../services/exam-data.service';
import { type DraftQuestion, type ImportRecord, type ImportState, type TopicDefinition,
  listImports, loadImport, updateImport, uploadImport } from '../services/exam-import-client';
import { ImportPdfPreviewComponent } from './import-pdf-preview.component';
import { TopicEditorComponent } from './topic-editor.component';

const STATE_LABELS: Record<ImportState, string> = {
  queued: 'In wachtrij', extracting: 'Documenten uitlezen', awaitingDefinitions: 'Hoofdstuk controleren',
  aligning: 'Antwoorden koppelen', tagging: 'Onderwerpen toekennen', review: 'Klaar voor beoordeling',
  published: 'Gepubliceerd', failed: 'Mislukt', cancelled: 'Geannuleerd',
};

function processing(record: ImportRecord | null): boolean {
  return !!record && ['queued', 'extracting', 'aligning', 'tagging'].includes(record.state);
}

@Component({
  selector: 'app-exam-import',
  imports: [TopicEditorComponent, ImportPdfPreviewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './exam-import.component.html',
  styleUrl: './exam-import.component.scss',
})
export class ExamImportComponent {
  readonly published = output<void>();
  private readonly data = inject(ExamDataService);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('uploadDialog');
  private readonly uploadForm = viewChild.required<ElementRef<HTMLFormElement>>('uploadForm');
  private request?: AbortController;
  private destroyed = false;
  protected readonly records = signal<ImportRecord[]>([]);
  protected readonly selected = signal<ImportRecord | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly chapterNumber = signal('');
  protected readonly chapterTitle = signal('');
  protected readonly contextText = signal('');
  protected readonly uploadTopics = signal<TopicDefinition[]>([]);
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

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.request?.abort();
    });
    effect((onCleanup) => {
      const selected = this.selected();
      if (!processing(selected) || this.busy() || !selected) return;
      const controller = new AbortController();
      const timer = setTimeout(() => {
        void loadImport(selected.id, controller.signal).then((record) => {
          if (!controller.signal.aborted && !this.destroyed) this.accept(record);
        }, (error: unknown) => {
          if (!controller.signal.aborted && !this.destroyed) {
            this.error.set(this.message(error));
          }
        });
      }, 5000);
      onCleanup(() => {
        clearTimeout(timer);
        controller.abort();
      });
    });
    void this.refreshList();
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
    this.uploadTopics.set(structuredClone(this.data.topicDefinitions()));
    this.dialog().nativeElement.showModal();
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
    await this.run(async (signal) => { this.records.set(await listImports(signal)); });
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
    const exam = data.get('exam');
    const answers = data.get('answers');
    const context = data.get('context');
    if (!(exam instanceof File) || !(answers instanceof File) || !exam.size || !answers.size) {
      this.error.set('Kies het examen en het bijbehorende antwoorddocument.');
      return;
    }
    if (exam.size > 20 * 1024 * 1024 || answers.size > 20 * 1024 * 1024 ||
      (context instanceof File && context.size > 5 * 1024 * 1024)) {
      this.error.set('Maximaal 20 MiB per PDF en 5 MiB voor de hoofdstukscreenshot.');
      return;
    }
    if (!this.validTopics(this.uploadTopics())) return;
    if (context instanceof File && !context.size) data.delete('context');
    data.delete('permission');
    data.set('metadata', JSON.stringify({
      subjectId: 'biology', level: 'havo', year: 0, timevak: 0,
      chapterNumber: this.chapterNumber().trim(), chapterTitle: this.chapterTitle().trim(),
      topics: this.uploadTopics(),
    }));
    data.set('contextText', this.contextText().trim());
    await this.run(async (signal) => {
      const record = await uploadImport(data, signal);
      if (signal.aborted) return;
      this.accept(record);
      this.dialog().nativeElement.close();
      form.querySelectorAll<HTMLInputElement>('input[type="file"]').forEach((input) => { input.value = ''; });
      const permission = form.querySelector<HTMLInputElement>('input[name="permission"]');
      if (permission) permission.checked = false;
      this.notice.set('De documenten zijn opgeslagen. De verwerking loopt op de achtergrond; publicatie volgt pas na goedkeuring.');
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
    this.records.update((records) => [record, ...records.filter((item) => item.id !== record.id)]);
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
