import { initializePasskeyManagement } from './passkeys.js';

const preferences = {
  language: localStorage.getItem('finance.language') || ((navigator.language || 'de').startsWith('de') ? 'de' : 'en'),
  theme: localStorage.getItem('finance.theme') || 'system'
};

const media = matchMedia('(prefers-color-scheme: dark)');
let messages = {};

async function boot() {
  applyTheme();
  await loadMessages();
  renderTranslations();
  initializePasskeyManagement({
    root: document.querySelector('[data-passkey-management]'),
    message: path => get(path === 'passkeys.registering' ? 'passkeys.adding' : path),
    locale: preferences.language
  });
}

async function loadMessages() {
  const response = await fetch(`/locales/${preferences.language}.json`, { cache: 'no-store' });
  if (!response.ok) throw new Error(`Locale request failed: ${response.status}`);
  messages = await response.json();
  document.documentElement.lang = preferences.language;
}

function get(path) {
  return path.split('.').reduce((value, key) => value?.[key], messages) || path;
}

function renderTranslations() {
  document.querySelectorAll('[data-i18n]').forEach(element => {
    element.textContent = get(element.dataset.i18n);
  });
  document.querySelectorAll('[data-i18n-placeholder]').forEach(element => {
    element.placeholder = get(element.dataset.i18nPlaceholder);
  });
  document.title = `${get('passkeys.title')} · FullWorth`;
}

function applyTheme() {
  const actual = preferences.theme === 'system'
    ? (media.matches ? 'dark' : 'light')
    : preferences.theme;
  document.documentElement.dataset.theme = actual;
}

media.addEventListener('change', () => {
  if (preferences.theme === 'system') applyTheme();
});

boot().catch(() => {
  const status = document.querySelector('#passkey-management-status');
  if (status) {
    status.textContent = 'Passkey settings are temporarily unavailable.';
    status.setAttribute('role', 'alert');
    status.hidden = false;
  }
});
