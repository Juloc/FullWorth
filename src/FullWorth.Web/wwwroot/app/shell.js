// Der kleine globale Teil jeder Razor-Seite (#154).
//
// Das Gegenstück zum alten app.js: DORT steht heute alles, von der Übersetzung über die Navigation
// bis zu jedem einzelnen Seitenrenderer, und deshalb lädt jede Seite alles. Hier steht nur, was
// wirklich auf jeder Seite gilt. Was eine Seite betrifft, lädt die Seite selbst.
//
// Was noch fehlt und in der nächsten Scheibe hierherzieht: Sitzungssperre (app/lock.js), globale
// Suche (app/global-search.js), die „Mehr"-Ansicht der unteren Leiste und das Coach-Dock. Alle vier
// brauchen den gemeinsamen Seitenkontext, den es erst geben muss — sie stehen noch in app.js und
// laufen dort weiter, solange die alte Hülle die übrigen Seiten bedient.
//
// Theme und Darstellung stehen bewusst NICHT hier: sie müssen vor dem ersten Zeichnen gelten und
// laufen deshalb als klassische Skripte im Kopf (app/theme.js, app/boot.js). Ein Modul käme zu spät,
// und das Ergebnis wäre ein Aufblitzen des falschen Themes.

import { i18n } from '../core/services.js';
import { state } from '../core/state.js';
import { isPrivate, togglePrivacy, onPrivacyChange } from '../components/privacy.js';

const root = document.documentElement;
const $ = selector => document.querySelector(selector);

function syncPrivacy() {
  const button = $('#privacy-toggle');
  if (!button) return;
  button.setAttribute('aria-pressed', String(isPrivate()));
  button.classList.toggle('active', isPrivate());
  // Das Attribut sitzt am <html>, weil app/boot.js es vor dem ersten Zeichnen setzt - ein hidden,
  // das JavaScript später nachträgt, schöbe die Leiste.
  root.dataset.privacy = isPrivate() ? 'on' : 'off';
}

function syncTheme() {
  const button = $('#theme-toggle');
  if (button) button.dataset.themePref = state.theme;
}

function bindTheme() {
  const button = $('#theme-toggle');
  if (!button) return;
  const order = ['system', 'light', 'dark'];
  button.addEventListener('click', () => {
    state.theme = order[(order.indexOf(state.theme) + 1) % order.length] || 'system';
    window.FullWorthTheme.writeThemeState({ mode: state.theme });
    window.FullWorthTheme.applyTheme();
    syncTheme();
  });
}

/**
 * Überschrift und Unterzeile der Topbar.
 *
 * In der alten Hülle setzte das renderPageHeader() bei jedem Ansichtswechsel. Auf einer echten Seite
 * gibt es keinen Wechsel — die Seite sagt am <body>, welche sie ist, und hier steht der Text dazu.
 * Eine Seite ohne Untertitel hat keinen Schlüssel dafür; ein leerer Wert wäre eine vergessene
 * Übersetzung und keine Absicht.
 */
function renderPageHeader() {
  const view = document.body.dataset.view;
  if (!view) return;
  const page = state.messages.pages?.[view];
  const title = $('#page-title');
  const subtitle = $('#page-subtitle');
  if (!title || !subtitle) return;
  // Fällt der Schlüssel aus, steht immer noch der Name aus der Seitenleiste da statt gar nichts.
  const fallback = document.querySelector(`.sidebar .nav-item[data-entry="${view}"] span`)?.textContent || '';
  title.textContent = page?.title ?? fallback;
  subtitle.textContent = page?.subtitle ?? '';
}

export async function startShell() {
  await i18n.load(state.lang);
  i18n.apply(document);
  renderPageHeader();

  bindTheme();
  syncTheme();
  $('#privacy-toggle')?.addEventListener('click', () => togglePrivacy());
  onPrivacyChange(syncPrivacy);
  syncPrivacy();
}

await startShell();
