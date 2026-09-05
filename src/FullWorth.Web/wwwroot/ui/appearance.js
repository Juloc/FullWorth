const STORAGE = Object.freeze({
  visualTheme: 'finance.visualTheme'
});

const VISUAL_THEMES = new Set(['clean', 'cute']);

const COPY = Object.freeze({
  de: { style: 'Stil', clean: 'Clean', cute: 'Cute' },
  en: { style: 'Style', clean: 'Clean', cute: 'Cute' }
});

function language() {
  return (document.documentElement.lang || navigator.language || 'en')
    .toLowerCase()
    .startsWith('de') ? 'de' : 'en';
}

function normalizeVisualTheme(value) {
  return VISUAL_THEMES.has(value) ? value : 'clean';
}

export function getAppearance() {
  return {
    visualTheme: normalizeVisualTheme(
      localStorage.getItem(STORAGE.visualTheme) || document.documentElement.dataset.visualTheme
    )
  };
}

export function applyAppearance(next = {}, options = {}) {
  const current = getAppearance();
  const appearance = {
    visualTheme: normalizeVisualTheme(next.visualTheme ?? current.visualTheme)
  };

  if (options.persist !== false) {
    localStorage.setItem(STORAGE.visualTheme, appearance.visualTheme);
  }

  document.documentElement.dataset.visualTheme = appearance.visualTheme;
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

function ensureSettingsControls(forceRebuild = false) {
  const grid = document.querySelector('#view-settings .settings-grid');
  if (!grid) return;

  if (forceRebuild) {
    grid.querySelectorAll('[data-appearance-control]').forEach(el => el.remove());
  }

  if (grid.querySelector('[data-appearance-control="visual-theme"]')) return;

  const copy = COPY[language()];
  const appearance = getAppearance();
  const style = makeSelect(
    'visual-theme',
    copy.style,
    [['clean', copy.clean], ['cute', copy.cute]],
    appearance.visualTheme,
    value => applyAppearance({ visualTheme: value })
  );

  grid.append(style);
}

function refreshSettingsValues() {
  const style = document.querySelector('#visual-theme');
  if (style) style.value = getAppearance().visualTheme;
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
    if (event.key === STORAGE.visualTheme) {
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
