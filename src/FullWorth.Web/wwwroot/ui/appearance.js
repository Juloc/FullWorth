const STORAGE = Object.freeze({
  primary: 'finance.color.primary',
  secondary: 'finance.color.secondary',
  tintLogo: 'finance.color.tintLogo',
  font: 'finance.font',
  baseSize: 'finance.typography.baseSize',
  weight: 'finance.typography.weight',
  letterSpacing: 'finance.typography.letterSpacing',
  lineHeight: 'finance.typography.lineHeight'
});

// Empty means "leave the monochrome tokens alone", which is not the same as a colour that happens to
// equal the default: it keeps tokens.css in charge, so light and dark each keep their own value.
const BRAND_DEFAULTS = Object.freeze({ primary: '', secondary: '', tintLogo: false });

// Deliberately few and deliberately calm. The first entry is the shipped monochrome look, so there is
// always a way back that is not "clear the field".
const BRAND_PRESETS = Object.freeze([
  { key: 'mono', primary: '', secondary: '' },
  { key: 'ink', primary: '#1d4ed8', secondary: '#0ea5e9' },
  { key: 'forest', primary: '#15803d', secondary: '#65a30d' },
  { key: 'plum', primary: '#6d28d9', secondary: '#db2777' },
  { key: 'copper', primary: '#b45309', secondary: '#0f766e' }
]);

const HEX = /^#[0-9a-f]{6}$/i;
const BRAND_MARK_URL = '/branding/fullworth-logo.svg';
// The greys the mark ships with, in the order the SVG declares them (three bars, then the wordmark).
const BRAND_MARK_GREYS = Object.freeze(['#C9CDD1', '#A5AAAF', '#878D92', '#2D3235']);
// How far each bar is mixed towards the page background, so a tinted mark keeps its ramp instead of
// becoming one solid block. The wordmark stays the pure colour.
const BRAND_MARK_MIX = Object.freeze([0.55, 0.35, 0.18, 0]);
const FONT_GROUPS = Object.freeze([
  {
    key: 'condensed',
    fonts: [
      ['default', 'Barlow Condensed'],
      ['arial-narrow', 'Arial Narrow']
    ]
  },
  {
    key: 'sans',
    fonts: [
      ['system', 'System UI'],
      ['segoe', 'Segoe UI'],
      ['aptos', 'Aptos'],
      ['helvetica', 'Helvetica'],
      ['arial', 'Arial'],
      ['verdana', 'Verdana'],
      ['tahoma', 'Tahoma'],
      ['trebuchet', 'Trebuchet MS'],
      ['century-gothic', 'Century Gothic']
    ]
  },
  {
    key: 'rounded',
    fonts: [
      ['fredoka', 'Fredoka'],
      ['comic', 'Comic Sans MS']
    ]
  },
  {
    key: 'serif',
    fonts: [
      ['georgia', 'Georgia'],
      ['times', 'Times New Roman']
    ]
  },
  {
    key: 'mono',
    fonts: [
      ['mono', 'Courier New / Monospace']
    ]
  }
]);

