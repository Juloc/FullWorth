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

// Vorher stand hier fest verdrahtet "#transactions-body [data-tx-select]" - ein Baustein unter
// components/, der eine ganz bestimmte Seite und ihre Attribute kennt. Das ist genau das, was
// components/ laut CLAUDE.md nicht darf ("kennt weder eine Seite noch den Server"), und es haette
// jede weitere Seite mit Long-Press-Mehrfachauswahl gezwungen, entweder denselben Seiten-Namen zu
// verwenden oder diesen Baustein selbst zu aendern. Jetzt meldet sich die Seite selbst an
// (registerRowSelection) und bringt drei Dinge mit: woran eine Zeile zu erkennen ist, wie man aus
// einer Zeile ihre Id liest, und wo der Auswahlzustand lebt (eine selection-list.js-Instanz) - dieser
// Baustein fuehrt danach nur noch Zeit (wie lange gehalten, wie weit bewegt) und ruft die
// Seiten-eigene Logik auf, ohne je "Buchung" oder "tx-select" zu lesen.
let rowSelection = null;

/**
 * @param {{rowSelector: string, getId: (row: Element) => string|null, list: {isSelected, setSelected, toggle, count}}} config
 */
export function registerRowSelection(config) {
  rowSelection = config;
}

function selectableRow(target) {
  if (!rowSelection) return null;
  return target.closest(rowSelection.rowSelector);
}

// Das Setzen selbst (state.setSelected in selection-list.js) uebernimmt auch das dispatch'te
// change-Event auf der gebundenen Checkbox - dieselbe Regie, die vorher direkt hier stand
// (input.checked=...; dispatchEvent(...)), jetzt an einer Stelle fuer jede Seite, die sie braucht.
function setRowSelected(row, selected) {
  const id = rowSelection?.getId(row);
  if (id == null) return;
  rowSelection.list.setSelected(id, selected);
}

function toggleRowSelected(row) {
  const id = rowSelection?.getId(row);
  if (id == null) return;
  rowSelection.list.toggle(id);
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

  const selRow = selectableRow(event.target);
  const widget = event.target.closest('#view-dashboard #dashboard-grid .widget');
  if (!selRow && !widget) return;
  if (isInlineControl(event.target) && !widget) return;
  if (widget && event.target.closest('.widget-controls')) return;

  clearHold();
  hold = {
    pointerId: event.pointerId,
    x: event.clientX,
    y: event.clientY,
    selRow,
    widgetId: widget?.dataset.id || null,
    timer: window.setTimeout(() => {
      suppressClickUntil = Date.now() + 700;
      if (selRow) setRowSelected(selRow, true);
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

  const selRow = selectableRow(event.target);
  if (selRow && !isInlineControl(event.target)) {
    if (Date.now() < suppressClickUntil) {
      event.preventDefault();
      event.stopImmediatePropagation();
      return;
    }
    if (rowSelection.list.count > 0) {
      event.preventDefault();
      event.stopImmediatePropagation();
      toggleRowSelected(selRow);
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
    if (selectableRow(event.target) || event.target.closest('#view-dashboard #dashboard-grid .widget'))
      event.preventDefault();
  }, true);
}
