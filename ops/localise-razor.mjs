// Markup mit data-i18n in serverseitig übersetztes Razor-Markup überführen (#154).
//
// Warum: eine Beschriftung, die im Dokument deutsch steht und nach dem ersten Bild durch die
// englische ersetzt wird, verschiebt alles daneben. In der alten Hülle fiel das nicht auf — dort
// wurde das ganze Dokument beim Start übersetzt, während noch keine Ansicht sichtbar war. Auf einer
// echten Seite passiert es vor den Augen des Benutzers. Gemessen: /rules verlor dabei 116 px in der
// Kopfzeile, weil „Erneut anwenden / Hinzufügen" zu „Re-apply / Add" wurde.
//
// Die data-i18n-Attribute bleiben stehen: wer die Sprache im laufenden Betrieb umstellt, bekommt
// den Text weiterhin ausgetauscht. Sie sind dann nur nicht mehr das, was beim ersten Bild zählt.
//
//   node ops/localise-razor.mjs            alle Seiten unter Pages/
//   node ops/localise-razor.mjs Rules      nur diese

import { readdirSync, readFileSync, writeFileSync } from 'node:fs';

const LF = String.fromCharCode(10);
const CRNL = String.fromCharCode(13, 10);
const PAGES = new URL('../src/FullWorth.Web/Pages/', import.meta.url);

const PREAMBLE = '@inject FullWorth.Web.Navigation.LocaleText Text';
const LANGUAGE = '    var language = FullWorth.Web.Navigation.LocaleText.Language(ViewContext.HttpContext);';

/** Ersetzt den Text eines Elements bzw. eines Attributs durch den Aufruf, der ihn serverseitig holt. */
export function localise(markup) {
  return markup
    // <span data-i18n="a.b">Text</span>  ->  ...>@Text.Get("a.b", language)</span>
    .replace(/(data-i18n="([\w.]+)"[^>]*>)([^<]*)(<)/g,
      (all, open, key, text, close) => text.trim() === '' ? all : `${open}@Text.Get("${key}", language)${close}`)
    // placeholder und title stehen als eigene Attribute daneben.
    //
    // Das (?!@Text\.Get) ist kein Schmuck: ohne es frisst der zweite Lauf den ersten. Der
    // eingesetzte Aufruf trägt selbst Anführungszeichen, also endet [^"]*" mitten darin, und
    // zurück bleibt ein halber Aufruf als sichtbarer Text im Markup. Das Skript läuft über ALLE
    // Seiten, sobald irgendwo eine neue dazukommt — es muss sich also beliebig oft wiederholen
    // lassen, ohne etwas anzurichten.
    .replace(/(data-i18n-placeholder="([\w.]+)"[^>]*?)placeholder="(?!@Text\.Get)[^"]*"/g,
      (_, before, key) => `${before}placeholder="@Text.Get("${key}", language)"`)
    .replace(/(data-i18n-title="([\w.]+)"[^>]*?)title="(?!@Text\.Get)[^"]*"/g,
      (_, before, key) => `${before}title="@Text.Get("${key}", language)"`);
}

function pageFiles(dir = PAGES, found = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      pageFiles(new URL(entry.name + '/', dir), found);
      continue;
    }
    if (entry.name.endsWith('.cshtml') && !entry.name.startsWith('_')) found.push(new URL(entry.name, dir));
  }
  return found;
}

function apply(file) {
  const before = readFileSync(file, 'utf8');
  let after = localise(before);
  if (after === before && before.includes(PREAMBLE)) return false;

  // @page muss die erste Direktive der Datei bleiben - @inject kommt dahinter.
  if (!after.includes(PREAMBLE)) {
    const firstBreak = after.indexOf(CRNL);
    after = after.slice(0, firstBreak + CRNL.length) + PREAMBLE + CRNL + after.slice(firstBreak + CRNL.length);
  }
  if (!after.includes(LANGUAGE.trim())) {
    const open = after.indexOf('@{');
    const close = after.indexOf('}', open);
    after = after.slice(0, open + 2) + CRNL + LANGUAGE + after.slice(open + 2, close) + after.slice(close);
  }
  writeFileSync(file, after.split(LF).join(LF));
  return true;
}

const only = process.argv[2];
let changed = 0;
for (const file of pageFiles()) {
  if (only && !file.pathname.includes(`/${only}/`)) continue;
  if (apply(file)) changed++;
}
console.log(`${changed} Seite(n) serverseitig uebersetzt.`);
