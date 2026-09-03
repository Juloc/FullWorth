// Lightweight product/savings enhancer. It deliberately augments the existing purchases workspace
// instead of owning navigation or financial state. All data is fetched from the canonical backend APIs;
// unconfirmed OCR/import drafts are already excluded there from product price observations.

const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));
const text = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en;
const spaceId = () => localStorage.getItem('finance.space') || '';
let lastProductId = null;
let scanQueued = false;
let productListBusy = false;
let analyticsBusy = false;
let dialogBusy = false;

function withSpace(path) {
  const [base, query = ''] = String(path).replace(/^\//, '').split('?');
  const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', spaceId());
  return `${base}?${params}`;
}

async function api(path) {
  const response = await fetch(`/bff/backend/${withSpace(path)}`);
  if (!response.ok) throw new Error(`${response.status}`);
  return response.status === 204 ? null : response.json();
}

function money(value, currency = 'EUR') {
  const amount = Number(value || 0);
  try { return new Intl.NumberFormat(document.documentElement.lang || 'de', { style: 'currency', currency }).format(amount); }
  catch { return `${amount.toFixed(2)} ${currency}`; }
}

function ensureStyle() {
  // Production CSP forbids JS-created inline <style> blocks (style-src 'self'); load the module's
  // CSS as a same-origin linked stylesheet instead, exactly once.
  if (document.querySelector('link[data-feature-css="purchase-price-insights"]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/features/purchase-price-insights.css';
  link.dataset.featureCss = 'purchase-price-insights';
  document.head.appendChild(link);
}

function savingsPercent(original, effective) {
  const o = Number(original);
  const e = Number(effective);
  if (!(o > 0) || !(e >= 0) || e >= o) return null;
  return ((o - e) / o) * 100;
}

function latestResourceProductId() {
  const entries = performance.getEntriesByType?.('resource') || [];
  const pattern = /\/api\/products\/([0-9a-f-]{36})(?:\?|$)/i;
  for (let i = entries.length - 1; i >= 0; i--) {
    const match = String(entries[i].name || '').match(pattern);
    if (match) return match[1];
  }
  return null;
}

async function decorateProductRows(panel) {
  if (productListBusy) return;
  const rows = [...panel.querySelectorAll('[data-product-id]')].filter(row => !row.dataset.paPriceInsightDone);
  if (rows.length === 0) return;
  productListBusy = true;
  try {
    const data = await api('api/products?limit=500');
    const byId = new Map((data?.items || []).map(item => [String(item.id), item]));
    for (const row of rows) {
      const product = byId.get(String(row.dataset.productId));
      row.dataset.paPriceInsightDone = 'true';
      if (!product || product.lastPrice == null) continue;
      const original = product.lastOriginalPrice;
      const effective = product.lastPrice;
      const saving = Number(product.lastDiscountAmount || (Number(original) > Number(effective) ? Number(original) - Number(effective) : 0));
      const percent = savingsPercent(original, effective);
      if (!(Number(original) > Number(effective)) && !(saving > 0)) continue;
      const target = row.lastElementChild || row;
      const detail = document.createElement('div');
      detail.className = 'pa-price-insight';
      detail.dataset.priceInsight = '';
      detail.innerHTML = `${original != null ? `<span class="pa-price-original">${esc(text('statt', 'was'))} ${esc(money(original, product.lastCurrency || 'EUR'))}</span>` : ''}${saving > 0 ? `<span class="pa-saving">${esc(text('gespart', 'saved'))} ${esc(money(saving, product.lastCurrency || 'EUR'))}${percent == null ? '' : ` · ${percent.toFixed(1)}%`}</span>` : ''}`;
      target.appendChild(detail);
    }
  } catch { /* supplementary UI only */ }
  finally { productListBusy = false; }
}

function decorateHistoryRows(dialog, observations) {
  const rows = [...dialog.querySelectorAll('.pa-history-row')];
  const reversed = [...(observations || [])].reverse();
  rows.forEach((row, index) => {
    if (row.querySelector('[data-history-price-insight]')) return;
    const observation = reversed[index];
    if (!observation) return;
    const effective = observation.effectivePrice ?? observation.unitPrice ?? observation.totalPrice;
    const original = observation.originalUnitPrice;
    const saving = Number(observation.discountAmount || (Number(original) > Number(effective) ? Number(original) - Number(effective) : 0));
    if (!(Number(original) > Number(effective)) && !(saving > 0)) return;
    const target = row.lastElementChild || row;
    const extra = document.createElement('div');
    extra.className = 'pa-history-extra';
    extra.dataset.historyPriceInsight = '';
    extra.innerHTML = `${original != null ? `<span class="pa-price-original">${esc(text('statt', 'was'))} ${esc(money(original, observation.currency || 'EUR'))}</span>` : ''}${saving > 0 ? `<span class="pa-saving">−${esc(money(saving, observation.currency || 'EUR'))}${observation.savingsPercent == null ? '' : ` · ${Number(observation.savingsPercent).toFixed(1)}%`}</span>` : ''}${observation.discountLabel ? `<span class="pa-chip">${esc(observation.discountLabel)}</span>` : ''}`;
    target.appendChild(extra);
  });
}

async function decorateProductDialog(dialog) {
  if (dialogBusy || dialog.dataset.paPriceInsightsMounted === 'true') return;
  const root = dialog.querySelector('.pa-product-detail');
  if (!root) return;
  const id = lastProductId || latestResourceProductId();
  if (!id) return;
  dialogBusy = true;
  try {
    const data = await api(`api/products/${id}`);
    const history = data?.history || {};
    const observations = history.observations || [];
    const latest = observations.length ? observations[observations.length - 1] : null;
    const card = document.createElement('div');
    card.className = 'pa-card pa-price-insights-card';
    card.dataset.priceInsightsCard = '';
    if (!latest) {
      card.innerHTML = `<div class="pa-card-head"><h3>${esc(text('Bestätigte Preise', 'Confirmed prices'))}</h3></div><div class="state-empty">${esc(text('Noch keine bestätigte Preisbeobachtung. OCR- und Import-Entwürfe werden hier bewusst nicht berücksichtigt.', 'No confirmed price observation yet. OCR and import drafts are intentionally excluded here.'))}</div>`;
    } else {
      const effective = latest.effectivePrice ?? latest.unitPrice ?? latest.totalPrice;
      const original = latest.originalUnitPrice;
      const saving = Number(latest.discountAmount || (Number(original) > Number(effective) ? Number(original) - Number(effective) : 0));
      const basis = history.latestComparisonBasis === 'reference'
        ? text('Preisänderung nutzt den Normal-/Referenzpreis, damit Aktionen die Teuerung nicht verfälschen.', 'Price change uses the normal/reference price so promotions do not distort inflation.')
        : text('Preisänderung nutzt den tatsächlich gezahlten Effektivpreis.', 'Price change uses the effective price actually paid.');
      card.innerHTML = `<div class="pa-card-head"><h3>${esc(text('Preis & Ersparnis', 'Price & savings'))}</h3><span class="pa-chip ok">${esc(text('nur bestätigt', 'confirmed only'))}</span></div><div class="pa-metrics"><div><span>${esc(text('Effektivpreis', 'Effective price'))}</span><strong>${esc(money(effective, latest.currency || 'EUR'))}</strong></div><div><span>${esc(text('Normalpreis', 'Reference price'))}</span><strong>${original == null ? '—' : esc(money(original, latest.currency || 'EUR'))}</strong></div><div><span>${esc(text('Gespart', 'Saved'))}</span><strong class="${saving > 0 ? 'pa-saving' : ''}">${saving > 0 ? esc(money(saving, latest.currency || 'EUR')) : '—'}</strong></div><div><span>${esc(text('Rabatt', 'Discount'))}</span><strong>${latest.savingsPercent == null ? '—' : `${Number(latest.savingsPercent).toFixed(1)}%`}</strong></div></div><div class="pa-reference-note">${esc(basis)}${latest.discountLabel ? ` · ${esc(latest.discountLabel)}` : ''}</div>`;
    }
    const header = root.querySelector('.panel-head');
    if (header) header.insertAdjacentElement('afterend', card); else root.prepend(card);
    decorateHistoryRows(dialog, observations);
    dialog.dataset.paPriceInsightsMounted = 'true';
  } catch { /* supplementary UI only */ }
  finally { dialogBusy = false; }
}

async function decorateAnalytics(panel) {
  if (analyticsBusy || panel.querySelector('[data-savings-card]')) return;
  const grid = panel.querySelector('.pa-analytics-grid');
  if (!grid) return;
  analyticsBusy = true;
  try {
    const savings = await api('api/purchase-analytics/savings');
    if (!savings) return;
    const card = document.createElement('div');
    card.className = 'pa-card pa-savings-card';
    card.dataset.savingsCard = '';
    const rows = (savings.byType || []).slice(0, 8).map(row => `<div class="pa-analytics-row"><div><strong>${esc(row.type || 'other')}</strong><span>${Number(row.count || 0)}×</span></div><strong class="pa-saving">${esc(money(row.amount, savings.currency || 'EUR'))}</strong></div>`).join('');
    card.innerHTML = `<div class="pa-card-head"><h3>${esc(text('Erkannte Ersparnis', 'Recognized savings'))}</h3><strong class="pa-saving">${esc(money(savings.totalSavings, savings.currency || 'EUR'))}</strong></div><div class="pa-metrics"><div><span>${esc(text('Artikelrabatte', 'Item discounts'))}</span><strong>${esc(money(savings.itemLinkedSavings, savings.currency || 'EUR'))}</strong></div><div><span>${esc(text('Warenkorb', 'Basket'))}</span><strong>${esc(money(savings.basketSavings, savings.currency || 'EUR'))}</strong></div></div>${rows || `<div class="state-empty">${esc(text('Keine bestätigten Rabatte im Zeitraum.', 'No confirmed discounts in this period.'))}</div>`}${savings.incompleteFx ? `<div class="warn-text">${esc(text('Einige Fremdwährungswerte konnten noch nicht umgerechnet werden.', 'Some foreign-currency values could not yet be converted.'))}</div>` : ''}`;
    grid.prepend(card);
  } catch { /* supplementary UI only */ }
  finally { analyticsBusy = false; }
}

function scan() {
  scanQueued = false;
  ensureStyle();
  const panel = document.querySelector('.purchase-advanced-panel:not([hidden])');
  const tab = document.querySelector('[data-pa-tab].active')?.dataset.paTab;
  if (panel && tab === 'products') void decorateProductRows(panel);
  if (panel && tab === 'analytics') void decorateAnalytics(panel);
  document.querySelectorAll('dialog.pa-dialog').forEach(dialog => {
    if (dialog.querySelector('.pa-product-detail')) void decorateProductDialog(dialog);
  });
}

function scheduleScan() {
  if (scanQueued) return;
  scanQueued = true;
  queueMicrotask(scan);
}

document.addEventListener('click', event => {
  const product = event.target.closest?.('[data-product-id]');
  if (product?.dataset.productId) lastProductId = product.dataset.productId;
  scheduleScan();
}, true);

function install() {
  if (!document.body) return;
  const observer = new MutationObserver(scheduleScan);
  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['hidden', 'class'] });
  scheduleScan();
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, { once: true });
else install();
