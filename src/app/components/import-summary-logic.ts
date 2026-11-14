import type { DraftQuestion, TopicDefinition } from '../services/exam-import-client';

export interface SummaryQuestion {
  id: string;
  number: number;
}

export interface TopicSummaryRow {
  id: string;
  title: string;
  /** In ascending question number. */
  questions: SummaryQuestion[];
}

export interface ImportSummary {
  topics: TopicSummaryRow[];
  /** Questions without any of the import's topics; the data has no separate "not relevant" flag. */
  notRelevant: SummaryQuestion[];
  /** Distinct questions with at least one topic; a question with several topics counts once here. */
  found: number;
}

/** Groups questions per topic in the import's topic order. A question with several topics appears under each. */
export function summarizeImport(topics: readonly TopicDefinition[], questions: readonly DraftQuestion[]): ImportSummary {
  const ordered = [...questions].sort((a, b) => a.number - b.number);
  const known = new Set(topics.map((topic) => topic.id));
  const relevant = (question: DraftQuestion) => question.topicIds.some((id) => known.has(id));
  const entry = (question: DraftQuestion): SummaryQuestion => ({ id: question.id, number: question.number });
  return {
    topics: topics.map((topic) => ({
      id: topic.id, title: topic.title,
      questions: ordered.filter((question) => question.topicIds.includes(topic.id)).map(entry),
    })),
    notRelevant: ordered.filter((question) => !relevant(question)).map(entry),
    found: ordered.filter(relevant).length,
  };
}

export function questionCount(count: number): string {
  return `${count} ${count === 1 ? 'vraag' : 'vragen'}`;
}
