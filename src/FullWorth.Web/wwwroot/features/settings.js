import { state } from '../core/state.js';
import { secureFetch } from '../security/secure-fetch.js';
import { createDialog } from '../ui/dialog.js';
import { openPinDialog } from '../ui/lock.js';
import { privacyDefault, setPrivacyDefault } from '../ui/privacy.js';
import { renderSharing, bindSharing } from './sharing.js';
import { downloadWealthBackup } from './wealth-portability.js';

let bound = false;

function dialog(ctx, html) {
  return createDialog(html, { closeLabel: ctx.get('common.close') });
}

async function openDeleteAccountDialog(ctx) {
  const dlg = dialog(ctx, `
    <form class="dialog-card" id="delete-account-form">
      <div class="panel-head"><h2>${ctx.get('settings.deleteAccount')}</h2></div>
      <p class="row-sub">${ctx.get('settings.deleteAccountExplain')}</p>
      <div class="form-grid">
        <label><span>${ctx.get('auth.password')}</span><input id="delete-account-password" type="password" autocomplete="current-password" required></label>
        <label class="check"><input id="delete-account-confirm" type="checkbox" required><span>${ctx.get('settings.deleteAccountConfirm')}</span></label>
      </div>
      <p id="delete-account-error" class="row-sub" hidden></p>
      <div class="dialog-actions">
        <button type="button" class="ghost" data-close>${ctx.get('common.cancel')}</button>
        <button type="submit" class="danger">${ctx.get('settings.deleteAccountAction')}</button>
      </div>
    </form>`);
  const form = dlg.querySelector('#delete-account-form');
  dlg.querySelectorAll('[data-close]').forEach(button =>
    button.addEventListener('click', () => { if (dlg.open) dlg.close('cancel'); }));
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const password = dlg.querySelector('#delete-account-password').value;
    const confirmed = dlg.querySelector('#delete-account-confirm').checked;
    const error = dlg.querySelector('#delete-account-error');
    if (!password || !confirmed) return;
    const submit = form.querySelector('button[type="submit"]');
    submit.disabled = true;
    error.hidden = true;
    try {
      const response = await secureFetch('/auth/account-deletion/request', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ currentPassword: password })
      });
      if (response.ok) {
        location.assign('/account/deletion');
        return;
      }
      const payload = await response.json().catch(() => ({}));
      error.textContent = payload.error === 'invalid_password'
        ? ctx.get('settings.deleteAccountPasswordInvalid')
        : ctx.get('settings.deleteAccountFailed');
      error.hidden = false;
    } catch {
      error.textContent = ctx.get('settings.deleteAccountFailed');
      error.hidden = false;
    } finally {
      submit.disabled = false;
    }
  });
  dlg.showModal();
}

async function openTwoFactorDialog(ctx) {
  let status;
  try {
    status = await secureFetch('/auth/two-factor/status', { cache: 'no-store' })
      .then(response => response.ok ? response.json() : Promise.reject());
  } catch {
    ctx.toast(ctx.get('common.error'));
    return;
  }

  if (status.enabled) {
    const dlg = dialog(ctx, `
      <form class="dialog-card" id="two-factor-disable-form">
        <div class="panel-head"><h2>${ctx.get('twoFactor.title')}</h2></div>
        <p class="row-sub">${ctx.get('twoFactor.enabled')}</p>
        <label><span>${ctx.get('twoFactor.code')}</span><input id="two-factor-disable-code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" required></label>
        <div class="dialog-actions"><button type="button" class="ghost" data-close>${ctx.get('common.cancel')}</button><button type="submit" class="danger">${ctx.get('twoFactor.disable')}</button></div>
      </form>`);
    dlg.querySelector('[data-close]')?.addEventListener('click', () => dlg.close());
    dlg.querySelector('form').addEventListener('submit', async event => {
      event.preventDefault();
      const code = dlg.querySelector('#two-factor-disable-code').value;
      const response = await secureFetch('/auth/two-factor/disable', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ code })
      });
      if (response.ok) {
        state.capabilities.twoFactorEnabled = false;
        dlg.close();
        ctx.toast(ctx.get('twoFactor.disabled'));
        return;
      }
      ctx.toast(ctx.get('twoFactor.invalidCode'));
    });
    dlg.showModal();
    return;
  }

  let setup;
  try {
    const response = await secureFetch('/auth/two-factor/setup', { method: 'POST' });
    if (!response.ok) throw new Error();
    setup = await response.json();
  } catch {
    ctx.toast(ctx.get('common.error'));
    return;
  }

  const dlg = dialog(ctx, `
    <form class="dialog-card" id="two-factor-enable-form">
      <div class="panel-head"><h2>${ctx.get('twoFactor.title')}</h2></div>
      <p class="row-sub">${ctx.get('twoFactor.setupHelp')}</p>
      <div class="row"><div class="row-main"><div class="row-title">${ctx.get('twoFactor.sharedKey')}</div><div class="row-sub"><code class="two-factor-key">${ctx.esc(setup.sharedKey)}</code></div></div></div>
      <label><span>${ctx.get('twoFactor.code')}</span><input id="two-factor-enable-code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" required></label>
      <div class="dialog-actions"><button type="button" class="ghost" data-close>${ctx.get('common.cancel')}</button><button type="submit">${ctx.get('twoFactor.enable')}</button></div>
    </form>`);
  dlg.querySelector('[data-close]')?.addEventListener('click', () => dlg.close());
  dlg.querySelector('form').addEventListener('submit', async event => {
    event.preventDefault();
    const code = dlg.querySelector('#two-factor-enable-code').value;
    const response = await secureFetch('/auth/two-factor/enable', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code })
    });
    if (response.ok) {
      state.capabilities.twoFactorEnabled = true;
      dlg.close();
      ctx.toast(ctx.get('twoFactor.enabledToast'));
      return;
    }
    ctx.toast(ctx.get('twoFactor.invalidCode'));
  });
  dlg.showModal();
}

export function bindSettings(ctx) {
  if (bound) return;
  bound = true;
  ctx.$('#delete-account')?.addEventListener('click', () => openDeleteAccountDialog(ctx));
  ctx.$('#admin-settings-link')?.addEventListener('click', () => location.assign('/admin'));
  ctx.$('#two-factor-settings')?.addEventListener('click', () => openTwoFactorDialog(ctx));
  ctx.$('#export-data')?.addEventListener('click', event => downloadWealthBackup(ctx, event.currentTarget));
  ctx.$('#lock-settings')?.addEventListener('click', () => openPinDialog(ctx));
  ctx.$('#privacy-default')?.addEventListener('change', event => setPrivacyDefault(event.target.checked));
  bindSharing(ctx);
}

export async function renderSettings(ctx, { accessSetup, renderBankingSettings } = {}) {
  ctx.$('#language').value = state.lang;
  ctx.$('#theme').value = state.theme;
  ctx.$('#privacy-default').checked = privacyDefault();

  await Promise.all([
    renderSharing(ctx),
    renderBankingSettings?.(ctx),
    accessSetup?.renderAiAccessSettings(),
    accessSetup?.renderCloudSettings()
  ]);
}
