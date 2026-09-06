import { ButtonRole, buttonClass } from './buttons.js';
// Themed confirmation dialog (§26/§30) replacing native window.confirm(), which renders as an
// unstyled browser prompt and cannot be skinned for the app's light/dark theme. Destructive actions
// render the confirm button as plain red text with no border/fill (Design System §06 "zerstörend:
// nur als Text, nie als Fläche" — never a filled surface, not even an outline).

export function confirmDialog(ctx, message, opts = {}) {
  return new Promise(resolve => {
    const title = opts.title || ctx.get('common.confirmTitle');
    const confirmLabel = opts.confirmLabel || ctx.get('common.confirm');
    const cancelLabel = opts.cancelLabel || ctx.get('common.cancel');
    const dlg = ctx.dialog(`<div class="dialog-card confirm-dialog">
      <h2>${ctx.esc(title)}</h2>
      <p class="row-sub">${ctx.esc(message)}</p>
      <div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(cancelLabel)}</button><button type="button" class="${buttonClass(opts.destructive ? ButtonRole.Danger : ButtonRole.Primary)}" data-confirm>${ctx.esc(confirmLabel)}</button></div>
    </div>`);
    let settled = false;
    const finish = value => {
      if (settled) return;
      settled = true;
      resolve(value);
      dlg.close();
    };
    dlg.querySelector('[data-cancel]').addEventListener('click', () => finish(false));
    dlg.querySelector('[data-confirm]').addEventListener('click', () => finish(true));
    // Dismissing via Esc (the native <dialog> cancel event) resolves to false, not a silent no-op.
    dlg.addEventListener('close', () => finish(false));
    dlg.showModal();
  });
}
