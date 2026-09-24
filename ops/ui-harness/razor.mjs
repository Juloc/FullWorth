// Eine Razor-Seite für die Prüf-Werkstatt zusammensetzen (#154).
//
// Warum es das braucht: die Werkstatt serviert `wwwroot` gegen Fixtures, ohne Anmeldung — das ist der
// einzige Weg, eine Seite anzuschauen und zu MESSEN, ohne Zugangsdaten. Seit die Buchungsseite eine
// Razor-Seite ist, liegt ihr Markup nicht mehr unter `wwwroot`, und die Werkstatt fiele für sie auf
// die alte Hülle zurück, in der es die Seite nicht mehr gibt: eine leere Seite, die aussieht wie ein
// Fehler. Genau das ist hier schon einmal passiert (siehe den Kommentar zum Fallback-Header im
// Server) und hat die Werkstatt selbst zum Beweis für einen Produktfehler gemacht.
//
// Das hier ist KEIN Razor. Es kennt genau die Konstrukte, die `_Layout.cshtml` benutzt, und wirft,
// wenn eines dazukommt, das es nicht kennt — lieber ein lauter Fehler in der Werkstatt als eine
// Seite, die anders aussieht als im Betrieb.

import { readFileSync, readdirSync } from 'node:fs';

const WWWROOT = new URL('../../src/FullWorth.Web/wwwroot/', import.meta.url);
/** Dieselben Ueberschriften, die PageHeadings serverseitig liest. */
const HEADINGS = JSON.parse(readFileSync(new URL('locales/de.json', WWWROOT), 'utf8')).pages ?? {};
const LOCALES = {
  de: JSON.parse(readFileSync(new URL('locales/de.json', WWWROOT), 'utf8')),
  en: JSON.parse(readFileSync(new URL('locales/en.json', WWWROOT), 'utf8')),
};
/** @Text.Get("a.b", language) - derselbe Nachschlag, den LocaleText serverseitig macht. */
const localeText = (key, language) =>
  key.split('.').reduce((node, part) => node?.[part], LOCALES[language] ?? LOCALES.de) ?? '';

/**
 * Dieselbe Regel wie LocaleText.Language und core/state.js: gespeicherte Wahl, sonst die Sprache des
 * Browsers, sonst Deutsch. Waehlte die Werkstatt hier anders als der Betrieb, wuerde sie genau den
 * Sprung verstecken, den sie messen soll - einmal passiert: sie lieferte immer Deutsch, der
 * Testbrowser meldete Englisch, und jede Beschriftung sprang nach dem ersten Bild um.
 */
export function languageOf(headers = {}) {
  const cookie = String(headers.cookie ?? '')
    .split('; ').find(part => part.startsWith('fw.lang='))?.slice('fw.lang='.length);
  if (cookie === 'de' || cookie === 'en') return cookie;

  const preferred = String(headers['accept-language'] ?? '').split(',')[0].split(';')[0].trim();
  if (!preferred) return 'de';
  return preferred.toLowerCase().startsWith('de') ? 'de' : 'en';
}

const WEB = new URL('../../src/FullWorth.Web/', import.meta.url);
const PAGES = new URL('Pages/', WEB);

function read(url) {
  return readFileSync(url, 'utf8');
}

/** Jede .cshtml unter Pages/, mit ihrer @page-Adresse. */
function findPages(dir = PAGES, found = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      findPages(new URL(entry.name + '/', dir), found);
      continue;
    }
    if (!entry.name.endsWith('.cshtml') || entry.name.startsWith('_')) continue;
    const file = new URL(entry.name, dir);
    const source = read(file);
    const match = source.match(/^@page\s+"([^"]+)"/m);
    if (match) found.push({ route: match[1], source });
  }
  return found;
}

/** Den Inhalt eines @section-Blocks. Verschachtelte Klammern kommen darin nicht vor. */
function section(source, name) {
  const start = source.indexOf('@section ' + name + ' {');
  if (start < 0) return '';
  const open = source.indexOf('{', start);
  const close = source.indexOf('\n}', open);
  return source.slice(open + 1, close).trim();
}

