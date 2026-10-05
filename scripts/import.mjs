import fs from 'node:fs/promises';
import path from 'node:path';
import { PDFDocument } from 'pdf-lib';
import { getDocument } from 'pdfjs-dist/legacy/build/pdf.mjs';

const dataDirectory = path.resolve('Data');
const sourceDirectory = path.join(dataDirectory, 'source');
const processedDirectory = path.join(dataDirectory, 'processed');
const examCodePattern = /(?:HA-1018-a-\d{2}-\d-[oc]|947-1018-a-HA-\d-[oc])/;

await fs.mkdir(sourceDirectory, { recursive: true });
await fs.mkdir(processedDirectory, { recursive: true });

async function readExamCode(filePath) {
  const pdf = await getDocument({
    data: new Uint8Array(await fs.readFile(filePath)),
    useSystemFonts: true,
  }).promise;
  const firstPage = await pdf.getPage(1);
  const text = (await firstPage.getTextContent()).items.map((item) => item.str).join(' ');
  return text.match(examCodePattern)?.[0] ?? null;
}

async function copyVerifiedSource(sourceName, processedName, expectedCode) {
  const sourcePath = path.join(sourceDirectory, sourceName);
  const actualCode = await readExamCode(sourcePath);
  if (actualCode !== expectedCode) {
    throw new Error(`${sourceName}: expected ${expectedCode}, found ${actualCode ?? 'no exam code'}`);
  }

  const outputPath = path.join(processedDirectory, processedName);
  await fs.copyFile(sourcePath, outputPath);
  console.log(`${sourceName} -> processed/${processedName} (${actualCode})`);
}

async function extractVerifiedBundlePages(bundleName, firstPage, lastPage, processedName, expectedCode) {
  const bundlePath = path.join(sourceDirectory, bundleName);
  const outputPath = path.join(processedDirectory, processedName);

  try {
    const existingCode = await readExamCode(outputPath);
    if (existingCode === expectedCode) {
      console.log(`processed/${processedName} already verified (${existingCode})`);
      return;
    }
  } catch {
    // Extract the answer key if no valid processed copy exists yet.
  }

  let bundle;
  try {
    bundle = await PDFDocument.load(await fs.readFile(bundlePath));
  } catch {
    throw new Error(`${processedName} is missing or invalid, and source/${bundleName} is unavailable`);
  }
  if (lastPage > bundle.getPageCount()) {
    throw new Error(`${bundleName}: requested page ${lastPage}, but bundle has ${bundle.getPageCount()} pages`);
  }

  const output = await PDFDocument.create();
  const pages = await output.copyPages(bundle, Array.from({ length: lastPage - firstPage + 1 }, (_, index) => firstPage + index - 1));
  for (const page of pages) output.addPage(page);

  await fs.writeFile(outputPath, await output.save());
  const actualCode = await readExamCode(outputPath);
  if (actualCode !== expectedCode) {
    throw new Error(`${processedName}: expected ${expectedCode}, found ${actualCode ?? 'no exam code'}`);
  }
  console.log(`${bundleName} pages ${firstPage}-${lastPage} -> processed/${processedName} (${actualCode})`);
}

await copyVerifiedSource('2023_tijdvlak1_examen.pdf', '2023_havo_tijdvak1_examen.pdf', 'HA-1018-a-23-1-o');
await extractVerifiedBundlePages('Examenbundel_Compleet_HAVO_Biologie.pdf', 164, 176, '2023_havo_tijdvak1_antwoorden.pdf', 'HA-1018-a-23-1-c');
await copyVerifiedSource('2024_tijdvlak1_examen.pdf', '2024_havo_tijdvak1_examen.pdf', 'HA-1018-a-24-1-o');
await copyVerifiedSource('2024_tijdvlak1_antwoorden.pdf', '2024_havo_tijdvak1_antwoorden.pdf', 'HA-1018-a-24-1-c');
await copyVerifiedSource('2025_tijdvlak2_antwoorden.pdf', '2025_havo_tijdvak2_antwoorden.pdf', 'HA-1018-a-25-2-c');
await copyVerifiedSource('2009_tijdvlak2_examen.pdf', '2009_havo_tijdvak2_examen.pdf', '947-1018-a-HA-2-o');
await copyVerifiedSource('2009_tijdvlak2_antwoorden.pdf', '2009_havo_tijdvak2_antwoorden.pdf', '947-1018-a-HA-2-c');
