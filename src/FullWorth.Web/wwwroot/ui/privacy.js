// Privacy / anonymized mode (UI_UX_SPEC §5). Global: applies to every authenticated page, dialog,
// tooltip and chart via the single shared MoneyValue/SensitiveValue rendering path in money.js.
// Two MVP states: off / on. The preference persists; an optional "default on" lives in Settings.

const KEY = 'finance.privacy';
const DEFAULT_KEY = 'finance.privacy.default';
const listeners = new Set();

let on = load();

function load() {
  const session = sessionStorage.getItem(KEY);
  if (session === 'on' || session === 'off') return session === 'on';
  // No per-session choice yet: fall back to the user's "start in privacy mode" default.
  return localStorage.getItem(DEFAULT_KEY) === 'on';
}

export function isPrivate() { return on; }

export function setPrivate(next) {
  on = !!next;
  sessionStorage.setItem(KEY, on ? 'on' : 'off');
  document.body.classList.toggle('privacy-on', on);
  listeners.forEach((fn) => fn(on));
}

export function togglePrivacy() { setPrivate(!on); }

export function onPrivacyChange(fn) { listeners.add(fn); return () => listeners.delete(fn); }

export function privacyDefault() { return localStorage.getItem(DEFAULT_KEY) === 'on'; }
export function setPrivacyDefault(next) { localStorage.setItem(DEFAULT_KEY, next ? 'on' : 'off'); }

// Apply the initial state to <body> as soon as the module loads so first paint is correct.
document.body.classList.toggle('privacy-on', on);
