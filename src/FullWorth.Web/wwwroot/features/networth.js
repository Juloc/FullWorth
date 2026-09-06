import { openRealEstateDetail } from './wealth-real-estate.js';
import { sectionCard, trendBadge, esc } from '../ui/ux-kit.js';
import { bindChartScrubber } from '../ui/chart-scrubber.js';
import { renderLoans, bindLoans } from './loans.js';

// Unified wealth view (UX rework §8 / delivery Phase D). The first screen explains wealth before it
// offers management tools: a trend card ("Wie entwickelt sich dein Vermögen?"), an allocation card
// ("Verteilung deines Vermögens"), the optional portfolio panel, and finally the asset/liability/loan
// editors behind a Details/Verwalten disclosure. Totals/history come from /api/wealth/*; type-specific
// modules own their detail logic. The optional emergency-fund card is driven by an explicit per-user,
// per-space target preference and never invents a target automatically.
let ctx = null;
let lastOverview = null;

// Trend window options for card 1's segmented control. `m` = months back from today.
// m=0 means all available history; m=-1 is reserved for the custom date range.
const WINDOWS = [
  { m: 6, sde: '6 M', sen: '6M', lde: 'Letzte 6 Monate', len: 'Last 6 months' },
  { m: 12, sde: '1 J', sen: '1Y', lde: 'Letzte 12 Monate', len: 'Last 12 months' },
  { m: 24, sde: '2 J', sen: '2Y', lde: 'Letzte 2 Jahre', len: 'Last 2 years' },
  { m: 60, sde: '5 J', sen: '5Y', lde: 'Letzte 5 Jahre', len: 'Last 5 years' },
  { m: 120, sde: '10 J', sen: '10Y', lde: 'Letzte 10 Jahre', len: 'Last 10 years' },
  { m: 0, sde: 'Max', sen: 'Max', lde: 'Gesamter verfügbarer Zeitraum', len: 'All available history' }
];

// View state so the trend window can be changed without re-fetching (or clobbering) the rest of the view.
const nw = { overview: null, history: [], assets: [], liabilities: [], accounts: [], accountGroups: [], portfolios: [], emergency: {}, currency: 'EUR', windowMonths: 12, customFrom: '', customTo: '' };

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
    window: 'Zeitraum', wealthCap: 'Vermögenswerte', noTrend: 'Noch keine Verlaufsdaten.',
    customRange: 'Freier Zeitraum', from: 'Von', to: 'Bis', applyRange: 'Anzeigen', invalidRange: 'Bitte gültigen Zeitraum wählen.',
    emergencyTitle: 'Notgroschen', emergencyHint: 'Liquiditätsreserve für unerwartete Ausgaben', emergencyTarget: 'Ziel', emergencyCurrent: 'Aktuell', emergencySetup: 'Notgroschen einrichten', emergencyEdit: 'Notgroschen bearbeiten', emergencyScope: 'Berücksichtigte Konten', emergencyAll: 'Alle liquiden Konten', emergencyEnabled: 'Notgroschen anzeigen', emergencyInvalid: 'Bitte ein Ziel größer als 0 eingeben.'
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
    window: 'Time range', wealthCap: 'Assets', noTrend: 'No history yet.',
    customRange: 'Custom range', from: 'From', to: 'To', applyRange: 'Show', invalidRange: 'Choose a valid date range.',
    emergencyTitle: 'Emergency fund', emergencyHint: 'Liquid reserve for unexpected expenses', emergencyTarget: 'Target', emergencyCurrent: 'Current', emergencySetup: 'Set up emergency fund', emergencyEdit: 'Edit emergency fund', emergencyScope: 'Included accounts', emergencyAll: 'All liquid accounts', emergencyEnabled: 'Show emergency fund', emergencyInvalid: 'Enter a target greater than 0.'
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
// No-op: the net-worth layout CSS lives in app.css (the app CSP blocks injected inline <style>).
function ensureUxStyles() { }

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

