import { ButtonRole, buttonClass } from './buttons.js';
import { createDialog } from './dialog.js';

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, char => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    "'": '&#39;',
    '"': '&quot;'
  }[char]));
}

export function confirmMessage({
  message,
  title = 'Confirm',
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  destructive = false,
  create = html => createDialog(html)
} = {}) {
  return new Promise(resolve => {
    const dlg = create(`<div class="dialog-card confirm-dialog">
      <h2>${escapeHtml(title)}</h2>
      <p class="row-sub">${escapeHtml(message)}</p>
      <div class="dialog-actions">
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${escapeHtml(cancelLabel)}</button>
        <button type="button" class="${buttonClass(destructive ? ButtonRole.Danger : ButtonRole.Primary)}" data-confirm>${escapeHtml(confirmLabel)}</button>
      </div>
    </div>`);

    let settled = false;
    const finish = value => {
      if (settled) return;
      settled = true;
      resolve(value);
      if (dlg.open) dlg.close();
    };

    dlg.querySelector('[data-cancel]').addEventListener('click', () => finish(false));
    dlg.querySelector('[data-confirm]').addEventListener('click', () => finish(true));
    dlg.addEventListener('close', () => finish(false));
    dlg.showModal();
  });
}

export function alertMessage({
  message,
  title = 'Notice',
  confirmLabel = 'OK',
  create = html => createDialog(html)
} = {}) {
  return new Promise(resolve => {
    const dlg = create(`<div class="dialog-card confirm-dialog">
      <h2>${escapeHtml(title)}</h2>
      <p class="row-sub">${escapeHtml(message)}</p>
      <div class="dialog-actions">
        <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-confirm>${escapeHtml(confirmLabel)}</button>
      </div>
    </div>`);

    let settled = false;
    const finish = () => {
      if (settled) return;
      settled = true;
      resolve();
      if (dlg.open) dlg.close();
    };

    dlg.querySelector('[data-confirm]').addEventListener('click', finish);
    dlg.addEventListener('close', finish);
    dlg.showModal();
  });
}

export function promptMessage({
  message,
  title = 'Input',
  confirmLabel = 'Save',
  cancelLabel = 'Cancel',
  initialValue = '',
  placeholder = '',
  required = false,
  maxLength = 250,
  create = html => createDialog(html)
} = {}) {
  return new Promise(resolve => {
    const dlg = create(`<form class="dialog-card confirm-dialog" method="dialog">
      <h2>${escapeHtml(title)}</h2>
      <label>${escapeHtml(message)}
        <input name="value" value="${escapeHtml(initialValue)}" placeholder="${escapeHtml(placeholder)}" ${required ? 'required' : ''} maxlength="${Number(maxLength) || 250}">
      </label>
      <div class="dialog-actions">
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${escapeHtml(cancelLabel)}</button>
        <button type="submit" class="${buttonClass(ButtonRole.Primary)}">${escapeHtml(confirmLabel)}</button>
      </div>
    </form>`);

    const form = dlg.querySelector('form');
    const input = dlg.querySelector('input[name="value"]');
    let settled = false;
    const finish = value => {
      if (settled) return;
      settled = true;
      resolve(value);
      if (dlg.open) dlg.close();
    };

    dlg.querySelector('[data-cancel]').addEventListener('click', () => finish(null));
    form.addEventListener('submit', event => {
      event.preventDefault();
      const value = input.value.trim();
      if (required && !value) {
        input.focus();
        return;
      }
      finish(value);
    });
    dlg.addEventListener('close', () => finish(null));
    dlg.showModal();
    queueMicrotask(() => input.focus());
  });
}

// App-context adapter used by normal feature modules.
export function confirmDialog(ctx, message, opts = {}) {
  return confirmMessage({
    message,
    title: opts.title || ctx.get('common.confirmTitle'),
    confirmLabel: opts.confirmLabel || ctx.get('common.confirm'),
    cancelLabel: opts.cancelLabel || ctx.get('common.cancel'),
    destructive: opts.destructive === true,
    create: html => ctx.dialog(html)
  });
}
