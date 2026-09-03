const SVG_NS = 'http://www.w3.org/2000/svg';

const SCENE_PROPS = Object.freeze({
  'receipt-scanning': { tone: 'accent', icon: '<path d="M7 3h10v18l-2-1.5L13 21l-2-1.5L9 21l-2-1.5L5 21V5a2 2 0 0 1 2-2Z"/><path d="M9 8h6M9 12h6M9 16h4"/>' },
  'budget-success': { tone: 'positive', icon: '<circle cx="12" cy="12" r="9"/><path d="m8 12 2.6 2.6L16.5 9"/>' },
  'budget-warning': { tone: 'warning', icon: '<path d="M12 3 2.8 20h18.4L12 3Z"/><path d="M12 9v5"/><path d="M12 17.5h.01"/>' },
  'goal-reached': { tone: 'positive', icon: '<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="4"/><path d="m15 9 5-5m0 0v4m0-4h-4"/>' },
  'investment-growth': { tone: 'positive', icon: '<path d="M4 19V9m5 10v-6m5 6V7m5 12V4"/><path d="m5 8 4-3 5 2 5-4"/>' },
  'portfolio-growth': { tone: 'positive', icon: '<path d="M4 18 9 13l3 3 7-9"/><path d="M15 7h4v4"/>' },
  house: { tone: 'accent', icon: '<path d="m3 11 9-7 9 7"/><path d="M5 10v10h14V10M9 20v-6h6v6"/>' },
  mortgage: { tone: 'accent', icon: '<path d="m4 10 8-6 8 6"/><path d="M6 9v11h12V9"/><path d="M9 14h6M9 17h4"/>' },
  'first-bank-connected': { tone: 'positive', icon: '<path d="m3 9 9-5 9 5M5 10v7m4-7v7m4-7v7"/><path d="M3 20h12"/><path d="m16 16 2 2 4-5"/>' },
  'amazon-import': { tone: 'accent', icon: '<path d="m4 8 8-4 8 4-8 4-8-4Z"/><path d="M4 8v8l8 4 8-4V8M12 12v8"/><path d="M8 14H5m1.5-2.5L4 14l2.5 2.5"/>' },
  shopping: { tone: 'accent', icon: '<path d="M5 8h14l-1 12H6L5 8Z"/><path d="M9 9V7a3 3 0 0 1 6 0v2"/>' },
  'saving-money': { tone: 'positive', icon: '<ellipse cx="12" cy="8" rx="6" ry="3"/><path d="M6 8v7c0 1.7 2.7 3 6 3s6-1.3 6-3V8M6 12c0 1.7 2.7 3 6 3s6-1.3 6-3"/>' },
  'subscription-found': { tone: 'accent', icon: '<path d="M6 3h9l3 3v9"/><path d="M15 3v4h4"/><circle cx="11" cy="15" r="4"/><path d="m14 18 4 3"/>' },
  'empty-transactions': { tone: 'neutral', icon: '<path d="M7 5v12m0 0-3-3m3 3 3-3M17 19V7m0 0-3 3m3-3 3 3"/>' },
  'empty-investments': { tone: 'neutral', icon: '<path d="M4 19h16M6 16l4-5 3 3 5-8"/><path d="M15 6h3v3"/>' },
  'insurance-empty': { tone: 'neutral', icon: '<path d="M12 3 5 6v5c0 4.7 3 8 7 10 4-2 7-5.3 7-10V6l-7-3Z"/><path d="M9 12h6M12 9v6"/>' }
});

// Later bespoke scene packs exercise the public extension API instead of growing appearance.js forever.
// Every entry still has the same semantic-composition fallback if registration ever fails.
const EXTENDED_BESPOKE_ASSETS = Object.freeze({
  lion: ['first-bank-connected', 'portfolio-growth'],
  elephant: ['mortgage'],
  tree: ['portfolio-growth'],
  vault: ['first-bank-connected', 'mortgage']
});

let observer;
let momentTimer;
let momentSerial = 0;

function registerExtendedBespokeAssets() {
  const register = window.FullWorthAppearance?.registerMascotAsset;
  if (!register) return;
  for (const [mascot, scenes] of Object.entries(EXTENDED_BESPOKE_ASSETS)) {
    for (const scene of scenes) register(mascot, scene, `/mascots/${mascot}.svg#${scene}`);
  }
}

function makeProp(scene, spec) {
  const prop = document.createElement('span');
  prop.className = 'mascot-scene-prop';
  prop.dataset.scene = scene;
  prop.dataset.tone = spec.tone || 'accent';
  prop.setAttribute('aria-hidden', 'true');

  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  svg.innerHTML = spec.icon;
  prop.appendChild(svg);
  return prop;
}

export function decorateMascotScene(slot) {
  if (!slot?.dataset?.mascotScene) return;
  const scene = slot.dataset.mascotScene;
  const spec = SCENE_PROPS[scene];
  const existing = slot.querySelector(':scope > .mascot-scene-prop');

  if (!spec) {
    existing?.remove();
    return;
  }

  // The mascot art is a scene-agnostic illustrated portrait, so the semantic prop always conveys the
  // specific scene (receipt, budget, etc.) as a small badge on top of it.
  if (existing?.dataset.scene === scene) return;
  existing?.remove();
  slot.appendChild(makeProp(scene, spec));
}

export function decorateAllMascotScenes() {
  document.querySelectorAll('.mascot-slot[data-mascot-scene]').forEach(decorateMascotScene);
}

