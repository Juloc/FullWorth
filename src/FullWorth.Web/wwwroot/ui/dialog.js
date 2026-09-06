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

function installMobileSwipe(dlg, card) {
  const head = card?.querySelector('.fw-dialog-head,.panel-head');
  if (!head) return;
  let pointerId = null, startY = 0, lastY = 0;
  head.addEventListener('pointerdown', event => {
    if (!matchMedia('(max-width:767px)').matches || event.target.closest('button,a,input,select,textarea')) return;
    if (!dlg.classList.contains('drawer') && !dlg.classList.contains('fw-dialog--sheet') && !card.classList.contains('tx-detail')) return;
    pointerId = event.pointerId; startY = lastY = event.clientY;
    head.setPointerCapture?.(pointerId); card.classList.add('fw-dialog-swiping');
  });
  head.addEventListener('pointermove', event => {
    if (event.pointerId !== pointerId) return;
    lastY = event.clientY;
    card.style.transform = `translateY(${Math.max(0, lastY - startY)}px)`;
  });
  const finish = event => {
    if (event.pointerId !== pointerId) return;
    const delta = Math.max(0, lastY - startY);
    pointerId = null; card.classList.remove('fw-dialog-swiping'); card.style.transform = '';
    if (delta > 90 && dlg.open) dlg.close('cancel');
  };
  head.addEventListener('pointerup', finish);
  head.addEventListener('pointercancel', finish);
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
    installMobileSwipe(dlg, card);
  }

  dlg.addEventListener('close', () => dlg.remove(), { once: true });
  return dlg;
}
