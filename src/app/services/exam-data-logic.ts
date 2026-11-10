export interface TopicQuestion {
  id: string;
  topics: string[];
  examFile?: string;
}

export interface CompletionStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

const COMPLETED_QUESTIONS_KEY = 'examinator-done-questions';

export function getQuestionsForTopic<T extends TopicQuestion>(questions: T[], topicId: string, examFile: string | null = null): T[] {
  return questions.filter((question) => question.topics.includes(topicId) && (examFile === null || question.examFile === examFile));
}

export function countQuestionsForTopic(questions: TopicQuestion[], topicId: string, examFile: string | null = null): number {
  return getQuestionsForTopic(questions, topicId, examFile).length;
}

export function readCompletedIds(storage: CompletionStorage): Set<string> {
  try {
    const storedIds: unknown = JSON.parse(storage.getItem(COMPLETED_QUESTIONS_KEY) ?? '[]');
    return new Set(Array.isArray(storedIds) ? storedIds.filter((id): id is string => typeof id === 'string') : []);
  } catch {
    return new Set();
  }
}

export function toggleCompletedId(storage: CompletionStorage, doneIds: Set<string>, questionId: string): Set<string> {
  const updatedIds = new Set(doneIds);
  if (updatedIds.has(questionId)) {
    updatedIds.delete(questionId);
  } else {
    updatedIds.add(questionId);
  }
  storage.setItem(COMPLETED_QUESTIONS_KEY, JSON.stringify([...updatedIds]));
  return updatedIds;
}

export function getProgressPercent(totalQuestions: number, completedQuestions: number): number {
  return totalQuestions ? (completedQuestions / totalQuestions) * 100 : 0;
}
