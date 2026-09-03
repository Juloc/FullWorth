import { money, setMoneyLocale } from '../ui/money.js';
import { isPrivate, onPrivacyChange } from '../ui/privacy.js';

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[char]));
const lang = () => document.documentElement.lang?.startsWith('en') ? 'en' : 'de';
const text = (de, en) => lang() === 'en' ? en : de;
const spaceId = () => localStorage.getItem('finance.space') || '';
const dateText = value => value ? new Intl.DateTimeFormat(lang() === 'en' ? 'en-US' : 'de-DE').format(new Date(`${String(value).slice(0, 10)}T12:00:00`)) : '—';
const amount = (value, currency) => {
  if (isPrivate()) return '••••••';
  if (value == null || !Number.isFinite(Number(value))) return '—';
  setMoneyLocale(lang());
  return money(Number(value), currency || 'EUR');
};

function withSpace(path) {
  const [base, query = ''] = path.split('?');
  const params = new URLSearchParams(query);
  if (spaceId() && !params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', spaceId());
  return `/bff/backend/${base.replace(/^\//, '')}${params.toString() ? `?${params}` : ''}`;
}
async function api(path, options) {
  const response = await fetch(withSpace(path), options);
  if (!response.ok) {
    let message = `${response.status}`;
    try { const body = await response.json(); message = body.error || body.title || body.message || message; } catch {}
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return response.json();
}
function toast(message) {
  const el = $('#toast'); if (!el) return;
  el.textContent = message; el.classList.add('show');
  clearTimeout(toast.timer); toast.timer = setTimeout(() => el.classList.remove('show'), 3000);
}
function ensureCss() {
  if ($('link[data-wealth-investments-css]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet'; link.href = '/features/wealth-investment-consolidation.css'; link.dataset.wealthInvestmentsCss = '1';
  document.head.appendChild(link);
}

let lastPortfolioId = null;
let portfolioCache = null;
let overviewCache = new Map();
let enhanceTimer = null;
let securityDialogState = null;

// Register before loading the existing portfolio UI so the selected id is retained for its modal.
document.addEventListener('click', event => {
  const target = event.target.closest('[data-portfolio]');
  if (target) lastPortfolioId = target.dataset.portfolio || null;
}, true);

ensureCss();
void import('./investment-performance-ui.js');

async function portfolios() {
  if (portfolioCache) return portfolioCache;
  portfolioCache = (await api('api/investments/portfolios')).filter(item => item.isArchived !== true);
  return portfolioCache;
}

async function enhanceWealthRows() {
  const root = $('#nw-investments-list');
  if (!root || !spaceId()) return;
  try {
    const active = await portfolios();
    const rows = $$('.row', root).filter(row => !row.classList.contains('wealth-investment-total'));
    rows.forEach((row, index) => {
      const portfolio = active[index];
      if (!portfolio || row.dataset.portfolio) return;
      row.dataset.portfolio = portfolio.id;
      row.classList.add('wealth-portfolio-drilldown');
      row.setAttribute('role', 'button');
      row.tabIndex = 0;
      row.setAttribute('aria-label', `${text('Depot öffnen', 'Open portfolio')}: ${portfolio.name}`);
      const side = document.createElement('span');
      side.className = 'wealth-investment-chevron';
      side.setAttribute('aria-hidden', 'true');
      side.textContent = '›';
      row.appendChild(side);
      row.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); row.click(); }
      });
    });
  } catch (error) { console.debug('Investment wealth drilldown unavailable', error); }
}

async function portfolioOverview(portfolioId) {
  if (!overviewCache.has(portfolioId)) overviewCache.set(portfolioId, api(`api/investments/portfolios/${portfolioId}/overview-v2`));
  return overviewCache.get(portfolioId);
}

function allocationBlock(positions, currency) {
  const totals = new Map();
  for (const position of positions || []) {
    if (position.marketValue == null) continue;
    totals.set(position.assetType || 'other', (totals.get(position.assetType || 'other') || 0) + Number(position.marketValue));
  }
  const total = [...totals.values()].reduce((sum, value) => sum + value, 0);
  if (!total) return '';
  const labels = { stock:text('Aktien','Stocks'), etf:'ETF', fund:text('Fonds','Funds'), bond:text('Anleihen','Bonds'), crypto:text('Krypto','Crypto'), commodity:text('Rohstoffe','Commodities'), cash:'Cash', other:text('Sonstige','Other') };
  const rows = [...totals.entries()].sort((a,b) => b[1]-a[1]).map(([type,value]) => {
    const pct = value / total * 100;
    return `<div class="wealth-investment-allocation-row"><div><strong>${esc(labels[type] || type)}</strong><span>${pct.toFixed(1)}%</span></div><div class="wealth-investment-allocation-track"><i style="width:${Math.max(0,Math.min(100,pct))}%"></i></div><span>${amount(value,currency)}</span></div>`;
  }).join('');
  return `<section class="ip-section wealth-investment-allocation" data-wealth-allocation><div class="ip-section-head"><h3>${esc(text('Asset-Allokation','Asset allocation'))}</h3></div>${rows}</section>`;
}