async function loadHistory(months, customFrom = nw.customFrom, customTo = nw.customTo) {
  const params = new URLSearchParams();
  if (months === -1) {
    if (customFrom) params.set('from', customFrom);
    if (customTo) params.set('to', customTo);
  } else {
    const end = new Date();
    params.set('to', localDate(end));
    if (months > 0) {
      const start = new Date(end);
      start.setMonth(end.getMonth() - months);
      params.set('from', localDate(start));
    }
  }
  try { return await ctx.api(`api/wealth/history?${params.toString()}`) || []; }
  catch { return []; }
}

export async function renderNetWorth(context) {
  ctx = context;
  ensureStyles();
  ensureUxStyles();
  if (!Number.isFinite(nw.windowMonths)) nw.windowMonths = 12;
  if (!nw.customTo) nw.customTo = localDate(new Date());
  if (!nw.customFrom) {
    const start = new Date();
    start.setFullYear(start.getFullYear() - 10);
    nw.customFrom = localDate(start);
  }

  let overview;
  try { overview = await ctx.api('api/wealth/overview'); }
  catch {
    const host = ctx.$('#view-networth');
    if (host) host.innerHTML = sectionCard(t('trendTitle'), `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.error'))}</div></div>`, { className: 'nw-hero' });
    return;
  }

  const [history, assets, liabilities, accounts, accountGroups, portfolios, emergencyPref] = await Promise.all([
    loadHistory(nw.windowMonths),
    ctx.api('api/assets').catch(() => []),
    ctx.api('api/liabilities').catch(() => []),
    ctx.api('api/accounts').catch(() => []),
    ctx.api('api/account-groups').catch(() => []),
    ctx.api('api/investments/portfolios').catch(() => []),
    ctx.api('api/preferences/wealth.emergencyFund').catch(() => ({ value: {} }))
  ]);

  lastOverview = overview;
  const linkedInvestmentAccounts = new Set((portfolios || []).map(item => item.accountId).filter(Boolean));
  nw.overview = overview;
  nw.history = history || [];
  nw.assets = assets || [];
  nw.liabilities = liabilities || [];
  nw.accounts = (accounts || []).filter(account => !linkedInvestmentAccounts.has(account.id));
  nw.accountGroups = accountGroups || [];
  nw.portfolios = portfolios || [];
  nw.emergency = emergencyPref?.value && typeof emergencyPref.value === 'object' ? emergencyPref.value : {};
  nw.currency = overview.currency;

  paintNetWorth();
}

// Build the whole view, then populate the management lists and (re)wire every control on fresh elements.
function paintNetWorth() {
  const host = ctx.$('#view-networth');
  if (!host) return;
  host.innerHTML = `${buildHeroCard()}${buildAllocationCard()}${buildEmergencyCard()}${investmentsCardMarkup()}${manageMarkup()}`;

  const hero = host.querySelector('.nw-hero');
  if (hero) wireHero(hero);
  host.querySelector('[data-action="new-asset"]')?.addEventListener('click', () => openAssetWizard());
  host.querySelector('[data-action="new-liability"]')?.addEventListener('click', () => openLiabilityDialog());
  host.querySelectorAll('[data-action="emergency-fund"]').forEach(button => button.addEventListener('click', () => openEmergencyFundDialog()));
  host.querySelectorAll('[data-emergency-w]').forEach(bar => { bar.style.width = bar.dataset.emergencyW + '%'; });

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

function currentRangeLabel() {
  if (nw.windowMonths === -1)
    return `${ctx.date(nw.customFrom)} – ${ctx.date(nw.customTo)}`;
  const win = WINDOWS.find(w => w.m === nw.windowMonths) || WINDOWS[1];
  return isDe() ? win.lde : win.len;
}

function heroTrendInner() {
  const stats = trendStats(nw.history);
  if (!stats.hasData) return `<span class="fw-trend">—</span>`;
  const sign = (!ctx.isPrivate() && stats.delta > 0) ? '+' : '';
  const cls = stats.delta > 0 ? 'positive' : stats.delta < 0 ? 'negative' : '';
  return `${trendBadge(stats.pct, true)}<div class="nw-trend-desc"><span class="nw-delta ${cls}">${sign}${ctx.money(stats.delta, nw.currency)}</span><span class="nw-window-label">${ctx.esc(currentRangeLabel())}</span></div>`;
}

// Smooth net-worth area chart: a Catmull-Rom-through-points curve emitted as a cubic-bezier <path>
// (rounded joins/caps via CSS), with a token-coloured <linearGradient> that fades the fill to
// transparent beneath the line and faint horizontal guides for depth. All geometry is derived from
// the real history values — only the smoothing/softening is cosmetic; no data is invented.
function smoothLinePath(pts) {
  if (!pts.length) return '';
  if (pts.length === 1) return `M${pts[0].x.toFixed(2)},${pts[0].y.toFixed(2)}`;
  let d = `M${pts[0].x.toFixed(2)},${pts[0].y.toFixed(2)}`;
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[i - 1] || pts[i]; const p1 = pts[i]; const p2 = pts[i + 1]; const p3 = pts[i + 2] || p2;
    const c1x = p1.x + (p2.x - p0.x) / 6; const c1y = p1.y + (p2.y - p0.y) / 6;
    const c2x = p2.x - (p3.x - p1.x) / 6; const c2y = p2.y - (p3.y - p1.y) / 6;
    d += ` C${c1x.toFixed(2)},${c1y.toFixed(2)} ${c2x.toFixed(2)},${c2y.toFixed(2)} ${p2.x.toFixed(2)},${p2.y.toFixed(2)}`;
  }
  return d;
}

