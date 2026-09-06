// Feature registry keeps app.js from growing another render switch.
// A feature entry can be any async/sync refresh callback.

export function createFeatureRegistry() {
  const features = new Map();

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

  async function refresh(name, context) {
    const handler = features.get(name);
    if (!handler) return false;
    await handler(context);
    return true;
  }

  const api = { register, has, refresh };
  return api;
}
