// Schreibt die Hülle: Seitenleiste und untere Leiste aus app/menu.js, und je Seite unter pages/
// deren Markup und deren Stylesheet.
//
// Beide stehen fertig im Dokument, damit beim Laden nichts eingefügt wird — eingefügtes Markup ist
// die häufigste Ursache für springende Seiten. Damit „fertig im Dokument" nicht „von Hand gepflegt"
// heißt, erzeugt dieses Skript es, und MenuParityTests prüft, dass die Datei dem Ergebnis entspricht.
//
// Eine Seite ist ein Ordner unter pages/ mit page.html, page.css und page.js. Das Markup landet im
// Dokument, nicht in einem Nachlader — geladen wird nichts, alles ist beim ersten Zeichnen da.
//
//   node ops/generate-shell.mjs          schreibt index.html
//   node ops/generate-shell.mjs --check  meldet nur, ob es passt   (Exit 1, wenn nicht)

import { readFileSync, writeFileSync, readdirSync, existsSync } from 'node:fs';
import { MENU, QUICK, ENTRIES } from '../src/FullWorth.Web/wwwroot/app/menu.js';

const NL = String.fromCharCode(10);
const root = new URL('../src/FullWorth.Web/wwwroot/', import.meta.url);
const indexPath = new URL('index.html', root);
const de = JSON.parse(readFileSync(new URL('locales/de.json', root), 'utf8'));

const text = key => key.split('.').reduce((node, part) => node?.[part], de)
  ?? (() => { throw new Error(`locales/de.json kennt ${key} nicht.`); })();

const svg = content => `<svg viewBox="0 0 24 24" aria-hidden="true">${content}</svg>`;
const label = entry => `<span data-i18n="${entry.label}">${text(entry.label)}</span>`;
const href = entry => entry.href ?? (entry.view === 'dashboard' ? '/' : `/${entry.view}`);

function item(entry, indent) {
  const attributes = [
    'class="nav-item"',
    `href="${href(entry)}"`,
    `data-entry="${entry.view}"`,
    entry.href ? null : `data-view="${entry.view}"`,
    entry.admin ? 'hidden' : null
  ].filter(Boolean).join(' ');

  return `${indent}<a ${attributes}>${svg(entry.icon)}${label(entry)}</a>`;
}

const chevron = svg('<path d="m9 6 6 6-6 6"/>');

const sidebar = MENU.map(group => [
  `      <div class="nav-group" data-group="${group.id}">`,
  `        <button class="nav-group-head" type="button" data-group="${group.id}" aria-expanded="true">`
  + `<span data-i18n="${group.label}">${text(group.label)}</span>${chevron}</button>`,
  '        <div class="nav-group-items">',
  ...group.items.map(entry => item(entry, '          ')),
  '        </div>',
  '      </div>'
].join(NL)).join(NL)
  // Direkt hinter dem Menü, weil es die Gruppen schließt, die zuletzt zu waren — noch während
  // geparst wird, also bevor irgendetwas gezeichnet ist.
  + NL + '      <script src="/app/nav-state.js"></' + 'script>';

const quick = QUICK.map(view => {
  const entry = ENTRIES.find(candidate => candidate.view === view);
  if (!entry) throw new Error(`QUICK nennt ${view}, das Menü kennt es nicht.`);
  return item(entry, '  ');
}).join(NL);

const more = '  <button id="bottom-more" class="nav-item" type="button">'
  + svg('<circle cx="5" cy="12" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="19" cy="12" r="1.6"/>')
  + `<span data-i18n="nav.more">${text('nav.more')}</span></button>`;

// Die Seiten. Ein Ordner mit einer page.html ist eine Seite, und der Pfad des Ordners ist die
// Adresse: pages/settings/security/passkeys liegt unter Einstellungen, und genau das sagt auch die
// Adresse im Browser. Alphabetisch, damit --check nicht bei jedem Lauf eine Änderung meldet.
function findPages(prefix = '') {
  const dir = new URL(`pages/${prefix}`, root);
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true })
    .filter(entry => entry.isDirectory())
    .map(entry => entry.name)
    .sort()
    .flatMap(name => {
      const path = prefix + name;
      const own = existsSync(new URL(`pages/${path}/page.html`, root)) ? [path] : [];
      return [...own, ...findPages(path + '/')];
    });
}

const pages = findPages();

const pageMarkup = pages
  .map(path => readFileSync(new URL(`pages/${path}/page.html`, root), 'utf8').trimEnd())
  .join(NL);

const pageStyles = pages
  .filter(path => existsSync(new URL(`pages/${path}/page.css`, root)))
  .map(path => `  <link rel="stylesheet" href="/pages/${path}/page.css">`)
  .join(NL);

function replace(html, id, body) {
  const pattern = new RegExp(String.raw`(<!-- ${id}:generiert -->)[^]*?(<!-- /${id} -->)`);
  if (!pattern.test(html)) throw new Error(`index.html hat keine Marken für ${id}.`);
  return html.replace(pattern, `$1${NL}${body}${NL}$2`);
}

const current = readFileSync(indexPath, 'utf8');
const next = [
  ['nav', sidebar],
  ['bottom-nav', quick + NL + more],
  ['seiten-css', pageStyles],
  ['seiten', pageMarkup]
].reduce((html, [id, body]) => replace(html, id, body), current);

if (process.argv.includes('--check')) {
  if (current !== next) {
    console.error('index.html ist nicht mehr das, was die Hülle ergibt. `node ops/generate-shell.mjs` ausführen.');
    process.exit(1);
  }
  console.log('index.html entspricht der Hülle.');
} else {
  writeFileSync(indexPath, next);
  console.log(`Geschrieben: ${ENTRIES.length} Einträge in ${MENU.length} Gruppen, ${pages.length} Seiten.`);
}