function trendChartGeometry(history) {
  const usable = (history || []).filter(point => Number.isFinite(Number(point.netWorth)));
  if (!usable.length) return null;
  const values = usable.map(point => Number(point.netWorth));
  const min = Math.min(...values); const max = Math.max(...values); const span = max - min || 1;
  const width = 900; const height = 200; const pad = 14;
  const pts = values.map((value, index) => ({
    x: (index / (values.length - 1 || 1)) * width,
    y: height - ((value - min) / span) * (height - pad * 2) - pad
  }));
  return { usable, values, width, height, pts };
}

function trendChartSvg(history) {
  const geometry = trendChartGeometry(history);
  if (!geometry) return `<div class="row-sub nw-chart-empty">${ctx.esc(t('noTrend'))}</div>`;
  const { width, height, pts } = geometry;
  const line = smoothLinePath(pts);
  const area = `${line} L${width.toFixed(2)},${height} L0,${height} Z`;
  const grid = [0.25, 0.5, 0.75].map(f => `<line class="nw-chart-grid" x1="0" y1="${(height * f).toFixed(1)}" x2="${width}" y2="${(height * f).toFixed(1)}" vector-effect="non-scaling-stroke"/>`).join('');
  return `<svg viewBox="0 0 ${width} ${height}" preserveAspectRatio="none" role="img" aria-label="${ctx.esc(ctx.get('analytics.trend'))}"><defs><linearGradient id="nw-trend-grad" x1="0" y1="0" x2="0" y2="1"><stop class="nw-trend-grad-top" offset="0%"/><stop class="nw-trend-grad-bottom" offset="100%"/></linearGradient></defs>${grid}<path class="nw-chart-area" d="${area}"/><path class="nw-chart-line" d="${line}" fill="none" stroke-width="3" vector-effect="non-scaling-stroke"/></svg>`;
}

function bindNetWorthScrubber(hero) {
  const svg = hero.querySelector('.nw-chart svg');
  const valueEl = hero.querySelector('.nw-hero-value .fw-summary-value');
  const geometry = trendChartGeometry(nw.history);
  if (!svg || !valueEl || !geometry) return;
  const points = geometry.usable.map((point, index) => ({
    x: geometry.pts[index].x,
    label: ctx.date(point.date),
    markers: [{ y: geometry.pts[index].y }],
    data: point
  }));
  bindChartScrubber(svg, points, {
    initialIndex: points.length - 1,
    onChange: point => { valueEl.innerHTML = ctx.money(point.data.netWorth, nw.currency); },
    onReset: () => { valueEl.innerHTML = ctx.money(nw.overview.netWorth, nw.currency); },
    formatAria: point => `${point.label}: ${ctx.money(point.data.netWorth, nw.currency)}`
  });
}

