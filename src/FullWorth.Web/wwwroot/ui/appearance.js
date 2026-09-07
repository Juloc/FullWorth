const STORAGE = Object.freeze({
  visualTheme: 'finance.visualTheme',
  font: 'finance.font',
  baseSize: 'finance.typography.baseSize',
  weight: 'finance.typography.weight',
  letterSpacing: 'finance.typography.letterSpacing',
  lineHeight: 'finance.typography.lineHeight'
});

const VISUAL_THEMES = new Set(['clean', 'cute']);
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
    style: 'Stil',
    clean: 'Clean',
    cute: 'Cute',
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
    style: 'Style',
    clean: 'Clean',
    cute: 'Cute',
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

function normalizeVisualTheme(value) {
  return VISUAL_THEMES.has(value) ? value : 'clean';
}

function normalizeFont(value) {
  return FONTS.has(value) ? value : 'default';
}

function numberInRange(value, [min, max], fallback) {
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
    visualTheme: normalizeVisualTheme(
      localStorage.getItem(STORAGE.visualTheme) || document.documentElement.dataset.visualTheme
    ),
    font: normalizeFont(
      localStorage.getItem(STORAGE.font) || document.documentElement.dataset.font
    ),
    ...typographyFromStorage()
  };
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
    visualTheme: normalizeVisualTheme(next.visualTheme ?? current.visualTheme),
    font: normalizeFont(next.font ?? current.font),
    baseSize: numberInRange(next.baseSize ?? current.baseSize, TYPOGRAPHY_LIMITS.baseSize, TYPOGRAPHY_DEFAULTS.baseSize),
    weight: numberInRange(next.weight ?? current.weight, TYPOGRAPHY_LIMITS.weight, TYPOGRAPHY_DEFAULTS.weight),
    letterSpacing: numberInRange(next.letterSpacing ?? current.letterSpacing, TYPOGRAPHY_LIMITS.letterSpacing, TYPOGRAPHY_DEFAULTS.letterSpacing),
    lineHeight: numberInRange(next.lineHeight ?? current.lineHeight, TYPOGRAPHY_LIMITS.lineHeight, TYPOGRAPHY_DEFAULTS.lineHeight)
  };

  if (options.persist !== false) {
    localStorage.setItem(STORAGE.visualTheme, appearance.visualTheme);
    localStorage.setItem(STORAGE.font, appearance.font);
    localStorage.setItem(STORAGE.baseSize, String(appearance.baseSize));
    localStorage.setItem(STORAGE.weight, String(appearance.weight));
    localStorage.setItem(STORAGE.letterSpacing, String(appearance.letterSpacing));
    localStorage.setItem(STORAGE.lineHeight, String(appearance.lineHeight));
  }

  document.documentElement.dataset.visualTheme = appearance.visualTheme;
  document.documentElement.dataset.font = appearance.font;
  applyTypographyVariables(appearance);
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

  if (!grid.querySelector('[data-appearance-control="visual-theme"]')) {
    const style = makeSelect(
      'visual-theme',
      copy.style,
      [['clean', copy.clean], ['cute', copy.cute]],
      appearance.visualTheme,
      value => applyAppearance({ visualTheme: value })
    );
    grid.append(style);
  }

  if (!grid.querySelector('[data-appearance-control="typography"]')) {
    grid.append(makeTypographyControls(copy, appearance));
  }
}

function refreshSettingsValues() {
  const appearance = getAppearance();
  const style = document.querySelector('#visual-theme');
  const font = document.querySelector('#appearance-font');
  if (style) style.value = appearance.visualTheme;
  if (font) font.value = appearance.font;

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