async function enhancePortfolioDialog(dialog) {
  if (!lastPortfolioId || !dialog?.open) return;
  let overview;
  try { overview = await portfolioOverview(lastPortfolioId); } catch { return; }
  const content = $('[data-ip-content]', dialog);
  if (!content) return;

  // Add allocation to the existing portfolio overview without re-calculating any values.
  const overviewSection = $('.ip-metrics', content);
  if (overviewSection && ! $('[data-wealth-allocation]', content)) {
    const holder = document.createElement('div');
    holder.innerHTML = allocationBlock(overview.positions || [], overview.portfolio?.currency || 'EUR');
    if (holder.firstElementChild) overviewSection.insertAdjacentElement('afterend', holder.firstElementChild);
  }

  // Existing portfolio rows do not expose the security id. Match the rendered canonical position
  // names against overview-v2 and annotate the rows, then open the security drilldown on demand.
  const byName = new Map((overview.positions || []).map(position => [position.name, position]));
  $$('.ip-row', content).forEach(row => {
    if (row.dataset.ipSecurity) return;
    const name = $('strong', row)?.textContent?.trim();
    const position = byName.get(name);
    if (!position) return;
    row.dataset.ipSecurity = position.securityId;
    row.classList.add('ip-security-row');
    row.setAttribute('role', 'button');
    row.tabIndex = 0;
    row.setAttribute('aria-label', `${text('Wertpapier öffnen','Open security')}: ${position.name}`);
    const open = () => openSecurityDetail(lastPortfolioId, position.securityId);
    row.addEventListener('click', event => { if (!event.target.closest('button')) open(); });
    row.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(); } });
  });
}

function priceStateText(price) {
  if (!price || price.state === 'missing') return text('Kein Kurs','Missing price');
  const label = ({ current:text('Aktuell','Current'), recent:text('Kürzlich','Recent'), stale:text('Veraltet','Stale'), historical:text('Historisch','Historical') })[price.state] || price.state;
  return `${label}${price.ageDays != null ? ` · ${price.ageDays}d` : ''}${price.source ? ` · ${price.source}` : ''}`;
}

function priceChart(history) {
  const points = (history || []).filter(item => item.price != null).sort((a,b) => String(a.priceDate || a.requestedDate).localeCompare(String(b.priceDate || b.requestedDate)));
  if (points.length < 2) return `<div class="fp-muted">${esc(text('Noch nicht genug Kursdaten.','Not enough price history yet.'))}</div>`;
  const values = points.map(item => Number(item.price));
  const min = Math.min(...values); const max = Math.max(...values); const span = max-min || 1;
  const width=720,height=220,pad=20;
  const coords = values.map((value,index) => `${pad+(index/(values.length-1))*(width-pad*2)},${height-pad-((value-min)/span)*(height-pad*2)}`).join(' ');
  return `<div class="ip-chart-wrap"><svg class="ip-chart" viewBox="0 0 ${width} ${height}" role="img" aria-label="${esc(text('Kursverlauf','Price history'))}"><polyline points="${coords}" class="ip-line ip-line-main"/></svg></div>`;
}

