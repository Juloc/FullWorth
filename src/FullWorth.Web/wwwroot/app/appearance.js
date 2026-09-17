// Baut das "Farben"-Panel in den Einstellungen und haelt es synchron. Die Farbmathematik selbst -
// Sitz -> Akzent-/Neutral-/Datenpalette, Kontrast, Logo-Einfaerbung - steht nicht mehr hier, sondern
// genau einmal in app/theme.js (window.FullWorthTheme, klassisch schon vor app/boot.js geladen). Diese
// Datei bleibt bewusst nur die UI-Schicht: das Panel selbst neu zu gestalten ist eine spaetere Slice.
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

// Vorschlaege im Panel: EIN Sitz pro Eintrag statt vorher zwei unabhaengiger Farben (Issue #149 §3
// diskontinuiert die zweite Farbe - der Sitz leitet Waschung/Rand/Volltext/Hover/Text selbst her).
const BRAND_PRESETS = Object.freeze([
  { key: 'mono', seed: '' },
  { key: 'ink', seed: '#1d4ed8' },
  { key: 'forest', seed: '#15803d' },
  { key: 'plum', seed: '#6d28d9' },
  { key: 'copper', seed: '#b45309' }
]);

const COPY = Object.freeze({
  de: {
    colors: 'Farben',
    colorsHint: 'Ein Farbton für Knöpfe, Links, Fokus und Diagramme',
    color: 'Farbe',
    presets: 'Vorschläge',
    tintLogo: 'Logo mitfärben',
    colorReset: 'Farbe zurücksetzen',
    presetNames: {
      mono: 'Monochrom',
      ink: 'Tiefblau',
      forest: 'Waldgrün',
      plum: 'Aubergine',
      copper: 'Kupfer'
    }
  },
  en: {
    colors: 'Colours',
    colorsHint: 'One colour drives buttons, links, focus and charts',
    color: 'Colour',
    presets: 'Suggestions',
    tintLogo: 'Tint the logo too',
    colorReset: 'Reset colour',
    presetNames: {
      mono: 'Monochrome',
      ink: 'Deep blue',
      forest: 'Forest',
      plum: 'Plum',
      copper: 'Copper'
    }
  }
});

function language() {
  return (document.documentElement.lang || navigator.language || 'en')
    .toLowerCase()
    .startsWith('de') ? 'de' : 'en';
}

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

function makeColorField(id, labelText, value, onChange) {
  const label = document.createElement('label');
  label.className = 'appearance-color-field';
  const text = document.createElement('span');
  text.textContent = labelText;
  const input = document.createElement('input');
  input.type = 'color';
  input.id = id;
  if (value) input.value = value;
  input.addEventListener('input', () => onChange(input.value));
  label.append(text, input);
  return label;
}

function makeColorControls(copy, appearance) {
  const root = document.createElement('section');
  root.className = 'panel appearance-colors';
  root.dataset.appearanceControl = 'colors';

  const head = document.createElement('div');
  head.className = 'panel-head';
  const title = document.createElement('h3');
  title.textContent = copy.colors;
  const hint = document.createElement('p');
  hint.className = 'row-sub';
  hint.textContent = copy.colorsHint;
  const headText = document.createElement('div');
  headText.append(title, hint);
  const reset = document.createElement('button');
  reset.type = 'button';
  reset.className = 'btn btn-secondary';
  reset.textContent = copy.colorReset;
  reset.addEventListener('click', () => applyAppearance({ ...APPEARANCE_DEFAULTS }));
  head.append(headText, reset);

  const fields = document.createElement('div');
  fields.className = 'appearance-color-fields';
  fields.append(
    makeColorField('brand-seed', copy.color, appearance.seed, value => applyAppearance({ seed: value }))
  );

  const presetsLabel = document.createElement('p');
  presetsLabel.className = 'row-sub';
  presetsLabel.textContent = copy.presets;
  const presets = document.createElement('div');
  presets.className = 'appearance-presets';
  for (const preset of BRAND_PRESETS) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'appearance-preset';
    button.dataset.brandPreset = preset.key;
    button.title = copy.presetNames[preset.key] || preset.key;
    button.setAttribute('aria-label', button.title);
    const swatch = document.createElement('span');
    swatch.className = 'appearance-swatch';
    if (preset.seed) swatch.style.backgroundColor = preset.seed;
    button.appendChild(swatch);
    const name = document.createElement('span');
    name.className = 'appearance-preset-name';
    name.textContent = button.title;
    button.appendChild(name);
    button.addEventListener('click', () => applyAppearance({ seed: preset.seed }));
    presets.appendChild(button);
  }

  const tint = document.createElement('label');
  tint.className = 'check';
  const tintInput = document.createElement('input');
  tintInput.type = 'checkbox';
  tintInput.id = 'brand-tint-logo';
  tintInput.checked = appearance.logoMode === 'themed';
  tintInput.disabled = !appearance.seed;
  tintInput.addEventListener('change', () => applyAppearance({ logoMode: tintInput.checked ? 'themed' : 'standard' }));
  const tintText = document.createElement('span');
  tintText.textContent = copy.tintLogo;
  tint.append(tintInput, tintText);

  root.append(head, fields, presetsLabel, presets, tint);
  return root;
}

