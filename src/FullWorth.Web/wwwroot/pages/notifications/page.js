// Notification preferences (UI_UX_SPEC §20). MVP supports Push only. A real master control
// enables/disables the device push subscription; below it, one functional toggle per supported
// notification type persists to the per-user+space preference store (notifications.types). No fake
// disabled controls: the status is what pushStatus() measured on this device (permission AND a
// subscription for the current server key AND a server row), never the permission alone.

import { pushStatus, enablePush, disablePush } from '../../push/push.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';

let ctx = null;
const TYPES = [
  'transactions_new',
  'bank_reauth',
  'bank_sync_error',
  'contract_due',
  'budget_near',
  'budget_over',
  'backup_failed',
  'purchase_review',
  'purchase_scan_failed',
  'purchase_unmatched',
  'purchase_return_deadline',
  'purchase_warranty_deadline',
  'property_energy_expiry',
  'property_valuation_stale'
];

const FALLBACK_LABELS = {
  de: {
    purchase_review: 'Kauf muss geprüft werden',
    purchase_scan_failed: 'Belegscan fehlgeschlagen',
    purchase_unmatched: 'Beleg noch nicht verknüpft',
    purchase_return_deadline: 'Rückgabefrist läuft ab',
    purchase_warranty_deadline: 'Garantie läuft ab',
    property_energy_expiry: 'Energieausweis läuft bald ab',
    property_valuation_stale: 'Immobilienbewertung ist veraltet'
  },
  en: {
    purchase_review: 'Purchase needs review',
    purchase_scan_failed: 'Receipt scan failed',
    purchase_unmatched: 'Receipt is not linked yet',
    purchase_return_deadline: 'Return deadline approaching',
    purchase_warranty_deadline: 'Warranty ending soon',
    property_energy_expiry: 'Energy certificate expiring',
    property_valuation_stale: 'Property valuation is stale'
  }
};

