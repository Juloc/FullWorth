import { api as sharedApi } from '../../core/services.js';
// Lightweight product/savings enhancer. It deliberately augments the existing purchases workspace
// instead of owning navigation or financial state. All data is fetched from the canonical backend APIs;
// unconfirmed OCR/import drafts are already excluded there from product price observations.

const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));
const text = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en;
let productListBusy = false;
let analyticsBusy = false;
let dialogBusy = false;

const api=path=>sharedApi(path);

function money(value, currency = 'EUR') {
  const amount = Number(value || 0);
  try { return new Intl.NumberFormat(document.documentElement.lang || 'de', { style: 'currency', currency }).format(amount); }
  catch { return `${amount.toFixed(2)} ${currency}`; }
}

function savingsPercent(original, effective) {
  const o = Number(original);
  const e = Number(effective);
  if (!(o > 0) || !(e >= 0) || e >= o) return null;
  return ((o - e) / o) * 100;
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
  const id = dialog.dataset.productId;
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
    // /purchase-analytics/discount-analytics statt /savings (#177).
    //
    // Es waren zwei Maschinen fuer dieselbe Frage, und die genutzte war die aermere: sie kannte
    // Gesamtbetrag, Artikel- und Warenkorbanteil und eine Aufschluesselung nach Art. Die andere
    // stand fertig und ohne Aufrufer daneben und kann zusaetzlich nach HAENDLER, Produkt und
    // Kategorie aufschluesseln und sagt, wie viele Kaeufe ueberhaupt einen Rabatt hatten. /savings
    // ist mit diesem Umzug geloescht; die Zahl je Art ("7 x Coupon") hat die andere dafuer bekommen.
    const savings = await api('api/purchase-analytics/discount-analytics');
    if (!savings) return;
    const card = document.createElement('div');
    card.className = 'pa-card pa-savings-card';
    card.dataset.savingsCard = '';
    const currency = savings.baseCurrency || 'EUR';
    const breakdown = (items, limit = 8) => (items || []).slice(0, limit).map(row =>
      `<div class="pa-analytics-row"><div><strong>${esc(row.name || 'other')}</strong><span>${Number(row.count || 0)}×</span></div><strong class="pa-saving">${esc(money(row.amount, currency))}</strong></div>`).join('');
    const rows = breakdown(savings.byType);
    // Nach Haendler ist die Aufschluesselung, die die alte Antwort gar nicht hatte - und die
    // interessanteste: sie sagt, WO es die Rabatte gibt.
    const merchantRows = breakdown(savings.byMerchant, 5);
    card.innerHTML = `<div class="pa-card-head"><h3>${esc(text('Erkannte Ersparnis', 'Recognized savings'))}</h3><strong class="pa-saving">${esc(money(savings.totalDiscountAmount, currency))}</strong></div>`
      + `<div class="pa-metrics"><div><span>${esc(text('Artikelrabatte', 'Item discounts'))}</span><strong>${esc(money(savings.itemDiscountAmount, currency))}</strong></div>`
      + `<div><span>${esc(text('Warenkorb', 'Basket'))}</span><strong>${esc(money(savings.basketOrUnallocatedDiscountAmount, currency))}</strong></div>`
      + `<div><span>${esc(text('Käufe mit Rabatt', 'Purchases with a discount'))}</span><strong>${Number(savings.purchasesWithDiscount || 0)} / ${Number(savings.purchaseCount || 0)}</strong></div></div>`
      + (rows || `<div class="state-empty">${esc(text('Keine bestätigten Rabatte im Zeitraum.', 'No confirmed discounts in this period.'))}</div>`)
      + (merchantRows ? `<div class="pa-card-head"><h3>${esc(text('Nach Händler', 'By merchant'))}</h3></div>${merchantRows}` : '')
      + (savings.incomplete ? `<div class="warn-text">${esc(text('Einige Fremdwährungswerte konnten noch nicht umgerechnet werden.', 'Some foreign-currency values could not yet be converted.'))}</div>` : '');
    grid.prepend(card);
  } catch { /* supplementary UI only */ }
  finally { analyticsBusy = false; }
}

export async function refreshPurchasePriceInsights(detail = {}) {
  const panel = document.querySelector('.purchase-advanced-panel:not([hidden])');
  const tab = detail.tab || document.querySelector('[data-pa-tab].active')?.dataset.paTab;
  if (panel && tab === 'products') await decorateProductRows(panel);
  if (panel && tab === 'analytics') await decorateAnalytics(panel);
  const dialog = detail.dialog;
  if (dialog?.querySelector('.pa-product-detail')) await decorateProductDialog(dialog);
}