function buildHeroCard() {
  const overview = nw.overview;
  const currency = nw.currency;
  const seg = `<div class="fw-cycle nw-windows" role="tablist" aria-label="${ctx.esc(t('window'))}">${WINDOWS.map(w =>
    `<button type="button" role="tab" data-window="${w.m}"${w.m === nw.windowMonths ? ' class="active" aria-selected="true"' : ' aria-selected="false"'}>${ctx.esc(isDe() ? w.sde : w.sen)}</button>`).join('')}</div>`;
  const custom = `<details class="nw-custom-range"${nw.windowMonths === -1 ? ' open' : ''}><summary>${ctx.esc(t('customRange'))}</summary><div class="nw-custom-range-fields"><label>${ctx.esc(t('from'))}<input type="date" data-range-from value="${ctx.esc(nw.customFrom)}"></label><label>${ctx.esc(t('to'))}<input type="date" data-range-to value="${ctx.esc(nw.customTo)}"></label><button type="button" class="secondary" data-range-apply>${ctx.esc(t('applyRange'))}</button><span class="nw-range-error" data-range-error hidden></span></div></details>`;
  const grossAssets = num(overview.totalAssets) + num(overview.accounts?.amount);
  const metrics = `<div class="nw-hero-metrics"><div><span class="nw-metric-label">${ctx.esc(ctx.get('dashboard.assets'))}</span><strong>${ctx.money(grossAssets, currency)}</strong></div><div><span class="nw-metric-label">${ctx.esc(ctx.get('dashboard.liabilities'))}</span><strong class="negative">${ctx.money(num(overview.totalLiabilities), currency)}</strong></div></div>`;
  const missing = (overview.missingCurrencies || []).join(', ');
  const fx = overview.isComplete ? '' : `<p class="nw-fx">${ctx.esc(t('fxIncomplete'))}${missing ? ` (${ctx.esc(missing)})` : ''}</p>`;
  const body = `<div class="nw-hero-head"><div class="nw-hero-value"><span class="fw-summary-label">${ctx.esc(ctx.get('dashboard.netWorth'))}</span><div class="fw-summary-value">${ctx.money(overview.netWorth, currency)}</div></div><div class="nw-hero-trend">${heroTrendInner()}</div></div>${seg}${custom}<div class="nw-chart">${trendChartSvg(nw.history)}</div>${metrics}${fx}`;
  return sectionCard(t('trendTitle'), body, { className: 'nw-hero' });
}

function repaintHeroTrend(hero) {
  const trendEl = hero.querySelector('.nw-hero-trend');
  if (trendEl) trendEl.innerHTML = heroTrendInner();
  const chartEl = hero.querySelector('.nw-chart');
  if (chartEl) chartEl.innerHTML = trendChartSvg(nw.history);
  bindNetWorthScrubber(hero);
}

function wireHero(hero) {
  bindNetWorthScrubber(hero);
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
      repaintHeroTrend(hero);
    });
  });

  hero.querySelector('[data-range-apply]')?.addEventListener('click', async () => {
    const from = hero.querySelector('[data-range-from]')?.value || '';
    const to = hero.querySelector('[data-range-to]')?.value || '';
    const error = hero.querySelector('[data-range-error]');
    if (!from || !to || from > to) {
      if (error) {
        error.textContent = t('invalidRange');
        error.hidden = false;
      }
      return;
    }
    if (error) error.hidden = true;
    nw.customFrom = from;
    nw.customTo = to;
    nw.windowMonths = -1;
    hero.querySelectorAll('[data-window]').forEach(other => {
      other.classList.remove('active');
      other.setAttribute('aria-selected', 'false');
    });
    nw.history = await loadHistory(-1, from, to);
    repaintHeroTrend(hero);
  });
}

/* ---- Card 2: "Verteilung deines Vermögens" --------------------------------------------------- */

// Donut geometry: a single ring whose segments are dash-slices of one radius. Round line-caps + a
// per-segment gap subtracted from each arc make the slices read as soft rounded arcs (the caps fill
// most of the gap back, leaving a hairline of breathing room). Colours stay on the --cat palette.
const DONUT_R = 80;
const DONUT_C = 2 * Math.PI * DONUT_R;

