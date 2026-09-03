const STORAGE = Object.freeze({
  visualTheme: 'finance.visualTheme',
  mascot: 'finance.mascot',
  mascotActivity: 'finance.mascotActivity'
});

const VISUAL_THEMES = new Set(['clean', 'cute']);
const ACTIVITIES = new Set(['subtle', 'normal', 'high']);
const CORE_SCENES = Object.freeze(['idle', 'happy', 'working', 'warning', 'celebrate', 'empty']);
const BESPOKE_SCENES = Object.freeze({
  lion: ['budget-success', 'budget-warning', 'goal-reached', 'investment-growth'],
  duck: ['receipt-scanning'],
  elephant: ['goal-reached', 'house'],
  penguin: ['receipt-scanning', 'budget-success', 'budget-warning'],
  raccoon: ['receipt-scanning', 'amazon-import'],
  tree: ['goal-reached', 'investment-growth', 'house']
});

const registry = new Map([
  ['lion', createMascot('lion', 'Lion', 'Löwe', '🦁')],
  ['duck', createMascot('duck', 'Duck', 'Ente', '🦆')],
  ['elephant', createMascot('elephant', 'Elephant', 'Elefant', '🐘')],
  ['penguin', createMascot('penguin', 'Penguin', 'Pinguin', '🐧')],
  ['raccoon', createMascot('raccoon', 'Raccoon', 'Waschbär', '🦝')],
  ['tree', createMascot('tree', 'Tree', 'Baum', '🌳')],
  ['ghost', createMascot('ghost', 'Ghost', 'Geist', '👻')],
  ['vault', createMascot('vault', 'Vault', 'Tresor', '🔐')]
]);

const SCENE_FALLBACKS = Object.freeze({
  'receipt-scanning': ['working', 'idle'],
  shopping: ['happy', 'working', 'idle'],
  'saving-money': ['happy', 'idle'],
  'budget-success': ['celebrate', 'success', 'happy', 'idle'],
  'budget-warning': ['warning', 'thinking', 'idle'],
  'investment-growth': ['happy', 'success', 'idle'],
  house: ['happy', 'idle'],
  mortgage: ['thinking', 'working', 'idle'],
  'goal-reached': ['celebrate', 'success', 'happy', 'idle'],
  'first-bank-connected': ['celebrate', 'success', 'happy', 'idle'],
  'portfolio-growth': ['happy', 'success', 'idle'],
  'subscription-found': ['thinking', 'working', 'idle'],
  'amazon-import': ['working', 'happy', 'idle'],
  'empty-transactions': ['empty', 'idle'],
  'empty-investments': ['empty', 'idle'],
  'insurance-empty': ['empty', 'idle']
});

const BASE_STATES = new Set([
  'idle', 'happy', 'celebrate', 'thinking', 'warning', 'sad', 'sleeping', 'working', 'empty', 'success'
]);

const EMPTY_SCENE_BY_VIEW = Object.freeze({
  transactions: 'empty-transactions',
  accounts: 'empty',
  budgets: 'saving-money',
  contracts: 'subscription-found',
  networth: 'empty-investments',
  analytics: 'empty',
  purchases: 'shopping',
  categories: 'empty',
  rules: 'empty',
  notifications: 'empty',
  merchants: 'shopping',
  audit: 'empty',
  dashboard: 'empty'
});

const COPY = Object.freeze({
  de: {
    style: 'Stil', clean: 'Clean', cute: 'Cute', mascot: 'Maskottchen', none: 'Keins',
    activity: 'Maskottchen-Präsenz', subtle: 'Dezent', normal: 'Normal', high: 'Viel',
    previewNone: 'Kein Maskottchen ausgewählt.', preview: 'Maskottchen-Vorschau. Spezielle Situationen verwenden automatisch passende Szenen.'
  },
  en: {
    style: 'Style', clean: 'Clean', cute: 'Cute', mascot: 'Mascot', none: 'None',
    activity: 'Mascot presence', subtle: 'Subtle', normal: 'Normal', high: 'High',
    previewNone: 'No mascot selected.', preview: 'Mascot preview. Special situations automatically use matching scenes.'
  }
});

function createCoreAssetMap(id) {
  const scenes = [...CORE_SCENES, ...(BESPOKE_SCENES[id] || [])];
  return Object.fromEntries(scenes.map(scene => [scene, `/mascots/${id}.svg#${scene}`]));
}

function createMascot(id, en, de, glyph) {
  return {
    id,
    labels: { en, de },
    glyph,
    assets: createCoreAssetMap(id)
  };
}

function language() {
  return (document.documentElement.lang || navigator.language || 'en').toLowerCase().startsWith('de') ? 'de' : 'en';
}

