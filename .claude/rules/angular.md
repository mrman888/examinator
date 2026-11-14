---
paths:
  - "src/**"
  - "angular.json"
  - "package.json"
---

Before editing frontend code, read `.github/instructions/angular.instructions.md` (signal-first Angular 22 conventions; it is the single source). After Angular changes run `npm run build` and `npm test`.

Bundled question data lives in `src/app/services/exam-data.service.ts` (large: grep, do not read whole); pure logic goes in `exam-data-logic.ts`. A new PDF in `Data/` is only served after its glob is added to `assets` in `angular.json` (see the `add-exam` skill).
