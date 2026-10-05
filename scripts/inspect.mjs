import fs from 'node:fs';
import { getDocument } from 'pdfjs-dist/legacy/build/pdf.mjs';

const pdf = await getDocument({
  data: new Uint8Array(fs.readFileSync('Examenbundel_Compleet_HAVO_Biologie.pdf')),
  useSystemFonts: true,
}).promise;
const pages = process.argv.slice(2).map(Number);
for (const pageNumber of pages.length ? pages : [9, 10, 12, 33, 44, 76, 108, 127, 140]) {
  const page = await pdf.getPage(pageNumber);
  const content = await page.getTextContent();
  const rows = new Map();
  for (const item of content.items) {
    if (!item.str.trim()) continue;
    const top = Math.round(page.view[3] - item.transform[5]);
    if (!rows.has(top)) rows.set(top, []);
    rows.get(top).push({ text: item.str, x: Math.round(item.transform[4]), font: item.fontName, size: Math.round(item.height) });
  }
  console.log(`\nPAGE ${pageNumber}`);
  for (const [top, items] of [...rows].sort((first, second) => first[0] - second[0])) {
    console.log(top, JSON.stringify(items));
  }
}