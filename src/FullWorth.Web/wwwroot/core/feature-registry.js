// Feature registry owns feature activation and cleanup.
// Existing render functions may return nothing; lifecycle-aware features can return an unmount callback.

export function createFeatureRegistry() {
  const features = new Map();
  let activeName = null;
  let cleanup = null;

  function register(name, refresh) {
    if (!name || typeof refresh !== 'function') {
      throw new TypeError('Feature registration requires a name and refresh function.');
    }
    features.set(name, refresh);
    return api;
  }

  function has(name) {
    return features.has(name);
  }

  async function activate(name, context) {
    const handler = features.get(name);
    if (!handler) return false;

    if (activeName !== name) {
      cleanup?.();
      cleanup = null;
      activeName = name;
    }

    const nextCleanup = await handler(context);
    if (typeof nextCleanup === 'function') {
      cleanup?.();
      cleanup = nextCleanup;
    }
    return true;
  }

  async function refresh(name, context) {
    return activate(name, context);
  }

  function unmount() {
    cleanup?.();
    cleanup = null;
    activeName = null;
  }

  const api = { register, has, activate, refresh, unmount };
  return api;
}