function donutSvg(segments, assetSum, label, currency) {
  const gap = segments.length > 1 ? 24 : 0; // circumference removed per slice; round caps add ~stroke-width back
  let cursor = 0;
  const arcs = segments.map(segment => {
    const full = (segment.amount / assetSum) * DONUT_C;
    const arc = Math.max(full - gap, 1);
    const dash = `${arc.toFixed(2)} ${(DONUT_C - arc).toFixed(2)}`;
    const offset = (-cursor).toFixed(2);
    cursor += full;
    return `<circle class="nw-donut-seg" cx="100" cy="100" r="${DONUT_R}" style="stroke:${segment.color}" stroke-dasharray="${dash}" stroke-dashoffset="${offset}"/>`;
  }).join('');
  return `<div class="nw-alloc-chart"><svg class="nw-donut" viewBox="0 0 200 200" role="img" aria-label="${ctx.esc(t('composition'))}"><circle class="nw-donut-track" cx="100" cy="100" r="${DONUT_R}"/><g transform="rotate(-90 100 100)">${arcs}</g></svg><div class="nw-donut-center"><span class="nw-donut-value">${ctx.money(assetSum, currency)}</span><span class="nw-donut-label">${ctx.esc(label)}</span></div></div>`;
}

function legendRow(label, amount, color, currency, negative = false, pct = null) {
  const pctText = (pct !== null && Number.isFinite(pct)) ? `<span class="nw-legend-pct">${pct.toFixed(0)}%</span>` : '';
  return `<div class="nw-legend-item"><span class="nw-dot" style="background:${color}"></span><span class="nw-legend-label">${ctx.esc(label)}</span>${pctText}<span class="nw-legend-amt${negative ? ' negative' : ''}">${ctx.money(amount, currency)}</span></div>`;
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

  // Allocation-first: a soft donut of the asset mix leads, its legend (with shares) sits under it, and
  // debt — which is not part of the asset ring — keeps its own thin bar beneath so it stays legible.
  const donut = assetSum > 0 ? donutSvg(segments, assetSum, t('wealthCap'), currency) : '';
  const debtBar = liabilities > 0
    ? `<div class="nw-alloc-block"><p class="nw-alloc-cap"><span>${ctx.esc(t('debt'))}</span><strong class="negative">${ctx.money(-liabilities, currency)}</strong></p><div class="fw-alloc nw-alloc-debt" role="img" aria-label="${ctx.esc(t('debt'))}"><span style="width:${Math.min(100, assetSum > 0 ? liabilities / assetSum * 100 : 100).toFixed(2)}%;background:var(--negative)"></span></div></div>`
    : '';

  const legendItems = segments.map(segment => legendRow(segment.label, segment.amount, segment.color, currency, false, assetSum > 0 ? segment.amount / assetSum * 100 : null));
  if (liabilities > 0) legendItems.push(legendRow(t('debt'), -liabilities, 'var(--negative)', currency, true));
  const legend = `<div class="nw-legend">${legendItems.join('')}</div>`;

  return sectionCard(t('allocationTitle'), `${donut}${legend}${debtBar}`, { className: 'nw-allocation' });
}

/* ---- Card 3: optional emergency fund / Notgroschen ----------------------------------------- */

function emergencyAccounts() {
  const pref = nw.emergency || {};
  return (nw.accounts || []).filter(account => {
    if (account.isActive === false || account.includeInNetWorth === false) return false;
    if (pref.accountId && String(account.id) !== String(pref.accountId)) return false;
    if (pref.accountGroupId && String(account.groupId || '') !== String(pref.accountGroupId)) return false;
    return true;
  });
}

function emergencyCurrentAmount() {
  return emergencyAccounts().reduce((sum, account) => {
    if (account.baseValue != null) return sum + num(account.baseValue);
    if (account.latestBalance && account.latestBalance.currency === nw.currency) return sum + num(account.latestBalance.amount);
    return sum;
  }, 0);
}

