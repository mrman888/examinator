import { ChangeDetectionStrategy, Component, ElementRef, effect, inject, input, signal, viewChild } from '@angular/core';
import { PdfPreviewService } from '../services/pdf-preview.service';

@Component({
  selector: 'app-import-pdf-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section [attr.aria-label]="label()" [attr.aria-busy]="loading()">
      <h3>{{ label() }} - PDF-pagina {{ page() }}</h3>
      <a [href]="url()" target="_blank" rel="noopener">Volledig document openen</a>
      @if (loading()) { <p role="status">Pagina laden...</p> }
      @if (error()) { <p role="alert">{{ error() }}</p> }
      <div class="sheet"><canvas #canvas [hidden]="loading() || !!error()"></canvas></div>
    </section>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    section { border: 1px solid var(--line); border-radius: 6px; padding: 12px; background: white; }
    h3 { font-size: 14px; margin: 0 0 8px; } a { font-size: 12px; color: var(--green); }
    .sheet { margin-top: 12px; } canvas { display: block; max-width: 100%; height: auto; }
    p { font-size: 12px; }
  `],
})
export class ImportPdfPreviewComponent {
  readonly url = input.required<string>();
  readonly page = input.required<number>();
  readonly label = input.required<string>();
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly pdf = inject(PdfPreviewService);

  constructor() {
    effect((onCleanup) => {
      const canvas = this.canvas().nativeElement;
      const url = this.url();
      const page = this.page();
      let obsolete = false;
      this.loading.set(true);
      this.error.set('');
      onCleanup(() => {
        obsolete = true;
        this.pdf.cancelRender(canvas);
      });
      void this.pdf.renderPage(url, page, canvas).then(() => {
        if (!obsolete) this.loading.set(false);
      }, () => {
        if (!obsolete) {
          this.loading.set(false);
          this.error.set('De PDF-pagina kon niet worden geladen. Controleer je sessie en het paginanummer.');
        }
      });
    });
  }
}
