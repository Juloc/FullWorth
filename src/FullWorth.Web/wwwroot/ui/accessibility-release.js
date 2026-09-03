const labels = {
  de: { close: 'Schließen', transactionSearch: 'Buchungen durchsuchen', direction: 'Richtung', flags: 'Filter' },
  en: { close: 'Close', transactionSearch: 'Search transactions', direction: 'Direction', flags: 'Filters' }
};

function copy() { return labels[document.documentElement.lang?.startsWith('en') ? 'en' : 'de']; }
function applyAccessibilityReleaseFixes() {
  const t = copy();
  const search = document.querySelector('#tx-query');
  if (search) search.setAttribute('aria-label', t.transactionSearch);
  const direction = document.querySelector('#tx-direction');
  if (direction) direction.setAttribute('aria-label', t.direction);
  const flags = document.querySelector('#tx-flags');
  if (flags) flags.setAttribute('aria-label', t.flags);

  document.querySelectorAll('#view-transactions thead th').forEach(header => header.setAttribute('scope', 'col'));
  document.querySelectorAll('dialog button[data-close], dialog button[value="cancel"]').forEach(button => {
    if (button.hasAttribute('aria-label')) return;
    const visible = button.textContent?.trim();
    if (visible === '×' || visible === '✕' || visible === '✖') button.setAttribute('aria-label', t.close);
  });
}

new MutationObserver(applyAccessibilityReleaseFixes).observe(document.body, { childList: true, subtree: true });
new MutationObserver(applyAccessibilityReleaseFixes).observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] });
queueMicrotask(applyAccessibilityReleaseFixes);
