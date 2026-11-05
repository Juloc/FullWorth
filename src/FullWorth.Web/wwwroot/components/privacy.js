// Privacy / anonymized mode (UI_UX_SPEC §5). Global: applies to every authenticated page, dialog,
// tooltip and chart via the single shared MoneyValue/SensitiveValue rendering path in money.js.
// Two states: off / on, set by the preference in Settings (it applies from the next page load).

const DEFAULT_KEY = 'finance.privacy.default';

// Whether amounts are hidden is decided once, by the "start in privacy mode" preference in Settings.
// There is no switch in the interface: it was a button in every page's header that few people needed.
const on = localStorage.getItem(DEFAULT_KEY) === 'on';

export function isPrivate() { return on; }

export function privacyDefault() { return on; }
export function setPrivacyDefault(next) { localStorage.setItem(DEFAULT_KEY, next ? 'on' : 'off'); }

// Apply the initial state to <body> as soon as the module loads so first paint is correct.
document.body.classList.toggle('privacy-on', on);
