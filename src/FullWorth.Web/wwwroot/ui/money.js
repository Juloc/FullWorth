// Shared money + sensitive-value rendering (UI_UX_SPEC §4.2 tabular numerals, §5 privacy, §18
// multi-currency). Every monetary value in the app MUST go through money()/sensitive() so privacy
// mode and formatting stay consistent — never format or mask values ad hoc per screen.
import { isPrivate } from './privacy.js';

let locale = 'de-DE';
export function setMoneyLocale(lang) { locale = lang === 'en' ? 'en-US' : 'de-DE'; }

// A stable mask (not a CSS blur) per spec §5; keeps the currency symbol so layout stays stable.
function currencySymbol(currency) {
  try {
    return (0).toLocaleString(locale, { style: 'currency', currency, minimumFractionDigits: 0 })
      .replace(/[\d\s.,]/g, '') || currency;
  } catch { return currency || '€'; }
}

export function money(value, currency = 'EUR') {
  if (isPrivate()) return `•••• ${currencySymbol(currency)}`;
  try {
    return new Intl.NumberFormat(locale, { style: 'currency', currency }).format(Number(value || 0));
  } catch {
    return `${Number(value || 0).toFixed(2)} ${currency}`;
  }
}

// A converted secondary amount shown under a native one (spec §18); masked together with money.
export function converted(value, currency) {
  if (isPrivate()) return `•••• ${currencySymbol(currency)}`;
  return money(value, currency);
}

// Account identifiers follow a Finanzguru-style hierarchy: a real external suffix (normally IBAN)
// is shown masked; accounts without one receive a stable app-local #code from the backend. If two
// external suffixes collide, the backend appends a #code after " · ". That non-sensitive code stays
// visible in privacy mode so two same-named/same-bank accounts remain distinguishable while the bank
// identifier itself is hidden.
export function maskIdentifier(value) {
  if (!value) return '';
  const text = String(value).trim();
  if (!text) return '';

  // No bank/card/provider suffix available: this is the stable local account code.
  if (text.startsWith('#')) return `ID ${text}`;

  const separator = ' · #';
  const marker = text.indexOf(separator);
  const disambiguator = marker >= 0 ? text.slice(marker) : '';
  return isPrivate() ? `••••${disambiguator}` : `•••• ${text}`;
}

// A percentage that must hide under privacy (investment perf etc.). Kept text-only.
export function percent(value) {
  if (isPrivate()) return '••%';
  const n = Number(value || 0);
  return `${n > 0 ? '+' : ''}${n.toFixed(1)}%`;
}
