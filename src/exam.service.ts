import { Injectable } from '@angular/core';
import { getDocument, type PDFDocumentProxy } from 'pdfjs-dist';
import {
  questions,
  topics,
  type DocumentSearchMatch,
  type ExamQuestion,
  type ExamYearGroup,
} from './exam-data';

export interface DocumentSearchResults {
  matches: DocumentSearchMatch[];
  matchesByPage: Map<number, DocumentSearchMatch[]>;
}

@Injectable({ providedIn: 'root' })
export class ExamService {
  readonly topics = topics;
  readonly questions = questions;

  activeTopic = topics[0].id;
  examOverviewOpen = false;
  searchQuery = '';
  searchMatches: DocumentSearchMatch[] = [];
  searchMatchesByPage = new Map<number, DocumentSearchMatch[]>();
  activeSearchMatchIndex = -1;
  activeSearchMatchId = '';
  searching = false;
  searchError = '';
  questionIndex = 0;
  showAnswer = false;
  private doneIds = this.readDoneIds();
  private searchVersion = 0;
  private readonly documents = new Map<string, Promise<PDFDocumentProxy>>();

  get filteredQuestions(): ExamQuestion[] {
    return this.questions.filter((question) => question.topics.includes(this.activeTopic));
  }

  get examYears(): number[] {
    return [...new Set(this.questions.map((question) => question.year))].sort((a, b) => b - a);
  }

  get examOverview(): ExamYearGroup[] {
    const questionsByYear = new Map<number, Map<number, ExamQuestion[]>>();
    for (const question of this.questions) {
      let questionsByTimevak = questionsByYear.get(question.year);
      if (!questionsByTimevak) {
        questionsByTimevak = new Map();
        questionsByYear.set(question.year, questionsByTimevak);
      }

      let timevakQuestions = questionsByTimevak.get(question.timevak);
      if (!timevakQuestions) {
        timevakQuestions = [];
        questionsByTimevak.set(question.timevak, timevakQuestions);
      }
      timevakQuestions.push(question);
    }

    return [...questionsByYear]
      .sort(([yearA], [yearB]) => yearB - yearA)
      .map(([year, questionsByTimevak]) => ({
        year,
        timevakken: [...questionsByTimevak]
          .sort(([timevakA], [timevakB]) => timevakA - timevakB)
          .map(([timevak, timevakQuestions]) => ({
            timevak,
            questions: timevakQuestions.sort((questionA, questionB) => questionA.number - questionB.number),
          })),
      }));
  }

  get selectedQuestion(): ExamQuestion | undefined {
    return this.filteredQuestions[this.questionIndex];
  }

  get previousAnswerPage(): number {
    return this.selectedQuestion && this.selectedQuestion.answerPage > 5 ? this.selectedQuestion.answerPage - 1 : 0;
  }

  get activeTopicTitle(): string {
    return this.topics.find((topic) => topic.id === this.activeTopic)?.title ?? '';
  }

  get completedCount(): number {
    return this.questions.filter((question) => this.doneIds.has(question.id)).length;
  }

  get progressPercent(): number {
    return this.questions.length ? (this.completedCount / this.questions.length) * 100 : 0;
  }

  countFor(topicId: string): number {
    return this.questions.filter((question) => question.topics.includes(topicId)).length;
  }

  searchMatchesForPage(pageNumber: number): DocumentSearchMatch[] {
    return this.searchMatchesByPage.get(pageNumber) ?? [];
  }

  isDone(questionId: string): boolean {
    return this.doneIds.has(questionId);
  }

  selectTopic(topicId: string): void {
    this.activeTopic = topicId;
    this.questionIndex = 0;
    this.showAnswer = false;
  }

  selectQuestion(question: ExamQuestion): void {
    this.questionIndex = this.filteredQuestions.findIndex((item) => item.id === question.id);
    this.showAnswer = false;
  }

  selectExamOverviewQuestion(question: ExamQuestion): void {
    this.activeTopic = question.topics[0] ?? this.activeTopic;
    this.questionIndex = this.filteredQuestions.findIndex((item) => item.id === question.id);
    this.examOverviewOpen = false;
    this.showAnswer = false;
  }

  moveQuestion(direction: number): void {
    this.questionIndex = Math.max(0, Math.min(this.filteredQuestions.length - 1, this.questionIndex + direction));
    this.showAnswer = false;
  }

  toggleAnswer(): void {
    this.showAnswer = !this.showAnswer;
  }

