const STORAGE = Object.freeze({
  primary: 'finance.color.primary',
  secondary: 'finance.color.secondary',
  tintLogo: 'finance.color.tintLogo'
});

// Empty means "leave the monochrome tokens alone", which is not the same as a colour that happens to
// equal the default: it keeps tokens.css in charge, so light and dark each keep their own value.
const BRAND_DEFAULTS = Object.freeze({ primary: '', secondary: '', tintLogo: false });

const BRAND_PRESETS = Object.freeze([
  { key: 'mono', primary: '', secondary: '' },
  { key: 'ink', primary: '#1d4ed8', secondary: '#0ea5e9' },
  { key: 'forest', primary: '#15803d', secondary: '#65a30d' },
  { key: 'plum', primary: '#6d28d9', secondary: '#db2777' },
  { key: 'copper', primary: '#b45309', secondary: '#0f766e' }
]);

const HEX = /^#[0-9a-f]{6}$/i;
const BRAND_MARK_URL = '/branding/fullworth-logo.svg';
const BRAND_MARK_GREYS = Object.freeze(['#C9CDD1', '#A5AAAF', '#878D92', '#2D3235']);
const BRAND_MARK_MIX = Object.freeze([0.55, 0.35, 0.18, 0]);

const COPY = Object.freeze({
  de: {
    colors: 'Farben',
    colorsHint: 'Primärfarbe für Aktionen, Sekundärfarbe für Links, Fokus und Diagramme',
    primary: 'Primärfarbe',
    secondary: 'Sekundärfarbe',
    presets: 'Vorschläge',
    tintLogo: 'Logo mitfärben',
    colorReset: 'Farben zurücksetzen',
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
    colorsHint: 'Primary drives actions, secondary drives links, focus and charts',
    primary: 'Primary',
    secondary: 'Secondary',
    presets: 'Suggestions',
    tintLogo: 'Tint the logo too',
    colorReset: 'Reset colours',
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

function normalizeColor(value) {
  const text = String(value ?? '').trim();
  return HEX.test(text) ? text.toLowerCase() : '';
}

function readableTextOn(hex) {
  const channel = index => {
    const value = parseInt(hex.slice(1 + index * 2, 3 + index * 2), 16) / 255;
    return value <= 0.03928 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4);
  };
  const luminance = 0.2126 * channel(0) + 0.7152 * channel(1) + 0.0722 * channel(2);
  return luminance > 0.42 ? '#151719' : '#ffffff';
}

function mixHex(from, to, amount) {
  const part = index => {
    const a = parseInt(from.slice(1 + index * 2, 3 + index * 2), 16);
    const b = parseInt(to.slice(1 + index * 2, 3 + index * 2), 16);
    return Math.round(a + (b - a) * amount).toString(16).padStart(2, '0');
  };
  return `#${part(0)}${part(1)}${part(2)}`;
}

function pageBackdrop() {
  return document.documentElement.dataset.theme === 'dark' ? '#121416' : '#ffffff';
}

export function getAppearance() {
  return {
    primary: normalizeColor(localStorage.getItem(STORAGE.primary)),
    secondary: normalizeColor(localStorage.getItem(STORAGE.secondary)),
    tintLogo: localStorage.getItem(STORAGE.tintLogo) === 'true'
  };
}

function applyBrandVariables(appearance) {
  const root = document.documentElement;
  const set = (name, value) => value ? root.style.setProperty(name, value) : root.style.removeProperty(name);

  set('--brand-primary', appearance.primary);
  set('--cta', appearance.primary);
  set('--cta-text', appearance.primary ? readableTextOn(appearance.primary) : '');
  set('--brand-secondary', appearance.secondary);
  set('--accent', appearance.secondary);
  set('--accent-soft', appearance.secondary ? `color-mix(in srgb, ${appearance.secondary} 12%, transparent)` : '');
  root.dataset.brandTint = appearance.tintLogo && appearance.primary ? 'on' : 'off';
}

let brandMarkSource = null;
async function tintBrandMark(appearance) {
  const marks = document.querySelectorAll('.brand-logo');
  if (!marks.length) return;
  const tint = appearance.tintLogo && appearance.primary;

  if (!tint) {
    marks.forEach(mark => { if (mark.dataset.brandOriginal) mark.src = mark.dataset.brandOriginal; });
    return;
  }

  if (brandMarkSource === null) {
    try { brandMarkSource = await (await fetch(BRAND_MARK_URL)).text(); }
    catch { brandMarkSource = ''; }
  }
  if (!brandMarkSource) return;

  const backdrop = pageBackdrop();
  let svg = brandMarkSource;
  BRAND_MARK_GREYS.forEach((grey, index) => {
    svg = svg.replaceAll(grey, mixHex(appearance.primary, backdrop, BRAND_MARK_MIX[index]));
  });
  svg = svg.replace(/@media \(prefers-color-scheme: dark\)[^}]*}[^}]*}/, '');
  const url = `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
  marks.forEach(mark => {
    if (!mark.dataset.brandOriginal) mark.dataset.brandOriginal = mark.getAttribute('src') || BRAND_MARK_URL;
    mark.src = url;
  });
}

export function applyAppearance(next = {}, options = {}) {
  const current = getAppearance();
  const appearance = {
    primary: normalizeColor(next.primary ?? current.primary),
    secondary: normalizeColor(next.secondary ?? current.secondary),
    tintLogo: Boolean(next.tintLogo ?? current.tintLogo)
  };

  if (options.persist !== false) {
    localStorage.setItem(STORAGE.primary, appearance.primary);
    localStorage.setItem(STORAGE.secondary, appearance.secondary);
    localStorage.setItem(STORAGE.tintLogo, String(appearance.tintLogo));
  }

  applyBrandVariables(appearance);
  void tintBrandMark(appearance);
  refreshAppearanceUi();
  window.dispatchEvent(new CustomEvent('fullworth:appearancechange', { detail: appearance }));
  return appearance;
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
  reset.addEventListener('click', () => applyAppearance({ ...BRAND_DEFAULTS }));
  head.append(headText, reset);

  const fields = document.createElement('div');
  fields.className = 'appearance-color-fields';
  fields.append(
    makeColorField('brand-primary', copy.primary, appearance.primary, value => applyAppearance({ primary: value })),
    makeColorField('brand-secondary', copy.secondary, appearance.secondary, value => applyAppearance({ secondary: value }))
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
    for (const color of [preset.primary, preset.secondary]) {
      const swatch = document.createElement('span');
      swatch.className = 'appearance-swatch';
      if (color) swatch.style.backgroundColor = color;
      button.appendChild(swatch);
    }
    const name = document.createElement('span');
    name.className = 'appearance-preset-name';
    name.textContent = button.title;
    button.appendChild(name);
    button.addEventListener('click', () => applyAppearance({ primary: preset.primary, secondary: preset.secondary }));
    presets.appendChild(button);
  }

  const tint = document.createElement('label');
  tint.className = 'check';
  const tintInput = document.createElement('input');
  tintInput.type = 'checkbox';
  tintInput.id = 'brand-tint-logo';
  tintInput.checked = appearance.tintLogo;
  tintInput.disabled = !appearance.primary;
  tintInput.addEventListener('change', () => applyAppearance({ tintLogo: tintInput.checked }));
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
  const primary = document.querySelector('#brand-primary');
  const secondary = document.querySelector('#brand-secondary');
  if (primary) primary.value = appearance.primary || normalizeColor(effective.getPropertyValue('--cta').trim()) || '#272727';
  if (secondary) secondary.value = appearance.secondary || normalizeColor(effective.getPropertyValue('--accent').trim()) || '#272727';
  const tint = document.querySelector('#brand-tint-logo');
  if (tint) {
    tint.checked = appearance.tintLogo;
    tint.disabled = !appearance.primary;
  }
  for (const button of document.querySelectorAll('[data-brand-preset]')) {
    const preset = BRAND_PRESETS.find(entry => entry.key === button.dataset.brandPreset);
    const active = Boolean(preset) && preset.primary === appearance.primary && preset.secondary === appearance.secondary;
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
  applyAppearance(getAppearance(), { persist: false });
  ensureSettingsControls();

  const observer = new MutationObserver(scheduleRefresh);
  observer.observe(document.body, { childList: true, subtree: true });

  const langObserver = new MutationObserver(() => ensureSettingsControls(true));
  langObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] });

  window.addEventListener('storage', event => {
    if (Object.values(STORAGE).includes(event.key)) applyAppearance(getAppearance(), { persist: false });
  });

  refreshAppearanceUi();
}

export const appearanceApi = Object.freeze({ getAppearance, applyAppearance, refreshAppearanceUi });
window.FullWorthAppearance = appearanceApi;
