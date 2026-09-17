// Verdrahtet die Darstellung-Regler in den Einstellungen (Sitzfeld, Schnellwahl, Logo-Schalter) mit
// der einen Farbmathematik-Engine (app/theme.js, window.FullWorthTheme). Bis zu dieser Slice baute
// diese Datei ihr eigenes Panel per DOM und hielt es per MutationObserver synchron (Issue #149) - das
// quetschte das Panel in .settings-grid's auto-fit-Spalten und brach deutsche Beschriftungen
// zeichenweise um. Die Regler stehen jetzt STATISCH in pages/settings/page.html, genau wie #language
// und #theme es schon immer taten: fertig im Dokument beim ersten Zeichnen, kein nachtraeglich
// eingefuegtes Markup, also auch kein Beobachter mehr noetig, der ein solches Einfuegen abwartet.
//
// Diese Datei bleibt trotzdem ein eigenes Modul statt in pages/settings/page.js aufzugehen: sie wird
// von app/boot.js beim DOMContentLoaded geladen (bevor app.js/pages/settings/page.js ueberhaupt
// importiert sind) und deckt zwei Dinge ab, die nichts mit "wird die Einstellungsseite gerade
// angezeigt" zu tun haben - die einmalige Bereinigung laengst abgeschalteter Typografie-Schluessel
// (LEGACY_TYPOGRAPHY_KEYS) und den Abgleich ueber Browser-Tabs hinweg (der storage-Listener unten).
// Beides muss laufen, sobald die Anwendung startet, unabhaengig davon, ob je jemand die
// Einstellungen oeffnet - genau die Trennung, die AGENTS/CLAUDE.md fuer app/boot.js vorschreiben
// ("Verhalten, kein Aussehen"). Was WIRKLICH nur die Einstellungsseite betrifft (Regler verdrahten,
// Vorschau/Werte synchron halten), steht folgerichtig unten in eigenen, klar benannten Funktionen.
const theme = window.FullWorthTheme;

const LEGACY_TYPOGRAPHY_KEYS = Object.freeze([
  'finance.font',
  'finance.typography.baseSize',
  'finance.typography.weight',
  'finance.typography.letterSpacing',
  'finance.typography.lineHeight'
]);

// Ein leerer Sitz heisst "tokens.css bleibt zustaendig", nicht "ein Farbwert, der zufaellig dem
// Standard gleicht" - siehe applyTheme in theme.js.
const APPEARANCE_DEFAULTS = Object.freeze({ seed: '', logoMode: 'standard' });

// Schnellwahl (Issue #149 §8, woertlich: "Weiß, Grau, Schwarz, Blau, Violett, Grün, optional
// weitere"). Jeder Eintrag ist nur ein Sitz-Hex, kein zweites Objekt mit eigenem Namen/Vorschau-Logik -
// der Klick unten ruft exakt denselben applyAppearance()-Pfad wie das freie Farbfeld, nur mit diesem
// statt einem eingetippten Wert. Weiss/Grau/Schwarz sind ECHTE Sitzwerte (nicht der leere
// "kein Sitz"-Zustand von APPEARANCE_DEFAULTS): C < 0,025 macht sie in theme.js automatisch
// monochrom, mit eigenen, dafuer vorgesehenen Helligkeitsstufen statt eines Farbthemas mit Chroma 0.
const APPEARANCE_PRESETS = Object.freeze([
  { key: 'white', seed: '#ffffff' },
  { key: 'grey', seed: '#808080' },
  { key: 'black', seed: '#000000' },
  { key: 'blue', seed: '#1d4ed8' },
  { key: 'purple', seed: '#6d28d9' },
  { key: 'green', seed: '#15803d' }
]);

export function getAppearance() {
  const { seed, logoMode } = theme.readThemeState();
  return { seed, logoMode };
}

function currentMode() {
  // Der Modus selbst ist app.js'/auth.js' Sache (state.theme); hier wird nur der schon aufgeloeste
  // Wert gelesen, den applyTheme zuletzt auf <html> geschrieben hat.
  return document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
}

