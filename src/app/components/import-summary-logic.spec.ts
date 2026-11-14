import { describe, expect, it } from 'vitest';
import type { DraftQuestion, TopicDefinition } from '../services/exam-import-client';
import { questionCount, summarizeImport } from './import-summary-logic';

const topic = (id: string): TopicDefinition => ({ id, title: id.toUpperCase(), description: 'd', keywords: [],
  exclusions: [], aliases: [], chapterNumber: null, paragraphNumber: null });
const question = (number: number, topicIds: string[]): DraftQuestion => ({ id: `q${number}`, number, title: 't',
  examPage: 1, examEndPage: 1, answerPage: 1, answerEndPage: 1, topicIds, reasons: [], needsReview: false, warnings: [] });

describe('summarizeImport', () => {
  const topics = [topic('b'), topic('a'), topic('c')];

  it('keeps the topic order and sorts question numbers', () => {
    const summary = summarizeImport(topics, [question(9, ['a']), question(2, ['a']), question(5, ['b'])]);
    expect(summary.topics.map((row) => row.id)).toEqual(['b', 'a', 'c']);
    expect(summary.topics[1].questions.map((item) => item.number)).toEqual([2, 9]);
  });

  it('counts a question with several topics under each topic but once in total', () => {
    const summary = summarizeImport(topics, [question(1, ['a', 'b']), question(2, ['b'])]);
    expect(summary.topics[0].questions.map((item) => item.number)).toEqual([1, 2]);
    expect(summary.topics[1].questions.map((item) => item.number)).toEqual([1]);
    expect(summary.found).toBe(2);
  });

  it('lists topics without questions', () => {
    expect(summarizeImport(topics, [question(1, ['a'])]).topics[2].questions).toEqual([]);
  });

  it('treats questions without a known topic as not relevant', () => {
    const summary = summarizeImport(topics, [question(4, []), question(1, ['a']), question(3, ['removed'])]);
    expect(summary.notRelevant.map((item) => item.number)).toEqual([3, 4]);
    expect(summary.found).toBe(1);
  });

  it('handles an empty import', () => {
    expect(summarizeImport([], [])).toEqual({ topics: [], notRelevant: [], found: 0 });
  });
});

describe('questionCount', () => {
  it('pluralises in Dutch', () => {
    expect(questionCount(1)).toBe('1 vraag');
    expect(questionCount(0)).toBe('0 vragen');
  });
});
