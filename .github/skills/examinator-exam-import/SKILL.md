---
name: examinator-exam-import
description: 'Use when adding or reprocessing supplied HAVO biology exam PDFs, official answer keys, or correction documents in Examinator. Covers PDF verification, normalization, question-page mapping, import.mjs updates, and checking the app overview. Trigger phrases: nieuw examen toevoegen, antwoorden/correctievoorschrift toevoegen, exam PDFs importeren, examen verwerken, tijdvak toevoegen.'
argument-hint: 'Geef het examenjaar/tijdvak op of verwijs naar de aangeleverde examen- en antwoord-PDFs.'
---

# Examen en correctievoorschrift verwerken

Verwerk aangeleverde examen- en antwoord-PDFs tot reproduceerbare bestanden voor de app. Behoud de bronbestanden, verifieer de interne documentcodes en werk zowel de importer als de vraagmetadata bij. Gebruik alleen door de gebruiker aangeleverde of al aanwezige officiele documenten; reconstrueer geen ontbrekende antwoorden.

## Projectafspraken

- `Data/source/` bevat de aangeleverde originelen. Overschrijf of hernoem die bestanden niet.
- `Data/processed/` bevat de genormaliseerde PDF's die de app gebruikt. Gebruik `YYYY_havo_tijdvakN_examen.pdf` en `YYYY_havo_tijdvakN_antwoorden.pdf`.
- Bestandsnamen zijn niet gezaghebbend. Bepaal jaar, tijdvak en documenttype met de interne code op pagina 1: `HA-1018-a-YY-T-o` voor het examen en `HA-1018-a-YY-T-c` voor het correctievoorschrift.
- `scripts/import.mjs` bevat expliciete importaanroepen. Alleen een PDF in `Data/source/` zetten is niet genoeg: voeg de juiste `copyVerifiedSource(...)`- of `extractVerifiedBundlePages(...)`-aanroep toe.
- De jaar- en tijdvakgroepen in de header worden afgeleid van `questions[]` in `src/main.ts`. Een verwerkt PDF-paar verschijnt dus pas in het overzicht als de bedoelde vragen en hun mapping daar zijn toegevoegd.
- `questions[]` is de inhoudelijke vraagcatalogus voor de onderwerpen in de app, niet automatisch een inventaris van elke vraag in het volledige examen. Wees expliciet over de gevraagde dekking. Als onduidelijk is welke vragen of onderwerpen opgenomen moeten worden, vraag dit eerst.

## Werkwijze

1. Inventariseer de aangeleverde PDFs en bestaande bestanden in `Data/source/` en `Data/processed/`. Gebruik interne codes om te bepalen of de bestanden een examen, correctievoorschrift, ander niveau/vak of ander tijdvak bevatten. Stop en vraag om verduidelijking als codes ontbreken, niet overeenkomen of niet bij HAVO biologie horen.
2. Bewaar de originelen ongewijzigd in `Data/source/`. Gebruik de bestaande bestandsnamen als bronargumenten in de importer; baseer de genormaliseerde bestandsnamen en verwachte codes op de interne PDF-codes.
3. Voor losse examen- of antwoord-PDF's: voeg een `copyVerifiedSource(sourceName, processedName, expectedCode)`-aanroep toe aan `scripts/import.mjs`.
4. Voor een correctievoorschrift dat in een grotere bundel zit: bepaal de exacte, aaneengesloten bronpagina's van de bijbehorende HAVO-code. Gebruik `extractVerifiedBundlePages(bundleName, firstPage, lastPage, processedName, expectedCode)`; `firstPage` en `lastPage` zijn 1-based. Verifieer dat de geextraheerde PDF op de eerste pagina de verwachte `-c`-code heeft. Als de officiele antwoordpagina's ontbreken, vraag om het juiste document in plaats van antwoorden te verzinnen.
5. Voer `npm run import` uit. De importer schrijft naar `Data/processed/` en controleert de interne code voor en na kopieren/extractie. Los elke code- of paginabereikfout op; omzeil de controle niet.
6. Bepaal per opgenomen vraag het examennummer, de titel, de relevante onderwerp-ID's, de examenpagina en de antwoordpagina in de genormaliseerde PDF's. Gebruik `npm run inspect -- Data/processed/<bestand>.pdf <paginanummers>` om PDF-tekst per pagina te inspecteren. Controleer scans, figuren en onduidelijke paginanummers visueel; leid geen mapping af uit aannames over paginavolgorde.
7. Voeg voor elke opgenomen vraag een item toe aan `questions[]` in `src/main.ts`, met een unieke ID zoals `hYY-tN-qNN`, de bestaande topic-ID's, titel, genormaliseerde `examFile`/`answerFile`, de juiste `examPage`/`answerPage`, `year`, `timevak` en vraagnummer `number`. Controleer dat de ID uniek is en de topic-ID's bestaan. Neem een vraag met meerdere relevante onderwerpen maar een keer op en geef alle passende topic-ID's mee.
8. Werk `Data/README.md` bij met het nieuwe verwerkte examenpaar, de herkomst van de bestanden en eventuele bijzonderheden, zoals bronbestandsnamen die niet overeenkomen met de interne code.
9. Controleer dat beide verwerkte bestanden bestaan, dat examen en correctievoorschrift hetzelfde jaar/tijdvak hebben en dat de mappings naar bestaande PDF-pagina's wijzen. Voer `npm run build` uit. Controleer daarna de app op `http://localhost:4200/`: het jaar en tijdvak moeten in het headeroverzicht staan, de bedoelde vragen moeten zichtbaar zijn en een geselecteerde vraag moet naar de juiste examenpagina en het bijbehorende antwoord navigeren.

## Afronding

Meld welke jaar/tijdvakset is toegevoegd, welke vragen zijn opgenomen, welke bron- en verwerkte bestanden zijn gebruikt, en welke checks zijn geslaagd. Benoem expliciet als een correctievoorschrift ontbreekt of als slechts een deel van de examenvragen in de onderwerpcatalogus is opgenomen.
