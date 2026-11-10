import { describe, expect, it } from 'vitest';
import {
  countQuestionsForTopic,
  getProgressPercent,
  getQuestionsForTopic,
  readCompletedIds,
  toggleCompletedId,
  type CompletionStorage,
  type TopicQuestion,
} from './exam-data-logic';

describe('exam data logic', () => {
  const questions: TopicQuestion[] = [
    { id: 'q1', topics: ['2.1'], examFile: '2024-t2.pdf' },
    { id: 'q2', topics: ['2.1', '2.2'], examFile: '2025-t1.pdf' },
    { id: 'q3', topics: ['2.2'], examFile: '2025-t1.pdf' },
  ];

  function createStorage(initialValue: string | null = null): CompletionStorage {
    let value = initialValue;
    return {
      getItem: () => value,
      setItem: (_key, nextValue) => { value = nextValue; },
    };
  }

  it('filters questions by topic and reports the matching count', () => {
    const filteredQuestions = getQuestionsForTopic(questions, '2.1');

    expect(filteredQuestions.map((question) => question.id)).toEqual(['q1', 'q2']);
    expect(countQuestionsForTopic(questions, '2.1')).toBe(2);
  });

  it('filters questions by the selected exam sitting', () => {
    expect(getQuestionsForTopic(questions, '2.1', '2024-t2.pdf').map((question) => question.id)).toEqual(['q1']);
    expect(countQuestionsForTopic(questions, '2.1', '2025-t1.pdf')).toBe(1);
  });

  it('calculates completion progress and handles an empty question list', () => {
    expect(getProgressPercent(4, 1)).toBe(25);
    expect(getProgressPercent(0, 0)).toBe(0);
  });

  it('toggles completion and persists the resulting IDs', () => {
    const storage = createStorage();
    const updatedIds = toggleCompletedId(storage, new Set(), 'q1');

    expect(updatedIds.has('q1')).toBe(true);
    expect(readCompletedIds(storage).has('q1')).toBe(true);
    expect(toggleCompletedId(storage, updatedIds, 'q1').has('q1')).toBe(false);
  });

  it('ignores malformed completion data', () => {
    const storage = createStorage('{invalid');

    expect(readCompletedIds(storage).size).toBe(0);
  });
});
