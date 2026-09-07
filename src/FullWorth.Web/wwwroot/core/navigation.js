let handler = null;

export function installNavigation(next) {
  if (typeof next !== 'function') throw new TypeError('Navigation handler must be a function.');
  handler = next;
  return () => { if (handler === next) handler = null; };
}

export function navigate(view, options = {}) {
  if (!handler) throw new Error('Navigation is not initialized.');
  return handler(view, options);
}
