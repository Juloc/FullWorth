// Advanced confirmed-purchase insights. This augments the existing analytics grid and never owns
// navigation or financial state. All calculations live in the backend so mobile/desktop show the same data.

const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));
const isDe = () => (document.documentElement.lang || 'de').toLowerCase().startsWith('de');
const text = (de, en) => isDe() ? de : en;
const spaceId = () => localStorage.getItem('finance.space') || '';
let busy = false;
let queued = false;

function withSpace(path) {
  const [base, query = ''] = String(path).replace(/^\//, '').split('?');
  const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', spaceId());
  return `${base}?${params}`;
}

async function api(path) {
  const response = await fetch(`/bff/backend/${withSpace(path)}`);
  if (!response.ok) throw new Error(`${response.status}`);
  return response.json();
}

function money(value, currency = 'EUR') {
  try { return new Intl.NumberFormat(document.documentElement.lang || 'de', { style: 'currency', currency }).format(Number(value || 0)); }
  catch { return `${Number(value || 0).toFixed(2)} ${currency}`; }
}

function date(value) {
  if (!value) return '—';
  try { return new Intl.DateTimeFormat(document.documentElement.lang || 'de').format(new Date(`${String(value).slice(0, 10)}T12:00:00`)); }
  catch { return String(value); }
}

function pct(value) {
  if (value == null || Number.isNaN(Number(value))) return '—';
  const n = Number(value);
  return `${n > 0 ? '+' : ''}${n.toFixed(1)}%`;
}

function ensureStyle() {
  // Production CSP forbids JS-created inline <style> blocks (style-src 'self'); load the module's
  // CSS as a same-origin linked stylesheet instead, exactly once.
  if (document.querySelector('link[data-feature-css="purchase-advanced-insights"]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/features/purchase-advanced-insights.css';
  link.dataset.featureCss = 'purchase-advanced-insights';
  document.head.appendChild(link);
}

function inflationCard(data) {
  const card = document.createElement('div');
  card.className = 'pa-card pa-advanced-insight';
  card.dataset.advancedInsight = 'inflation';
  const rows = (data?.products || []).slice(0, 6).map(row => {
    const change = Number(row.changePercent || 0);
    return `<div class="pa-analytics-row"><div><strong>${esc(row.productName)}</strong><span>${esc(row.firstMerchant || '')} → ${esc(row.latestMerchant || '')} · ${esc(row.unit || '')}</span></div><strong class="${change > 0 ? 'positive' : change < 0 ? 'negative' : ''}">${esc(pct(change))}</strong></div>`;
  }).join('');
  card.innerHTML = `<div class="pa-card-head"><div><h3>${esc(text('Persönliche Preisentwicklung', 'Personal price trend'))}</h3><div class="pa-insight-note">${esc(text('Bestätigte gleiche Produkte, nach deinen Ausgaben gewichtet.', 'Confirmed matching products, weighted by your spend.'))}</div></div><strong class="pa-insight-kpi">${esc(pct(data?.personalInflationPercent))}</strong></div><div class="pa-chip">${Number(data?.trackedProducts || 0)} ${esc(text('Produkte verglichen', 'products compared'))}</div>${rows || `<div class="state-empty">${esc(text('Noch nicht genug wiederholte Produktkäufe.', 'Not enough repeated product purchases yet.'))}</div>`}${data?.incompleteFx ? `<div class="warn-text">${esc(text('Einige Fremdwährungswerte fehlen.', 'Some FX conversions are missing.'))}</div>` : ''}`;
  return card;
}

function basketCard(data) {
  const card = document.createElement('div');
  card.className = 'pa-card pa-advanced-insight';
  card.dataset.advancedInsight = 'basket';
  const months = data?.months || [];
  const latest = months.at(-1);
  const rows = months.slice(-6).reverse().map(row => `<div class="pa-analytics-row"><div><strong>${esc(row.month)}</strong><span>${Number(row.purchaseCount || 0)} ${esc(text('Käufe', 'purchases'))} · ${esc(text('Median', 'median'))} ${esc(money(row.medianBasket, data.currency))}</span></div><strong>${esc(money(row.averageBasket, data.currency))}</strong></div>`).join('');
  card.innerHTML = `<div class="pa-card-head"><div><h3>${esc(text('Warenkorbtrend', 'Basket trend'))}</h3><div class="pa-insight-note">${esc(text('Monatlicher Durchschnitt bestätigter Käufe.', 'Monthly average of confirmed purchases.'))}</div></div><strong class="pa-insight-kpi">${latest ? esc(money(latest.averageBasket, data.currency)) : '—'}</strong></div><div class="pa-chip">${esc(text('Änderung', 'change'))}: ${esc(pct(data?.averageBasketChangePercent))}</div>${rows || `<div class="state-empty">${esc(text('Noch keine bestätigten Käufe im Zeitraum.', 'No confirmed purchases in this period.'))}</div>`}${data?.incompleteFx ? `<div class="warn-text">${esc(text('Einige Fremdwährungswerte fehlen.', 'Some FX conversions are missing.'))}</div>` : ''}`;
  return card;
}

function restockCard(data) {
  const card = document.createElement('div');
  card.className = 'pa-card pa-advanced-insight';
  card.dataset.advancedInsight = 'restock';
  const rows = (data?.items || []).slice(0, 8).map(row => `<div class="pa-analytics-row"><div><strong>${esc(row.productName)}</strong><span>${esc(text('erwartet', 'expected'))} ${esc(date(row.expectedNextPurchase))} · ~${Number(row.typicalIntervalDays || 0).toFixed(0)} ${esc(text('Tage', 'days'))} · ${Math.round(Number(row.confidence || 0) * 100)}%</span></div><span class="pa-restock-status ${esc(row.status)}">${esc(statusLabel(row.status, row.daysUntil))}</span></div>`).join('');
  card.innerHTML = `<div class="pa-card-head"><div><h3>${esc(text('Wiederkauf-Prognose', 'Restock forecast'))}</h3><div class="pa-insight-note">${esc(text('Aus deinem tatsächlichen Kaufabstand; keine automatische Bestellung.', 'From your real purchase intervals; never auto-orders.'))}</div></div><span class="pa-chip">${Number(data?.count || 0)}</span></div>${rows || `<div class="state-empty">${esc(text('Noch nicht genug Kaufhistorie für Prognosen.', 'Not enough purchase history for forecasts yet.'))}</div>`}`;
  return card;
}

function statusLabel(status, daysUntil) {
  if (status === 'overdue') return text(`${Math.abs(Number(daysUntil || 0))} T. überfällig`, `${Math.abs(Number(daysUntil || 0))}d overdue`);
  if (status === 'due_soon') return text(`in ${Number(daysUntil || 0)} T.`, `in ${Number(daysUntil || 0)}d`);
  if (status === 'upcoming') return text(`in ${Number(daysUntil || 0)} T.`, `in ${Number(daysUntil || 0)}d`);
  return text(`später`, `later`);
}

async function decorate(panel) {
  if (busy || panel.querySelector('[data-advanced-insight]')) return;
  const grid = panel.querySelector('.pa-analytics-grid');
  if (!grid) return;
  busy = true;
  try {
    const [inflation, basket, restock] = await Promise.all([
      api('api/purchase-analytics/personal-inflation'),
      api('api/purchase-analytics/basket-trend'),
      api('api/purchase-analytics/restock-forecast?horizonDays=90')
    ]);
    grid.append(inflationCard(inflation), basketCard(basket), restockCard(restock));
  } catch { /* supplemental analytics never break the purchase workspace */ }
  finally { busy = false; }
}

function scan() {
  queued = false;
  ensureStyle();
  const panel = document.querySelector('.purchase-advanced-panel:not([hidden])');
  const tab = document.querySelector('[data-pa-tab].active')?.dataset.paTab;
  if (panel && tab === 'analytics') void decorate(panel);
}

function schedule() {
  if (queued) return;
  queued = true;
  queueMicrotask(scan);
}

document.addEventListener('click', schedule, true);
function install() {
  if (!document.body) return;
  new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['hidden', 'class'] });
  schedule();
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, { once: true });
else install();