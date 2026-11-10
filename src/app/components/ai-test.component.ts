import { Component, OnDestroy, signal } from '@angular/core';
import { sendTestPrompt } from '../services/ai-test-client';

@Component({
  selector: 'app-ai-test',
  template: `
    <section class="ai-panel" aria-labelledby="ai-title">
      <p class="ai-label">AZURE FOUNDRY</p>
      <h1 id="ai-title">AI-verbinding testen</h1>
      <p>Stel een korte vraag. Je invoer wordt via de API naar Azure Foundry gestuurd.
        Dit is alleen een verbindingstest; er worden geen examens geimporteerd.</p>
      <form (submit)="submit($event)">
        <label for="ai-prompt">Je vraag</label>
        <textarea id="ai-prompt" #input rows="5" maxlength="2000"
          [value]="prompt()" (input)="prompt.set(input.value)" [disabled]="loading()"
          placeholder="Leg kort uit wat fotosynthese is."></textarea>
        <div class="ai-actions">
          <small>{{ prompt().length }} / 2000 tekens</small>
          <button type="submit" [disabled]="loading() || !prompt().trim()">
            {{ loading() ? 'Antwoord ophalen...' : 'Verstuur naar AI' }}
          </button>
        </div>
      </form>
      <div aria-live="polite" [attr.aria-busy]="loading()">
        @if (loading()) {
          <p>Even geduld. Bij het eerste verzoek kan het starten van de containers extra tijd kosten.</p>
        }
        @if (error()) {
          <p class="ai-error" role="alert">{{ error() }}</p>
          <a href="/.auth/login/aad?post_login_redirect_uri=/">Opnieuw aanmelden met Microsoft</a>
        }
        @if (reply()) {
          <h2>Antwoord</h2>
          <div class="ai-reply">{{ reply() }}</div>
          <small>AI kan fouten maken. Controleer het antwoord.</small>
        }
      </div>
    </section>
  `,
  styles: [`
    :host { width: min(100%, 850px); overflow: auto; padding: 0 0 24px 24px; }
    .ai-panel { padding: clamp(18px, 4vw, 36px); border: 1px solid var(--line); border-radius: 12px; background: var(--paper); }
    .ai-label { color: var(--green); font-size: 11px; font-weight: bold; letter-spacing: 1px; }
    h1 { font-size: 26px; } h2 { font-size: 18px; }
    p { line-height: 1.6; } label { display: block; margin: 24px 0 8px; font-weight: bold; }
    textarea { box-sizing: border-box; width: 100%; padding: 12px; border: 1px solid var(--line); border-radius: 6px; font: inherit; resize: vertical; }
    textarea:focus { outline: 2px solid var(--green); outline-offset: 2px; }
    .ai-actions { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin: 12px 0 24px; flex-wrap: wrap; }
    button { padding: 10px 16px; border: 0; border-radius: 6px; background: var(--green); color: white; cursor: pointer; }
    button:disabled { opacity: .55; cursor: default; }
    .ai-reply { white-space: pre-wrap; overflow-wrap: anywhere; padding: 16px; background: var(--green-pale); border-radius: 6px; line-height: 1.6; margin-bottom: 12px; }
    .ai-error { color: #962e2e; } small { color: #58665e; } a { color: var(--green); }
  `],
})
export class AiTestComponent implements OnDestroy {
  readonly prompt = signal('');
  readonly reply = signal('');
  readonly error = signal('');
  readonly loading = signal(false);
  private request?: AbortController;

  async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.loading() || !this.prompt().trim()) return;
    this.loading.set(true);
    this.error.set('');
    this.reply.set('');
    this.request = new AbortController();
    const timer = setTimeout(() => this.request?.abort(), 150_000);
    try {
      this.reply.set(await sendTestPrompt(this.prompt().trim(), this.request.signal));
    } catch (error: unknown) {
      this.error.set(error instanceof TypeError
        ? 'Geen verbinding met de API. Meld je opnieuw aan of controleer of de website en API bereikbaar zijn.'
        : error instanceof Error && error.name !== 'AbortError'
          ? error.message : 'Het verzoek is afgebroken of duurde te lang. Probeer opnieuw.');
    } finally {
      clearTimeout(timer);
      this.loading.set(false);
    }
  }

  ngOnDestroy(): void {
    this.request?.abort();
  }
}
