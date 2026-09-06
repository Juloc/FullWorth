// Small route helper. Rendering/mounting stays with the feature registry.

export function createRouter({ views, defaultView = 'dashboard' }) {
  const known = new Set(views);
  const viewPath = new Map([[defaultView, '/']]);
  views.forEach(view => {
    if (view !== defaultView) viewPath.set(view, '/' + view);
  });

  function pathForView(view) {
    return viewPath.get(view) || '/';
  }

  function viewFromPath(pathname) {
    const segment = String(pathname || '/')
      .replace(/^\/+|\/+$/g, '')
      .split('/')[0];

    return segment && known.has(segment) ? segment : defaultView;
  }

  function write(view, { query = '', replace = false, state = null } = {}) {
    const suffix = query ? (String(query).startsWith('?') ? String(query) : '?' + String(query)) : '';
    const url = pathForView(view) + suffix;
    history[replace ? 'replaceState' : 'pushState'](state, '', url);
    return url;
  }

  return {
    pathForView,
    viewFromPath,
    write
  };
}
