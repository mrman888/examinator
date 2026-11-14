import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { type TopicDefinition } from '../services/exam-import-client';

@Component({
  selector: 'app-topic-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="topic-editor">
      @for (topic of topics(); track topic.id) {
        <fieldset>
          <legend>{{ topic.paragraphNumber || 'Onderwerp' }}: {{ topic.title || 'Nieuw onderwerp' }}</legend>
          <label>Titel <input #title [value]="topic.title" [disabled]="disabled()"
            (input)="change(topic.id, 'title', title.value)" maxlength="160" /></label>
          <label>Leerinhoud / afbakening <textarea #description rows="2" [value]="topic.description"
            [disabled]="disabled()" (input)="change(topic.id, 'description', description.value)" maxlength="2000"></textarea></label>
          <div class="row">
            <label>Trefwoorden (komma's) <input #keywords [value]="topic.keywords.join(', ')"
              [disabled]="disabled()" (input)="changeList(topic.id, 'keywords', keywords.value)" maxlength="1000" /></label>
            <label>Uitsluitingen (komma's) <input #exclusions [value]="topic.exclusions.join(', ')"
              [disabled]="disabled()" (input)="changeList(topic.id, 'exclusions', exclusions.value)" maxlength="1000" /></label>
          </div>
          <div class="row">
            <label>Zoeknamen / synoniemen <input #aliases [value]="topic.aliases.join(', ')"
              [disabled]="disabled()" (input)="changeList(topic.id, 'aliases', aliases.value)" maxlength="1000" /></label>
            <label>Hoofdstuk (metadata) <input #chapter [value]="topic.chapterNumber ?? ''"
              [disabled]="disabled()" (input)="change(topic.id, 'chapterNumber', chapter.value || null)" maxlength="40" /></label>
            <label>Paragraaf (metadata) <input #paragraph [value]="topic.paragraphNumber ?? ''"
              [disabled]="disabled()" (input)="change(topic.id, 'paragraphNumber', paragraph.value || null)" maxlength="40" /></label>
          </div>
          <button type="button" (click)="remove(topic.id)" [disabled]="disabled()">Onderwerp verwijderen</button>
        </fieldset>
      }
      <button type="button" (click)="add()" [disabled]="disabled() || topics().length >= 30">Onderwerp toevoegen</button>
    </div>
  `,
  styles: [`
    fieldset { border: 1px solid var(--color-border); border-radius: 8px; margin: 12px 0; padding: 14px; min-width: 0; }
    legend { font-weight: bold; font-size: 13px; }
    label { display: grid; gap: 5px; margin: 8px 0; font-size: 12px; }
    input, textarea { width: 100%; min-width: 0; padding: 8px; border: 1px solid var(--color-border); border-radius: 4px; font: inherit; }
    .row { display: flex; flex-wrap: wrap; gap: 12px; } .row label { flex: 1 1 180px; }
    button { padding: 7px 10px; background: var(--color-accent-subtle); color: var(--color-accent-strong); border: 1px solid var(--color-border); border-radius: 5px; cursor: pointer; }
    :is(input, textarea, button):focus-visible { outline: 2px solid var(--color-accent); outline-offset: 2px; }
    button:disabled { opacity: .5; cursor: default; }
  `],
})
export class TopicEditorComponent {
  readonly topics = input.required<TopicDefinition[]>();
  readonly disabled = input(false);
  readonly topicsChanged = output<TopicDefinition[]>();

  protected change(id: string, field: 'title' | 'description' | 'chapterNumber' | 'paragraphNumber', value: string | null): void {
    this.topicsChanged.emit(this.topics().map((topic) => topic.id === id ? { ...topic, [field]: value } : topic));
  }

  protected changeList(id: string, field: 'keywords' | 'exclusions' | 'aliases', value: string): void {
    const items = [...new Set(value.split(',').map((item) => item.trim()).filter(Boolean))];
    this.topicsChanged.emit(this.topics().map((topic) => topic.id === id ? { ...topic, [field]: items } : topic));
  }

  protected remove(id: string): void {
    this.topicsChanged.emit(this.topics().filter((topic) => topic.id !== id));
  }

  protected add(): void {
    this.topicsChanged.emit([...this.topics(), {
      id: `topic-${crypto.randomUUID()}`, title: '', description: '', keywords: [], exclusions: [], aliases: [],
      chapterNumber: null, paragraphNumber: null,
    }]);
  }
}
