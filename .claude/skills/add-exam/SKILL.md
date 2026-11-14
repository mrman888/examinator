---
name: add-exam
description: Add a new bundled (hardcoded) exam sitting to the Examinator trainer, meaning source PDFs in Data/, the angular.json assets glob, and question entries in src/app/services/exam-data.service.ts with ids like h25-t1-q12. Use when asked to add questions or an exam year/tijdvak by hand, fix bundled question data, or decide between bundling and the import feature. Not for the upload-based import flow (see import-test).
---

# Add a bundled exam

Bundled exams are plain data in the frontend. The alternative is the import feature (`import-test` skill), which stores exams in Azure and publishes them into the catalog; prefer it unless the user explicitly wants a hardcoded sitting. A published import of the same `year:timevak` replaces the bundled questions for that sitting at runtime (`ExamDataService.questions`).

## 1. PDFs in `Data/`

- Add one exam PDF and one answers (correctievoorschrift) PDF. **Never modify an existing PDF in `Data/`** (they may not be licensed for redistribution); only add files, and tell the user the new files carry the same licensing question.
- Existing names: `<year>_havo_tijdvak<n>_examen.pdf` and `<year>_havo_tijdvak<n>_antwoorden.pdf`. One answers file is irregular (`2025_tijdvlak2_antwoorden.pdf`); keep names that match your own scheme and the filename rule in `pdf-preview.service.ts` (`^[a-zA-Z0-9_.-]+\.pdf$`, served from `Data/<file>`).
- **A PDF is only served after it is added to `assets` in `angular.json`** (build target options, `input: "Data"`, `output: "Data"`). Current globs are `2025_havo_tijdvak*.pdf`, `2025_tijdvlak2_antwoorden.pdf` and `2024_havo_tijdvak2_*.pdf`; a new year or an irregular name needs its own glob. Check the built output (`dist\examinator\browser\Data\`) after `npm run build`.
- `server/Web/Dockerfile` copies `Data/` wholesale, and a `Data/` change rebuilds the Web image (see `deployment` skill).

## 2. Question entries (`src/app/services/exam-data.service.ts`)

Append objects to the `QUESTIONS` array, grouped by sitting, in this exact shape (`ExamQuestion`):

```ts
{ id: 'h25-t1-q12', topics: ['2.1'], title: 'Bloedbestanddelen en hun functie',
  examFile: '2025_havo_tijdvak1_examen.pdf', examPage: 7,
  answerFile: '2025_havo_tijdvak1_antwoorden.pdf', answerPage: 7,
  year: 2025, timevak: 1, number: 12 }
```

- `id` = `h` + two-digit year + `-t` + timevak + `-q` + question number (`h24-t2-q47`). It must be unique, and it is the key for the user's completion state in localStorage, so never renumber or rename existing ids.
- `year`, `timevak`, `number` must agree with the id; `title` is a short Dutch description of the question's subject.
- `examPage` / `answerPage` are the 1-based PDF pages where the question and its answer start; verify by opening the PDFs (a PDF reader, or pdf.js through `npm run inspect`, see below), do not guess.
- `topics`: the bundled data uses the legacy ids `'2.1'`-`'2.5'` (`TOPICS`), a question may have several. Imports use canonical ids (`biology-cells` = 2.1, `biology-dna` = 2.2, `biology-cell-division` = 2.3, `biology-cell-culture` = 2.4, `biology-organisms` = 2.5, listed in `server/README.md`); the frontend converts them through `LEGACY_TOPIC_IDS`. Only a question that tests the topic's knowledge belongs under it, not one that merely mentions a keyword. A genuinely new topic needs a stable canonical id (not a paragraph number) in `INITIAL_DEFINITIONS`, an entry in `TOPICS`/`LEGACY_TOPIC_IDS` if it also needs a legacy id, and the `server/README.md` list updated.
- All UI text and domain strings stay Dutch.

## 3. Logic and tests

- Pure logic (filtering by topic or sitting, progress, completion storage) belongs in `exam-data-logic.ts`, not in the service. The service only holds data and signals.
- `exam-data.service.spec.ts` tests `exam-data-logic.ts` with synthetic questions, and `components/signal-components.spec.ts` asserts counts against the service, so a pure data addition normally needs no spec change. Add or update specs when you change logic, topics or ids, and avoid asserting total bundled question counts.

## 4. Verify

`npm run build` then `npm test` (single spec form in `verify-change`). Then check in the dev server (`server/README.md`, local-run section) that the sitting shows under the year, the question opens on the right exam page, and the answer drawer shows the right answer page.

## `scripts/inspect.mjs` and `npm run import`

- `npm run inspect [page numbers]` runs `scripts/inspect.mjs`, a pdf.js dump of text rows and fonts per page. It is a leftover helper for locating pages: it reads `Examenbundel_Compleet_HAVO_Biologie.pdf` from the repo root (git-ignored, usually absent), so point it at the PDF you are working with by editing a local copy of the path, and do not commit that change.
- `npm run import` is declared in `package.json` but `scripts/import.mjs` does not exist. Do not use it; the real import is the Api/worker flow.
