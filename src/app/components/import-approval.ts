import type { DraftQuestion, TopicDefinition } from '../services/exam-import-client';

/** Dutch reason why a question cannot be approved in bulk, or null when it passes the checks the server also applies. */
export function approvalIssue(question: DraftQuestion, topics: readonly TopicDefinition[]): string | null {
  if (question.answerPage === null || question.answerEndPage === null) return 'antwoordpagina ontbreekt';
  if (question.answerEndPage < question.answerPage) return 'antwoordpagina\'s zijn ongeldig';
  if (question.examEndPage < question.examPage) return 'examenpagina\'s zijn ongeldig';
  if (!question.title.trim()) return 'titel ontbreekt';
  const known = new Set(topics.map((topic) => topic.id));
  if (new Set(question.topicIds).size !== question.topicIds.length || question.topicIds.some((id) => !known.has(id))) {
    return 'onderwerpen zijn ongeldig';
  }
  return null;
}

export interface BulkApproval {
  questions: DraftQuestion[];
  approved: number;
}

/** Confirms every unconfirmed question that passes validation; the others stay untouched. */
export function approveAll(questions: readonly DraftQuestion[], topics: readonly TopicDefinition[]): BulkApproval {
  let approved = 0;
  const next = questions.map((question) => {
    if (!question.needsReview || approvalIssue(question, topics) !== null) return question;
    approved++;
    return { ...question, needsReview: false };
  });
  return { questions: next, approved };
}

export interface AttentionItem {
  id: string;
  number: number;
  issue: string;
}

export interface Attention {
  /** Unconfirmed questions with a real validation issue. */
  issues: AttentionItem[];
  /** Unconfirmed questions without an issue; they only need the reviewer's check. */
  unchecked: number;
}

/** Splits the unconfirmed questions into real validation issues and plain "not yet checked" ones. */
export function attentionSummary(questions: readonly DraftQuestion[], topics: readonly TopicDefinition[]): Attention {
  const issues: AttentionItem[] = [];
  let unchecked = 0;
  for (const question of questions) {
    if (!question.needsReview) continue;
    const issue = approvalIssue(question, topics);
    if (issue === null) unchecked++;
    else issues.push({ id: question.id, number: question.number, issue });
  }
  return { issues, unchecked };
}
