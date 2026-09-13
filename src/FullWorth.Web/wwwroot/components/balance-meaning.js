// A balance row showed an amount and a date and never said WHAT the amount is. "Available" and
// "booked" differ by exactly the pending authorisations a reader is trying to account for, so a figure
// that names neither cannot be reconciled against anything the bank shows.
//
// The classification is the SERVER's: Accounts/CurrentBalances.Meaning derives it from the provider's
// balance type and ships it as `meaning` on every balance. This module only turns it into wording.
// Deliberately NOT a client-side map from balanceType — that would be one more copy of the very table
// the backend just reduced to one.

const LABEL_KEYS = Object.freeze({
  available: 'accounts.meaning_available',
  booked: 'accounts.meaning_booked',
  expected: 'accounts.meaning_expected'
});

// `recorded` (a manual anchor, an imported statement figure, a type no provider documents) has no
// label on purpose: the row already says "manuell erfasst" / "aus Import", and claiming booked or
// available for a figure that says neither would be inventing information.
export function balanceMeaningLabel(balance, get) {
  const key = LABEL_KEYS[balance?.meaning];
  if (!key) return null;
  const label = get(key);
  // get() echoes the key back when the locale lacks it — never print a key at the user.
  if (!label || label === key) return null;
  const hintKey = `${key}_hint`;
  const hint = get(hintKey);
  return { label, hint: !hint || hint === hintKey ? '' : hint };
}

// The small muted line under an amount. The caller passes its own get/esc so this module stays free of
// i18n and DOM plumbing and can be used from any surface that renders a balance.
export function balanceMeaningLine(balance, get, esc) {
  const meaning = balanceMeaningLabel(balance, get);
  if (!meaning) return '';
  const title = meaning.hint ? ` title="${esc(meaning.hint)}"` : '';
  return `<div class="amount-meaning"${title}>${esc(meaning.label)}</div>`;
}
