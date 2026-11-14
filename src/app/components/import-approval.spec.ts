import { describe, expect, it } from 'vitest';
import { approvalIssue, approveAll, attentionSummary } from './import-approval';
import type { DraftQuestion, TopicDefinition } from '../services/exam-import-client';

const topics = [{ id: 't1' }] as TopicDefinition[];
const q = (id: string, patch: Partial<DraftQuestion> = {}): DraftQuestion => ({
  id, number: 1, title: 'Titel', examPage: 1, examEndPage: 1, answerPage: 2, answerEndPage: 2,
  topicIds: ['t1'], reasons: [], needsReview: true, warnings: [], ...patch,
});

describe('bulk approval logic', () => {
  it('approves valid unconfirmed questions and keeps their values', () => {
    const result = approveAll([q('a'), q('b', { needsReview: false })], topics);
    expect(result.approved).toBe(1);
    expect(result.questions.map((x) => x.needsReview)).toEqual([false, false]);
    expect(result.questions[0]).toEqual({ ...q('a'), needsReview: false });
  });

  it('skips questions that fail validation', () => {
    const bad = [q('a', { answerPage: null, answerEndPage: null }), q('b', { topicIds: ['x'] }), q('c', { title: ' ' })];
    const result = approveAll(bad, topics);
    expect(result.approved).toBe(0);
    expect(result.questions).toEqual(bad);
    expect(bad.map((x) => approvalIssue(x, topics))).toEqual([
      'antwoordpagina ontbreekt', 'onderwerpen zijn ongeldig', 'titel ontbreekt']);
  });

  it('lists only real issues and counts plain unchecked questions', () => {
    const result = attentionSummary([
      q('a'), q('b', { number: 2, answerPage: null, answerEndPage: null }),
      q('c', { needsReview: false, answerPage: null }), q('d', { number: 4, title: ' ' })], topics);
    expect(result.issues).toEqual([
      { id: 'b', number: 2, issue: 'antwoordpagina ontbreekt' }, { id: 'd', number: 4, issue: 'titel ontbreekt' }]);
    expect(result.unchecked).toBe(1);
  });

  it('reports nothing for a fresh import without validation issues', () => {
    expect(attentionSummary([q('a'), q('b')], topics)).toEqual({ issues: [], unchecked: 2 });
  });
});
