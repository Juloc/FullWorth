// Shared locale loader and translation lookup.

export function createI18n({ state, fetchImpl } = {}) {
  if (!state) throw new TypeError('createI18n requires app state.');
  const fetcher = fetchImpl || ((input, init) => window.fetch(input, init));

  function get(path) {
    return String(path || '')
      .split('.')
      .reduce((value, key) => value?.[key], state.messages) || path;
  }

  async function load(language = state.lang) {
    const response = await fetcher(`/locales/${language}.json`, { cache: 'no-store' });
    if (!response.ok) throw new Error(`Locale load failed: ${response.status}`);
    state.messages = await response.json();
    state.lang = language;
    document.documentElement.lang = language;
    return state.messages;
  }

  function apply(root = document) {
    root.querySelectorAll?.('[data-i18n]').forEach(element => {
      element.textContent = get(element.dataset.i18n);
    });
    root.querySelectorAll?.('[data-i18n-placeholder]').forEach(element => {
      element.placeholder = get(element.dataset.i18nPlaceholder);
    });
    root.querySelectorAll?.('[data-i18n-title]').forEach(element => {
      element.title = get(element.dataset.i18nTitle);
    });
  }

  return { get, load, apply };
}
