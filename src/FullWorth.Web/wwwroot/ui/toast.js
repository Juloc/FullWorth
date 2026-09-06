// Shared transient status message controller.

export function createToast(element, { defaultDuration = 3200 } = {}) {
  let timer = null;

  function show(message, duration = defaultDuration) {
    if (!element) return;
    element.textContent = String(message ?? '');
    element.classList.add('show');
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      element.classList.remove('show');
      timer = null;
    }, duration);
  }

  function hide() {
    if (!element) return;
    if (timer) clearTimeout(timer);
    timer = null;
    element.classList.remove('show');
  }

  return { show, hide };
}