function typeLabel(type) {
  const key = `notifications.type_${type}`;
  const translated = ctx.get(key);
  if (translated && translated !== key) return translated;
  const language = (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? 'de' : 'en';
  return FALLBACK_LABELS[language][type] || translated || type;
}

export async function renderNotifications(context) {
  ctx = context;
  const body = ctx.$('#notifications-body');

  let prefs = {};
  try { const p = await ctx.api('api/preferences/notifications.types'); prefs = (p && p.value && p.value.types) || {}; } catch { prefs = {}; }
  const on = t => prefs[t] !== false;
  let showAmounts = false;
  try { const d = await ctx.api('api/preferences/notifications.display'); showAmounts = !!(d && d.value && d.value.showAmounts); } catch { showAmounts = false; }

  body.innerHTML = `
    <p class="row-sub notif-intro">${ctx.esc(ctx.get('notifications.intro'))}</p>
    <div class="notif-push" data-push-state="checking">
      <div class="row-main">
        <div class="row-title">${ctx.esc(ctx.get('notifications.push'))}</div>
        <div class="row-sub notif-push-status"><span class="notif-dot" aria-hidden="true"></span><span data-push-status>${ctx.esc(ctx.get('notifications.pushChecking'))}</span></div>
      </div>
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-push-toggle disabled>${ctx.esc(ctx.get('notifications.pushEnable'))}</button>
      <div class="notif-push-more">
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-push-test disabled>${ctx.esc(ctx.get('notifications.test'))}</button>
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-push-off>${ctx.esc(ctx.get('notifications.pushDisable'))}</button>
      </div>
    </div>
    <div class="rows notif-types">
      <label class="row notif-type-row"><div class="row-main"><div class="row-title notif-type-title">${ctx.esc(ctx.get('notifications.showAmounts'))}</div></div><span class="fw-toggle"><input type="checkbox" data-show-amounts ${showAmounts ? 'checked' : ''}><span class="fw-toggle-track"></span></span></label>
    </div>
    <p class="row-sub">${ctx.esc(ctx.get('notifications.showAmountsHint'))}</p>
    <h3 class="notif-h">${ctx.esc(ctx.get('notifications.typesHeading'))}</h3>
    <div class="rows notif-types">
      ${TYPES.map(t => `<label class="row notif-type-row"><div class="row-main"><div class="row-title notif-type-title">${ctx.esc(typeLabel(t))}</div></div><span class="fw-toggle"><input type="checkbox" data-type="${t}" ${on(t) ? 'checked' : ''}><span class="fw-toggle-track"></span></span></label>`).join('')}
    </div>
    <p class="row-sub notif-foot">${ctx.esc(ctx.get('notifications.pushHint'))}</p>`;

  const pushRow = body.querySelector('.notif-push');
  const statusEl = body.querySelector('[data-push-status]');
  const toggleBtn = body.querySelector('[data-push-toggle]');
  const testBtn = body.querySelector('[data-push-test]');
  const offBtn = body.querySelector('[data-push-off]');

  // What each measured state says and what the main button does. 'locked' states have nothing the
  // button could change from here (the browser or the browser's settings decide).
  const STATES = {
    on: { text: 'pushOn', button: 'pushDisable', turnsOff: true },
    off: { text: 'pushOff', button: 'pushEnable' },
    stale: { text: 'pushStale', button: 'pushReenable' },
    unverified: { text: 'pushUnverified', button: 'pushDisable', turnsOff: true },
    denied: { text: 'pushDenied', button: 'pushEnable', locked: true },
    'no-worker': { text: 'pushNoWorker', button: 'pushEnable', locked: true },
    unsupported: { text: 'pushUnsupported', button: 'pushEnable', locked: true }
  };
  const FAILURE_TEXT = {
    unsupported: 'pushUnsupported', denied: 'pushDenied', 'not-configured': 'pushNotConfigured', 'no-key': 'pushUnverified',
    'no-worker': 'pushNoWorker', subscribe: 'pushSubscribeFailed', save: 'pushSaveFailed'
  };
  let current = 'checking';

  const refresh = async () => {
    current = await pushStatus();
    const state = STATES[current];
    pushRow.dataset.pushState = current;
    statusEl.textContent = ctx.get('notifications.' + state.text);
    toggleBtn.textContent = ctx.get('notifications.' + state.button);
    toggleBtn.disabled = !!state.locked;
    testBtn.disabled = current !== 'on';
  };

  const turnOff = async () => {
    const result = await disablePush();
    if (!result.ok) ctx.toast(ctx.get('notifications.pushDisableFailed'));
    else if (!result.serverRemoved) ctx.toast(ctx.get('notifications.pushDisableServer'));
  };
  const turnOn = async () => {
    const result = await enablePush();
    if (!result.ok) ctx.toast(ctx.get('notifications.' + FAILURE_TEXT[result.reason]));
  };
  // Whatever happened, the status is measured again afterwards instead of assumed.
  const change = async action => {
    toggleBtn.disabled = true;
    testBtn.disabled = true;
    offBtn.disabled = true;
    try {
      await action();
      await refresh();
    } catch (err) {
      ctx.toast(err.message || ctx.get('common.error'));
    } finally { offBtn.disabled = false; }
  };
  toggleBtn.addEventListener('click', () => change(STATES[current].turnsOff ? turnOff : turnOn));
  offBtn.addEventListener('click', () => change(turnOff));

  testBtn.addEventListener('click', async () => {
    testBtn.disabled = true;
    try {
      await ctx.api('api/push/test', { method: 'POST' });
      ctx.toast(ctx.get('notifications.testSent'));
    } catch (err) {
      const message = err.message || '';
      ctx.toast(/no_devices/.test(message) ? ctx.get('notifications.testNoDevice')
        : /delivery_failed/.test(message) ? ctx.get('notifications.testFailed')
        : message || ctx.get('common.error'));
    } finally { testBtn.disabled = current !== 'on'; }
  });

  body.querySelector('[data-show-amounts]').addEventListener('change', async event => {
    const box = event.target;
    try {
      await ctx.api('api/preferences/notifications.display', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ showAmounts: box.checked }) });
      ctx.toast(ctx.get('common.saved'));
    } catch (err) { box.checked = !box.checked; ctx.toast(err.message || ctx.get('common.error')); }
  });

  body.querySelectorAll('input[data-type]').forEach(cb => cb.addEventListener('change', async () => {
    const map = {};
    body.querySelectorAll('input[data-type]').forEach(x => { map[x.dataset.type] = x.checked; });
    try {
      await ctx.api('api/preferences/notifications.types', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ types: map }) });
      ctx.toast(ctx.get('common.saved'));
    } catch (err) { cb.checked = !cb.checked; ctx.toast(err.message || ctx.get('common.error')); }
  }));

  refresh().catch(err => ctx.toast(err.message || ctx.get('common.error')));
}
