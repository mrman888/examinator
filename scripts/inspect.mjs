import fs from 'node:fs';
import { getDocument } from 'pdfjs-dist/legacy/build/pdf.mjs';

const filePath = process.argv[2] ?? 'Data/processed/2024_havo_tijdvak1_examen.pdf';
const requestedPages = process.argv.slice(3).map(Number);
const pdf = await getDocument({
  data: new Uint8Array(fs.readFileSync(filePath)),
  useSystemFonts: true,
}).promise;
for (const pageNumber of requestedPages.length ? requestedPages : [2, 3, 4]) {
  const page = await pdf.getPage(pageNumber);
  const content = await page.getTextContent();
  const rows = new Map();
  for (const item of content.items) {
    if (!item.str.trim()) continue;
    const top = Math.round(page.view[3] - item.transform[5]);
    if (!rows.has(top)) rows.set(top, []);
    rows.get(top).push({ text: item.str, x: Math.round(item.transform[4]), font: item.fontName, size: Math.round(item.height) });
  }
  console.log(`\n${filePath} PAGE ${pageNumber}`);
  for (const [top, items] of [...rows].sort((first, second) => first[0] - second[0])) {
    console.log(top, JSON.stringify(items));
  }
}