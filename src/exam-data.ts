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

export interface ExamTimevakGroup {
  timevak: number;
  questions: ExamQuestion[];
}

export interface ExamYearGroup {
  year: number;
  timevakken: ExamTimevakGroup[];
}

export interface DocumentSearchMatch {
  id: string;
  pageNumber: number;
  left: number;
  top: number;
  width: number;
  height: number;
}

export const topics = [
  { id: '2.1', title: 'Menselijke en dierlijke cellen' },
  { id: '2.2', title: 'DNA en specialisatie van cellen' },
  { id: '2.3', title: 'Celdeling en kanker' },
  { id: '2.4', title: 'Kweken van cellen, weefsel en organen' },
  { id: '2.5', title: 'Bacteriën, schimmels en planten' },
];

export const questions: ExamQuestion[] = [
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
  { id: 'h09-t2-q1', topics: ['2.5'], title: 'Stikstofkringloop in de Maarsseveense Plassen', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 3, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 4, year: 2009, timevak: 2, number: 1 },
  { id: 'h09-t2-q2', topics: ['2.5'], title: 'Algen onder de microscoop', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 3, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 4, year: 2009, timevak: 2, number: 2 },
  { id: 'h09-t2-q3', topics: ['2.5'], title: 'Algen en schimmelsporen', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 3, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 4, year: 2009, timevak: 2, number: 3 },
  { id: 'h09-t2-q4', topics: ['2.1'], title: 'Besmetting met malaria', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 4, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 4, year: 2009, timevak: 2, number: 4 },
  { id: 'h09-t2-q5', topics: ['2.1'], title: 'Rode bloedcellen bij malaria', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 5, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 4, year: 2009, timevak: 2, number: 5 },
  { id: 'h09-t2-q6', topics: ['2.2'], title: 'Insecticideresistentie bij muggen', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 5, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2009, timevak: 2, number: 6 },
  { id: 'h09-t2-q7', topics: ['2.2'], title: 'Chromosomen in een spermacel', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 5, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2009, timevak: 2, number: 7 },
  { id: 'h09-t2-q8', topics: ['2.2'], title: 'Overerving van varkensoren', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 7, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2009, timevak: 2, number: 8 },
  { id: 'h09-t2-q9', topics: ['2.2'], title: 'Genotypen bij varkens', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 7, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2009, timevak: 2, number: 9 },
  { id: 'h09-t2-q10', topics: ['2.2'], title: 'Kruisingsresultaten bij varkens', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 8, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2009, timevak: 2, number: 10 },
  { id: 'h09-t2-q11', topics: ['2.2'], title: 'DNA-test voor fokvarkens', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 8, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2009, timevak: 2, number: 11 },
  { id: 'h09-t2-q12', topics: ['2.5'], title: 'Verteringsproducten van lactose door bacteriën', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 9, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2009, timevak: 2, number: 12 },
  { id: 'h09-t2-q13', topics: ['2.5'], title: 'Dissimilatie door darmbacteriën', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 9, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2009, timevak: 2, number: 13 },
  { id: 'h09-t2-q14', topics: ['2.1'], title: 'Waterstof in het bloed', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 9, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 5, year: 2009, timevak: 2, number: 14 },
  { id: 'h09-t2-q15', topics: ['2.1'], title: 'Waterstof in uitgeademde lucht', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 10, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2009, timevak: 2, number: 15 },
  { id: 'h09-t2-q16', topics: ['2.1'], title: 'Lactose-intolerantie meten', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 10, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2009, timevak: 2, number: 16 },
  { id: 'h09-t2-q17', topics: ['2.1'], title: 'Glucoseconcentratie in bloed', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 11, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2009, timevak: 2, number: 17 },
  { id: 'h09-t2-q18', topics: ['2.2'], title: 'Genotype voor lactose-intolerantie', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 11, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2009, timevak: 2, number: 18 },
  { id: 'h09-t2-q20', topics: ['2.5'], title: 'Zuurstof onder het wateroppervlak', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 12, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2009, timevak: 2, number: 20 },
  { id: 'h09-t2-q22', topics: ['2.1'], title: 'Osmoregulatie bij vlokreeften', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 13, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 6, year: 2009, timevak: 2, number: 22 },
  { id: 'h09-t2-q23', topics: ['2.1'], title: 'Glycogeen in levercellen', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 14, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2009, timevak: 2, number: 23 },
  { id: 'h09-t2-q24', topics: ['2.1'], title: 'Uitscheidingsorganen', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 15, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2009, timevak: 2, number: 24 },
  { id: 'h09-t2-q25', topics: ['2.1'], title: 'Opname van aminozuren door darmvlokken', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 15, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2009, timevak: 2, number: 25 },
  { id: 'h09-t2-q26', topics: ['2.1'], title: 'Bloedvaten van de lever', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 15, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2009, timevak: 2, number: 26 },
  { id: 'h09-t2-q27', topics: ['2.1'], title: 'Hormoonregeling van bloedglucose', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 15, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2009, timevak: 2, number: 27 },
  { id: 'h09-t2-q28', topics: ['2.2'], title: 'Mutatie in erfelijk materiaal', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 16, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2009, timevak: 2, number: 28 },
  { id: 'h09-t2-q29', topics: ['2.2'], title: 'Voorwaarde voor soortvorming', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 16, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 7, year: 2009, timevak: 2, number: 29 },
  { id: 'h09-t2-q33', topics: ['2.1'], title: 'Eiwitproductie in cellen', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 19, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 8, year: 2009, timevak: 2, number: 33 },
  { id: 'h09-t2-q35', topics: ['2.5'], title: 'Clostridium botulinum', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 20, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 8, year: 2009, timevak: 2, number: 35 },
  { id: 'h09-t2-q36', topics: ['2.1'], title: 'Botuline en impulsoverdracht', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 21, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 8, year: 2009, timevak: 2, number: 36 },
  { id: 'h09-t2-q37', topics: ['2.1'], title: 'Botuline en scherp zien', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 21, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 8, year: 2009, timevak: 2, number: 37 },
  { id: 'h09-t2-q38', topics: ['2.2', '2.5'], title: 'Genlocatie in een bacterie', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 21, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 8, year: 2009, timevak: 2, number: 38 },
  { id: 'h09-t2-q39', topics: ['2.1'], title: 'Botox en oogspieren', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 22, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 8, year: 2009, timevak: 2, number: 39 },
  { id: 'h09-t2-q40', topics: ['2.1'], title: 'Botox en gezichtsspieren', examFile: '2009_havo_tijdvak2_examen.pdf', examPage: 22, answerFile: '2009_havo_tijdvak2_antwoorden.pdf', answerPage: 8, year: 2009, timevak: 2, number: 40 },
];
