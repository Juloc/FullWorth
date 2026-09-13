/**
 * The transient message strip.
 *
 * The whole reason this file is more than four lines: a `<dialog>` opened with `showModal()` renders
 * in the browser's TOP LAYER, which sits above every z-index there is. A toast is a plain positioned
 * element, so it was painted underneath — dimmed behind the backdrop on a desktop, and completely
 * hidden on a phone, where the dialog covers the whole screen.
 *
 * That mattered more than it sounds: over 170 places report a failure from inside a dialog with a
 * toast. Every one of them was invisible exactly when it was needed most.
 *
 * The fix is to put the toast in the top layer too. `popover` is the only way to get there without
 * being a modal dialog, and a manual popover neither takes focus nor closes on an outside click.
 */

// One controller per element. Three files used to reach for #toast and drive it by hand, each with
// its own timer; with a popover involved that is worse than untidy, because an element that is not
// shown as a popover is display:none and a hand-written classList.add('show') would display nothing
// at all. Handing every caller the same controller is what keeps that from being possible.
const controllers = new WeakMap();

export function createToast(element, { defaultDuration = 3200 } = {}) {
  if (element && controllers.has(element)) return controllers.get(element);

  let timer = null;

  // Feature-detected once. Where popover is missing the toast behaves exactly as it did before -
  // fine on its own, still behind a modal dialog, which is better than not showing at all.
  const supportsPopover =
    !!element &&
    typeof element.showPopover === 'function' &&
    typeof HTMLElement !== 'undefined' &&
    Object.prototype.hasOwnProperty.call(HTMLElement.prototype, 'popover');

  if (supportsPopover && !element.hasAttribute('popover')) {
    // "manual", not "auto": an auto popover light-dismisses on the next click anywhere, which for a
    // message that appears while somebody is typing means it vanishes as they carry on.
    element.setAttribute('popover', 'manual');
  }

  function enterTopLayer() {
    if (!supportsPopover) return;
    try {
      // Re-showing lifts it above a dialog that opened after the toast did. Showing an already-open
      // popover throws, so it is closed first.
      if (element.matches(':popover-open')) element.hidePopover();
      element.showPopover();
    } catch {
      // A detached element refuses. Not worth failing a message over.
    }
  }

  function leaveTopLayer() {
    if (!supportsPopover) return;
    try {
      if (element.matches(':popover-open')) element.hidePopover();
    } catch {
      // Same.
    }
  }

  function show(message, duration = defaultDuration) {
    if (!element) return;
    element.textContent = String(message ?? '');
    // The class BEFORE the top layer, and the order is not cosmetic: showing the popover first left
    // the element at opacity 0 and it stayed there - measured in the browser, invisible in every way
    // a test could have checked, because it was in the top layer and reported as open.
    element.classList.add('show');
    enterTopLayer();
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      element.classList.remove('show');
      timer = null;
      // After the fade, not before: leaving the top layer sets display:none at once, and the message
      // would vanish mid-transition instead of fading.
      setTimeout(leaveTopLayer, 250);
    }, duration);
  }

  function hide() {
    if (!element) return;
    if (timer) clearTimeout(timer);
    timer = null;
    element.classList.remove('show');
    leaveTopLayer();
  }

  const controller = { show, hide };
  if (element) controllers.set(element, controller);
  return controller;
}

/**
 * The app's one toast, for modules that are not handed a context object. Same controller and the
 * same timer as the app's own, because it is the same element.
 */
export function showToast(message, duration) {
  createToast(document.getElementById('toast')).show(message, duration);
}