export function applyAppearance(next = {}, options = {}) {
  const current = getAppearance();
  // Normalisierung (gueltiges Hex ja/nein) passiert einzig in theme.js - hier kein zweites Hex-Parsing.
  const merged = {
    seed: next.seed !== undefined ? next.seed : current.seed,
    logoMode: next.logoMode !== undefined ? next.logoMode : current.logoMode
  };

  if (options.persist !== false) theme.writeThemeState(merged);

  const applied = theme.applyTheme({ mode: currentMode(), ...merged });
  refreshAppearanceUi();
  window.dispatchEvent(new CustomEvent('fullworth:appearancechange', { detail: applied }));
  return applied;
}

// Schreibt den aktuellen Zustand in die STATISCHEN Regler zurueck (Farbfeld-Wert, aktive Schnellwahl,
// Logo-Schalter). Die Live-Vorschau selbst steht hier absichtlich NICHT drin: sie braucht keinen
// Abgleich, weil sie exakt dieselben Custom Properties/Klassen wie der Rest der App liest
// (--data-1..6, --cta, --accent, .btn-*) und jede applyTheme()-Aenderung sie automatisch ueber die
// CSS-Kaskade neu einfaerbt - derselbe Grund, aus dem kein Seiten-Neuladen und kein Async-Umweg noetig
// ist.
export function refreshAppearanceUi() {
  const appearance = getAppearance();
  const effective = getComputedStyle(document.documentElement);

  const seedField = document.querySelector('#appearance-seed');
  if (seedField) seedField.value = appearance.seed || effective.getPropertyValue('--cta').trim() || '#272727';

  const logoToggle = document.querySelector('#appearance-logo-mode');
  if (logoToggle) {
    logoToggle.checked = appearance.logoMode === 'themed';
    logoToggle.disabled = !appearance.seed;
  }

  for (const button of document.querySelectorAll('[data-appearance-preset]')) {
    const preset = APPEARANCE_PRESETS.find(entry => entry.key === button.dataset.appearancePreset);
    const active = Boolean(preset) && preset.seed === appearance.seed;
    button.classList.toggle('active', active);
    button.setAttribute('aria-pressed', String(active));
  }
}

// Einmaliges Verdrahten der Darstellung-Regler - kein Nachbau, kein Beobachter: das Markup steht
// schon in pages/settings/page.html, exakt wie #theme/#language, die app.js ebenfalls nur einmal
// beim Start verdrahtet (siehe app.js bind()).
function wireAppearanceSettings() {
  const seedField = document.querySelector('#appearance-seed');
  seedField?.addEventListener('input', () => applyAppearance({ seed: seedField.value }));

  document.querySelector('#appearance-seed-reset')?.addEventListener('click', () => applyAppearance({ ...APPEARANCE_DEFAULTS }));

  for (const button of document.querySelectorAll('[data-appearance-preset]')) {
    const preset = APPEARANCE_PRESETS.find(entry => entry.key === button.dataset.appearancePreset);
    if (preset) button.addEventListener('click', () => applyAppearance({ seed: preset.seed }));
  }

  const logoToggle = document.querySelector('#appearance-logo-mode');
  logoToggle?.addEventListener('change', () => applyAppearance({ logoMode: logoToggle.checked ? 'themed' : 'standard' }));
}

export function initAppearance() {
  for (const key of LEGACY_TYPOGRAPHY_KEYS) localStorage.removeItem(key);

  // Modus + Sitz sind schon vor dem ersten Bild angewendet (app/boot.js ruft dieselbe Engine
  // synchron auf); dieser Aufruf hier ist der Nach-DOMContentLoaded-Abgleich fuer den Fall, dass sich
  // seit dem pre-paint-Aufruf etwas geaendert hat (z.B. ein anderer Tab).
  applyAppearance(getAppearance(), { persist: false });

  wireAppearanceSettings();
  refreshAppearanceUi();

  window.addEventListener('storage', event => {
    // Die Schluesselnamen stehen nur einmal, in theme.js - kein zweites Mal hier abgeschrieben.
    if (event.key === theme.STORAGE_KEYS.seed || event.key === theme.STORAGE_KEYS.logoMode) {
      applyAppearance(getAppearance(), { persist: false });
    }
  });
}

export const appearanceApi = Object.freeze({ getAppearance, applyAppearance, refreshAppearanceUi });
window.FullWorthAppearance = appearanceApi;
