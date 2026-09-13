// Schreibt die Seitenleiste und die untere Leiste aus app/menu.js in index.html.
//
// Beide stehen fertig im Dokument, damit beim Laden nichts eingefügt wird — eingefügtes Markup ist
// die häufigste Ursache für springende Seiten. Damit „fertig im Dokument" nicht „von Hand gepflegt"
// heißt, erzeugt dieses Skript es, und MenuParityTests prüft, dass die Datei dem Ergebnis entspricht.
//
//   node ops/generate-menu.mjs          schreibt index.html
//   node ops/generate-menu.mjs --check  meldet nur, ob es passt   (Exit 1, wenn nicht)

import { readFileSync, writeFileSync } from 'node:fs';
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

function replace(html, id, body) {
  const pattern = new RegExp(String.raw`(<!-- ${id}:generiert -->)[^]*?(<!-- /${id} -->)`);
  if (!pattern.test(html)) throw new Error(`index.html hat keine Marken für ${id}.`);
  return html.replace(pattern, `$1${NL}${body}${NL}$2`);
}

const current = readFileSync(indexPath, 'utf8');
const next = replace(replace(current, 'nav', sidebar), 'bottom-nav', quick + NL + more);

if (process.argv.includes('--check')) {
  if (current !== next) {
    console.error('index.html entspricht app/menu.js nicht. `node ops/generate-menu.mjs` ausführen.');
    process.exit(1);
  }
  console.log('index.html entspricht app/menu.js.');
} else {
  writeFileSync(indexPath, next);
  console.log(`Menü geschrieben: ${ENTRIES.length} Einträge in ${MENU.length} Gruppen.`);
}