  closeAnswer(): void {
    this.showAnswer = false;
  }

  closeExamOverview(): void {
    this.examOverviewOpen = false;
  }

  toggleDone(questionId: string): void {
    if (this.doneIds.has(questionId)) {
      this.doneIds.delete(questionId);
    } else {
      this.doneIds.add(questionId);
    }
    localStorage.setItem('examinator-done-questions', JSON.stringify([...this.doneIds]));
  }

  beginSearch(query: string): number {
    this.searchQuery = query;
    this.searchVersion++;
    this.searchMatches = [];
    this.searchMatchesByPage = new Map();
    this.activeSearchMatchIndex = -1;
    this.activeSearchMatchId = '';
    this.searchError = '';
    this.searching = Boolean(query.trim());
    return this.searchVersion;
  }

  resetSearch(): void {
    this.searchVersion++;
    this.searchMatches = [];
    this.searchMatchesByPage = new Map();
    this.activeSearchMatchIndex = -1;
    this.activeSearchMatchId = '';
    this.searching = false;
    this.searchError = '';
  }

  isCurrentSearch(version: number): boolean {
    return version === this.searchVersion;
  }

  setSearchResults(version: number, results: DocumentSearchResults): boolean {
    if (!this.isCurrentSearch(version)) return false;
    this.searchMatches = results.matches;
    this.searchMatchesByPage = results.matchesByPage;
    this.searching = false;
    return true;
  }

  setSearchError(version: number): void {
    if (!this.isCurrentSearch(version)) return;
    this.searching = false;
    this.searchError = 'Zoeken is mislukt.';
  }

  stopSearch(version: number): void {
    if (this.isCurrentSearch(version)) this.searching = false;
  }

  async findDocumentMatches(
    file: string,
    query: string,
    version: number,
    getAvailableWidth: (pageNumber: number) => number | undefined,
  ): Promise<DocumentSearchResults | undefined> {
    const document = await this.loadDocument(file);
    const normalizedQuery = query.toLocaleLowerCase();
    const matches: DocumentSearchMatch[] = [];
    const matchesByPage = new Map<number, DocumentSearchMatch[]>();

    for (let pageNumber = 1; pageNumber <= document.numPages; pageNumber++) {
      if (!this.isCurrentSearch(version)) return undefined;

      const page = await document.getPage(pageNumber);
      const content = await page.getTextContent();
      const baseViewport = page.getViewport({ scale: 1 });
      const availableWidth = getAvailableWidth(pageNumber) ?? baseViewport.width;
      const scale = Math.min(availableWidth / baseViewport.width, 1.4);
      const viewport = page.getViewport({ scale });
      const pageMatches: DocumentSearchMatch[] = [];

      for (let itemIndex = 0; itemIndex < content.items.length; itemIndex++) {
        const item = content.items[itemIndex];
        if (!('str' in item) || !item.str) continue;

        const text = item.str.toLocaleLowerCase();
        const itemWidth = item.width * scale;
        const itemHeight = Math.max(item.height * scale, 8);
        const [baselineX, baselineY] = viewport.convertToViewportPoint(item.transform[4], item.transform[5]);
        let start = text.indexOf(normalizedQuery);
        while (start !== -1) {
          const matchWidth = Math.max(itemWidth * normalizedQuery.length / item.str.length, 5);
          const match = {
            id: `${pageNumber}-${itemIndex}-${start}`,
            pageNumber,
            left: baselineX + itemWidth * start / item.str.length,
            top: baselineY - itemHeight,
            width: matchWidth,
            height: itemHeight,
          };
          matches.push(match);
          pageMatches.push(match);
          start = text.indexOf(normalizedQuery, start + normalizedQuery.length);
        }
      }

      if (pageMatches.length) matchesByPage.set(pageNumber, pageMatches);
    }

    if (!this.isCurrentSearch(version)) return undefined;
    return { matches, matchesByPage };
  }

  loadDocument(file: string): Promise<PDFDocumentProxy> {
    let documentPromise = this.documents.get(file);
    if (!documentPromise) {
      documentPromise = getDocument({ url: `Data/processed/${file}` }).promise;
      this.documents.set(file, documentPromise);
    }
    return documentPromise;
  }

  private readDoneIds(): Set<string> {
    try {
      return new Set(JSON.parse(localStorage.getItem('examinator-done-questions') ?? '[]') as string[]);
    } catch {
      return new Set();
    }
  }
}