function currentAppearance() {
  return window.FullWorthAppearance?.getAppearance?.() || {
    mascot: document.documentElement.dataset.mascot || 'none',
    mascotActivity: document.documentElement.dataset.mascotActivity || 'normal'
  };
}

export function showMascotMoment(scene, options = {}) {
  const appearance = currentAppearance();
  if (appearance.mascot === 'none' || appearance.mascotActivity === 'subtle') return false;
  if (!window.FullWorthAppearance?.renderMascotScene) return false;

  let host = document.querySelector('#mascot-moment');
  if (!host) {
    host = document.createElement('div');
    host.id = 'mascot-moment';
    host.setAttribute('aria-hidden', 'true');
    const slot = document.createElement('div');
    slot.className = 'mascot-moment-scene';
    host.appendChild(slot);
    document.body.appendChild(host);
  }

  const slot = host.querySelector('.mascot-moment-scene');
  const serial = String(++momentSerial);
  host.dataset.serial = serial;
  host.classList.remove('show');
  window.FullWorthAppearance.renderMascotScene(slot, scene, { eager: true, force: true });
  decorateMascotScene(slot);

  requestAnimationFrame(() => {
    if (host.dataset.serial === serial) host.classList.add('show');
  });

  clearTimeout(momentTimer);
  const defaultDuration = appearance.mascotActivity === 'high' ? 3200 : 2400;
  const duration = Math.max(900, Number(options.duration || defaultDuration));
  momentTimer = setTimeout(() => host.classList.remove('show'), duration);
  return true;
}

function inspectAddedNode(node) {
  if (!(node instanceof Element)) return;

  const budgets = [];
  if (node.matches('.budget-detail')) budgets.push(node);
  node.querySelectorAll?.('.budget-detail').forEach(x => budgets.push(x));
  for (const detail of budgets) {
    const status = detail.querySelector('.budget-status');
    const scene = status?.classList.contains('over') || status?.classList.contains('near')
      ? 'budget-warning'
      : 'budget-success';
    showMascotMoment(scene, { duration: 2600 });
  }
}

// Coalesce the full-document scene sweep to at most once per animation frame, mirroring
// appearance.js scheduleRefresh, so a mutation adding N nodes triggers one sweep, not N.
let decorateScheduled = false;
function scheduleDecorate() {
  if (decorateScheduled) return;
  decorateScheduled = true;
  requestAnimationFrame(() => {
    decorateScheduled = false;
    decorateAllMascotScenes();
  });
}

// Real-estate forms only close through changedAndClose after their API request succeeds. Track a
// submitted form and show its semantic moment on that successful close. Manual close/cancel actions
// invalidate the token, so closing a failed request does not create a false celebration.
function armSuccessfulDialogMoment(form, scene) {
  const dlg = form.closest('dialog');
  if (!dlg) return;

  const token = `${Date.now()}-${Math.random()}`;
  dlg.dataset.mascotSubmitToken = token;
  let cancelled = false;

  const cancelMoment = () => { cancelled = true; delete dlg.dataset.mascotSubmitToken; };
  dlg.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', cancelMoment, { once: true }));
  dlg.addEventListener('cancel', cancelMoment, { once: true });
  dlg.addEventListener('close', () => {
    if (!cancelled && dlg.dataset.mascotSubmitToken === token) {
      showMascotMoment(scene, { duration: 2800 });
    }
  }, { once: true });
}

function bindSemanticTriggers() {
  document.addEventListener('change', event => {
    if (event.target?.id === 'receipt-file' && event.target.files?.length) {
      showMascotMoment('receipt-scanning', { duration: 3000 });
    }
  });

  document.addEventListener('submit', event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    if (form.matches('[data-property-form]')) armSuccessfulDialogMoment(form, 'house');
    else if (form.matches('[data-debt-form]')) armSuccessfulDialogMoment(form, 'mortgage');
    // A different form in the same dialog submitting invalidates a pending moment, so an unrelated
    // successful close cannot celebrate a property/debt save whose own request may have failed.
    else { const dlg = form.closest('dialog'); if (dlg) delete dlg.dataset.mascotSubmitToken; }
  }, true);

  document.addEventListener('click', event => {
    const sync = event.target.closest?.('[data-sync-days]');
    if (sync) showMascotMoment('amazon-import', { duration: 3200 });
  });

  window.addEventListener('fullworth:mascot-moment', event => {
    const detail = event.detail || {};
    if (detail.scene) showMascotMoment(detail.scene, detail.options || {});
  });

  window.addEventListener('fullworth:appearancechange', () => {
    const appearance = currentAppearance();
    if (appearance.mascot === 'none' || appearance.mascotActivity === 'subtle') {
      document.querySelector('#mascot-moment')?.classList.remove('show');
    }
    requestAnimationFrame(decorateAllMascotScenes);
  });
}

export function initMascotScenes() {
  if (observer) return;
  registerExtendedBespokeAssets();
  bindSemanticTriggers();
  decorateAllMascotScenes();

  observer = new MutationObserver(records => {
    for (const record of records) {
      record.addedNodes.forEach(inspectAddedNode);
    }
    scheduleDecorate();
  });
  observer.observe(document.body, { childList: true, subtree: true });

  const queued = Array.isArray(window.__fullworthMascotMomentQueue)
    ? window.__fullworthMascotMomentQueue.splice(0)
    : [];
  queued.forEach(item => showMascotMoment(item.scene, item.options || {}));
}

export const mascotScenesApi = Object.freeze({
  showMascotMoment,
  decorateMascotScene,
  decorateAllMascotScenes,
  scenes: Object.keys(SCENE_PROPS)
});

window.FullWorthMascotScenes = mascotScenesApi;