function ensureSettingsControls(forceRebuild = false) {
  const grid = document.querySelector('#view-settings .settings-grid');
  if (!grid) return;

  if (forceRebuild) grid.querySelectorAll('[data-appearance-control]').forEach(el => el.remove());

  if (!grid.querySelector('[data-appearance-control="colors"]')) {
    grid.append(makeColorControls(COPY[language()], getAppearance()));
  }
}

function refreshSettingsValues() {
  const appearance = getAppearance();
  const effective = getComputedStyle(document.documentElement);
  const seedField = document.querySelector('#brand-seed');
  if (seedField) seedField.value = appearance.seed || effective.getPropertyValue('--cta').trim() || '#272727';
  const tint = document.querySelector('#brand-tint-logo');
  if (tint) {
    tint.checked = appearance.logoMode === 'themed';
    tint.disabled = !appearance.seed;
  }
  for (const button of document.querySelectorAll('[data-brand-preset]')) {
    const preset = BRAND_PRESETS.find(entry => entry.key === button.dataset.brandPreset);
    const active = Boolean(preset) && preset.seed === appearance.seed;
    button.classList.toggle('active', active);
    button.setAttribute('aria-pressed', String(active));
  }
}

export function refreshAppearanceUi() {
  ensureSettingsControls();
  refreshSettingsValues();
}

let mutationScheduled = false;
function scheduleRefresh() {
  if (mutationScheduled) return;
  mutationScheduled = true;
  requestAnimationFrame(() => {
    mutationScheduled = false;
    ensureSettingsControls();
    refreshSettingsValues();
  });
}

export function initAppearance() {
  for (const key of LEGACY_TYPOGRAPHY_KEYS) localStorage.removeItem(key);

  // Modus + Sitz sind schon vor dem ersten Bild angewendet (app/boot.js ruft dieselbe Engine
  // synchron auf); dieser Aufruf hier ist der Nach-DOMContentLoaded-Abgleich fuer den Fall, dass sich
  // seit dem pre-paint-Aufruf etwas geaendert hat (z.B. ein anderer Tab), plus das Panel selbst.
  applyAppearance(getAppearance(), { persist: false });
  ensureSettingsControls();

  const observer = new MutationObserver(scheduleRefresh);
  observer.observe(document.body, { childList: true, subtree: true });

  const langObserver = new MutationObserver(() => ensureSettingsControls(true));
  langObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] });

  window.addEventListener('storage', event => {
    // Die Schluesselnamen stehen nur einmal, in theme.js - kein zweites Mal hier abgeschrieben.
    if (event.key === theme.STORAGE_KEYS.seed || event.key === theme.STORAGE_KEYS.logoMode) {
      applyAppearance(getAppearance(), { persist: false });
    }
  });

  refreshAppearanceUi();
}

export const appearanceApi = Object.freeze({ getAppearance, applyAppearance, refreshAppearanceUi });
window.FullWorthAppearance = appearanceApi;