function emergencyScopeLabel() {
  const pref = nw.emergency || {};
  if (pref.accountId) {
    const account = (nw.accounts || []).find(item => String(item.id) === String(pref.accountId));
    return account?.displayName || account?.institutionName || t('emergencyAll');
  }
  if (pref.accountGroupId) {
    const group = (nw.accountGroups || []).find(item => String(item.id) === String(pref.accountGroupId));
    return group?.name || t('emergencyAll');
  }
  return t('emergencyAll');
}

function buildEmergencyCard() {
  const pref = nw.emergency || {};
  const target = num(pref.targetAmount);
  if (pref.enabled !== true || target <= 0) return '';
  const current = emergencyCurrentAmount();
  const pct = Math.max(0, Math.min(100, target > 0 ? current / target * 100 : 0));
  const body = `<div class="nw-emergency">
    <div class="fw-summary">
      <div><span class="fw-summary-label">${ctx.esc(t('emergencyCurrent'))}</span><span class="fw-summary-value">${ctx.money(current, nw.currency)}</span></div>
      <div><span class="fw-summary-label">${ctx.esc(t('emergencyTarget'))}</span><span class="fw-summary-value">${ctx.money(target, nw.currency)}</span></div>
    </div>
    <div class="progress ontrack"><span data-emergency-w="${pct.toFixed(2)}"></span></div>
    <div class="row-sub">${ctx.esc(Math.round(pct) + ' % · ' + emergencyScopeLabel())}</div>
  </div>`;
  return sectionCard(t('emergencyTitle'), body, {
    sub: t('emergencyHint'),
    className: 'nw-emergency-card',
    action: { label: t('emergencyEdit'), attr: 'data-action="emergency-fund"' }
  });
}