function normalizeVisualTheme(value) {
  return VISUAL_THEMES.has(value) ? value : 'clean';
}

function normalizeMascot(value) {
  return value === 'none' || registry.has(value) ? value : 'none';
}

function normalizeActivity(value) {
  return ACTIVITIES.has(value) ? value : 'normal';
}

export function getAppearance() {
  return {
    visualTheme: normalizeVisualTheme(localStorage.getItem(STORAGE.visualTheme) || document.documentElement.dataset.visualTheme),
    mascot: normalizeMascot(localStorage.getItem(STORAGE.mascot) || document.documentElement.dataset.mascot),
    mascotActivity: normalizeActivity(localStorage.getItem(STORAGE.mascotActivity) || document.documentElement.dataset.mascotActivity)
  };
}

export function applyAppearance(next = {}, options = {}) {
  const current = getAppearance();
  const appearance = {
    visualTheme: normalizeVisualTheme(next.visualTheme ?? current.visualTheme),
    mascot: normalizeMascot(next.mascot ?? current.mascot),
    mascotActivity: normalizeActivity(next.mascotActivity ?? current.mascotActivity)
  };

  if (options.persist !== false) {
    localStorage.setItem(STORAGE.visualTheme, appearance.visualTheme);
    localStorage.setItem(STORAGE.mascot, appearance.mascot);
    localStorage.setItem(STORAGE.mascotActivity, appearance.mascotActivity);
  }

  const root = document.documentElement;
  root.dataset.visualTheme = appearance.visualTheme;
  root.dataset.mascot = appearance.mascot;
  root.dataset.mascotActivity = appearance.mascotActivity;

  // Warm the sprite geometry cache for the active mascot so its art renders without a glyph flash.
  if (appearance.mascot !== 'none' && !spriteViews.has(appearance.mascot)) {
    loadSpriteViews(appearance.mascot).then(views => { if (views) refreshAppearanceUi(); });
  }

  refreshAppearanceUi();
  window.dispatchEvent(new CustomEvent('fullworth:appearancechange', { detail: appearance }));
  return appearance;
}

export function listMascots() {
  return [...registry.values()].map(m => ({
    id: m.id,
    glyph: m.glyph,
    label: m.labels[language()] || m.labels.en
  }));
}

export function registerMascot(definition) {
  if (!definition || !definition.id || definition.id === 'none') throw new Error('Mascot id is required.');
  const id = String(definition.id).trim().toLowerCase();
  if (!id) throw new Error('Mascot id is required.');

  registry.set(id, {
    id,
    labels: {
      en: definition.labels?.en || definition.label || id,
      de: definition.labels?.de || definition.label || definition.labels?.en || id
    },
    glyph: definition.glyph || '•',
    assets: { ...(definition.assets || {}) }
  });

  ensureSettingsControls(true);
  refreshAppearanceUi();
}

export function registerMascotAsset(mascotId, scene, url) {
  const mascot = registry.get(mascotId);
  if (!mascot) throw new Error(`Unknown mascot: ${mascotId}`);
  if (!scene || !url) throw new Error('Scene and URL are required.');
  mascot.assets[String(scene)] = String(url);
  refreshGenericEmptyStates(true);
  refreshSceneSlots(true);
}

export function resolveMascotScene(scene, mascotId = getAppearance().mascot) {
  if (mascotId === 'none') return null;
  const mascot = registry.get(mascotId);
  if (!mascot) return null;

  const requested = String(scene || 'idle');
  const candidates = [
    requested,
    ...(SCENE_FALLBACKS[requested] || []),
    ...(BASE_STATES.has(requested) ? [] : ['idle'])
  ];

  const seen = new Set();
  for (const candidate of candidates) {
    if (!candidate || seen.has(candidate)) continue;
    seen.add(candidate);
    if (mascot.assets[candidate]) {
      return { kind: 'asset', mascotId, scene: candidate, src: mascot.assets[candidate], glyph: mascot.glyph };
    }
  }

  return { kind: 'glyph', mascotId, scene: candidates[0] || 'idle', glyph: mascot.glyph };
}

// Sprites are horizontal sheets of 128px cells; each scene is a <view id> with an offset viewBox.
// Chromium does not honour named <view> fragments in <img src>, so instead of `.svg#scene` we frame
// the cell with a nested <svg viewBox="<cell>"> that embeds the full sheet via <image>. The per-scene
// viewBox and sheet width are read once from the SVG's own <view> elements (source of truth), cached.
const SVG_NS = 'http://www.w3.org/2000/svg';
const XLINK_NS = 'http://www.w3.org/1999/xlink';
const SPRITE_CELL = 128;
const spriteViews = new Map();
const spriteLoads = new Map();

