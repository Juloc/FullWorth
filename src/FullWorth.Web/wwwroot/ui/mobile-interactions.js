// Mobile-first interaction layer plus small cross-app identity fallbacks. Desktop controls remain unchanged.
const MOBILE_QUERY = '(max-width: 767px)';
const LONG_PRESS_MS = 520;
const MOVE_TOLERANCE = 12;

let hold = null;
let suppressClickUntil = 0;

function isMobile() {
  return window.matchMedia(MOBILE_QUERY).matches;
}

function clearHold() {
  if (hold?.timer) window.clearTimeout(hold.timer);
  hold = null;
}

function isInlineControl(target) {
  return Boolean(target.closest('button,a,input,label,select,textarea,[contenteditable="true"],[data-cat-edit]'));
}

function transactionCheckbox(row) {
  return row?.querySelector('[data-tx-select]') || null;
}

function transactionSelectionCount() {
  return document.querySelectorAll('#transactions-body [data-tx-select]:checked').length;
}

function syncTransactionSelectionUi() {
  document.querySelectorAll('#transactions-body .tx-row').forEach(row => {
    const checked = Boolean(transactionCheckbox(row)?.checked);
    row.classList.toggle('tx-selected', checked);
    row.setAttribute('aria-selected', String(checked));
  });
  document.querySelector('#view-transactions')?.classList.toggle(
    'tx-mobile-selection-mode',
    isMobile() && transactionSelectionCount() > 0
  );
}

function setTransactionSelected(row, selected) {
  const input = transactionCheckbox(row);
  if (!input || input.checked === selected) {
    syncTransactionSelectionUi();
    return;
  }
  input.checked = selected;
  input.dispatchEvent(new Event('change', { bubbles: true }));
  queueMicrotask(syncTransactionSelectionUi);
}

function toggleTransactionSelected(row) {
  const input = transactionCheckbox(row);
  if (!input) return;
  setTransactionSelected(row, !input.checked);
}

function dashboardEditing() {
  const bar = document.querySelector('#dashboard-editbar');
  return Boolean(bar && !bar.hidden);
}

function selectDashboardWidget(id, selected = true) {
  if (!id) return;
  const card = document.querySelector(`#dashboard-grid .widget[data-id="${CSS.escape(id)}"]`);
  card?.classList.toggle('mobile-edit-selected', selected);
}

function toggleDashboardWidget(card) {
  card?.classList.toggle('mobile-edit-selected');
}

function enterDashboardEdit(widgetId) {
  if (!dashboardEditing()) {
    // The contextual header action remains the single edit-mode code path; mobile renders it as a compact
    // icon, and long-pressing a widget invokes exactly the same state transition.
    document.querySelector('#primary-action')?.click();
  }

  let attempts = 0;
  const mark = () => {
    if (!dashboardEditing() && attempts++ < 30) {
      requestAnimationFrame(mark);
      return;
    }
    selectDashboardWidget(widgetId, true);
  };
  requestAnimationFrame(mark);
}

function beginHold(event) {
  if (!isMobile() || event.pointerType === 'mouse' || !event.isPrimary) return;

  const txRow = event.target.closest('#transactions-body .tx-row');
  const widget = event.target.closest('#view-dashboard #dashboard-grid .widget');
  if (!txRow && !widget) return;
  if (isInlineControl(event.target) && !widget) return;
  if (widget && event.target.closest('.widget-controls')) return;

  clearHold();
  hold = {
    pointerId: event.pointerId,
    x: event.clientX,
    y: event.clientY,
    txRow,
    widgetId: widget?.dataset.id || null,
    timer: window.setTimeout(() => {
      suppressClickUntil = Date.now() + 700;
      if (txRow) setTransactionSelected(txRow, true);
      else if (widget) {
        if (dashboardEditing()) toggleDashboardWidget(widget);
        else enterDashboardEdit(widget.dataset.id);
      }
      navigator.vibrate?.(8);
      clearHold();
    }, LONG_PRESS_MS)
  };
}

