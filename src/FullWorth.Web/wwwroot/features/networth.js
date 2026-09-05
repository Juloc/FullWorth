import { openRealEstateDetail } from './wealth-real-estate.js';
import { sectionCard, trendBadge, esc } from '../ui/ux-kit.js';
import { renderLoans, bindLoans } from './loans.js';

// Unified wealth view (UX rework §8 / delivery Phase D). The first screen explains wealth before it
// offers management tools: a trend card ("Wie entwickelt sich dein Vermögen?"), an allocation card
// ("Verteilung deines Vermögens"), the optional portfolio panel, and finally the asset/liability/loan
// editors behind a Details/Verwalten disclosure. Totals/history come from /api/wealth/*; type-specific
// modules own their detail logic. A Reserve/Notgroschen card is intentionally NOT rendered because the
// wealth overview API exposes no configured emergency-fund target (the plan only shows it when configured).
let ctx = null;
let lastOverview = null;

// Trend window options for card 1's segmented control. `m` = months back from today.
const WINDOWS = [
  { m: 6, sde: '6 M', sen: '6M', lde: 'Letzte 6 Monate', len: 'Last 6 months' },
  { m: 12, sde: '1 J', sen: '1Y', lde: 'Letzte 12 Monate', len: 'Last 12 months' },
  { m: 24, sde: '2 J', sen: '2Y', lde: 'Letzte 2 Jahre', len: 'Last 2 years' },
  { m: 60, sde: '5 J', sen: '5Y', lde: 'Letzte 5 Jahre', len: 'Last 5 years' }
];

// View state so the trend window can be changed without re-fetching (or clobbering) the rest of the view.
const nw = { overview: null, history: [], assets: [], liabilities: [], accounts: [], portfolios: [], currency: 'EUR', windowMonths: 12 };

const ASSET_KINDS = [
  'real_estate', 'vehicle', 'precious_metal', 'collectible',
  'receivable', 'business_interest', 'insurance_pension', 'other'
];
const LIABILITY_KINDS = ['loan', 'mortgage', 'credit_card', 'other'];
const CYCLES = ['monthly', 'quarterly', 'yearly', 'weekly'];

const COPY = {
  de: {
    addValue: 'Wert hinzufügen', chooseType: 'Art des Vermögenswerts',
    chooseTypeHint: 'Wähle den Typ. Weitere Details können später ergänzt werden.',
    investmentHint: 'Aktien, ETFs und andere Wertpapiere werden über ein Depot verwaltet und nicht als manueller Wert angelegt.',
    investmentTotal: 'Investments gesamt', portfolio: 'Depot', active: 'Aktiv',
    realEstate: 'Immobilien', vehicles: 'Fahrzeuge', otherValues: 'Weitere Werte',
    valueHistory: 'Werthistorie', details: 'Details', updateValue: 'Wert aktualisieren', current: 'Aktuell',
    noValuations: 'Noch keine Bewertungen vorhanden.', fxIncomplete: 'Gesamtsumme unvollständig: Für mindestens eine Währung fehlt ein Wechselkurs.',
    dataIncomplete: 'Daten unvollständig', composition: 'Zusammensetzung', accounts: 'Konten', manualAssets: 'Weitere Vermögenswerte', investments: 'Investments', debt: 'Schulden',
    real_estate: 'Immobilie', vehicle: 'Fahrzeug', precious_metal: 'Edelmetall', collectible: 'Sammlerstück / Wertgegenstand',
    receivable: 'Forderung / privates Darlehen', business_interest: 'Unternehmensbeteiligung', insurance_pension: 'Versicherung / Vorsorge', other: 'Sonstiger Wert',
    manual: 'Manuell', purchase_price: 'Kaufpreis', internal_estimate: 'FullWorth-Schätzung', external_provider: 'Externer Anbieter', appraisal: 'Gutachten', import: 'Import', legacy: 'Übernommen',
    trendTitle: 'Wie entwickelt sich dein Vermögen?', allocationTitle: 'Verteilung deines Vermögens',
    manageTitle: 'Details & Verwalten', manageHint: 'Vermögenswerte, Schulden und Kredite bearbeiten',
    window: 'Zeitraum', wealthCap: 'Vermögenswerte', noTrend: 'Noch keine Verlaufsdaten.'
  },
  en: {
    addValue: 'Add asset', chooseType: 'Asset type', chooseTypeHint: 'Choose a type. Additional details can be completed later.',
    investmentHint: 'Stocks, ETFs and other securities are managed through an investment portfolio, not as manual assets.',
    investmentTotal: 'Investments total', portfolio: 'Portfolio', active: 'Active',
    realEstate: 'Real estate', vehicles: 'Vehicles', otherValues: 'Other assets',
    valueHistory: 'Value history', details: 'Details', updateValue: 'Update value', current: 'Current', noValuations: 'No valuations yet.',
    fxIncomplete: 'Total is incomplete: at least one required FX rate is missing.', dataIncomplete: 'Data incomplete', composition: 'Composition',
    accounts: 'Accounts', manualAssets: 'Other assets', investments: 'Investments', debt: 'Debt',
    real_estate: 'Real estate', vehicle: 'Vehicle', precious_metal: 'Precious metal', collectible: 'Collectible / valuable',
    receivable: 'Receivable / private loan', business_interest: 'Business interest', insurance_pension: 'Insurance / pension', other: 'Other asset',
    manual: 'Manual', purchase_price: 'Purchase price', internal_estimate: 'FullWorth estimate', external_provider: 'External provider', appraisal: 'Appraisal', import: 'Import', legacy: 'Migrated',
    trendTitle: 'How is your wealth developing?', allocationTitle: 'Your wealth distribution',
    manageTitle: 'Details & manage', manageHint: 'Edit assets, liabilities and loans',
    window: 'Time range', wealthCap: 'Assets', noTrend: 'No history yet.'
  }
};

