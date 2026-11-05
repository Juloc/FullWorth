// Die Module der Belegkamera sind Browser-Module ohne "type": "module" im Paket. node bekommt sie
// deshalb als temporaere .mjs-Kopien (mit angepassten Importen) - die Dateien selbst bleiben unveraendert.
import { copyFileSync, existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const source = join(here, '../../src/FullWorth.Web/wwwroot/pages/purchases');
const names = ['receipt-vision', 'receipt-stitch'];

export async function loadModules(overrides = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'receipt-vision-'));
  const loaded = {};
  for (const name of names) {
    const file = overrides[name] ?? join(source, `${name}.js`);
    if (!existsSync(file)) continue;
    const text = readFileSync(file, 'utf8').replace(/from '\.\/(receipt-[a-z]+)\.js'/g, "from './$1.mjs'");
    writeFileSync(join(dir, `${name}.mjs`), text);
  }
  for (const name of names) {
    if (existsSync(join(dir, `${name}.mjs`))) loaded[name] = await import(pathToFileURL(join(dir, `${name}.mjs`)).href);
  }
  return loaded;
}