function moveHold(event) {
  if (!hold || hold.pointerId !== event.pointerId) return;
  if (Math.abs(event.clientX - hold.x) > MOVE_TOLERANCE || Math.abs(event.clientY - hold.y) > MOVE_TOLERANCE)
    clearHold();
}

function endHold(event) {
  if (hold?.pointerId === event.pointerId) clearHold();
}

function captureMobileClick(event) {
  if (!isMobile()) return;

  const txRow = event.target.closest('#transactions-body .tx-row');
  if (txRow && !isInlineControl(event.target)) {
    if (Date.now() < suppressClickUntil) {
      event.preventDefault();
      event.stopImmediatePropagation();
      return;
    }
    if (transactionSelectionCount() > 0) {
      event.preventDefault();
      event.stopImmediatePropagation();
      toggleTransactionSelected(txRow);
      return;
    }
  }

  const widget = event.target.closest('#view-dashboard #dashboard-grid .widget');
  if (widget && dashboardEditing() && !event.target.closest('.widget-controls')) {
    event.preventDefault();
    event.stopImmediatePropagation();
    if (Date.now() >= suppressClickUntil) toggleDashboardWidget(widget);
  }
}

function syncPrimaryActionKind() {
  const button = document.querySelector('#primary-action');
  if (!button) return;
  const label = String(button.textContent || '').trim();
  const edit = /bearbeit|edit|anpass|customi[sz]/i.test(label);
  button.dataset.mobileKind = edit ? 'edit' : 'add';
  if (label) button.setAttribute('aria-label', label);
}

function monogramHue(name) {
  let hash = 0;
  for (const ch of String(name || '')) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
  return hash % 360;
}

function repairBrokenIdentities() {
  document.querySelectorAll('.fw-ident.fw-ident-failed').forEach(identity => {
    if (identity.querySelector('img') || identity.textContent.trim()) return;
    const scope = identity.closest('.fw-row,.row,.tx-row,.merchant-row,.contract-row') || identity.parentElement;
    const label = scope?.querySelector('.fw-row-title,.row-title,.tx-cp-main strong,strong')?.textContent?.trim() || '?';
    identity.textContent = (label[0] || '?').toUpperCase();
    identity.classList.remove('fw-ident-failed');
    identity.classList.add('fw-monogram');
    identity.style.setProperty('--ident-h', String(monogramHue(label)));
  });
}

export function initMobileInteractions() {
  document.addEventListener('pointerdown', beginHold, true);
  document.addEventListener('pointermove', moveHold, true);
  document.addEventListener('pointerup', endHold, true);
  document.addEventListener('pointercancel', endHold, true);
  document.addEventListener('click', captureMobileClick, true);
  document.addEventListener('contextmenu', event => {
    if (!isMobile()) return;
    if (event.target.closest('#transactions-body .tx-row,#view-dashboard #dashboard-grid .widget'))
      event.preventDefault();
  }, true);
  document.addEventListener('change', event => {
    if (event.target.matches?.('[data-tx-select]')) queueMicrotask(syncTransactionSelectionUi);
  }, true);
  document.addEventListener('click', event => {
    if (event.target.closest?.('[data-selection-clear]')) queueMicrotask(syncTransactionSelectionUi);
  }, true);

  const body = document.querySelector('#transactions-body');
  if (body) new MutationObserver(() => queueMicrotask(syncTransactionSelectionUi))
    .observe(body, { childList: true, subtree: true });

  const primary = document.querySelector('#primary-action');
  if (primary) new MutationObserver(syncPrimaryActionKind)
    .observe(primary, { childList: true, subtree: true, characterData: true });

  const main = document.querySelector('main');
  if (main) new MutationObserver(() => queueMicrotask(repairBrokenIdentities))
    .observe(main, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });

  window.matchMedia(MOBILE_QUERY).addEventListener('change', syncTransactionSelectionUi);
  syncPrimaryActionKind();
  syncTransactionSelectionUi();
  repairBrokenIdentities();
}
