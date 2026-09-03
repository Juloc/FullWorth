import { assertPasskey, isPasskeySupported, passkeyErrorKey } from '../passkeys/passkeys.js';

// Inactivity lock (§ security): after LOCK_AFTER of no interaction the app covers itself with an
// opaque lock screen. Unlocking is primarily a passkey (WebAuthn) and falls back to a PIN. The
// server session stays valid the whole time — this only gates the UI, so it re-locks on reload too.
const LOCK_AFTER_MS = 10 * 60 * 1000;
const ACTIVITY_KEY = 'finance.lastActivity';
const LOCKED_KEY = 'finance.locked';
const ACTIVITY_EVENTS = ['pointerdown', 'keydown', 'touchstart'];

let ctx;
let overlay;
let idleTimer = 0;
let onUnlock;
let capability = { hasPin: false, hasPasskey: false };

const now = () => Date.now();
const lockConfigured = () => capability.hasPin || capability.hasPasskey;
const isLocked = () => Boolean(overlay) && !overlay.hidden;

// /auth/* endpoints are not proxied through /bff/backend, so call them with plain fetch; the shared
// browser-fetch wrapper still attaches credentials and the antiforgery header.
const authFetch = (url, options = {}) => fetch(url, {
  credentials: 'same-origin',
  cache: 'no-store',
  headers: { Accept: 'application/json', ...(options.headers || {}) },
  ...options
});

async function loadCapability() {
  try {
    const [pin, passkeys] = await Promise.all([
      authFetch('/auth/pin').then(r => r.ok ? r.json() : { isSet: false }),
      authFetch('/auth/passkeys').then(r => r.ok ? r.json() : [])
    ]);
    capability = {
      hasPin: Boolean(pin?.isSet),
      hasPasskey: isPasskeySupported() && Array.isArray(passkeys) && passkeys.length > 0
    };
  } catch {
    capability = { hasPin: false, hasPasskey: false };
  }
  return capability;
}

function markActivity() {
  if (isLocked()) return;
  localStorage.setItem(ACTIVITY_KEY, String(now()));
  reschedule();
}

function reschedule() {
  clearTimeout(idleTimer);
  if (lockConfigured()) idleTimer = setTimeout(lock, LOCK_AFTER_MS);
}

function idleExceeded() {
  const last = Number(localStorage.getItem(ACTIVITY_KEY) || 0);
  return last > 0 && now() - last > LOCK_AFTER_MS;
}

function setError(message) {
  const box = overlay.querySelector('.lock-error');
  box.textContent = message || '';
  box.hidden = !message;
}

function buildOverlay() {
  if (overlay) return;
  overlay = document.createElement('div');
  overlay.className = 'lock-overlay';
  overlay.hidden = true;
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'true');
  overlay.setAttribute('aria-labelledby', 'lock-title');
  const passkeyBtn = capability.hasPasskey
    ? `<button type="button" class="primary-action" data-lock="passkey">${ctx.esc(ctx.get('lock.unlockPasskey'))}</button>` : '';
  const pinToggle = capability.hasPin
    ? `<button type="button" class="ghost" data-lock="pin-toggle">${ctx.esc(ctx.get('lock.usePin'))}</button>` : '';
  overlay.innerHTML = `<div class="lock-card">
    <span class="lock-mark" aria-hidden="true">F</span>
    <h2 id="lock-title">${ctx.esc(ctx.get('lock.title'))}</h2>
    <p class="lock-sub">${ctx.esc(ctx.get('lock.subtitle'))}</p>
    <div class="lock-actions">${passkeyBtn}${pinToggle}</div>
    <form class="lock-pin" ${capability.hasPasskey ? 'hidden' : ''}>
      <input type="password" inputmode="numeric" autocomplete="off" name="pin" maxlength="12"
        placeholder="${ctx.esc(ctx.get('lock.pinPlaceholder'))}" aria-label="${ctx.esc(ctx.get('lock.pin'))}">
      <button type="submit" class="primary-action">${ctx.esc(ctx.get('lock.unlock'))}</button>
    </form>
    <p class="lock-error" role="alert" hidden></p>
    <button type="button" class="ghost danger" data-lock="logout">${ctx.esc(ctx.get('lock.logout'))}</button>
  </div>`;
  document.body.appendChild(overlay);

  overlay.querySelector('[data-lock="passkey"]')?.addEventListener('click', ev => unlockWithPasskey(ev.currentTarget));
  overlay.querySelector('[data-lock="pin-toggle"]')?.addEventListener('click', () => {
    const form = overlay.querySelector('.lock-pin');
    form.hidden = false;
    form.querySelector('input').focus();
  });
  overlay.querySelector('.lock-pin').addEventListener('submit', ev => { ev.preventDefault(); unlockWithPin(ev.currentTarget); });
  overlay.querySelector('[data-lock="logout"]').addEventListener('click', logout);
}

