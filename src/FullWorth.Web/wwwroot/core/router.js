// Kleiner Routenhelfer. Das Zeichnen und Einhängen bleibt bei der Feature-Registry.
//
// Jede Seite hat eine Adresse. Der Regelfall ist /<seite>, und die Startseite ist /. Eine Seite, die
// unter einer anderen liegt, nennt ihre Adresse selbst — /settings/security/passkeys gehört sichtbar
// unter Einstellungen, und eine Adresse, die das nicht zeigt, wäre eine Adresse, die lügt.

export function createRouter({ views, defaultView = 'dashboard', paths = {} }) {
  const known = new Set(views);
  const viewPath = new Map([[defaultView, '/']]);
  views.forEach(view => {
    if (view !== defaultView) viewPath.set(view, paths[view] ?? '/' + view);
  });

  // Die längste passende Adresse gewinnt: /settings/security/passkeys ist auch ein Treffer für
  // /settings, und ohne diese Reihenfolge landete man auf der Elternseite.
  const byLength = [...viewPath].filter(([view]) => view !== defaultView)
    .sort((left, right) => right[1].length - left[1].length);

  function pathForView(view) {
    return viewPath.get(view) || '/';
  }

  function viewFromPath(pathname) {
    const clean = '/' + String(pathname || '/').replace(/^\/+|\/+$/g, '');
    const nested = byLength.find(([, path]) => clean === path);
    if (nested) return nested[0];

    const segment = clean.slice(1).split('/')[0];
    return segment && known.has(segment) ? segment : defaultView;
  }

  function write(view, { query = '', replace = false, state = null, path = null } = {}) {
    const suffix = query ? (String(query).startsWith('?') ? String(query) : '?' + String(query)) : '';
    const base = path || pathForView(view);
    const url = base + suffix;
    history[replace ? 'replaceState' : 'pushState'](state, '', url);
    return url;
  }

  return {
    pathForView,
    viewFromPath,
    write
  };
}
