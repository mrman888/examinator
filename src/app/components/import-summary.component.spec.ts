import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { DraftQuestion, TopicDefinition } from '../services/exam-import-client';
import { ImportSummaryComponent } from './import-summary.component';

const topic = (id: string, title: string): TopicDefinition => ({ id, title, description: 'd', keywords: [],
  exclusions: [], aliases: [], chapterNumber: null, paragraphNumber: null });
const question = (number: number, topicIds: string[]): DraftQuestion => ({ id: `q${number}`, number, title: 't',
  examPage: 1, examEndPage: 1, answerPage: 1, answerEndPage: 1, topicIds, reasons: [], needsReview: false, warnings: [] });

describe('ImportSummaryComponent', () => {
  const topics = [topic('cel', 'Celdeling'), topic('erf', 'Erfelijkheid'), topic('eco', 'Ecologie')];

  function render(questions: DraftQuestion[]) {
    const fixture = TestBed.createComponent(ImportSummaryComponent);
    fixture.componentRef.setInput('topics', topics);
    fixture.componentRef.setInput('questions', questions);
    return fixture;
  }

  it('renders the Dutch summary per topic, empty topics, not relevant and the total', async () => {
    const fixture = render([question(3, ['cel']), question(7, ['cel', 'erf']), question(5, []), question(2, [])]);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    const items = [...root.querySelectorAll('.topics li')].map((li) => li.textContent!.replace(/\s+/g, ' ').trim());
    expect(items[0]).toBe('Celdeling — 2 vragen: 37');
    expect(items[1]).toBe('Erfelijkheid — 1 vraag: 7');
    expect(items[2]).toBe('Ecologie — Geen vragen gevonden');
    expect(root.querySelector('.not-relevant')!.textContent).toContain('Niet relevant voor de gekozen onderwerpen: 2 vragen');
    expect([...root.querySelectorAll('details .number')].map((b) => b.textContent)).toEqual(['2', '5']);
    expect(root.querySelector('.total')!.textContent).toContain('2 vragen gevonden in totaal');
    expect(root.textContent).toContain('Een vraag kan bij meerdere onderwerpen horen');
  });

  it('hides the expandable list when every question is relevant', async () => {
    const fixture = render([question(1, ['cel'])]);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('.not-relevant')!.textContent).toContain('0 vragen');
    expect(root.querySelector('details')).toBeNull();
  });

  it('updates when the questions input changes', async () => {
    const fixture = render([question(1, [])]);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('.total')!.textContent).toContain('0 vragen gevonden');
    fixture.componentRef.setInput('questions', [question(1, ['eco'])]);
    await fixture.whenStable();
    expect(root.querySelector('.total')!.textContent).toContain('1 vragen gevonden');
    expect(root.querySelectorAll('.topics li')[2].textContent).toContain('1 vraag: 1');
  });

  it('emits the question id from an accessible button', async () => {
    const fixture = render([question(3, ['cel'])]);
    const selected: string[] = [];
    fixture.componentInstance.questionSelected.subscribe((id) => selected.push(id));
    await fixture.whenStable();
    const button = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.topics .number')!;
    expect(button.getAttribute('aria-label')).toBe('Ga naar vraag 3');
    button.click();
    expect(selected).toEqual(['q3']);
  });
});
