// Notification preferences (UI_UX_SPEC §20). MVP supports Push only. A real master control
// enables/disables the device push subscription; below it, one functional toggle per supported
// notification type persists to the per-user+space preference store (notifications.types). No fake
// disabled controls: when push is unsupported/denied/not configured, the status says so plainly
// instead of showing an always-on checkbox that implies the feature works.

import { enablePush, disablePush } from '../push/push.js';

let ctx = null;
const TYPES = [
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

  body.innerHTML = `
    <p class="row-sub notif-intro">${ctx.esc(ctx.get('notifications.intro'))}</p>
    <div class="row notif-push">
      <div class="row-main"><div class="row-title">${ctx.esc(ctx.get('notifications.push'))}</div><div class="row-sub" data-push-status>${ctx.esc(ctx.get('notifications.pushOff'))}</div></div>
      <button type="button" class="ghost" data-push-toggle>${ctx.esc(ctx.get('notifications.pushEnable'))}</button>
    </div>
    <h3 class="notif-h">${ctx.esc(ctx.get('notifications.typesHeading'))}</h3>
    ${TYPES.map(t => `<label class="row toggle-row"><div class="row-main"><div class="row-title">${ctx.esc(typeLabel(t))}</div></div><input type="checkbox" data-type="${t}" ${on(t) ? 'checked' : ''}></label>`).join('')}`;

  const statusEl = body.querySelector('[data-push-status]');
  const toggleBtn = body.querySelector('[data-push-toggle]');
  const supported = ('serviceWorker' in navigator) && ('PushManager' in window) && ('Notification' in window);
  if (!supported) {
    statusEl.textContent = ctx.get('notifications.pushUnsupported');
    toggleBtn.disabled = true;
  } else if (Notification.permission === 'granted') {
    statusEl.textContent = ctx.get('notifications.pushOn');
    toggleBtn.textContent = ctx.get('notifications.pushDisable');
  }

  toggleBtn.addEventListener('click', async () => {
    toggleBtn.disabled = true;
    try {
      if (Notification.permission === 'granted') {
        await disablePush();
        statusEl.textContent = ctx.get('notifications.pushOff');
        toggleBtn.textContent = ctx.get('notifications.pushEnable');
      } else {
        const res = await enablePush();
        if (res && res.ok) {
          statusEl.textContent = ctx.get('notifications.pushOn');
          toggleBtn.textContent = ctx.get('notifications.pushDisable');
        } else {
          const key = res && res.reason === 'denied' ? 'pushDenied'
            : res && (res.reason === 'not-configured' || res.reason === 'no-key') ? 'pushNotConfigured'
            : 'pushUnsupported';
          statusEl.textContent = ctx.get('notifications.' + key);
          ctx.toast(ctx.get('notifications.' + key));
        }
      }
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
    finally { toggleBtn.disabled = false; }
  });

  body.querySelectorAll('input[data-type]').forEach(cb => cb.addEventListener('change', async () => {
    const map = {};
    body.querySelectorAll('input[data-type]').forEach(x => { map[x.dataset.type] = x.checked; });
    try {
      await ctx.api('api/preferences/notifications.types', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ types: map }) });
      ctx.toast(ctx.get('common.saved'));
    } catch (err) { cb.checked = !cb.checked; ctx.toast(err.message || ctx.get('common.error')); }
  }));
}