function loadSpriteViews(mascotId) {
  if (spriteViews.has(mascotId)) return Promise.resolve(spriteViews.get(mascotId));
  if (spriteLoads.has(mascotId)) return spriteLoads.get(mascotId);
  const promise = fetch(`/mascots/${mascotId}.svg`)
    .then(response => (response.ok ? response.text() : Promise.reject(new Error('sprite fetch failed'))))
    .then(text => {
      const doc = new DOMParser().parseFromString(text, 'image/svg+xml');
      const root = doc.querySelector('svg');
      const width = root ? parseFloat(root.getAttribute('width')) : NaN;
      const cells = new Map();
      doc.querySelectorAll('view[id]').forEach(view => cells.set(view.id, view.getAttribute('viewBox')));
      const entry = width > 0 && cells.size ? { width, cells } : null;
      if (entry) spriteViews.set(mascotId, entry);
      return entry;
    })
    .catch(() => null)
    .finally(() => spriteLoads.delete(mascotId));
  spriteLoads.set(mascotId, promise);
  return promise;
}

function spriteGeometry(mascotId, scene) {
  const views = spriteViews.get(mascotId);
  const viewBox = views?.cells.get(scene);
  return viewBox ? { viewBox, sheetWidth: views.width, href: `/mascots/${mascotId}.svg` } : null;
}

function appendGlyph(container, glyph) {
  const span = document.createElement('span');
  span.className = 'mascot-slot-glyph';
  span.textContent = glyph;
  container.appendChild(span);
}

// Sprite fallback: frame a scene cell with a nested <svg viewBox> that embeds the sheet.
function buildSpriteSvg(geometry) {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('class', 'mascot-slot-sprite');
  svg.setAttribute('viewBox', geometry.viewBox);
  svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
  svg.setAttribute('aria-hidden', 'true');
  const image = document.createElementNS(SVG_NS, 'image');
  image.setAttribute('href', geometry.href);
  image.setAttributeNS(XLINK_NS, 'href', geometry.href);
  image.setAttribute('x', '0');
  image.setAttribute('y', '0');
  image.setAttribute('width', String(geometry.sheetWidth));
  image.setAttribute('height', String(SPRITE_CELL));
  svg.appendChild(image);
  return svg;
}

// When the rich portrait is unavailable, fall back to the vector sprite cell, then to a glyph.
// Decoration must always degrade and never block a workflow.
function renderSpriteFallback(container, resolved, scene, options) {
  container.replaceChildren();
  const geometry = spriteGeometry(resolved.mascotId, resolved.scene);
  if (geometry) {
    container.appendChild(buildSpriteSvg(geometry));
  } else {
    appendGlyph(container, resolved.glyph);
    loadSpriteViews(resolved.mascotId).then(views => {
      if (views && container.dataset.mascotScene === scene) {
        container.dataset.mascotResolvedKey = '';
        renderMascotScene(container, scene, { ...options, force: true });
      }
    });
  }
}

export function renderMascotScene(container, scene = 'idle', options = {}) {
  if (!container) return null;
  container.classList.add('mascot-slot');
  container.setAttribute('aria-hidden', 'true');
  container.dataset.mascotScene = scene;

  const appearance = getAppearance();
  const resolved = resolveMascotScene(scene, options.mascotId || appearance.mascot);
  if (!resolved) {
    if (container.dataset.mascotResolvedKey !== 'none') container.replaceChildren();
    container.dataset.mascotResolvedKey = 'none';
    container.hidden = true;
    return null;
  }

  // The rich illustrated portrait (/mascots/art/<id>.webp) is the primary art and is scene-agnostic;
  // the semantic prop (mascot-scenes.js) conveys the scene on top of it. Key per mascot so a scene
  // change never reloads the image; the sprite/glyph path remains as a graceful fallback.
  const resolvedKey = resolved.kind === 'asset'
    ? `art:${resolved.mascotId}`
    : `glyph:${resolved.mascotId}:${resolved.scene}:${resolved.glyph}`;
  if (!options.force && container.dataset.mascotResolvedKey === resolvedKey) {
    container.hidden = false;
    return resolved;
  }

  container.dataset.mascotResolvedKey = resolvedKey;
  container.hidden = false;
  container.replaceChildren();
  if (resolved.kind === 'asset') {
    const art = document.createElement('img');
    art.className = 'mascot-slot-art';
    art.src = `/mascots/art/${resolved.mascotId}.webp`;
    art.alt = '';
    art.decoding = 'async';
    art.loading = options.eager ? 'eager' : 'lazy';
    art.addEventListener('error', () => {
      if (container.dataset.mascotScene === scene) renderSpriteFallback(container, resolved, scene, options);
    }, { once: true });
    container.appendChild(art);
  } else {
    appendGlyph(container, resolved.glyph);
  }

  return resolved;
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
    grid.querySelectorAll('[data-appearance-control], .appearance-mascot-preview').forEach(el => el.remove());
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

  const mascotValues = [['none', copy.none], ...listMascots().map(m => [m.id, `${m.glyph} ${m.label}`])];
  const mascot = makeSelect(
    'mascot',
    copy.mascot,
    mascotValues,
    appearance.mascot,
    value => applyAppearance({ mascot: value })
  );

  const activity = makeSelect(
    'mascot-activity',
    copy.activity,
    [['subtle', copy.subtle], ['normal', copy.normal], ['high', copy.high]],
    appearance.mascotActivity,
    value => applyAppearance({ mascotActivity: value })
  );

  const preview = document.createElement('div');
  preview.className = 'appearance-mascot-preview';
  preview.dataset.appearancePreview = 'true';
  const slot = document.createElement('div');
  slot.dataset.mascotScene = 'idle';
  const text = document.createElement('div');
  text.className = 'appearance-mascot-preview-copy';
  preview.append(slot, text);

  grid.append(style, mascot, activity, preview);
  refreshAppearanceUi();
}