function lock() {
  if (!lockConfigured() || isLocked()) return;
  buildOverlay();
  setError('');
  localStorage.setItem(LOCKED_KEY, '1');
  overlay.hidden = false;
  document.body.classList.add('locked');
  overlay.querySelector('.lock-card [data-lock="passkey"], .lock-card input')?.focus();
}

function unlock() {
  if (overlay) overlay.hidden = true;
  document.body.classList.remove('locked');
  localStorage.removeItem(LOCKED_KEY);
  markActivity();
  onUnlock?.();
}

async function unlockWithPasskey(button) {
  button.disabled = true;
  setError('');
  try {
    await assertPasskey({ beginUrl: '/auth/passkeys/unlock/begin', completeUrl: '/auth/passkeys/unlock/complete' });
    unlock();
  } catch (err) {
    setError(ctx.get(passkeyErrorKey(err, 'auth')));
  } finally {
    button.disabled = false;
  }
}

async function unlockWithPin(form) {
  const button = form.querySelector('button[type="submit"]');
  button.disabled = true;
  setError('');
  try {
    const response = await authFetch('/auth/pin/verify', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: form.pin.value })
    });
    if (response.ok) { form.pin.value = ''; unlock(); return; }
    setError(ctx.get(response.status === 423 ? 'lock.pinLocked' : 'lock.pinWrong'));
  } catch {
    setError(ctx.get('common.error'));
  } finally {
    button.disabled = false;
  }
}

async function logout() {
  try { await authFetch('/auth/logout', { method: 'POST' }); } catch { /* redirect regardless */ }
  location.href = '/auth/login';
}

// Called once at boot: loads which factors exist, starts activity tracking, and locks straight away
// if the stored state says we were locked or the idle window already elapsed (e.g. after a reload).
export async function initLock(context, { onUnlock: unlockCallback } = {}) {
  ctx = context;
  onUnlock = unlockCallback;
  await loadCapability();
  ACTIVITY_EVENTS.forEach(name => window.addEventListener(name, markActivity, { passive: true }));
  document.addEventListener('visibilitychange', () => {
    if (document.hidden || isLocked()) return;
    if (lockConfigured() && idleExceeded()) lock(); else markActivity();
  });
  if (lockConfigured() && (localStorage.getItem(LOCKED_KEY) === '1' || idleExceeded())) lock();
  else markActivity();
  return { locked: isLocked() };
}

// Settings dialog to set / change / remove the fallback PIN. Kept here so all lock UI lives together.
export function openPinDialog(context) {
  ctx = context;
  authFetch('/auth/pin').then(r => r.ok ? r.json() : { isSet: false }).then(status => {
    const isSet = Boolean(status?.isSet);
    const dlg = ctx.dialog(`<form class="dialog-card">
      <h2>${ctx.esc(ctx.get('lock.pinSettingsTitle'))}</h2>
      <p class="row-sub">${ctx.esc(ctx.get(isSet ? 'lock.pinSetHint' : 'lock.pinUnsetHint'))}</p>
      <label>${ctx.esc(ctx.get('lock.newPin'))}<input name="pin" type="password" inputmode="numeric" autocomplete="off" maxlength="12" placeholder="${ctx.esc(ctx.get('lock.pinPlaceholder'))}"></label>
      <p class="lock-error" role="alert" hidden></p>
      <div class="dialog-actions">
        ${isSet ? `<button type="button" class="ghost danger" data-pin="remove">${ctx.esc(ctx.get('lock.removePin'))}</button>` : ''}
        <button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button>
        <button type="submit">${ctx.esc(ctx.get('common.save'))}</button>
      </div>
    </form>`);
    const err = dlg.querySelector('.lock-error');
    const showErr = m => { err.textContent = m; err.hidden = !m; };
    dlg.querySelector('[data-cancel]').addEventListener('click', () => dlg.close());
    dlg.querySelector('[data-pin="remove"]')?.addEventListener('click', async () => {
      await authFetch('/auth/pin', { method: 'DELETE' });
      dlg.close();
      ctx.toast(ctx.get('lock.pinRemoved'));
    });
    dlg.querySelector('form').addEventListener('submit', async ev => {
      ev.preventDefault();
      const pin = dlg.querySelector('[name="pin"]').value;
      const response = await authFetch('/auth/pin', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pin })
      });
      if (response.ok) { dlg.close(); ctx.toast(ctx.get('lock.pinSaved')); }
      else showErr(ctx.get('lock.pinInvalid'));
    });
    dlg.showModal();
  });
}
