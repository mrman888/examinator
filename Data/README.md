# Exam data

`source/` contains the original supplied PDFs and should remain unchanged. Filenames are not authoritative: use the internal exam code on page 1 to identify year, timevak, and document type. The complete reference bundle is not currently present; extracted processed files are retained.

`processed/` contains the page-ready PDFs used by the application. Names follow `YYYY_havo_tijdvakN_examen.pdf` and `YYYY_havo_tijdvakN_antwoorden.pdf`. Question-to-page and topic mappings are maintained in `src/main.ts`.

## Processed sets

| Internal set | Processed files | Provenance |
| --- | --- | --- |
| HAVO 2009-2 | `2009_havo_tijdvak2_examen.pdf`, `2009_havo_tijdvak2_antwoorden.pdf` | Copied from `source/2009_tijdvlak2_examen.pdf` and `source/2009_tijdvlak2_antwoorden.pdf`; matching legacy internal codes are `947-1018-a-HA-2-o` and `947-1018-a-HA-2-c`. Questions 1-18, 20, 22-29, 33, and 35-40 are mapped to the existing topics 2.1, 2.2, and 2.5. Questions 19, 21, 30-32, and 34 concern ethology/ecology or biodiversity and are not included in the current topic catalog. |
| HAVO 2023-1 | `2023_havo_tijdvak1_examen.pdf`, `2023_havo_tijdvak1_antwoorden.pdf` | Exam copied from `source/2023_tijdvlak1_examen.pdf`; matching answer pages were extracted earlier from the complete bundle. Both internal codes are verified as `HA-1018-a-23-1`. |
| HAVO 2024-1 | `2024_havo_tijdvak1_examen.pdf`, `2024_havo_tijdvak1_antwoorden.pdf` | Copied from the corrected supplied source files. Internal codes verified as `HA-1018-a-24-1-o` and `HA-1018-a-24-1-c`. |
| HAVO 2024-2 | `2024_havo_tijdvak2_examen.pdf`, `2024_havo_tijdvak2_antwoorden.pdf` | Matching internal codes verified. |
| HAVO 2025-1 | `2025_havo_tijdvak1_examen.pdf`, `2025_havo_tijdvak1_antwoorden.pdf` | Matching internal codes verified. |
| HAVO 2025-2 | `2025_havo_tijdvak2_examen.pdf`, `2025_havo_tijdvak2_antwoorden.pdf` | Exam internal code is `HA-1018-a-25-2-o`; answer copied from `source/2025_tijdvlak2_antwoorden.pdf` and verified as `HA-1018-a-25-2-c`. |

## Source notes

- The HAVO 2009-2 documents use legacy internal codes beginning `947-1018-a-HA-2-`; the importer verifies these codes as well as the newer format.
- `source/2024_tijdvlak1_examen.pdf` and `source/2024_tijdvlak1_antwoorden.pdf` now form a matching HAVO 2024 timevak 1 pair.
- `source/2023_tijdvlak1_examen.pdf` is internally HAVO 2023 timevak 1. Its matching correction pages are extracted from the complete bundle.
- `source/2025_tijdvlak2_examen.pdf` is internally HAVO 2024 timevak 2, despite its filename. It is retained as an original source; the normalized 2024-2 exam is in `processed/`.
- The complete exam bundle was previously used as the extraction source but is not currently in `source/`. The imported exam/key PDFs in `processed/` remain available to the app.

## Reprocess supplied files

Run `npm run import` from the project root. The importer checks internal PDF codes before copying or extracting files and writes normalized outputs to `processed/`.
