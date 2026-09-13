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

// Checking the box IS the selection: the transactions feature listens for the change event and owns
// everything that follows - the row styling, the selection bar and the mobile selection mode. This
// layer used to re-apply all three from a MutationObserver on the list, which is the kind of
// after-the-fact repair the frontend architecture guard forbids.
function setTransactionSelected(row, selected) {
  const input = transactionCheckbox(row);
  if (!input || input.checked === selected) return;
  input.checked = selected;
  input.dispatchEvent(new Event('change', { bubbles: true }));
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
}