function refreshSettingsValues() {
  const appearance = getAppearance();
  const style = document.querySelector('#visual-theme');
  const mascot = document.querySelector('#mascot');
  const activity = document.querySelector('#mascot-activity');
  if (style) style.value = appearance.visualTheme;
  if (mascot) mascot.value = appearance.mascot;
  if (activity) activity.value = appearance.mascotActivity;

  const preview = document.querySelector('.appearance-mascot-preview');
  if (preview) {
    const slot = preview.querySelector('[data-mascot-scene]');
    const copy = COPY[language()];
    renderMascotScene(slot, 'idle');
    const text = preview.querySelector('.appearance-mascot-preview-copy');
    if (text) text.textContent = appearance.mascot === 'none' ? copy.previewNone : copy.preview;
  }
}

function sceneForEmptyState(element) {
  const view = element.closest('.view');
  const viewName = view?.id?.startsWith('view-') ? view.id.slice(5) : '';
  return EMPTY_SCENE_BY_VIEW[viewName] || 'empty';
}

function refreshGenericEmptyStates(force = false) {
  const appearance = getAppearance();
  const show = appearance.mascot !== 'none' && appearance.mascotActivity !== 'subtle';

  document.querySelectorAll('.state-empty').forEach(emptyState => {
    const copy = emptyState.querySelector('.row-sub') || emptyState;
    let slot = copy.querySelector(':scope > .state-empty-mascot');
    if (!show) {
      slot?.remove();
      return;
    }

    if (!slot) {
      slot = document.createElement('span');
      slot.className = 'state-empty-mascot';
      copy.prepend(slot);
    }
    renderMascotScene(slot, sceneForEmptyState(emptyState), { force });
  });
}

function refreshSceneSlots(force = false) {
  document.querySelectorAll('[data-mascot-scene]').forEach(slot => {
    if (slot.classList.contains('state-empty-mascot')) return;
    renderMascotScene(slot, slot.dataset.mascotScene || 'idle', { force });
  });
}

function refreshCompanion() {
  const appearance = getAppearance();
  const actions = document.querySelector('.topbar-actions');
  let companion = document.querySelector('#mascot-companion');
  const show = appearance.mascot !== 'none' && appearance.mascotActivity === 'high' && actions;

  if (!show) {
    companion?.remove();
    return;
  }

  if (!companion) {
    companion = document.createElement('span');
    companion.id = 'mascot-companion';
    companion.setAttribute('aria-hidden', 'true');
    actions.prepend(companion);
  }

  renderMascotScene(companion, 'idle', { eager: true });
}

export function refreshAppearanceUi() {
  ensureSettingsControls();
  refreshSettingsValues();
  refreshCompanion();
  refreshGenericEmptyStates();
  refreshSceneSlots();
}

let mutationScheduled = false;
function scheduleRefresh() {
  if (mutationScheduled) return;
  mutationScheduled = true;
  requestAnimationFrame(() => {
    mutationScheduled = false;
    ensureSettingsControls();
    refreshCompanion();
    refreshGenericEmptyStates();
    document.querySelectorAll('[data-mascot-scene]:not(.mascot-slot)').forEach(slot => renderMascotScene(slot, slot.dataset.mascotScene || 'idle'));
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

export const appearanceApi = Object.freeze({
  getAppearance,
  applyAppearance,
  listMascots,
  registerMascot,
  registerMascotAsset,
  resolveMascotScene,
  renderMascotScene,
  refreshAppearanceUi
});

window.FullWorthAppearance = appearanceApi;