/** Alles, was weder Direktive noch Kommentar noch Abschnitt ist — der eigentliche Rumpf. */
function body(source) {
  let rest = source
    .replace(/^@page\s+"[^"]+"\s*/m, '')
    .replace(/^@inject .*$/gm, '')
    .replace(/@\{[^]*?\n\}/, '')
    .replace(/@\*[^]*?\*@/g, '');
  for (const name of ['Styles', 'Scripts']) {
    const start = rest.indexOf('@section ' + name + ' {');
    if (start < 0) continue;
    const close = rest.indexOf('\n}', rest.indexOf('{', start));
    rest = rest.slice(0, start) + rest.slice(close + 2);
  }
  return rest.trim();
}

/** Was die Seite in ViewData legt, als einfache Zuweisungen gelesen. */
function viewData(source) {
  const values = {};
  for (const match of source.matchAll(/ViewData\["(\w+)"\]\s*=\s*"([^"]*)"/g))
    values[match[1]] = match[2];
  return values;
}

/**
 * Setzt den Rahmen zusammen. `navigation` und `bottomNavigation` kommen von aussen, weil sie aus
 * app/menu.js entstehen und dieselbe Quelle sind, aus der auch index.html gebaut wird.
 */
/** Ersetzt die serverseitigen Textaufrufe der Seite durch den deutschen Text. */
function localiseBody(markup, language) {
  return markup.replace(/@Text\.Get\("([\w.]+)", language\)/g, (_, key) => localeText(key, language));
}

export function renderRazorPage(route, { navigation, bottomNavigation }, language = 'de') {
  const page = findPages().find(candidate => candidate.route === route);
  if (!page) return null;

  const data = viewData(page.source);
  const layout = read(new URL('Shared/_Layout.cshtml', PAGES));

  // Dieselbe Regel wie PageHeadings: Seitentitel, sonst der Name aus der Navigation.
  const NAV = JSON.parse(readFileSync(new URL('locales/de.json', WWWROOT), 'utf8')).nav ?? {};
  const heading = HEADINGS[data.ActiveView] ?? (NAV[data.ActiveView] ? { title: NAV[data.ActiveView] } : {});

  let html = layout
    .replace(/@\*[^]*?\*@/g, '')
    // @inject und der Kopfblock des Rahmens tragen keine Ausgabe.
    .replace(/^@inject .*$/gm, '')
    .replace(/^@\{[^]*?\n?\}$/m, '')
    .replace('@(ViewData["PageTitle"] as string ?? Headings.Title(view))', heading.title ?? '')
    .replace('@(ViewData["PageSubtitle"] as string ?? Headings.Subtitle(view))', heading.subtitle ?? '')
    .replace('@(view ?? string.Empty)', data.ActiveView ?? '')
    .replace('<partial name="_Navigation" />', navigation)
    .replace('<partial name="_BottomNavigation" />', bottomNavigation)
    .replace('@RenderBody()', localiseBody(body(page.source), language))
    .replace(/@await RenderSectionAsync\("Styles", required: false\)/, section(page.source, 'Styles'))
    .replace(/@await RenderSectionAsync\("Scripts", required: false\)/, section(page.source, 'Scripts'))
    .replace(/@\(ViewData\["(\w+)"\] as string \?\? "([^"]*)"\)/g, (_, key, fallback) => data[key] ?? fallback)
    .replace(/@\(ViewData\["(\w+)"\] as string \?\? string\.Empty\)/g, (_, key) => data[key] ?? '');

  // Die Seitenleiste kommt aus index.html und weiss deshalb nicht, welche Seite gerade offen ist -
  // serverseitig setzt Razor das. Ohne diese Zeile misst die Werkstatt eine Navigation ohne
  // Markierung und damit etwas anderes als den Betrieb.
  if (data.ActiveView) {
    html = html.replace(
      new RegExp('class="nav-item"([^>]*data-entry="' + data.ActiveView + '")', 'g'),
      'class="nav-item active"$1');
  }

  const leftover = html.match(/@[A-Za-z(]/);
  if (leftover) {
    throw new Error(
      `ui-harness/razor.mjs kennt "${html.slice(html.indexOf(leftover[0]), html.indexOf(leftover[0]) + 60)}" nicht. ` +
      'Der Rahmen hat ein Razor-Konstrukt bekommen, das hier nachgezogen werden muss.');
  }
  return html;
}