function isDe() { return !(document.documentElement.lang || '').toLowerCase().startsWith('en'); }
function t(key) { return COPY[isDe() ? 'de' : 'en'][key] || key; }
function num(value) { const n = Number(value); return Number.isFinite(n) ? n : 0; }

function ensureStyles() {
  if (document.querySelector('link[data-wealth-assets-css]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/features/wealth-assets.css';
  link.dataset.wealthAssetsCss = '1';
  document.head.appendChild(link);
}

// New presentation-layer styles for the trend/allocation/manage cards. Injected once; everything else
// reuses app.css `.fw-*` primitives and design tokens (--cat-1..--cat-8, --negative, --cta, spacing).
function ensureUxStyles() {
  if (document.getElementById('networth-ux-css')) return;
  const style = document.createElement('style');
  style.id = 'networth-ux-css';
  style.textContent = `
    .nw-hero-head{display:flex;justify-content:space-between;align-items:flex-start;gap:var(--s4);flex-wrap:wrap}
    .nw-hero .fw-summary-value{font-size:34px}
    .nw-hero-trend{display:flex;flex-direction:column;align-items:flex-end;gap:4px;text-align:right}
    .nw-trend-desc{display:flex;flex-direction:column;align-items:flex-end;line-height:1.2}
    .nw-delta{font-weight:600;font-variant-numeric:tabular-nums;font-size:13px}
    .nw-delta.positive{color:var(--positive)}
    .nw-delta.negative{color:var(--negative)}
    .nw-window-label{color:var(--muted);font-size:11px}
    .nw-windows{margin:var(--s3) 0}
    .nw-chart{margin:var(--s2) 0}
    .nw-chart svg{display:block;width:100%;height:140px}
    .nw-chart-line{stroke:var(--cta)}
    .nw-chart-fill{fill:var(--cta);opacity:.10}
    .nw-chart-empty{padding:var(--s5) 0;text-align:center}
    .nw-hero-metrics{display:flex;gap:var(--s6);flex-wrap:wrap;margin-top:var(--s3)}
    .nw-hero-metrics strong{display:block;font-variant-numeric:tabular-nums;font-size:16px}
    .nw-hero-metrics strong.negative{color:var(--negative)}
    .nw-metric-label{display:block;color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.06em;margin-bottom:2px}
    .nw-fx{margin:var(--s3) 0 0;color:var(--warning);font-size:12px}
    .nw-alloc-block{margin:var(--s2) 0 var(--s3)}
    .nw-alloc-cap{margin:0 0 4px;color:var(--muted);font-size:12px;display:flex;justify-content:space-between;gap:var(--s3)}
    .nw-alloc-cap strong{color:var(--text);font-variant-numeric:tabular-nums}
    .nw-alloc-debt{background:var(--negative-soft)}
    .nw-legend{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:6px var(--s5);margin-top:var(--s3)}
    .nw-legend-item{display:flex;align-items:center;gap:8px;font-size:13px}
    .nw-dot{width:10px;height:10px;border-radius:3px;flex:none}
    .nw-legend-label{flex:1;color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .nw-legend-amt{font-variant-numeric:tabular-nums;font-weight:600}
    .nw-legend-amt.negative{color:var(--negative)}
    .nw-manage{margin-top:var(--s4);border:1px solid var(--line);border-radius:var(--radius-card);background:var(--surface);overflow:hidden}
    .nw-manage>summary{position:relative;cursor:pointer;padding:var(--s4) calc(var(--s4) + 20px) var(--s4) var(--s4);display:flex;flex-direction:column;gap:2px;list-style:none;font-weight:600}
    .nw-manage>summary::-webkit-details-marker{display:none}
    .nw-manage>summary::after{content:'▾';position:absolute;right:var(--s4);top:var(--s4);color:var(--muted)}
    .nw-manage[open]>summary::after{content:'▴'}
    .nw-manage-hint{font-weight:400;color:var(--muted);font-size:12px}
    .nw-manage-body{padding:0 var(--s4) var(--s4)}
    .nw-manage-body .fw-card{box-shadow:none;border:1px solid var(--line)}
    .nw-manage-body .fw-card+.fw-card{margin-top:var(--s3)}
    #nw-investments .panel-head{margin-bottom:var(--s3)}
  `;
  document.head.appendChild(style);
}

export function bindNetWorth(context) {
  // Just store ctx + ensure the shared asset stylesheet is present. The static index.html add/manage
  // buttons are replaced when renderNetWorth rebuilds #view-networth, so their listeners are (re)wired
  // there on freshly-created elements rather than here.
  ctx = context;
  ensureStyles();
}

export function newAsset(context) {
  if (context) ctx = context;
  ensureStyles();
  return openAssetWizard();
}

async function loadHistory(months) {
  const end = new Date();
  const start = new Date();
  start.setMonth(end.getMonth() - months);
  try { return await ctx.api(`api/wealth/history?from=${localDate(start)}&to=${localDate(end)}`) || []; }
  catch { return []; }
}

export async function renderNetWorth(context) {
  ctx = context;
  ensureStyles();
  ensureUxStyles();
  if (!nw.windowMonths) nw.windowMonths = 12;

  let overview;
  try { overview = await ctx.api('api/wealth/overview'); }
  catch {
    const host = ctx.$('#view-networth');
    if (host) host.innerHTML = sectionCard(t('trendTitle'), `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.error'))}</div></div>`, { className: 'nw-hero' });
    return;
  }

  const [history, assets, liabilities, accounts, portfolios] = await Promise.all([
    loadHistory(nw.windowMonths),
    ctx.api('api/assets').catch(() => []),
    ctx.api('api/liabilities').catch(() => []),
    ctx.api('api/accounts').catch(() => []),
    ctx.api('api/investments/portfolios').catch(() => [])
  ]);

  lastOverview = overview;
  const linkedInvestmentAccounts = new Set((portfolios || []).map(item => item.accountId).filter(Boolean));
  nw.overview = overview;
  nw.history = history || [];
  nw.assets = assets || [];
  nw.liabilities = liabilities || [];
  nw.accounts = (accounts || []).filter(account => !linkedInvestmentAccounts.has(account.id));
  nw.portfolios = portfolios || [];
  nw.currency = overview.currency;

  paintNetWorth();
}

// Build the whole view, then populate the management lists and (re)wire every control on fresh elements.
function paintNetWorth() {
  const host = ctx.$('#view-networth');
  if (!host) return;
  host.innerHTML = `${buildHeroCard()}${buildAllocationCard()}${investmentsCardMarkup()}${manageMarkup()}`;

  const hero = host.querySelector('.nw-hero');
  if (hero) wireHero(hero);
  host.querySelector('[data-action="new-asset"]')?.addEventListener('click', () => openAssetWizard());
  host.querySelector('[data-action="new-liability"]')?.addEventListener('click', () => openLiabilityDialog());

  renderAccounts(nw.accounts);
  renderAssets(nw.assets);
  renderLiabilities(nw.liabilities);
  renderInvestments(nw.portfolios, nw.overview);

  // Loans are owned by features/loans.js. Re-bind its "add" button (our rebuilt markup replaced the
  // static one) and re-render #nw-loans so the list survives internal refreshes, not just view opens.
  bindLoans(ctx);
  renderLoans(ctx);
}

/* ---- Card 1: "Wie entwickelt sich dein Vermögen?" -------------------------------------------- */

function trendStats(history) {
  const usable = (history || []).filter(point => Number.isFinite(Number(point.netWorth)));
  if (usable.length < 2) return { hasData: false, pct: 0, delta: 0 };
  const first = Number(usable[0].netWorth);
  const last = Number(usable.at(-1).netWorth);
  const delta = last - first;
  const pct = first !== 0 ? (delta / Math.abs(first)) * 100 : (delta !== 0 ? 100 : 0);
  return { hasData: true, pct, delta };
}

function heroTrendInner() {
  const stats = trendStats(nw.history);
  if (!stats.hasData) return `<span class="fw-trend">—</span>`;
  const win = WINDOWS.find(w => w.m === nw.windowMonths) || WINDOWS[1];
  const sign = (!ctx.isPrivate() && stats.delta > 0) ? '+' : '';
  const cls = stats.delta > 0 ? 'positive' : stats.delta < 0 ? 'negative' : '';
  return `${trendBadge(stats.pct, true)}<div class="nw-trend-desc"><span class="nw-delta ${cls}">${sign}${ctx.money(stats.delta, nw.currency)}</span><span class="nw-window-label">${ctx.esc(isDe() ? win.lde : win.len)}</span></div>`;
}

// Reused/ported from the previous SVG trend rendering: a net-worth polyline with a soft area fill.
function trendChartSvg(history, currency) {
  const usable = (history || []).filter(point => Number.isFinite(Number(point.netWorth)));
  if (!usable.length) return `<div class="row-sub nw-chart-empty">${ctx.esc(t('noTrend'))}</div>`;
  const values = usable.map(point => Number(point.netWorth));
  const min = Math.min(...values); const max = Math.max(...values); const span = max - min || 1;
  const width = 900; const height = 200;
  const points = values.map((value, index) => `${(index / (values.length - 1 || 1)) * width},${height - ((value - min) / span) * (height - 24) - 12}`).join(' ');
  const area = `0,${height} ${points} ${width},${height}`;
  return `<svg viewBox="0 0 ${width} ${height}" preserveAspectRatio="none" role="img" aria-label="${ctx.esc(ctx.get('analytics.trend'))}"><polygon class="nw-chart-fill" points="${area}"/><polyline class="nw-chart-line" points="${points}" fill="none" stroke-width="3" vector-effect="non-scaling-stroke"/></svg><div class="row-sub">${ctx.money(values.at(-1), currency)}</div>`;
}

function buildHeroCard() {
  const overview = nw.overview;
  const currency = nw.currency;
  const seg = `<div class="fw-cycle nw-windows" role="tablist" aria-label="${ctx.esc(t('window'))}">${WINDOWS.map(w =>
    `<button type="button" role="tab" data-window="${w.m}"${w.m === nw.windowMonths ? ' class="active" aria-selected="true"' : ' aria-selected="false"'}>${ctx.esc(isDe() ? w.sde : w.sen)}</button>`).join('')}</div>`;
  const grossAssets = num(overview.totalAssets) + num(overview.accounts?.amount);
  const metrics = `<div class="nw-hero-metrics"><div><span class="nw-metric-label">${ctx.esc(ctx.get('dashboard.assets'))}</span><strong>${ctx.money(grossAssets, currency)}</strong></div><div><span class="nw-metric-label">${ctx.esc(ctx.get('dashboard.liabilities'))}</span><strong class="negative">${ctx.money(num(overview.totalLiabilities), currency)}</strong></div></div>`;
  const missing = (overview.missingCurrencies || []).join(', ');
  const fx = overview.isComplete ? '' : `<p class="nw-fx">${ctx.esc(t('fxIncomplete'))}${missing ? ` (${ctx.esc(missing)})` : ''}</p>`;
  const body = `<div class="nw-hero-head"><div class="nw-hero-value"><span class="fw-summary-label">${ctx.esc(ctx.get('dashboard.netWorth'))}</span><div class="fw-summary-value">${ctx.money(overview.netWorth, currency)}</div></div><div class="nw-hero-trend">${heroTrendInner()}</div></div>${seg}<div class="nw-chart">${trendChartSvg(nw.history, currency)}</div>${metrics}${fx}`;
  return sectionCard(t('trendTitle'), body, { className: 'nw-hero' });
}

function wireHero(hero) {
  hero.querySelectorAll('[data-window]').forEach(button => {
    button.addEventListener('click', async () => {
      const months = Number(button.dataset.window);
      if (months === nw.windowMonths) return;
      nw.windowMonths = months;
      hero.querySelectorAll('[data-window]').forEach(other => {
        const on = other === button;
        other.classList.toggle('active', on);
        other.setAttribute('aria-selected', String(on));
      });
      nw.history = await loadHistory(months);
      const trendEl = hero.querySelector('.nw-hero-trend');
      if (trendEl) trendEl.innerHTML = heroTrendInner();
      const chartEl = hero.querySelector('.nw-chart');
      if (chartEl) chartEl.innerHTML = trendChartSvg(nw.history, nw.currency);
    });
  });
}

/* ---- Card 2: "Verteilung deines Vermögens" --------------------------------------------------- */

function legendRow(label, amount, color, currency, negative = false) {
  return `<div class="nw-legend-item"><span class="nw-dot" style="background:${color}"></span><span class="nw-legend-label">${ctx.esc(label)}</span><span class="nw-legend-amt${negative ? ' negative' : ''}">${ctx.money(amount, currency)}</span></div>`;
}

function buildAllocationCard() {
  const overview = nw.overview;
  const currency = nw.currency;
  const accounts = num(overview.accounts?.amount);
  const investments = num(overview.investments?.amount);
  const manualTotal = num(overview.manualAssets?.amount);
  // Split the (already FX-converted) manual-asset total into real estate vs. other assets using the
  // native-currency proportion from the assets list, so both show as separate allocation segments.
  const included = (nw.assets || []).filter(item => item.includeInNetWorth !== false);
  const manualRaw = included.reduce((sum, item) => sum + num(item.currentValue), 0);
  const realEstateRaw = included.filter(item => item.kind === 'real_estate').reduce((sum, item) => sum + num(item.currentValue), 0);
  const realEstateRatio = manualRaw > 0 ? realEstateRaw / manualRaw : 0;
  const realEstate = manualTotal * realEstateRatio;
  const otherAssets = manualTotal - realEstate;
  const liabilities = num(overview.totalLiabilities);

  const segments = [
    { label: t('accounts'), amount: accounts, color: 'var(--cat-2)' },
    { label: t('investments'), amount: investments, color: 'var(--cat-1)' },
    { label: t('realEstate'), amount: realEstate, color: 'var(--cat-3)' },
    { label: t('otherValues'), amount: otherAssets, color: 'var(--cat-4)' }
  ].filter(segment => segment.amount > 0.005);
  const assetSum = segments.reduce((sum, segment) => sum + segment.amount, 0);

  if (assetSum <= 0 && liabilities <= 0) {
    return sectionCard(t('allocationTitle'), emptyRow(), { className: 'nw-allocation' });
  }

  const assetBar = assetSum > 0
    ? `<div class="nw-alloc-block"><p class="nw-alloc-cap"><span>${ctx.esc(t('wealthCap'))}</span><strong>${ctx.money(assetSum, currency)}</strong></p><div class="fw-alloc" role="img" aria-label="${ctx.esc(t('wealthCap'))}">${segments.map(segment =>
      `<span style="width:${(segment.amount / assetSum * 100).toFixed(2)}%;background:${segment.color}"></span>`).join('')}</div></div>`
    : '';
  const debtBar = liabilities > 0
    ? `<div class="nw-alloc-block"><p class="nw-alloc-cap"><span>${ctx.esc(t('debt'))}</span><strong class="negative">${ctx.money(-liabilities, currency)}</strong></p><div class="fw-alloc nw-alloc-debt" role="img" aria-label="${ctx.esc(t('debt'))}"><span style="width:${Math.min(100, assetSum > 0 ? liabilities / assetSum * 100 : 100).toFixed(2)}%;background:var(--negative)"></span></div></div>`
    : '';

  const legendItems = segments.map(segment => legendRow(segment.label, segment.amount, segment.color, currency));
  if (liabilities > 0) legendItems.push(legendRow(t('debt'), -liabilities, 'var(--negative)', currency, true));
  const legend = `<div class="nw-legend">${legendItems.join('')}</div>`;

  return sectionCard(t('allocationTitle'), `${assetBar}${debtBar}${legend}`, { className: 'nw-allocation' });
}

/* ---- Card 4: optional portfolio panel (ids preserved for parity/import enhancement modules) --- */

function investmentsCardMarkup() {
  // Kept `.panel-head` so feature-parity-ui.js / investment-import-ui.js / parity-final-ui.js can still
  // inject their manage/import buttons, and #nw-investments / #nw-investments-list so their content and
  // wealth-investment-consolidation.js keep their targets.
  return `<article id="nw-investments" class="fw-card nw-invest" hidden><div class="panel-head fw-card-head"><h2 class="fw-card-title">${ctx.esc(t('investments'))}</h2></div><div id="nw-investments-list" class="rows"></div></article>`;
}

/* ---- Card 5: Details / Verwalten (management surfaces one level down) -------------------------- */

function manageMarkup() {
  const add = key => ({ label: ctx.get('common.add'), attr: `data-action="${key}"` });
  const accountsCard = sectionCard(t('accounts'), `<div id="nw-accounts" class="rows"></div>`, { className: 'nw-sub' });
  const assetsCard = sectionCard(t('manualAssets'), `<div id="assets-list" class="rows"></div>`, { className: 'nw-sub', action: add('new-asset') });
  const liabilitiesCard = sectionCard(t('debt'), `<div id="liabilities-list" class="rows"></div>`, { className: 'nw-sub', action: add('new-liability') });
  const loansCard = sectionCard(ctx.get('loans.title'), `<div id="nw-loans" class="rows"></div>`, { className: 'nw-sub', action: add('new-loan') });
  return `<details class="nw-manage"><summary><span>${ctx.esc(t('manageTitle'))}</span><span class="nw-manage-hint">${ctx.esc(t('manageHint'))}</span></summary><div class="nw-manage-body">${accountsCard}${assetsCard}${liabilitiesCard}${loansCard}</div></details>`;
}

/* ---- Management list renderers (unchanged behaviour; targets live inside the Details section) --- */

function renderAccounts(accounts) {
  const el = ctx.$('#nw-accounts'); if (!el) return; el.innerHTML = '';
  if (!accounts.length) { el.innerHTML = emptyRow(); return; }
  const groups = new Map();
  for (const account of accounts) {
    const key = account.institutionName || ctx.get('accounts.manual');
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(account);
  }
  const frag = document.createDocumentFragment();
  for (const [institution, list] of groups) {
    const head = document.createElement('div'); head.className = 'row-group'; head.textContent = institution; frag.appendChild(head);
    for (const account of list) {
      const row = document.createElement('div'); row.className = 'row';
      const balance = account.latestBalance ? ctx.money(account.latestBalance.amount, account.latestBalance.currency) : '—';
      row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(account.displayName || account.institutionName)}</div></div><div class="amount">${balance}</div>`;
      frag.appendChild(row);
    }
  }
  el.appendChild(frag);
}

function renderAssets(assets) {
  const el = ctx.$('#assets-list'); if (!el) return; el.innerHTML = '';
  if (!assets.length) { el.innerHTML = emptyRow(); return; }
  const groups = [
    [t('realEstate'), assets.filter(item => item.kind === 'real_estate')],
    [t('vehicles'), assets.filter(item => item.kind === 'vehicle')],
    [t('otherValues'), assets.filter(item => !['real_estate', 'vehicle'].includes(item.kind))]
  ].filter(([, items]) => items.length);
  const frag = document.createDocumentFragment();
  for (const [title, items] of groups) {
    const head = document.createElement('div'); head.className = 'row-group wealth-group-title'; head.textContent = title; frag.appendChild(head);
    for (const asset of items) frag.appendChild(assetRow(asset));
  }
  el.appendChild(frag);
}

function assetRow(asset) {
  const row = document.createElement('div');
  row.className = `row nw-item${asset.includeInNetWorth ? '' : ' nw-excluded'}`;
  const detailAction = asset.kind === 'real_estate'
    ? `<button class="icon-button" data-detail title="${ctx.esc(t('details'))}" aria-label="${ctx.esc(t('details'))}">›</button>`
    : `<button class="icon-button" data-history title="${ctx.esc(t('valueHistory'))}" aria-label="${ctx.esc(t('valueHistory'))}">↗</button>`;
  row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(asset.name)}${asset.includeInNetWorth ? '' : ` <span class="tx-marker">${ctx.esc(ctx.get('networth.excluded'))}</span>`}</div><div class="row-sub">${ctx.esc(t(asset.kind || 'other'))}${asset.valuedAt ? ` · ${ctx.esc(dateValue(asset.valuedAt))}` : ''}</div></div><div class="row-side"><span class="amount">${ctx.money(asset.currentValue, asset.currency)}</span>${detailAction}<button class="icon-button" data-toggle title="${ctx.esc(ctx.get(asset.includeInNetWorth ? 'networth.exclude' : 'networth.include'))}">${asset.includeInNetWorth ? '◉' : '○'}</button><button class="icon-button" data-edit title="${ctx.esc(ctx.get('common.edit'))}">✎</button></div>`;
  row.querySelector('[data-edit]').onclick = () => openAssetForm(asset.kind || 'other', asset);
  row.querySelector('[data-history]')?.addEventListener('click', () => openValuationHistory(asset));
  row.querySelector('[data-detail]')?.addEventListener('click', () => openRealEstateDetail(ctx, asset, () => renderNetWorth(ctx)));
  row.querySelector('[data-toggle]').onclick = async () => {
    try { await ctx.api(`api/assets/${asset.id}`, jsonBody({ ...assetToWrite(asset), includeInNetWorth: !asset.includeInNetWorth }, 'PUT')); await renderNetWorth(ctx); }
    catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
  return row;
}

function renderLiabilities(liabilities) {
  const el = ctx.$('#liabilities-list'); if (!el) return; el.innerHTML = '';
  if (!liabilities.length) { el.innerHTML = emptyRow(); return; }
  const frag = document.createDocumentFragment();
  for (const item of liabilities) {
    const row = document.createElement('div'); row.className = `row nw-item${item.includeInNetWorth ? '' : ' nw-excluded'}`;
    row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(item.name)}${item.includeInNetWorth ? '' : ` <span class="tx-marker">${ctx.esc(ctx.get('networth.excluded'))}</span>`}</div><div class="row-sub">${ctx.esc(ctx.get(`networth.liabilityKind_${item.kind || 'other'}`))}</div></div><div class="row-side"><span class="amount">${ctx.money(item.currentBalance, item.currency)}</span><button class="icon-button" data-toggle>${item.includeInNetWorth ? '◉' : '○'}</button><button class="icon-button" data-edit>✎</button></div>`;
    row.querySelector('[data-edit]').onclick = () => openLiabilityDialog(item);
    row.querySelector('[data-toggle]').onclick = async () => {
      try { await ctx.api(`api/liabilities/${item.id}`, jsonBody({ ...liabilityToWrite(item), includeInNetWorth: !item.includeInNetWorth }, 'PUT')); await renderNetWorth(ctx); }
      catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
    };
    frag.appendChild(row);
  }
  el.appendChild(frag);
}

function renderInvestments(portfolios, overview) {
  const panel = ctx.$('#nw-investments'); const list = ctx.$('#nw-investments-list'); if (!panel || !list) return;
  const active = (portfolios || []).filter(item => item.isArchived !== true); const total = Number(overview.investments?.amount || 0);
  if (!active.length && total === 0) { panel.hidden = true; list.innerHTML = ''; return; }
  panel.hidden = false; list.innerHTML = `<div class="row wealth-investment-total"><div class="row-main"><div class="row-title">${ctx.esc(t('investmentTotal'))}</div><div class="row-sub">${ctx.esc(overview.investmentDataIncomplete ? t('dataIncomplete') : t('active'))}</div></div><div class="amount">${ctx.money(total, overview.currency)}</div></div>`;
  for (const portfolio of active) {
    const row = document.createElement('div'); row.className = 'row';
    row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(portfolio.name)}</div><div class="row-sub">${ctx.esc(t('portfolio'))} · ${ctx.esc(portfolio.currency || overview.currency)}</div></div>`;
    list.appendChild(row);
  }
}

/* ---- Add / edit / delete dialogs (unchanged) ------------------------------------------------- */

function openAssetWizard() {
  const dlg = ctx.dialog(`<form method="dialog" class="dialog-card wealth-wizard"><div class="panel-head"><div><h2>${ctx.esc(t('addValue'))}</h2><div class="row-sub">${ctx.esc(t('chooseTypeHint'))}</div></div><button value="cancel" aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div><div class="wealth-type-grid" role="list" aria-label="${ctx.esc(t('chooseType'))}">${ASSET_KINDS.map(kind => `<button type="button" class="wealth-type" data-kind="${kind}" role="listitem"><strong>${ctx.esc(t(kind))}</strong></button>`).join('')}</div><div class="wealth-investment-hint">${ctx.esc(t('investmentHint'))}</div></form>`);
  dlg.querySelectorAll('[data-kind]').forEach(button => button.onclick = () => { const kind = button.dataset.kind; dlg.close(); openAssetForm(kind); });
  dlg.showModal();
}

function openAssetForm(kind, existing) {
  const asset = existing || {}; const selectedKind = existing?.kind || kind || 'other'; const currency = asset.currency || lastOverview?.currency || 'EUR';
  const dlg = ctx.dialog(`<form class="dialog-card"><div class="panel-head"><div><h2>${ctx.esc(ctx.get(existing ? 'networth.editAsset' : 'networth.newAsset'))}</h2><div class="row-sub">${ctx.esc(t(selectedKind))}</div></div><button type="button" data-close>×</button></div><label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="160" value="${ctx.esc(asset.name || '')}"></label><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.value'))}<input name="value" type="number" min="0" step="0.01" required value="${asset.currentValue ?? ''}"></label><label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(currency)}" minlength="3" maxlength="3" required></label></div><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.valuedAt'))}<input name="valuedAt" type="date" value="${dateValue(asset.valuedAt)}"></label><label>${ctx.esc(ctx.get('networth.growth'))}<input name="growth" type="number" step="0.01" value="${asset.annualGrowthRate ?? ''}"></label></div><label class="check"><input type="checkbox" name="include" ${asset.includeInNetWorth === false ? '' : 'checked'}> ${ctx.esc(ctx.get('networth.includeInNetWorth'))}</label><label>${ctx.esc(ctx.get('contracts.notes'))}<textarea name="notes" maxlength="1000" rows="2">${ctx.esc(asset.notes || '')}</textarea></label><div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get(existing ? 'common.apply' : 'common.create'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    const body = { name: fd.get('name'), kind: selectedKind, currentValue: Number(fd.get('value')), currency: String(fd.get('currency') || 'EUR').toUpperCase(), valuedAt: fd.get('valuedAt') || null, annualGrowthRate: numberOrNull(fd.get('growth')), includeInNetWorth: event.currentTarget.include.checked, notes: textOrNull(fd.get('notes')) };
    try { const created = await ctx.api(existing ? `api/assets/${existing.id}` : 'api/assets', jsonBody(body, existing ? 'PUT' : 'POST')); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderNetWorth(ctx); if (!existing && selectedKind === 'real_estate' && created?.id) await openRealEstateDetail(ctx, created, () => renderNetWorth(ctx)); }
    catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

async function openValuationHistory(asset) {
  let values;
  try { values = await ctx.api(`api/assets/${asset.id}/valuations`); } catch (error) { ctx.toast(error.message || ctx.get('common.error')); return; }
  const dlg = ctx.dialog(`<div class="dialog-card wealth-history-dialog"><div class="panel-head"><div><h2>${ctx.esc(asset.name)}</h2><div class="row-sub">${ctx.esc(t('valueHistory'))}</div></div><button type="button" data-close>×</button></div><div class="wealth-valuations">${values?.length ? values.map(value => `<div class="row wealth-valuation${value.isCurrent ? ' is-current' : ''}"><div class="row-main"><div class="row-title">${ctx.money(value.amount, value.currency)}${value.isCurrent ? ` <span class="tx-marker">${ctx.esc(t('current'))}</span>` : ''}</div><div class="row-sub">${ctx.esc(dateValue(value.valuedAt))} · ${ctx.esc(t(value.method || 'manual'))}</div></div></div>`).join('') : `<div class="row-sub">${ctx.esc(t('noValuations'))}</div>`}</div><form class="wealth-value-form"><h3>${ctx.esc(t('updateValue'))}</h3><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.value'))}<input name="amount" type="number" min="0" step="0.01" value="${asset.currentValue}" required></label><label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(asset.currency)}" minlength="3" maxlength="3" required></label></div><label>${ctx.esc(ctx.get('networth.valuedAt'))}<input name="valuedAt" type="date" value="${localDate(new Date())}"></label><div class="dialog-actions"><button type="submit">${ctx.esc(ctx.get('common.apply'))}</button></div></form></div>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    try { await ctx.api(`api/assets/${asset.id}/valuations`, jsonBody({ amount: Number(fd.get('amount')), currency: String(fd.get('currency') || asset.currency).toUpperCase(), valuedAt: fd.get('valuedAt') || null, method: 'manual', isAccepted: true })); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderNetWorth(ctx); }
    catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

function openLiabilityDialog(existing) {
  const item = existing || {}; const currency = item.currency || lastOverview?.currency || 'EUR';
  const options = (list, selected, prefix) => list.map(value => `<option value="${value}"${value === selected ? ' selected' : ''}>${ctx.esc(ctx.get(prefix + value))}</option>`).join('');
  const dlg = ctx.dialog(`<form class="dialog-card"><div class="panel-head"><h2>${ctx.esc(ctx.get(existing ? 'networth.editLiability' : 'networth.newLiability'))}</h2><button type="button" data-close>×</button></div><label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="160" value="${ctx.esc(item.name || '')}"></label><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.kind'))}<select name="kind">${options(LIABILITY_KINDS, item.kind || 'loan', 'networth.liabilityKind_')}</select></label><label>${ctx.esc(ctx.get('contracts.billingCycle'))}<select name="cycle">${options(CYCLES, item.paymentCycle || 'monthly', 'contracts.cycle_')}</select></label></div><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.balance'))}<input name="balance" type="number" min="0" step="0.01" required value="${item.currentBalance ?? ''}"></label><label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(currency)}" minlength="3" maxlength="3" required></label></div><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.interestRate'))}<input name="interest" type="number" step="0.001" value="${item.interestRate ?? ''}"></label><label>${ctx.esc(ctx.get('networth.payment'))}<input name="payment" type="number" step="0.01" value="${item.regularPayment ?? ''}"></label></div><div class="rule-grid"><label>${ctx.esc(ctx.get('contracts.nextDue'))}<input name="nextDue" type="date" value="${dateValue(item.nextDueDate)}"></label><label>${ctx.esc(ctx.get('contracts.endDate'))}<input name="end" type="date" value="${dateValue(item.endDate)}"></label></div><label class="check"><input type="checkbox" name="include" ${item.includeInNetWorth === false ? '' : 'checked'}> ${ctx.esc(ctx.get('networth.includeInNetWorth'))}</label><label>${ctx.esc(ctx.get('contracts.notes'))}<textarea name="notes" maxlength="1000" rows="2">${ctx.esc(item.notes || '')}</textarea></label><div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get(existing ? 'common.apply' : 'common.create'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    const body = { name: fd.get('name'), kind: fd.get('kind'), currentBalance: Number(fd.get('balance')), currency: String(fd.get('currency') || 'EUR').toUpperCase(), interestRate: numberOrNull(fd.get('interest')), regularPayment: numberOrNull(fd.get('payment')), paymentCycle: fd.get('cycle'), nextDueDate: fd.get('nextDue') || null, endDate: fd.get('end') || null, includeInNetWorth: event.currentTarget.include.checked, notes: textOrNull(fd.get('notes')) };
    try { await ctx.api(existing ? `api/liabilities/${existing.id}` : 'api/liabilities', jsonBody(body, existing ? 'PUT' : 'POST')); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderNetWorth(ctx); }
    catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

function assetToWrite(item) { return { name: item.name, kind: item.kind || 'other', currentValue: item.currentValue, currency: item.currency, valuedAt: item.valuedAt || null, annualGrowthRate: item.annualGrowthRate ?? null, includeInNetWorth: item.includeInNetWorth !== false, notes: item.notes || null }; }
function liabilityToWrite(item) { return { name: item.name, kind: item.kind || 'other', currentBalance: item.currentBalance, currency: item.currency, interestRate: item.interestRate ?? null, regularPayment: item.regularPayment ?? null, paymentCycle: item.paymentCycle || 'monthly', nextDueDate: item.nextDueDate || null, endDate: item.endDate || null, includeInNetWorth: item.includeInNetWorth !== false, notes: item.notes || null }; }
function jsonBody(body, method = 'POST') { return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }; }
function emptyRow() { return `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`; }
function dateValue(value) { return value ? String(value).slice(0, 10) : ''; }
function localDate(value) { return `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, '0')}-${String(value.getDate()).padStart(2, '0')}`; }
function numberOrNull(value) { const text = String(value ?? '').trim(); return text === '' ? null : Number(text); }
function textOrNull(value) { const text = String(value ?? '').trim(); return text || null; }