const FONTS = new Set(FONT_GROUPS.flatMap(group => group.fonts.map(([value]) => value)));
const TYPOGRAPHY_DEFAULTS = Object.freeze({
  baseSize: 13,
  weight: 400,
  letterSpacing: 0,
  lineHeight: 1.5
});
const TYPOGRAPHY_LIMITS = Object.freeze({
  baseSize: [11, 18],
  weight: [300, 600],
  letterSpacing: [-0.05, 0.12],
  lineHeight: [1.1, 1.9]
});

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
    },
    typography: 'Typografie',
    typographyHint: 'Live auf die gesamte Oberfläche anwenden',
    font: 'Schrift',
    reset: 'Typografie zurücksetzen',
    baseSize: 'Basisgröße',
    weight: 'Basis-Dicke',
    letterSpacing: 'Buchstabenabstand',
    lineHeight: 'Zeilenhöhe',
    previewTitle: 'Live-Vorschau',
    previewText: 'FullWorth · Überblick · 1.234,56 € · Verträge, Buchungen und Analysen',
    groups: {
      condensed: 'Condensed',
      sans: 'Sans',
      rounded: 'Rounded & Playful',
      serif: 'Serif',
      mono: 'Monospace'
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
    },
    typography: 'Typography',
    typographyHint: 'Apply live across the full interface',
    font: 'Font',
    reset: 'Reset typography',
    baseSize: 'Base size',
    weight: 'Base weight',
    letterSpacing: 'Letter spacing',
    lineHeight: 'Line height',
    previewTitle: 'Live preview',
    previewText: 'FullWorth · Overview · €1,234.56 · Contracts, transactions and analytics',
    groups: {
      condensed: 'Condensed',
      sans: 'Sans',
      rounded: 'Rounded & Playful',
      serif: 'Serif',
      mono: 'Monospace'
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

// Relative luminance per WCAG, so the label on a primary-coloured button stays readable whatever the
// user picks. Without this, a light primary gets white text on it and the button becomes unreadable.
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


function normalizeFont(value) {
  return FONTS.has(value) ? value : 'default';
}

function numberInRange(value, [min, max], fallback) {
  if (value === null || value === '') return fallback;
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return fallback;
  return Math.min(max, Math.max(min, parsed));
}

function typographyFromStorage() {
  return {
    baseSize: numberInRange(localStorage.getItem(STORAGE.baseSize), TYPOGRAPHY_LIMITS.baseSize, TYPOGRAPHY_DEFAULTS.baseSize),
    weight: numberInRange(localStorage.getItem(STORAGE.weight), TYPOGRAPHY_LIMITS.weight, TYPOGRAPHY_DEFAULTS.weight),
    letterSpacing: numberInRange(localStorage.getItem(STORAGE.letterSpacing), TYPOGRAPHY_LIMITS.letterSpacing, TYPOGRAPHY_DEFAULTS.letterSpacing),
    lineHeight: numberInRange(localStorage.getItem(STORAGE.lineHeight), TYPOGRAPHY_LIMITS.lineHeight, TYPOGRAPHY_DEFAULTS.lineHeight)
  };
}

export function getAppearance() {
  return {
    primary: normalizeColor(localStorage.getItem(STORAGE.primary)),
    secondary: normalizeColor(localStorage.getItem(STORAGE.secondary)),
    tintLogo: localStorage.getItem(STORAGE.tintLogo) === 'true',
    font: normalizeFont(
      localStorage.getItem(STORAGE.font) || document.documentElement.dataset.font
    ),
    ...typographyFromStorage()
  };
}

// Writes the properties the rest of the app already reads, inline on <html> so they beat the
// [data-theme] blocks in tokens.css. An empty colour REMOVES the property again rather than writing a
// default, which hands control back to tokens.css and keeps light/dark each on their own value.
function applyBrandVariables(appearance) {
  const root = document.documentElement;
  const set = (name, value) => value ? root.style.setProperty(name, value) : root.style.removeProperty(name);

  set('--brand-primary', appearance.primary);
  set('--cta', appearance.primary);
  set('--cta-text', appearance.primary ? readableTextOn(appearance.primary) : '');

  set('--brand-secondary', appearance.secondary);
  set('--accent', appearance.secondary);
  // A translucent tint works over both themes, so the soft variant needs no per-theme branch.
  set('--accent-soft', appearance.secondary ? `color-mix(in srgb, ${appearance.secondary} 12%, transparent)` : '');

  root.dataset.brandTint = appearance.tintLogo && appearance.primary ? 'on' : 'off';
}

// Recolours the brand mark by rewriting the greys in the SVG source and handing the result to the
// existing <img> as a data URL. A CSS mask would have been less code but flattens the three bars into
// one solid shape, which loses the ramp the mark is built on.
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
    // A failed fetch must leave the logo alone, not blank it.
    catch { brandMarkSource = ''; }
  }
  if (!brandMarkSource) return;

  const backdrop = pageBackdrop();
  let svg = brandMarkSource;
  BRAND_MARK_GREYS.forEach((grey, index) => {
    svg = svg.replaceAll(grey, mixHex(appearance.primary, backdrop, BRAND_MARK_MIX[index]));
  });
  // The shipped mark carries its own prefers-color-scheme block; a tinted one is derived from the
  // current theme instead, so that block would fight it.
  svg = svg.replace(/@media \(prefers-color-scheme: dark\)[^}]*}[^}]*}/, '');
  const url = `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
  marks.forEach(mark => {
    if (!mark.dataset.brandOriginal) mark.dataset.brandOriginal = mark.getAttribute('src') || BRAND_MARK_URL;
    mark.src = url;
  });
}

function applyTypographyVariables(appearance) {
  const root = document.documentElement;
  root.style.setProperty('--font-size-base', `${appearance.baseSize}px`);
  root.style.setProperty('--font-weight-base', String(appearance.weight));
  root.style.setProperty('--letter-spacing-base', `${appearance.letterSpacing}em`);
  root.style.setProperty('--line-height-base', String(appearance.lineHeight));
}

export function applyAppearance(next = {}, options = {}) {
  const current = getAppearance();
  const appearance = {
    primary: normalizeColor(next.primary ?? current.primary),
    secondary: normalizeColor(next.secondary ?? current.secondary),
    tintLogo: Boolean(next.tintLogo ?? current.tintLogo),
    font: normalizeFont(next.font ?? current.font),
    baseSize: numberInRange(next.baseSize ?? current.baseSize, TYPOGRAPHY_LIMITS.baseSize, TYPOGRAPHY_DEFAULTS.baseSize),
    weight: numberInRange(next.weight ?? current.weight, TYPOGRAPHY_LIMITS.weight, TYPOGRAPHY_DEFAULTS.weight),
    letterSpacing: numberInRange(next.letterSpacing ?? current.letterSpacing, TYPOGRAPHY_LIMITS.letterSpacing, TYPOGRAPHY_DEFAULTS.letterSpacing),
    lineHeight: numberInRange(next.lineHeight ?? current.lineHeight, TYPOGRAPHY_LIMITS.lineHeight, TYPOGRAPHY_DEFAULTS.lineHeight)
  };

  if (options.persist !== false) {
    localStorage.setItem(STORAGE.primary, appearance.primary);
    localStorage.setItem(STORAGE.secondary, appearance.secondary);
    localStorage.setItem(STORAGE.tintLogo, String(appearance.tintLogo));
    localStorage.setItem(STORAGE.font, appearance.font);
    localStorage.setItem(STORAGE.baseSize, String(appearance.baseSize));
    localStorage.setItem(STORAGE.weight, String(appearance.weight));
    localStorage.setItem(STORAGE.letterSpacing, String(appearance.letterSpacing));
    localStorage.setItem(STORAGE.lineHeight, String(appearance.lineHeight));
  }

  document.documentElement.dataset.font = appearance.font;
  applyTypographyVariables(appearance);
  applyBrandVariables(appearance);
  void tintBrandMark(appearance);
  refreshAppearanceUi();
  window.dispatchEvent(new CustomEvent('fullworth:appearancechange', { detail: appearance }));
  return appearance;
}

function makeSelect(id, labelText, values, selected, onChange) {
  const label = document.createElement('label');
  label.dataset.appearanceControl = id;
  const span = document.createElement('span');
  span.textContent = labelText;
  const select = document.createElement('select');
  select.id = id;

  for (const [value, text] of values) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = text;
    select.appendChild(option);
  }

  select.value = selected;
  select.addEventListener('change', event => onChange(event.target.value));
  label.append(span, select);
  return label;
}

function makeFontSelect(copy, selected) {
  const label = document.createElement('label');
  label.className = 'typography-font';
  const span = document.createElement('span');
  span.textContent = copy.font;
  const select = document.createElement('select');
  select.id = 'appearance-font';

  for (const group of FONT_GROUPS) {
    const optgroup = document.createElement('optgroup');
    optgroup.label = copy.groups[group.key];
    for (const [value, text] of group.fonts) {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = text;
      optgroup.appendChild(option);
    }
    select.appendChild(optgroup);
  }

  select.value = selected;
  select.addEventListener('change', event => applyAppearance({ font: event.target.value }));
  label.append(span, select);
  return label;
}

function formatValue(key, value) {
  if (key === 'baseSize') return `${Number(value).toFixed(Number(value) % 1 ? 1 : 0)} px`;
  if (key === 'weight') return String(Math.round(value));
  if (key === 'letterSpacing') return `${Number(value).toFixed(3).replace(/0+$/, '').replace(/\.$/, '')} em`;
  return Number(value).toFixed(2).replace(/0+$/, '').replace(/\.$/, '');
}

function makeRange(key, labelText, min, max, step, value) {
  const label = document.createElement('label');
  label.className = 'typography-range';
  label.dataset.typographyRange = key;

  const head = document.createElement('span');
  head.className = 'typography-range-head';
  const text = document.createElement('span');
  text.textContent = labelText;
  const output = document.createElement('output');
  output.dataset.typographyOutput = key;
  output.textContent = formatValue(key, value);
  head.append(text, output);

  const input = document.createElement('input');
  input.type = 'range';
  input.min = String(min);
  input.max = String(max);
  input.step = String(step);
  input.value = String(value);
  input.dataset.typographyInput = key;
  input.addEventListener('input', event => {
    const numeric = Number(event.target.value);
    output.textContent = formatValue(key, numeric);
    applyAppearance({ [key]: numeric });
  });

  label.append(head, input);
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
    // Two swatches, because a preset is a PAIR - showing one colour would hide half the choice.
    for (const [colorVar, color] of [['--preset-a', preset.primary], ['--preset-b', preset.secondary]]) {
      const swatch = document.createElement('span');
      swatch.className = 'appearance-swatch';
      swatch.dataset.presetSlot = colorVar;
      if (color) swatch.style.setProperty('background-color', color);
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

function makeTypographyControls(copy, appearance) {
  const root = document.createElement('div');
  root.className = 'settings-typography';
  root.dataset.appearanceControl = 'typography';

  const head = document.createElement('div');
  head.className = 'settings-typography-head';
  const intro = document.createElement('div');
  const title = document.createElement('strong');
  title.textContent = copy.typography;
  const hint = document.createElement('small');
  hint.textContent = copy.typographyHint;
  intro.append(title, hint);

  const reset = document.createElement('button');
  reset.type = 'button';
  reset.className = 'btn btn-secondary typography-reset';
  reset.textContent = copy.reset;
  reset.addEventListener('click', () => applyAppearance({
    font: 'default',
    ...TYPOGRAPHY_DEFAULTS
  }));

  head.append(intro, reset);

  const controls = document.createElement('div');
  controls.className = 'typography-controls';
  controls.append(
    makeFontSelect(copy, appearance.font),
    makeRange('baseSize', copy.baseSize, 11, 18, 0.5, appearance.baseSize),
    makeRange('weight', copy.weight, 300, 600, 25, appearance.weight),
    makeRange('letterSpacing', copy.letterSpacing, -0.05, 0.12, 0.005, appearance.letterSpacing),
    makeRange('lineHeight', copy.lineHeight, 1.1, 1.9, 0.05, appearance.lineHeight)
  );

  const preview = document.createElement('div');
  preview.className = 'typography-preview';
  const previewTitle = document.createElement('strong');
  previewTitle.textContent = copy.previewTitle;
  const previewText = document.createElement('span');
  previewText.textContent = copy.previewText;
  preview.append(previewTitle, previewText);

  root.append(head, controls, preview);
  return root;
}

function ensureSettingsControls(forceRebuild = false) {
  const grid = document.querySelector('#view-settings .settings-grid');
  if (!grid) return;

  if (forceRebuild) {
    grid.querySelectorAll('[data-appearance-control]').forEach(el => el.remove());
  }

  const copy = COPY[language()];
  const appearance = getAppearance();

  if (!grid.querySelector('[data-appearance-control="colors"]')) {
    grid.append(makeColorControls(copy, appearance));
  }

  if (!grid.querySelector('[data-appearance-control="typography"]')) {
    grid.append(makeTypographyControls(copy, appearance));
  }
}

function refreshSettingsValues() {
  const appearance = getAppearance();
  const font = document.querySelector('#appearance-font');
  if (font) font.value = appearance.font;

  // An empty stored colour means "the token decides". The native colour input cannot show "nothing",
  // so it is filled with the value actually in effect, read back off the computed style.
  const effective = getComputedStyle(document.documentElement);
  const primary = document.querySelector('#brand-primary');
  const secondary = document.querySelector('#brand-secondary');
  if (primary) primary.value = appearance.primary || normalizeColor(effective.getPropertyValue('--cta').trim()) || '#272727';
  if (secondary) secondary.value = appearance.secondary || normalizeColor(effective.getPropertyValue('--accent').trim()) || '#272727';
  const tint = document.querySelector('#brand-tint-logo');
  if (tint) {
    tint.checked = appearance.tintLogo;
    // Tinting needs something to tint with.
    tint.disabled = !appearance.primary;
  }
  for (const button of document.querySelectorAll('[data-brand-preset]')) {
    const preset = BRAND_PRESETS.find(entry => entry.key === button.dataset.brandPreset);
    const active = Boolean(preset) && preset.primary === appearance.primary && preset.secondary === appearance.secondary;
    button.classList.toggle('active', active);
    button.setAttribute('aria-pressed', String(active));
  }

  for (const key of Object.keys(TYPOGRAPHY_DEFAULTS)) {
    const input = document.querySelector(`[data-typography-input="${key}"]`);
    const output = document.querySelector(`[data-typography-output="${key}"]`);
    if (input) input.value = String(appearance[key]);
    if (output) output.textContent = formatValue(key, appearance[key]);
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
    if (Object.values(STORAGE).includes(event.key)) {
      applyAppearance(getAppearance(), { persist: false });
    }
  });

  refreshAppearanceUi();
}

export const appearanceApi = Object.freeze({
  getAppearance,
  applyAppearance,
  refreshAppearanceUi
});

window.FullWorthAppearance = appearanceApi;
