// Seitenleiste und untere Leiste für die Prüf-Werkstatt (#154).
//
// Im Betrieb schreibt Razor sie aus _Navigation.cshtml und _BottomNavigation.cshtml, gespeist aus
// NavigationCatalog. Die Werkstatt kann kein C# ausführen — also liest sie GENAU DIESE beiden
// Dateien und ersetzt darin nur die eine Schleife über die Einträge; alles andere (Markenzeile,
// Einklapp-Knopf, Fuß mit Raum und Theme-Schalter, das nav-state-Skript) bleibt wortwörtlich stehen.
//
// Der erste Versuch hat die Leiste stattdessen aus app/menu.js neu gebaut. Das sah richtig aus und
// war es nicht: die Markenzeile fehlte, und mit ihr der Einklapp-Knopf — die Werkstatt zeigte eine
// Seitenleiste, die es so nirgends gibt. Was nur an einer Stelle steht, wird hier nicht abgeschrieben.
//
// Die Einträge selbst kommen aus app/menu.js, weil das die Liste ist, die auch der Browser benutzt;
// NavigationCatalogParityTests hält sie mit der serverseitigen zusammen.

import { readFileSync } from 'node:fs';
import { MENU, QUICK, ENTRIES } from '../../src/FullWorth.Web/wwwroot/app/menu.js';

const NL = String.fromCharCode(10);
const WWWROOT = new URL('../../src/FullWorth.Web/wwwroot/', import.meta.url);
const PARTIALS = new URL('../../src/FullWorth.Web/Pages/Shared/', import.meta.url);
const LOCALES = {
  de: JSON.parse(readFileSync(new URL('locales/de.json', WWWROOT), 'utf8')),
  en: JSON.parse(readFileSync(new URL('locales/en.json', WWWROOT), 'utf8')),
};

const text = (key, language) => key.split('.').reduce((node, part) => node?.[part], LOCALES[language])
  ?? (() => { throw new Error(`locales/${language}.json kennt ${key} nicht.`); })();

const svg = content => `<svg viewBox="0 0 24 24" aria-hidden="true">${content}</svg>`;
const href = entry => entry.href ?? (entry.view === 'dashboard' ? '/' : `/${entry.view}`);

/** Ein Eintrag, so wie ihn die Partial schreibt. Die aktive Markierung setzt razor.mjs danach. */
const item = (entry, indent, language) => `${indent}<a class="nav-item" href="${href(entry)}" `
  + `data-entry="${entry.view}" data-view="${entry.view}"${entry.admin ? ' hidden' : ''}>`
  + `${svg(entry.icon)}<span data-i18n="${entry.label}">${text(entry.label, language)}</span></a>`;

/**
 * Direktiven, Kopfblock und Kommentare tragen keine Ausgabe; die Textaufrufe ausserhalb der
 * Schleife (die Beschriftung von "Mehr", der Titel des Theme-Schalters) werden hier aufgeloest -
 * in derselben Sprache, die der Server fuer diese Anfrage waehlen wuerde. Waere die Leiste immer
 * deutsch und die Seite englisch, wuerde die Werkstatt einen Sprung messen, den es nicht gibt.
 */
function body(name, language) {
  return readFileSync(new URL(name, PARTIALS), 'utf8')
    .replace(/@\*[^]*?\*@/g, '')
    .replace(/^@inject .*$/gm, '')
    .replace(/@\{[^]*?\n\}/, '')
    .replace(/@Text\.Get\("([\w.]+)", language\)/g, (_, key) => text(key, language))
    .trim();
}

/**
 * Ersetzt einen @foreach-Block durch fertiges Markup.
 *
 * Gesucht wird ab `@foreach` die zugehörige schließende Klammer, über die Verschachtelung gezählt —
 * beide Partials haben eine Schleife in der Schleife, ein Muster bis zur ersten `}` träfe die falsche.
 */
function replaceLoop(markup, generated) {
  const start = markup.indexOf('@foreach');
  if (start < 0) throw new Error('ui-harness/navigation.mjs: kein @foreach in der Partial.');
  let depth = 0, index = markup.indexOf('{', start);
  for (; index < markup.length; index++) {
    if (markup[index] === '{') depth++;
    else if (markup[index] === '}' && --depth === 0) break;
  }
  if (depth !== 0) throw new Error('ui-harness/navigation.mjs: die Schleife schliesst nicht.');
  return markup.slice(0, start) + generated.trim() + markup.slice(index + 1);
}

const chevron = svg('<path d="m9 6 6 6-6 6"/>');

const groups = language => MENU.map(group => [
  `<div class="nav-group" data-group="${group.id}">`,
  `            <button class="nav-group-head" type="button" data-group="${group.id}" aria-expanded="true">`
  + `<span data-i18n="${group.label}">${text(group.label, language)}</span>${chevron}</button>`,
  '            <div class="nav-group-items">',
  ...group.items.map(entry => item(entry, '                ', language)),
  '            </div>',
  '        </div>'
].join(NL)).join(NL + '        ');

const quick = language => QUICK.map(view => {
  const entry = ENTRIES.find(candidate => candidate.view === view);
  if (!entry) throw new Error(`QUICK nennt ${view}, das Menü kennt es nicht.`);
  return item(entry, '    ', language);
}).join(NL).trim();

/** Beide Leisten in der Sprache, die der Server fuer diese Anfrage waehlen wuerde. */
export function shellNavigation(language = 'de') {
  return {
    navigation: replaceLoop(body('_Navigation.cshtml', language), groups(language)),
    bottomNavigation: replaceLoop(body('_BottomNavigation.cshtml', language), quick(language))
  };
}