async function openEmergencyFundDialog() {
  const pref = nw.emergency || {};
  const accountOptions = (nw.accounts || []).filter(account => account.isActive !== false).map(account =>
    `<option value="account:${ctx.esc(account.id)}"${String(pref.accountId || '') === String(account.id) ? ' selected' : ''}>${ctx.esc(account.displayName || account.institutionName)}</option>`).join('');
  const groupOptions = (nw.accountGroups || []).map(group =>
    `<option value="group:${ctx.esc(group.id)}"${String(pref.accountGroupId || '') === String(group.id) ? ' selected' : ''}>${ctx.esc(group.name)}</option>`).join('');
  const scopeValue = pref.accountId ? 'account:' + pref.accountId : pref.accountGroupId ? 'group:' + pref.accountGroupId : '';
  const dlg = ctx.dialog(`<form class="dialog-card" method="dialog">
    <div class="panel-head"><h2>${ctx.esc(t('emergencyTitle'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label class="check"><input type="checkbox" name="enabled"${pref.enabled === true ? ' checked' : ''}>${ctx.esc(t('emergencyEnabled'))}</label>
    <label>${ctx.esc(t('emergencyTarget'))}<input type="number" min="0" step="0.01" inputmode="decimal" name="target" value="${ctx.esc(pref.targetAmount || '')}" placeholder="0,00"></label>
    <label>${ctx.esc(t('emergencyScope'))}<select name="scope"><option value="">${ctx.esc(t('emergencyAll'))}</option><optgroup label="${ctx.esc(t('accounts'))}">${accountOptions}</optgroup><optgroup label="${ctx.esc(isDe() ? 'Kontogruppen' : 'Account groups')}">${groupOptions}</optgroup></select></label>
    <div class="row-sub" data-error hidden></div>
    <div class="dialog-actions"><button type="button" class="ghost" data-close2>${ctx.esc(ctx.get('common.cancel'))}</button><button type="button" data-save>${ctx.esc(ctx.get('common.save'))}</button></div>
  </form>`);
  const scope = dlg.querySelector('[name="scope"]'); if (scope) scope.value = scopeValue;
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-close2]').onclick = () => dlg.close();
  dlg.querySelector('[data-save]').onclick = async () => {
    const fd = new FormData(dlg.querySelector('form'));
    const enabled = !!fd.get('enabled');
    const targetAmount = Number(fd.get('target') || 0);
    const error = dlg.querySelector('[data-error]');
    if (enabled && (!Number.isFinite(targetAmount) || targetAmount <= 0)) {
      if (error) { error.hidden = false; error.textContent = t('emergencyInvalid'); }
      return;
    }
    const selectedScope = String(fd.get('scope') || '');
    const next = {
      enabled,
      targetAmount: Number.isFinite(targetAmount) ? targetAmount : 0,
      accountId: selectedScope.startsWith('account:') ? selectedScope.slice(8) : null,
      accountGroupId: selectedScope.startsWith('group:') ? selectedScope.slice(6) : null
    };
    try {
      await ctx.api('api/preferences/wealth.emergencyFund', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(next)
      });
      dlg.close();
      await renderNetWorth(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
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
  const emergencyBody = `<div class="row-sub">${ctx.esc((nw.emergency?.enabled === true && num(nw.emergency?.targetAmount) > 0) ? ctx.money(nw.emergency.targetAmount, nw.currency) + ' · ' + emergencyScopeLabel() : t('emergencyHint'))}</div>`;
  const emergencyCard = sectionCard(t('emergencyTitle'), emergencyBody, { className: 'nw-sub', action: { label: (nw.emergency?.enabled === true ? t('emergencyEdit') : t('emergencySetup')), attr: 'data-action="emergency-fund"' } });
  return `<details class="nw-manage"><summary><span>${ctx.esc(t('manageTitle'))}</span><span class="nw-manage-hint">${ctx.esc(t('manageHint'))}</span></summary><div class="nw-manage-body">${emergencyCard}${accountsCard}${assetsCard}${liabilitiesCard}${loansCard}</div></details>`;
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

function askCoachAboutWealth(entityType, item, label, amount) {
  window.dispatchEvent(new CustomEvent('fullworth:coach-open', { detail: {
    entityType,
    entityId: item.id,
    entityLabel: label,
    details: {
      value: entityType === 'asset' ? String(amount ?? '') : '',
      balance: entityType === 'liability' ? String(amount ?? '') : '',
      currency: item.currency || '',
      kind: item.kind || '',
      includeInNetWorth: String(item.includeInNetWorth !== false)
    }
  }}));
}

function wealthCoachButton(onclick) {
  const button = document.createElement('button');
  button.type = 'button'; button.className = 'ghost nw-coach'; button.textContent = 'Coach';
  button.onclick = onclick; return button;
}

function assetRow(asset) {
  const row = document.createElement('div');
  row.className = `row nw-item${asset.includeInNetWorth ? '' : ' nw-excluded'}`;
  const detailAction = asset.kind === 'real_estate'
    ? `<button class="icon-button" data-detail title="${ctx.esc(t('details'))}" aria-label="${ctx.esc(t('details'))}">›</button>`
    : `<button class="icon-button" data-history title="${ctx.esc(t('valueHistory'))}" aria-label="${ctx.esc(t('valueHistory'))}">↗</button>`;
  row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(asset.name)}${asset.includeInNetWorth ? '' : ` <span class="tx-marker">${ctx.esc(ctx.get('networth.excluded'))}</span>`}</div><div class="row-sub">${ctx.esc(t(asset.kind || 'other'))}${asset.valuedAt ? ` · ${ctx.esc(dateValue(asset.valuedAt))}` : ''}</div></div><div class="row-side"><span class="amount">${ctx.money(asset.currentValue, asset.currency)}</span>${detailAction}<button class="icon-button" data-toggle title="${ctx.esc(ctx.get(asset.includeInNetWorth ? 'networth.exclude' : 'networth.include'))}">${asset.includeInNetWorth ? '◉' : '○'}</button><button class="icon-button" data-edit title="${ctx.esc(ctx.get('common.edit'))}">✎</button></div>`;
  row.querySelector('.row-side')?.prepend(wealthCoachButton(() => askCoachAboutWealth('asset', asset, asset.name, asset.currentValue)));
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
    row.querySelector('.row-side')?.prepend(wealthCoachButton(() => askCoachAboutWealth('liability', item, item.name, item.currentBalance)));
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
    row.appendChild(wealthCoachButton(() => window.dispatchEvent(new CustomEvent('fullworth:coach-open', { detail: {
      entityType:'portfolio', entityId:portfolio.id, entityLabel:portfolio.name,
      details:{currency:portfolio.currency||overview.currency||''}
    }}))));
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
