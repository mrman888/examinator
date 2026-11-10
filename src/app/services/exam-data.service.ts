import { Injectable, computed, signal } from '@angular/core';
import { countQuestionsForTopic, getProgressPercent, getQuestionsForTopic, readCompletedIds, toggleCompletedId } from './exam-data-logic';

export interface ExamQuestion {
  id: string;
  topics: string[];
  title: string;
  examFile: string;
  examPage: number;
  answerFile: string;
  answerPage: number;
  year: number;
  timevak: number;
  number: number;
}

export interface ExamTopic {
  id: string;
  title: string;
}

export interface ExamSitting {
  year: number;
  timevak: number;
  examFile: string;
}

const TOPICS: ExamTopic[] = [
  { id: '2.1', title: 'Menselijke en dierlijke cellen' },
  { id: '2.2', title: 'DNA en specialisatie van cellen' },
  { id: '2.3', title: 'Celdeling en kanker' },
  { id: '2.4', title: 'Kweken van cellen, weefsel en organen' },
  { id: '2.5', title: 'Bacteriën, schimmels en planten' },
];

const QUESTIONS: ExamQuestion[] = [
  { id: 'h25-t1-q12', topics: ['2.1'], title: 'Bloedbestanddelen en hun functie', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 7, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 7, year: 2025, timevak: 1, number: 12 },
  { id: 'h25-t1-q15', topics: ['2.2', '2.4'], title: 'Differentiatie van bloedstamcellen', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 8, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 7, year: 2025, timevak: 1, number: 15 },
  { id: 'h25-t1-q18', topics: ['2.5'], title: 'Gaswisseling bij eendenkroos', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 9, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 8, year: 2025, timevak: 1, number: 18 },
  { id: 'h25-t1-q20', topics: ['2.5'], title: 'Bacteriën en zuurstof in het water', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 10, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 8, year: 2025, timevak: 1, number: 20 },
  { id: 'h25-t1-q35', topics: ['2.1', '2.5'], title: 'Luchtwegcellen en schimmelsporen', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 15, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2025, timevak: 1, number: 35 },
  { id: 'h25-t1-q36', topics: ['2.5'], title: 'Onderdelen van een schimmelcel', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 16, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2025, timevak: 1, number: 36 },
  { id: 'h25-t1-q38', topics: ['2.5'], title: 'Schimmelcellen en ergosterol', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 17, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 12, year: 2025, timevak: 1, number: 38 },
  { id: 'h25-t1-q39', topics: ['2.2', '2.5'], title: 'Een mutatie in een schimmelgen', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 17, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 12, year: 2025, timevak: 1, number: 39 },
  { id: 'h25-t1-q41', topics: ['2.5'], title: 'Schimmels in de kringloop', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 18, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 12, year: 2025, timevak: 1, number: 41 },
  { id: 'h25-t1-q42', topics: ['2.5'], title: 'Amylase van een schimmel', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 18, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 12, year: 2025, timevak: 1, number: 42 },
  { id: 'h25-t1-q45', topics: ['2.1'], title: 'Signaaloverdracht bij smaakzintuigcellen', examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 19, answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 13, year: 2025, timevak: 1, number: 45 },
  { id: 'h25-t2-q3', topics: ['2.1'], title: 'Energieproductie in hartcellen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 3, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 5, year: 2025, timevak: 2, number: 3 },
  { id: 'h25-t2-q5', topics: ['2.2'], title: 'Pigmentcellen en het EDN3-gen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 4, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 5, year: 2025, timevak: 2, number: 5 },
  { id: 'h25-t2-q6', topics: ['2.2'], title: 'De oorsprong van een mutatie', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 4, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 6, year: 2025, timevak: 2, number: 6 },
  { id: 'h25-t2-q7', topics: ['2.2'], title: 'Overerving van fibromelanose', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 5, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 6, year: 2025, timevak: 2, number: 7 },
  { id: 'h25-t2-q8', topics: ['2.2'], title: 'Fenotypen bij een kruising', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 5, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 6, year: 2025, timevak: 2, number: 8 },
  { id: 'h25-t2-q9', topics: ['2.2'], title: 'Inteelt en erfelijke afwijkingen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 5, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 6, year: 2025, timevak: 2, number: 9 },
  { id: 'h25-t2-q10', topics: ['2.5'], title: 'Enzymen en gistcellen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 6, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 7, year: 2025, timevak: 2, number: 10 },
  { id: 'h25-t2-q11', topics: ['2.5'], title: 'Alcoholgisting door gistcellen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 6, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 7, year: 2025, timevak: 2, number: 11 },
  { id: 'h25-t2-q18', topics: ['2.1'], title: 'Antistoffen en afweercellen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 9, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 7, year: 2025, timevak: 2, number: 18 },
  { id: 'h25-t2-q19', topics: ['2.1'], title: 'T-cellen en virusinfecties', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 9, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 8, year: 2025, timevak: 2, number: 19 },
  { id: 'h25-t2-q20', topics: ['2.2'], title: 'Een mutatie in het coronavirus', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 9, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 8, year: 2025, timevak: 2, number: 20 },
  { id: 'h25-t2-q28', topics: ['2.5'], title: 'Bacteriën en urease', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 13, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 9, year: 2025, timevak: 2, number: 28 },
  { id: 'h25-t2-q30', topics: ['2.5'], title: 'Fotosynthese bij zonnedauw', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 14, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 10, year: 2025, timevak: 2, number: 30 },
  { id: 'h25-t2-q32', topics: ['2.5'], title: 'Stikstof en plantengroei', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 15, answerFile: '2025_tijdvlak2_antwoorden.pdf', answerPage: 10, year: 2025, timevak: 2, number: 32 },
  { id: 'h24-t2-q2', topics: ['2.5'], title: 'Bacteriën in een kringloop', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 3, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2024, timevak: 2, number: 2 },
  { id: 'h24-t2-q12', topics: ['2.1'], title: 'Bindweefsel in een litteken', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 7, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2024, timevak: 2, number: 12 },
  { id: 'h24-t2-q20', topics: ['2.2', '2.5'], title: 'Een bacterie en genetische variatie', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 10, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 9, year: 2024, timevak: 2, number: 20 },
  { id: 'h24-t2-q23', topics: ['2.2', '2.5'], title: 'DNA-analyse van een bacterie-infectie', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 11, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 9, year: 2024, timevak: 2, number: 23 },
  { id: 'h24-t2-q26', topics: ['2.1'], title: 'Antistoffen bij een virusinfectie', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 12, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 9, year: 2024, timevak: 2, number: 26 },
  { id: 'h24-t2-q47', topics: ['2.3'], title: 'Gamma Knife bij een brughoektumor', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 19, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 12, year: 2024, timevak: 2, number: 47 },
  { id: 'h24-t2-q48', topics: ['2.3'], title: 'Bestraling en tumorcellen', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 19, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 12, year: 2024, timevak: 2, number: 48 },
];

@Injectable({ providedIn: 'root' })
export class ExamDataService {
  readonly topics = TOPICS;
  readonly questions = QUESTIONS;
  readonly years = [...new Set(QUESTIONS.map((question) => question.year))].sort((left, right) => right - left);
  readonly exams: ExamSitting[] = [...new Map(QUESTIONS.map((question) => [question.examFile, {
    year: question.year,
    timevak: question.timevak,
    examFile: question.examFile,
  }])).values()].sort((left, right) => right.year - left.year || left.timevak - right.timevak);
  private readonly doneIds = signal(readCompletedIds(localStorage));
  readonly completedCount = computed(() =>
    this.questions.filter((question) => this.doneIds().has(question.id)).length);
  readonly progressPercent = computed(() => getProgressPercent(this.questions.length, this.completedCount()));

  getExamsForYear(year: number): ExamSitting[] {
    return this.exams.filter((exam) => exam.year === year);
  }

  getQuestionsForTopic(topicId: string, examFile: string | null = null): ExamQuestion[] {
    return getQuestionsForTopic(this.questions, topicId, examFile);
  }

  countFor(topicId: string, examFile: string | null = null): number {
    return countQuestionsForTopic(this.questions, topicId, examFile);
  }

  isDone(questionId: string): boolean {
    return this.doneIds().has(questionId);
  }

  toggleDone(questionId: string): void {
    this.doneIds.update((ids) => toggleCompletedId(localStorage, ids, questionId));
  }
}