async function openSecurityDetail(portfolioId, securityId) {
  try {
    const end = new Date(); const start = new Date(end); start.setFullYear(start.getFullYear()-1);
    const iso = date => `${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}`;
    const [allSecurities, overview, effectivePrice, history, trades] = await Promise.all([
      api('api/investments/securities'),
      portfolioOverview(portfolioId),
      api(`api/market-data/securities/${securityId}/effective-price`).catch(() => null),
      api(`api/market-data/securities/${securityId}/history?from=${iso(start)}&to=${iso(end)}`).catch(() => []),
      api(`api/investments/portfolios/${portfolioId}/trades`).catch(() => [])
    ]);
    const security = (allSecurities || []).find(item => item.id === securityId);
    const position = (overview.positions || []).find(item => item.securityId === securityId);
    if (!security || !position) throw new Error(text('Wertpapier ist in diesem Depot nicht verfügbar.','Security is not available in this portfolio.'));
    const securityTrades = (trades || []).filter(item => item.securityId === securityId);
    const dividends = securityTrades.filter(item => item.tradeType === 'dividend');
    const avgCost = position.costBasis != null && Number(position.quantity) > 0 ? Number(position.costBasis) / Number(position.quantity) : null;

    const dialog = document.createElement('dialog');
    dialog.className = 'fp-dialog ip-dialog wealth-security-dialog';
    dialog.innerHTML = `<div class="fp-dialog-card ip-card"><div class="fp-dialog-head ip-head"><div><h2>${esc(security.name)}</h2><div class="fp-muted">${esc([security.isin,security.ticker,security.assetType,security.currency].filter(Boolean).join(' · '))}</div></div><button type="button" data-security-close aria-label="${esc(text('Schließen','Close'))}">×</button></div>
      <div class="ip-content">
        <div class="ip-metrics">
          ${metric(text('Marktwert','Market value'), amount(position.marketValue, overview.portfolio.currency))}
          ${metric(text('Stück','Quantity'), isPrivate() ? '••••••' : String(position.quantity))}
          ${metric(text('Kostenbasis','Cost basis'), amount(position.costBasis, overview.portfolio.currency))}
          ${metric(text('Ø Einstand','Average cost'), amount(avgCost, overview.portfolio.currency))}
          ${metric(text('Nicht realisiert','Unrealized'), amount(position.unrealizedResult, overview.portfolio.currency))}
          ${metric(text('Aktueller Kurs','Current price'), amount(effectivePrice?.price ?? position.price, effectivePrice?.currency ?? position.priceCurrency ?? security.currency))}
        </div>
        <div class="wealth-security-freshness ${effectivePrice?.state === 'stale' || effectivePrice?.state === 'missing' ? 'is-warning' : ''}"><strong>${esc(priceStateText(effectivePrice))}</strong><span>${effectivePrice?.priceDate ? esc(dateText(effectivePrice.priceDate)) : ''}</span></div>
        <section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Kursverlauf','Price history'))}</h3><span>1Y</span></div>${priceChart(history)}</section>
        <section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Depot','Portfolio'))}</h3></div><div class="ip-row"><div><strong>${esc(overview.portfolio.name)}</strong><div class="fp-muted">${esc(overview.portfolio.currency)}</div></div><button type="button" class="ghost" data-open-performance>${esc(text('Performance öffnen','Open performance'))}</button></div></section>
        <section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Transaktionen','Transactions'))}</h3><span>${securityTrades.length}</span></div>${tradeRows(securityTrades, overview.portfolio.currency)}</section>
        <section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Dividenden / Ausschüttungen','Dividends / distributions'))}</h3><span>${dividends.length}</span></div>${tradeRows(dividends, overview.portfolio.currency)}</section>
      </div></div>`;
    document.body.appendChild(dialog);
    securityDialogState = { dialog, portfolioId, securityId };
    $('[data-security-close]', dialog).onclick = () => dialog.close();
    $('[data-open-performance]', dialog).onclick = () => {
      dialog.close();
      const parent = $$('.ip-dialog').find(item => item.open && !item.classList.contains('wealth-security-dialog'));
      parent?.querySelector('[data-ip-tab="performance"]')?.click();
    };
    dialog.addEventListener('close', () => { if (securityDialogState?.dialog === dialog) securityDialogState = null; dialog.remove(); });
    dialog.showModal();
  } catch (error) { toast(error.message || text('Wertpapier konnte nicht geladen werden.','Could not load security.')); }
}

function metric(label, value) { return `<div class="ip-metric"><span>${esc(label)}</span><strong>${value}</strong></div>`; }
function tradeRows(rows, currency) {
  if (!rows.length) return `<div class="fp-muted">${esc(text('Keine Einträge.','No entries.'))}</div>`;
  return `<div class="ip-list">${rows.map(row => `<div class="ip-row"><div><strong>${esc(row.tradeType || '—')}</strong><div class="fp-muted">${esc(dateText(row.tradeDate))}${row.quantity != null ? ` · ${esc(String(row.quantity))}` : ''}</div></div><div class="ip-row-value"><strong>${amount(row.amount, row.currency || currency)}</strong></div></div>`).join('')}</div>`;
}

function scheduleEnhance() { clearTimeout(enhanceTimer); enhanceTimer = setTimeout(async () => { await enhanceWealthRows(); const dialog = $$('.ip-dialog').find(item => item.open && !item.classList.contains('wealth-security-dialog')); if (dialog) await enhancePortfolioDialog(dialog); }, 40); }

new MutationObserver(scheduleEnhance).observe(document.body, { childList:true, subtree:true });
document.addEventListener('fullworth-space-changed', () => { portfolioCache = null; overviewCache.clear(); scheduleEnhance(); });
onPrivacyChange(() => { if (securityDialogState?.dialog?.open) { const id = securityDialogState.securityId; const portfolio = securityDialogState.portfolioId; securityDialogState.dialog.close(); void openSecurityDetail(portfolio, id); } });
scheduleEnhance();
