import { AfterViewInit, ChangeDetectorRef, Component, ElementRef, HostListener, inject, OnDestroy, QueryList, ViewChild, ViewChildren } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy, type RenderTask } from 'pdfjs-dist';

GlobalWorkerOptions.workerSrc = '/assets/pdf.worker.min.mjs';

interface ExamQuestion {
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

const topics = [
  { id: '2.1', title: 'Menselijke en dierlijke cellen' },
  { id: '2.2', title: 'DNA en specialisatie van cellen' },
  { id: '2.3', title: 'Celdeling en kanker' },
  { id: '2.4', title: 'Kweken van cellen, weefsel en organen' },
  { id: '2.5', title: 'Bacteriën, schimmels en planten' },
];

const questions: ExamQuestion[] = [
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
  { id: 'h25-t2-q3', topics: ['2.1'], title: 'Energieproductie in hartcellen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 3, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2025, timevak: 2, number: 3 },
  { id: 'h25-t2-q5', topics: ['2.2'], title: 'Pigmentcellen en het EDN3-gen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 4, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2025, timevak: 2, number: 5 },
  { id: 'h25-t2-q6', topics: ['2.2'], title: 'De oorsprong van een mutatie', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 4, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2025, timevak: 2, number: 6 },
  { id: 'h25-t2-q7', topics: ['2.2'], title: 'Overerving van fibromelanose', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 5, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2025, timevak: 2, number: 7 },
  { id: 'h25-t2-q8', topics: ['2.2'], title: 'Fenotypen bij een kruising', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 5, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2025, timevak: 2, number: 8 },
  { id: 'h25-t2-q9', topics: ['2.2'], title: 'Inteelt en erfelijke afwijkingen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 5, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2025, timevak: 2, number: 9 },
  { id: 'h25-t2-q10', topics: ['2.5'], title: 'Enzymen en gistcellen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 6, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2025, timevak: 2, number: 10 },
  { id: 'h25-t2-q11', topics: ['2.5'], title: 'Alcoholgisting door gistcellen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 6, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2025, timevak: 2, number: 11 },
  { id: 'h25-t2-q18', topics: ['2.1'], title: 'Antistoffen en afweercellen', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 9, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2025, timevak: 2, number: 18 },
  { id: 'h25-t2-q19', topics: ['2.1'], title: 'T-cellen en virusinfecties', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 9, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 8, year: 2025, timevak: 2, number: 19 },
  { id: 'h25-t2-q20', topics: ['2.2'], title: 'Een mutatie in het coronavirus', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 9, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 8, year: 2025, timevak: 2, number: 20 },
  { id: 'h25-t2-q28', topics: ['2.5'], title: 'Bacteriën en urease', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 13, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 9, year: 2025, timevak: 2, number: 28 },
  { id: 'h25-t2-q30', topics: ['2.5'], title: 'Fotosynthese bij zonnedauw', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 14, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 10, year: 2025, timevak: 2, number: 30 },
  { id: 'h25-t2-q32', topics: ['2.5'], title: 'Stikstof en plantengroei', examFile: '2025_havo_tijdvak2_examen.pdf', examPage: 15, answerFile: '2025_havo_tijdvak2_antwoorden.pdf', answerPage: 10, year: 2025, timevak: 2, number: 32 },
  { id: 'h24-t2-q2', topics: ['2.5'], title: 'Bacteriën in een kringloop', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 3, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2024, timevak: 2, number: 2 },
  { id: 'h24-t2-q12', topics: ['2.1'], title: 'Bindweefsel in een litteken', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 7, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2024, timevak: 2, number: 12 },
  { id: 'h24-t2-q20', topics: ['2.2', '2.5'], title: 'Een bacterie en genetische variatie', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 10, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 9, year: 2024, timevak: 2, number: 20 },
  { id: 'h24-t2-q23', topics: ['2.2', '2.5'], title: 'DNA-analyse van een bacterie-infectie', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 11, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 9, year: 2024, timevak: 2, number: 23 },
  { id: 'h24-t2-q26', topics: ['2.1'], title: 'Antistoffen bij een virusinfectie', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 12, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 9, year: 2024, timevak: 2, number: 26 },
  { id: 'h24-t2-q47', topics: ['2.3'], title: 'Gamma Knife bij een brughoektumor', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 19, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 12, year: 2024, timevak: 2, number: 47 },
  { id: 'h24-t2-q48', topics: ['2.3'], title: 'Bestraling en tumorcellen', examFile: '2024_havo_tijdvak2_examen.pdf', examPage: 19, answerFile: '2024_havo_tijdvak2_antwoorden.pdf', answerPage: 12, year: 2024, timevak: 2, number: 48 },
  { id: 'h23-t1-q7', topics: ['2.5'], title: 'Bodemschimmels en plantenwortels', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 5, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 6, year: 2023, timevak: 1, number: 7 },
  { id: 'h23-t1-q8', topics: ['2.5'], title: 'Schimmels in het voedselbos', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 5, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 6, year: 2023, timevak: 1, number: 8 },
  { id: 'h23-t1-q9', topics: ['2.5'], title: 'Stoffen in het voedselbos', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 5, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 6, year: 2023, timevak: 1, number: 9 },
  { id: 'h23-t1-q10', topics: ['2.5'], title: 'Planten in verschillende lichtomstandigheden', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 6, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 7, year: 2023, timevak: 1, number: 10 },
  { id: 'h23-t1-q11', topics: ['2.5'], title: 'Aanpassingen van schaduwplanten', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 6, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 7, year: 2023, timevak: 1, number: 11 },
  { id: 'h23-t1-q12', topics: ['2.5'], title: 'Planten en licht', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 6, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 7, year: 2023, timevak: 1, number: 12 },
  { id: 'h23-t1-q13', topics: ['2.5'], title: 'Plantengroei in het voedselbos', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 6, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 7, year: 2023, timevak: 1, number: 13 },
  { id: 'h23-t1-q24', topics: ['2.5'], title: 'Suikertransport in teunisbloemen', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 12, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 9, year: 2023, timevak: 1, number: 24 },
  { id: 'h23-t1-q25', topics: ['2.5'], title: 'Kruisbestuiving bij planten', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 12, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 9, year: 2023, timevak: 1, number: 25 },
  { id: 'h23-t1-q26', topics: ['2.5'], title: 'Planten en geluidsgolven', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 13, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 9, year: 2023, timevak: 1, number: 26 },
  { id: 'h23-t1-q27', topics: ['2.5'], title: 'Plantenonderzoek en hypothesen', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 13, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 9, year: 2023, timevak: 1, number: 27 },
  { id: 'h23-t1-q28', topics: ['2.5'], title: 'Nectar en bestuivers', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 14, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 10, year: 2023, timevak: 1, number: 28 },
  { id: 'h23-t1-q29', topics: ['2.5'], title: 'Reacties van planten op geluid', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 14, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 10, year: 2023, timevak: 1, number: 29 },
  { id: 'h23-t1-q30', topics: ['2.1', '2.2', '2.4'], title: 'Differentiatie van beenmergstamcellen', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 15, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 10, year: 2023, timevak: 1, number: 30 },
  { id: 'h23-t1-q31', topics: ['2.1'], title: 'Reticulocyten en rode bloedcellen', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 16, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 10, year: 2023, timevak: 1, number: 31 },
  { id: 'h23-t1-q32', topics: ['2.1'], title: 'Bloedarmoede en hartslag', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 16, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 10, year: 2023, timevak: 1, number: 32 },
  { id: 'h23-t1-q33', topics: ['2.1'], title: 'Bloedcellen bij bèta-thalassemie', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 16, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 10, year: 2023, timevak: 1, number: 33 },
  { id: 'h23-t1-q34', topics: ['2.1'], title: 'Bloedvaten en bloedtransfusie', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 17, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 10, year: 2023, timevak: 1, number: 34 },
  { id: 'h23-t1-q35', topics: ['2.2', '2.4'], title: 'Mutaties in stamcellen', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 17, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2023, timevak: 1, number: 35 },
  { id: 'h23-t1-q36', topics: ['2.1', '2.4'], title: 'Afstoting na een stamceltransplantatie', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 18, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2023, timevak: 1, number: 36 },
  { id: 'h23-t1-q37', topics: ['2.1', '2.4'], title: 'Afweer na stamceltransplantatie', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 18, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2023, timevak: 1, number: 37 },
  { id: 'h23-t1-q38', topics: ['2.2'], title: 'Overerving van bèta-thalassemie', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 19, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2023, timevak: 1, number: 38 },
  { id: 'h23-t1-q39', topics: ['2.2'], title: 'DNA-onderzoek voor een stamceldonor', examFile: '2023_havo_tijdvak1_examen.pdf', examPage: 19, answerFile: '2023_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2023, timevak: 1, number: 39 },
  { id: 'h24-t1-q1', topics: ['2.1'], title: 'Hartkleppen en de bloedsomloop', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 2, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 5, year: 2024, timevak: 1, number: 1 },
  { id: 'h24-t1-q3', topics: ['2.1'], title: 'Zuurstoftransport en energie in cellen', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 3, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 5, year: 2024, timevak: 1, number: 3 },
  { id: 'h24-t1-q4', topics: ['2.1'], title: 'Bloedplaatjes en bloedstolling', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 3, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 5, year: 2024, timevak: 1, number: 4 },
  { id: 'h24-t1-q5', topics: ['2.1'], title: 'De hartcyclus', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 3, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 5, year: 2024, timevak: 1, number: 5 },
  { id: 'h24-t1-q7', topics: ['2.1'], title: 'Witte bloedcellen in weefsel', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 4, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 5, year: 2024, timevak: 1, number: 7 },
  { id: 'h24-t1-q8', topics: ['2.2', '2.3', '2.4'], title: 'Stamcellen en groei van een hartklep', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 5, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 5, year: 2024, timevak: 1, number: 8 },
  { id: 'h24-t1-q9', topics: ['2.1'], title: 'Celorganellen en afvalstoffen', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 5, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 5, year: 2024, timevak: 1, number: 9 },
  { id: 'h24-t1-q24', topics: ['2.2'], title: 'SRY-gen en organisatieniveaus', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 13, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 8, year: 2024, timevak: 1, number: 24 },
  { id: 'h24-t1-q25', topics: ['2.2'], title: 'Een mutatie in het SRY-gen', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 13, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 9, year: 2024, timevak: 1, number: 25 },
  { id: 'h24-t1-q27', topics: ['2.2'], title: 'Genexpressie in borstweefsel', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 14, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 9, year: 2024, timevak: 1, number: 27 },
  { id: 'h24-t1-q34', topics: ['2.1'], title: 'Antistoffen en celreceptoren', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 17, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 10, year: 2024, timevak: 1, number: 34 },
  { id: 'h24-t1-q35', topics: ['2.1'], title: 'Afweer en impulsoverdracht', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 17, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 10, year: 2024, timevak: 1, number: 35 },
  { id: 'h24-t1-q39', topics: ['2.5'], title: 'Eiwitbouwstoffen voor quinoa', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 19, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2024, timevak: 1, number: 39 },
  { id: 'h24-t1-q40', topics: ['2.5'], title: 'Knolletjesbacteriën en de stikstofkringloop', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 19, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2024, timevak: 1, number: 40 },
  { id: 'h24-t1-q41', topics: ['2.5'], title: 'Biomassa van een plant', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 19, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2024, timevak: 1, number: 41 },
  { id: 'h24-t1-q42', topics: ['2.5'], title: 'Meststoffen en eutrofiëring', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 19, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2024, timevak: 1, number: 42 },
  { id: 'h24-t1-q43', topics: ['2.5'], title: 'Osmose bij een verzilte bodem', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 20, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2024, timevak: 1, number: 43 },
  { id: 'h24-t1-q44', topics: ['2.1', '2.5'], title: 'Zoutopslag in plantencellen', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 20, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2024, timevak: 1, number: 44 },
  { id: 'h24-t1-q45', topics: ['2.5'], title: 'Melkzuurbacteriën en fermentatie', examFile: '2024_havo_tijdvak1_examen.pdf', examPage: 20, answerFile: '2024_havo_tijdvak1_antwoorden.pdf', answerPage: 11, year: 2024, timevak: 1, number: 45 },
];

@Component({
  selector: 'app-root',
  template: `
    <main class="app-shell">
      <header class="topbar">
        <a class="brand" href="#" aria-label="Examinator startpagina">
          <span class="brand-mark">E</span>
          <span><strong>Examinator</strong><small>Biologie · HAVO</small></span>
        </a>
        <div class="topbar-status"><span class="status-dot"></span> Examenjaren 2023-2025</div>
      </header>

      <div class="workspace">
        <aside class="sidebar">
          <div class="sidebar-heading"><span>ONDERWERPEN</span><span>{{ topics.length }}</span></div>
          <nav class="topic-nav" aria-label="Onderwerpen">
            @for (topic of topics; track topic.id) {
              <button class="topic-button" [class.active]="activeTopic === topic.id" (click)="selectTopic(topic.id)">
                <span class="topic-number">{{ topic.id }}</span>
                <span class="topic-name">{{ topic.title }}</span>
                <span class="topic-count">{{ countFor(topic.id) }}</span>
              </button>
            }
          </nav>

          <div class="question-list-heading">
            <span>VRAGEN</span>
            <span>{{ filteredQuestions.length }}</span>
          </div>
          <div class="question-list">
            @for (question of filteredQuestions; track question.id) {
              <button class="question-link" [class.selected]="selectedQuestion?.id === question.id" (click)="selectQuestion(question)">
                <span class="question-list-number">{{ question.number }}</span>
                <span class="question-link-copy"><strong>{{ question.title }}</strong><small>{{ question.year }} · tijdvak {{ question.timevak }}</small></span>
                @if (isDone(question.id)) { <span class="done-dot" aria-label="Gemaakt"></span> }
              </button>
            } @empty {
              <p class="empty-list">Nog geen vragen uit 2023, 2024 of 2025 voor dit onderwerp.</p>
            }
          </div>

          <div class="sidebar-footer">
            <span class="progress-label">JOUW VOORTGANG</span>
            <div class="progress-copy"><strong>{{ completedCount }}</strong><span>van {{ questions.length }} vragen gemaakt</span></div>
            <div class="progress-track"><span [style.width.%]="progressPercent"></span></div>
          </div>
        </aside>

        <section class="content-area">
          @if (selectedQuestion; as question) {
            <div class="content-heading">
              <div>
                <div class="breadcrumb">Biologie <span>/</span> {{ activeTopic }} {{ activeTopicTitle }}</div>
                <h1>{{ question.title }}</h1>
                <p class="exam-meta">HAVO {{ question.year }} <span>·</span> Tijdvak {{ question.timevak }} <span>·</span> Vraag {{ question.number }}</p>
              </div>
              <button class="done-button" [class.is-done]="isDone(question.id)" (click)="toggleDone(question.id)">
                <span class="checkmark">{{ isDone(question.id) ? '✓' : '+' }}</span>
                {{ isDone(question.id) ? 'Gemaakt' : 'Markeer als gemaakt' }}
              </button>
            </div>

            <article class="exam-panel">
              <div class="panel-bar">
                <div><span class="panel-kicker">EXAMENVRAAG</span><strong>Vraag {{ question.number }}</strong></div>
                <span class="page-label">Pagina {{ question.examPage }}</span>
              </div>
              @if (examError) {
                <p class="pdf-message pdf-error">{{ examError }}</p>
              } @else if (examLoading && examPageNumbers.length === 0) {
                <p class="pdf-message">Examendocument laden…</p>
              }
              <div class="exam-document" #examDocument aria-live="polite">
                @for (page of examPageNumbers; track page) {
                  <section class="exam-page-sheet" [attr.data-page]="page">
                    <div class="exam-page-label">Examenpagina {{ page }}</div>
                      @if (examPageErrors.get(page); as error) {
                        <p class="pdf-message pdf-error">{{ error }}</p>
                      }
                    <div class="exam-paper">
                      @if (!renderedExamPages.has(page)) {
                        <span class="exam-page-placeholder">Pagina laden…</span>
                      }
                      <canvas #examPageCanvas class="pdf-canvas" [attr.data-page]="page" [class.is-hidden]="!renderedExamPages.has(page)" [attr.aria-label]="'Examenpagina ' + page"></canvas>
                    </div>
                  </section>
                }
              </div>
              <div class="panel-actions">
                <button class="answer-button" (click)="toggleAnswer()">{{ showAnswer ? 'Verberg antwoord' : 'Laat antwoord zien' }}</button>
                <div class="question-navigation">
                  <button aria-label="Vorige vraag" [disabled]="questionIndex === 0" (click)="moveQuestion(-1)">← Vorige</button>
                  <span>{{ questionIndex + 1 }} / {{ filteredQuestions.length }}</span>
                  <button aria-label="Volgende vraag" [disabled]="questionIndex >= filteredQuestions.length - 1" (click)="moveQuestion(1)">Volgende →</button>
                </div>
              </div>
            </article>

            <div class="answer-backdrop" [class.is-open]="showAnswer" [attr.aria-hidden]="!showAnswer" (click)="closeAnswer()"></div>
            <aside class="answer-drawer" [class.is-open]="showAnswer" role="dialog" [attr.aria-modal]="showAnswer ? 'true' : null" [attr.aria-hidden]="!showAnswer" [attr.inert]="showAnswer ? null : ''" [attr.aria-label]="'Correctievoorschrift voor vraag ' + question.number">
                <header class="drawer-header">
                  <div class="drawer-heading">
                    <span class="panel-kicker">CORRECTIEVOORSCHRIFT</span>
                    <strong>Antwoord · vraag {{ question.number }}</strong>
                    <span class="drawer-meta">HAVO {{ question.year }} · tijdvak {{ question.timevak }}</span>
                  </div>
                  <span class="page-label">Pagina {{ question.answerPage }}</span>
                  <button class="drawer-close" aria-label="Sluit antwoordpaneel" title="Sluiten" (click)="closeAnswer()"><span aria-hidden="true">×</span></button>
                </header>
                <div class="pdf-page answer-page" aria-live="polite">
                  @if (answerError) {
                    <p class="pdf-message pdf-error">{{ answerError }}</p>
                  } @else if (answerLoading) {
                    <p class="pdf-message">Antwoordpagina laden…</p>
                  }
                  <div class="answer-page-stack">
                    <div class="answer-sheet">
                      <span class="answer-sheet-label">Antwoordpagina {{ question.answerPage }}</span>
                      <canvas #answerCanvas class="pdf-canvas" [class.is-hidden]="answerLoading || !!answerError" aria-label="Officieel correctievoorschrift voor de geselecteerde vraag"></canvas>
                    </div>
                    @if (previousAnswerPage; as page) {
                      <div class="answer-sheet previous-answer-sheet">
                        <span class="answer-sheet-label">Voorgaande antwoorden · pagina {{ page }}</span>
                        @if (previousAnswerError) {
                          <p class="pdf-message pdf-error">{{ previousAnswerError }}</p>
                        } @else if (previousAnswerLoading) {
                          <p class="pdf-message">Voorgaande antwoorden laden…</p>
                        }
                        <canvas #previousAnswerCanvas class="pdf-canvas" [class.is-hidden]="previousAnswerLoading || !!previousAnswerError" aria-label="Voorgaande antwoorden in het correctievoorschrift"></canvas>
                      </div>
                    }
                  </div>
                </div>
            </aside>
          } @else {
            <div class="empty-state">
              <span class="empty-index">{{ activeTopic }}</span>
              <p class="breadcrumb">Biologie <span>/</span> {{ activeTopicTitle }}</p>
              <h1>Geen vragen gevonden</h1>
              <p>In de gekoppelde HAVO-examens van 2023, 2024 en 2025 staat geen vraag die bij dit onderwerp hoort.</p>
              <p class="empty-hint">Kies een ander onderwerp of voeg meer passende examenvragen toe aan de map Data.</p>
            </div>
          }
        </section>
      </div>
    </main>
  `,
})
class AppComponent implements AfterViewInit, OnDestroy {
  private readonly changeDetector = inject(ChangeDetectorRef);
  @ViewChild('examDocument') private examDocument?: ElementRef<HTMLDivElement>;
  @ViewChildren('examPageCanvas') private examCanvases!: QueryList<ElementRef<HTMLCanvasElement>>;
  @ViewChild('answerCanvas') private answerCanvas?: ElementRef<HTMLCanvasElement>;
  @ViewChild('previousAnswerCanvas') private previousAnswerCanvas?: ElementRef<HTMLCanvasElement>;

  readonly topics = topics;
  readonly questions = questions;
  activeTopic = topics[0].id;
  questionIndex = 0;
  showAnswer = false;
  examLoading = true;
  examPageNumbers: number[] = [];
  renderedExamPages = new Set<number>();
  examPageErrors = new Map<number, string>();
  answerLoading = true;
  previousAnswerLoading = true;
  examError = '';
  answerError = '';
  previousAnswerError = '';
  private doneIds = this.readDoneIds();
  private currentExamFile = '';
  private examRenderVersion = 0;
  private examObserver?: IntersectionObserver;
  private renderingExamPages = new Set<string>();
  private documents = new Map<string, Promise<PDFDocumentProxy>>();
  private renderTasks = new Map<HTMLCanvasElement, RenderTask>();
  private renderVersions = new WeakMap<HTMLCanvasElement, number>();

  ngAfterViewInit(): void {
    void this.renderExamDocument();
  }

  ngOnDestroy(): void {
    this.examObserver?.disconnect();
    this.cancelRenderTasks();
  }

  get filteredQuestions(): ExamQuestion[] {
    return this.questions.filter((question) => question.topics.includes(this.activeTopic));
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

  isDone(questionId: string): boolean {
    return this.doneIds.has(questionId);
  }

  selectTopic(topicId: string): void {
    this.activeTopic = topicId;
    this.questionIndex = 0;
    this.showAnswer = false;
    void this.renderExamDocument();
  }

  selectQuestion(question: ExamQuestion): void {
    this.questionIndex = this.filteredQuestions.findIndex((item) => item.id === question.id);
    this.showAnswer = false;
    void this.renderExamDocument();
  }

  moveQuestion(direction: number): void {
    this.questionIndex = Math.max(0, Math.min(this.filteredQuestions.length - 1, this.questionIndex + direction));
    this.showAnswer = false;
    void this.renderExamDocument();
  }

  toggleAnswer(): void {
    this.showAnswer = !this.showAnswer;
    if (this.showAnswer) {
      this.renderAnswerPage();
    }
  }

  closeAnswer(): void {
    this.showAnswer = false;
  }

  @HostListener('document:keydown.escape')
  closeAnswerOnEscape(): void {
    if (this.showAnswer) this.closeAnswer();
  }

  toggleDone(questionId: string): void {
    if (this.doneIds.has(questionId)) {
      this.doneIds.delete(questionId);
    } else {
      this.doneIds.add(questionId);
    }
    localStorage.setItem('examinator-done-questions', JSON.stringify([...this.doneIds]));
  }

  private async renderExamDocument(): Promise<void> {
    const question = this.selectedQuestion;
    if (!question) return;

    if (this.currentExamFile === question.examFile && this.examPageNumbers.length > 0) {
      this.scrollToExamPage(question.examPage);
      this.renderExamPage(question.examPage);
      return;
    }

    const renderVersion = ++this.examRenderVersion;
    this.examObserver?.disconnect();
    this.cancelRenderTasks();
    this.currentExamFile = question.examFile;
    this.examPageNumbers = [];
    this.renderedExamPages = new Set();
    this.examPageErrors = new Map();
    this.renderingExamPages.clear();
    this.examLoading = true;
    this.examError = '';
    this.changeDetector.detectChanges();

    try {
      const document = await this.loadDocument(question.examFile);
      if (renderVersion !== this.examRenderVersion) return;

      this.examPageNumbers = Array.from({ length: document.numPages }, (_, index) => index + 1);
      this.examLoading = false;
      this.changeDetector.detectChanges();
      if (renderVersion !== this.examRenderVersion) return;
      this.observeExamPages(question.examFile, renderVersion);
      this.scrollToExamPage(question.examPage);
      this.renderExamPage(question.examPage);
    } catch {
      if (renderVersion !== this.examRenderVersion) return;
      this.examLoading = false;
      this.examError = 'Het examendocument kon niet worden geladen.';
      this.changeDetector.detectChanges();
    }
  }

  private observeExamPages(file: string, version: number): void {
    const container = this.examDocument?.nativeElement;
    if (!container) return;

    this.examObserver?.disconnect();
    if (typeof IntersectionObserver === 'undefined') {
      for (const page of this.examPageNumbers) this.renderExamPage(page);
      return;
    }

    this.examObserver = new IntersectionObserver((entries) => {
      if (version !== this.examRenderVersion) return;
      for (const entry of entries) {
        if (entry.isIntersecting) {
          this.renderExamPage(Number((entry.target as HTMLElement).dataset['page']));
        }
      }
    }, { root: container, rootMargin: '420px 0px' });

    container.querySelectorAll<HTMLElement>('.exam-page-sheet').forEach((page) => this.examObserver?.observe(page));
  }

  private renderExamPage(pageNumber: number): void {
    const file = this.currentExamFile;
    const canvas = this.examCanvases.find((item) => Number(item.nativeElement.dataset.page) === pageNumber)?.nativeElement;
    const renderKey = `${file}:${pageNumber}`;
    if (!file || !canvas || this.renderedExamPages.has(pageNumber) || this.examPageErrors.has(pageNumber) || this.renderingExamPages.has(renderKey)) return;
    void this.renderPage(file, pageNumber, canvas, 'exam');
  }

  private scrollToExamPage(pageNumber: number): void {
    const container = this.examDocument?.nativeElement;
    const page = container?.querySelector<HTMLElement>(`.exam-page-sheet[data-page="${pageNumber}"]`);
    if (!container || !page) return;
    const top = page.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop;
    container.scrollTo({ top, behavior: 'smooth' });
  }

  private renderAnswerPage(): void {
    const question = this.selectedQuestion;
    const canvas = this.answerCanvas?.nativeElement;
    if (question && canvas) {
      void this.renderPage(question.answerFile, question.answerPage, canvas, 'answer');
      const previousCanvas = this.previousAnswerCanvas?.nativeElement;
      if (this.previousAnswerPage && previousCanvas) {
        void this.renderPage(question.answerFile, this.previousAnswerPage, previousCanvas, 'previousAnswer');
      }
    }
  }

  private async renderPage(file: string, pageNumber: number, canvas: HTMLCanvasElement, kind: 'exam' | 'answer' | 'previousAnswer'): Promise<void> {
    const renderKey = `${file}:${pageNumber}`;
    if (kind === 'exam') {
      if (this.renderingExamPages.has(renderKey) || this.renderedExamPages.has(pageNumber)) return;
      this.renderingExamPages.add(renderKey);
    }

    const version = (this.renderVersions.get(canvas) ?? 0) + 1;
    this.renderVersions.set(canvas, version);
    this.renderTasks.get(canvas)?.cancel();
    this.renderTasks.delete(canvas);
    this.setPreviewState(kind, true, '');

    try {
      const document = await this.loadDocument(file);
      const rendered = await this.renderCanvasPage(document, pageNumber, canvas);
      if (rendered) {
        if (kind === 'exam') {
          if (this.currentExamFile !== file) return;
          this.renderedExamPages = new Set(this.renderedExamPages).add(pageNumber);
          this.examPageErrors.delete(pageNumber);
          this.changeDetector.detectChanges();
        } else {
          this.setPreviewState(kind, false, '');
        }
      }
    } catch (error) {
      if (error instanceof Error && error.name === 'RenderingCancelledException') return;
      const message = 'De PDF-pagina kon niet worden geladen. Probeer de vraag opnieuw.';
      if (kind === 'exam') {
        if (this.currentExamFile !== file) return;
        this.examPageErrors = new Map(this.examPageErrors).set(pageNumber, message);
        this.changeDetector.detectChanges();
      } else {
        this.setPreviewState(kind, false, message);
      }
    } finally {
      if (kind === 'exam') this.renderingExamPages.delete(renderKey);
    }
  }

  private loadDocument(file: string): Promise<PDFDocumentProxy> {
    let documentPromise = this.documents.get(file);
    if (!documentPromise) {
      documentPromise = getDocument({ url: `Data/processed/${file}` }).promise;
      this.documents.set(file, documentPromise);
    }
    return documentPromise;
  }

  private async renderCanvasPage(document: PDFDocumentProxy, pageNumber: number, canvas: HTMLCanvasElement): Promise<boolean> {
    const version = (this.renderVersions.get(canvas) ?? 0) + 1;
    this.renderVersions.set(canvas, version);
    this.renderTasks.get(canvas)?.cancel();
    this.renderTasks.delete(canvas);

    try {
      const page = await document.getPage(pageNumber);
      if (this.renderVersions.get(canvas) !== version) return false;

      const baseViewport = page.getViewport({ scale: 1 });
      const availableWidth = canvas.parentElement?.clientWidth ?? baseViewport.width;
      const scale = Math.min(availableWidth / baseViewport.width, 1.4);
      const viewport = page.getViewport({ scale });
      const pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = Math.ceil(viewport.width * pixelRatio);
      canvas.height = Math.ceil(viewport.height * pixelRatio);
      canvas.style.width = `${viewport.width}px`;
      canvas.style.height = `${viewport.height}px`;

      const renderTask = page.render({ canvas, viewport, transform: [pixelRatio, 0, 0, pixelRatio, 0, 0] });
      this.renderTasks.set(canvas, renderTask);
      await renderTask.promise;
      return this.renderVersions.get(canvas) === version;
    } catch (error) {
      if (this.renderVersions.get(canvas) !== version) return false;
      if (error instanceof Error && error.name === 'RenderingCancelledException') return false;
      throw error;
    } finally {
      if (this.renderVersions.get(canvas) === version) {
        this.renderTasks.delete(canvas);
      }
    }
  }

  private cancelRenderTasks(): void {
    for (const task of this.renderTasks.values()) {
      task.cancel();
    }
    this.renderTasks.clear();
    this.renderVersions = new WeakMap<HTMLCanvasElement, number>();
  }

  private setPreviewState(kind: 'exam' | 'answer' | 'previousAnswer', loading: boolean, error: string): void {
    if (kind === 'exam') {
      this.examLoading = loading;
      this.examError = error;
    } else if (kind === 'answer') {
      this.answerLoading = loading;
      this.answerError = error;
    } else {
      this.previousAnswerLoading = loading;
      this.previousAnswerError = error;
    }
    if (!loading) {
      this.changeDetector.detectChanges();
    }
  }

  private readDoneIds(): Set<string> {
    try {
      return new Set(JSON.parse(localStorage.getItem('examinator-done-questions') ?? '[]') as string[]);
    } catch {
      return new Set();
    }
  }
}

bootstrapApplication(AppComponent).catch(console.error);