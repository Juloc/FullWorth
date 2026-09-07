const bus = new EventTarget();

export function onAppEvent(type, handler) {
  const listener = event => handler(event.detail);
  bus.addEventListener(type, listener);
  return () => bus.removeEventListener(type, listener);
}

export function emitAppEvent(type, detail = undefined) {
  bus.dispatchEvent(new CustomEvent(type, { detail }));
}
