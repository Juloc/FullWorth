const CLOSE_ICON = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg>';

function ensureHeader(dlg, card, closeLabel) {
  let head = card.querySelector(':scope > .panel-head') || card.querySelector('.panel-head');
  if (!head) {
    head = document.createElement('div');
    head.className = 'panel-head';
    const title = card.querySelector(':scope > h2');
    if (title) {
      card.insertBefore(head, title);
      head.appendChild(title);
    } else {
      card.prepend(head);
      head.classList.add('fw-dialog-head-minimal');
    }
  }
  head.classList.add('fw-dialog-head');

  let close = head.querySelector('[data-close], button[value="cancel"]');
  if (!close) {
    close = document.createElement('button');
    close.type = 'button';
    close.dataset.close = '';
    head.appendChild(close);
  }
  close.type = 'button';
  close.classList.add('fw-dialog-close');
  close.setAttribute('aria-label', close.getAttribute('aria-label') || closeLabel || 'Close');
  close.innerHTML = CLOSE_ICON;
  close.addEventListener('click', () => {
    if (dlg.open) dlg.close('cancel');
  });
}

export function createDialog(html, options = {}) {
  const dlg = document.createElement('dialog');
  dlg.className = 'fw-dialog';
  if (options.mobileMode === 'sheet') dlg.classList.add('fw-dialog--sheet');
  if (options.className) dlg.classList.add(...String(options.className).split(/\s+/).filter(Boolean));
  dlg.innerHTML = html;
  document.body.appendChild(dlg);

  const card = dlg.querySelector('.dialog-card');
  if (card) {
    card.classList.add('fw-dialog-card');
    ensureHeader(dlg, card, options.closeLabel);
  }

  dlg.addEventListener('close', () => dlg.remove(), { once: true });
  return dlg;
}
