import { openRealEstateDetail, refreshWealthExtensions } from './wealth-real-estate.js';
import { sectionCard, trendBadge, esc, identityIcon } from '../ui/ux-kit.js';
import { bindChartScrubber } from '../ui/chart-scrubber.js';
import { renderLoans, bindLoans } from './loans.js';
import { loadFinanzguruCompleteness, finanzguruCompletenessNotice } from './data-completeness.js';
import { MoneyVariant, moneyClass, maskIdentifier } from '../ui/money.js';
import { balanceMeaningLine } from '../ui/balance-meaning.js';
import { openFormDialog, FieldKind } from '../ui/form-dialog.js';
import { basisSummary, lineKey, projectSeries as projectPreviewSeries, realValue, surplusAt } from './wealth-preview.js';

// Unified wealth view (UX rework §8 / delivery Phase D). The first screen explains wealth before it
// offers management tools: a trend card ("Wie entwickelt sich dein Vermögen?") whose chart carries the
// measured history AND the configurable forward preview past today, an allocation card
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
const nw = { overview: null, history: [], bookingActivity: [], importCompleteness: null, assets: [], liabilities: [], accounts: [], accountGroups: [], portfolios: [], emergency: {}, projection: {}, currency: 'EUR', windowMonths: 12, customFrom: '', customTo: '' };

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
    pensionAssets: 'Altersvorsorge',
    tiedLabel: 'davon gebunden',
    tiedNote: 'Altersvorsorge – dieses Guthaben steht erst ab Rentenbeginn zur Verfügung.',
    valueHistory: 'Werthistorie', details: 'Details', updateValue: 'Wert aktualisieren', current: 'Aktuell',
    noValuations: 'Noch keine Bewertungen vorhanden.', fxIncomplete: 'Gesamtsumme unvollständig: Für mindestens eine Währung fehlt ein Wechselkurs.',
    fxIncompleteWhich: 'Unvollständig, weil ein Wechselkurs fehlt',
    fxRatesUsed: 'Umgerechnet mit', fxRateAsOf: 'Kurs vom', fxRateStale: 'Kurs ist älter als ein paar Tage',
    moreDetails: 'Mehr Angaben',
    dataIncomplete: 'Daten unvollständig', composition: 'Zusammensetzung', accounts: 'Konten', manualAssets: 'Weitere Vermögenswerte', investments: 'Investments', debt: 'Schulden',
    real_estate: 'Immobilie', vehicle: 'Fahrzeug', precious_metal: 'Edelmetall', collectible: 'Sammlerstück / Wertgegenstand',
    receivable: 'Forderung / privates Darlehen', business_interest: 'Unternehmensbeteiligung', insurance_pension: 'Versicherung / Vorsorge', other: 'Sonstiger Wert',
    manual: 'Manuell', purchase_price: 'Kaufpreis', internal_estimate: 'FullWorth-Schätzung', external_provider: 'Externer Anbieter', appraisal: 'Gutachten', import: 'Import', legacy: 'Übernommen',
    trendTitle: 'Wie entwickelt sich dein Vermögen?', allocationTitle: 'Verteilung deines Vermögens',
    manageTitle: 'Details & Verwalten', manageHint: 'Vermögenswerte, Schulden und Kredite bearbeiten',
    window: 'Zeitraum', noTrend: 'Noch keine Verlaufsdaten.',
    // Deliberately NOT the same word as the hero's "Vermögenswerte": a donut cannot draw a negative
    // slice, so its total only ever covers the categories it can actually show. Naming it "Vermögenswerte"
    // too used to put two different numbers on screen under the identical label.
    assetMix: 'Anlagemix', assetMixNote: 'Werte mit negativem Saldo sind hier ausgeblendet, zählen aber zum Nettovermögen oben.',
    customRange: 'Freier Zeitraum', from: 'Von', to: 'Bis', applyRange: 'Anzeigen', invalidRange: 'Bitte gültigen Zeitraum wählen.',
    bookingActivity: 'Buchungen', bookingHistoryHint: 'Ältere Buchungen sind vorhanden. Ohne bestätigte Kontozuordnung und Kontostand werden sie als Buchungsaktivität gezeigt, nicht als Vermögensstand.',
    bookingOnlyHint: 'Importierte Buchungen sind vorhanden, aber noch kein belastbarer historischer Vermögensstand.',
    emergencyTitle: 'Notgroschen', emergencyHint: 'Liquiditätsreserve für unerwartete Ausgaben', emergencyTarget: 'Ziel', emergencyCurrent: 'Aktuell', emergencySetup: 'Notgroschen einrichten', emergencyEdit: 'Notgroschen bearbeiten', emergencyScope: 'Berücksichtigte Konten', emergencyAll: 'Alle liquiden Konten', emergencyEnabled: 'Notgroschen anzeigen', emergencyInvalid: 'Bitte ein Ziel größer als 0 eingeben.',
    projectionLabel: 'Vorschau', projectionAdjust: 'Vorschau anpassen', projectionOff: 'Aus',
    projectionAssumption: 'Annahme', projectionPerMonth: 'pro Monat', projectionPerYear: 'pro Jahr',
    projectionNotMeasured: 'gerechnet, nicht gemessen',
    projectionNotInRange: 'Die Vorschau beginnt heute. Wähle einen Zeitraum, der heute enthält, um sie im Verlauf zu sehen.',
    projectionHorizon: 'Zeitraum', projectionYearsShort: 'J.', projectionYearsLong: 'Jahren',
    projectionSavings: 'Sparrate pro Monat', projectionReturn: 'Rendite pro Jahr (%)', projectionInflation: 'Inflation pro Jahr (%)',
    projectionFromHistory: 'Sparrate aus deinem eigenen Verlauf berechnet:',
    projectionHistoryWas: 'Aus deinem Verlauf ergäbe sich:',
    projectionNoHistory: 'Für eine Sparrate aus dem Verlauf fehlt noch Historie. Trag sie selbst ein.',
    projectionIn: 'In', projectionReal: 'Kaufkraft von heute',
    basisTitle: 'Woraus die Vorschau rechnet', basisIncome: 'Einnahmen', basisFixed: 'Fixkosten',
    basisVariable: 'Sonstige Ausgaben', basisSurplus: 'Überschuss pro Monat',
    basisObserved: 'Durchschnitt der letzten', basisMonths: 'Monate',
    basisBudget: 'Budgetgrenzen zum Vergleich', basisGrowth: 'Erwartete Steigerung pro Jahr',
    basisUseObserved: 'Stattdessen aus dem Vermögensverlauf schätzen',
    basisNoIncome: 'Keine Einnahmen hinterlegt – die Vorschau schätzt aus dem Vermögensverlauf.',
    projectionContributed: 'Eingezahlt', projectionGrowth: 'Wertzuwachs',
    projectionNote: 'Gerechnet mit monatlicher Verzinsung deiner Annahmen. Steuern, Gebühren und Schwankungen sind nicht enthalten. Das ist keine Prognose und keine Anlageempfehlung.'
  },
  en: {
    addValue: 'Add asset', chooseType: 'Asset type', chooseTypeHint: 'Choose a type. Additional details can be completed later.',
    investmentHint: 'Stocks, ETFs and other securities are managed through an investment portfolio, not as manual assets.',
    investmentTotal: 'Investments total', portfolio: 'Portfolio', active: 'Active',
    realEstate: 'Real estate', vehicles: 'Vehicles', otherValues: 'Other assets',
    pensionAssets: 'Pension',
    tiedLabel: 'of which tied',
    tiedNote: 'Pension — this balance is not available to you before retirement.',
    valueHistory: 'Value history', details: 'Details', updateValue: 'Update value', current: 'Current', noValuations: 'No valuations yet.',
    fxIncomplete: 'Total is incomplete: at least one required FX rate is missing.', dataIncomplete: 'Data incomplete', composition: 'Composition',
    fxIncompleteWhich: 'Incomplete because an FX rate is missing',
    fxRatesUsed: 'Converted at', fxRateAsOf: 'rate of', fxRateStale: 'this rate is more than a few days old',
    moreDetails: 'More details',
    accounts: 'Accounts', manualAssets: 'Other assets', investments: 'Investments', debt: 'Debt',
    real_estate: 'Real estate', vehicle: 'Vehicle', precious_metal: 'Precious metal', collectible: 'Collectible / valuable',
    receivable: 'Receivable / private loan', business_interest: 'Business interest', insurance_pension: 'Insurance / pension', other: 'Other asset',
    manual: 'Manual', purchase_price: 'Purchase price', internal_estimate: 'FullWorth estimate', external_provider: 'External provider', appraisal: 'Appraisal', import: 'Import', legacy: 'Migrated',
    trendTitle: 'How is your wealth developing?', allocationTitle: 'Your wealth distribution',
    manageTitle: 'Details & manage', manageHint: 'Edit assets, liabilities and loans',
    window: 'Time range', noTrend: 'No history yet.',
    assetMix: 'Asset mix', assetMixNote: 'Values with a negative balance are hidden here but still count toward the net worth above.',
    customRange: 'Custom range', from: 'From', to: 'To', applyRange: 'Show', invalidRange: 'Choose a valid date range.',
    bookingActivity: 'Bookings', bookingHistoryHint: 'Older bookings are available. Without a confirmed account mapping and balance they are shown as booking activity, not as net worth.',
    bookingOnlyHint: 'Imported bookings are available, but no reliable historical net-worth value exists yet.',
    emergencyTitle: 'Emergency fund', emergencyHint: 'Liquid reserve for unexpected expenses', emergencyTarget: 'Target', emergencyCurrent: 'Current', emergencySetup: 'Set up emergency fund', emergencyEdit: 'Edit emergency fund', emergencyScope: 'Included accounts', emergencyAll: 'All liquid accounts', emergencyEnabled: 'Show emergency fund', emergencyInvalid: 'Enter a target greater than 0.',
    projectionLabel: 'Projection', projectionAdjust: 'Adjust projection', projectionOff: 'Off',
    projectionAssumption: 'Assumption', projectionPerMonth: 'per month', projectionPerYear: 'per year',
    projectionNotMeasured: 'calculated, not measured',
    projectionNotInRange: 'The projection starts today. Choose a range that includes today to see it in the chart.',
    projectionHorizon: 'Horizon', projectionYearsShort: 'yr', projectionYearsLong: 'years',
    projectionSavings: 'Savings per month', projectionReturn: 'Return per year (%)', projectionInflation: 'Inflation per year (%)',
    projectionFromHistory: 'Savings rate calculated from your own history:',
    projectionHistoryWas: 'Your history would suggest:',
    projectionNoHistory: 'Not enough history for a savings rate yet. Enter one yourself.',
    projectionIn: 'In', projectionReal: "In today's purchasing power",
    basisTitle: 'What the preview is built from', basisIncome: 'Income', basisFixed: 'Fixed costs',
    basisVariable: 'Other spending', basisSurplus: 'Surplus per month',
    basisObserved: 'Average of the last', basisMonths: 'months',
    basisBudget: 'Budget limits, for comparison', basisGrowth: 'Expected increase per year',
    basisUseObserved: 'Estimate from the net-worth curve instead',
    basisNoIncome: 'No income configured - the preview estimates from the net-worth curve.',
    projectionContributed: 'Paid in', projectionGrowth: 'Growth',
    projectionNote: 'Calculated with monthly compounding of your assumptions. Taxes, fees and volatility are not included. This is not a forecast and not investment advice.'
  }
};

function isDe() { return !(document.documentElement.lang || '').toLowerCase().startsWith('en'); }
function t(key) { return COPY[isDe() ? 'de' : 'en'][key] || key; }
function num(value) { const n = Number(value); return Number.isFinite(n) ? n : 0; }

function ensureStyles() {
  if (document.querySelector('link[data-wealth-assets-css]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/styles/features/wealth-assets.css';
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

function rangeParams(months, customFrom = nw.customFrom, customTo = nw.customTo) {
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
  return params;
}

async function loadHistory(months, customFrom = nw.customFrom, customTo = nw.customTo) {
  try { return await ctx.api(`api/wealth/history?${rangeParams(months, customFrom, customTo).toString()}`) || []; }
  catch { return []; }
}

async function loadBookingActivity(months, customFrom = nw.customFrom, customTo = nw.customTo) {
  try { return await ctx.api(`api/wealth/booking-activity?${rangeParams(months, customFrom, customTo).toString()}`) || []; }
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

  const [history, bookingActivity, importCompleteness, assets, liabilities, accounts, accountGroups, portfolios, emergencyPref, projectionPref, previewBasis] = await Promise.all([
    loadHistory(nw.windowMonths),
    loadBookingActivity(nw.windowMonths),
    loadFinanzguruCompleteness(ctx.api),
    ctx.api('api/assets').catch(() => []),
    ctx.api('api/liabilities').catch(() => []),
    ctx.api('api/accounts').catch(() => []),
    ctx.api('api/account-groups').catch(() => []),
    ctx.api('api/investments/portfolios').catch(() => []),
    ctx.api('api/preferences/wealth.emergencyFund').catch(() => ({ value: {} })),
    ctx.api('api/preferences/wealth.projection').catch(() => ({ value: {} })),
    ctx.api('api/wealth/preview-basis?months=6').catch(() => null)
  ]);

  lastOverview = overview;
  // Authoritative, from the same calculation that produced the totals. Deriving it from the portfolio
  // list would hide accounts the total still counts: a depot that cannot value itself (no trades, no
  // price, no rate) does not replace its account's balance, so that account stays in Accounts.
  const linkedInvestmentAccounts = new Set(
    (Array.isArray(overview?.accountsRepresentedByDepots) ? overview.accountsRepresentedByDepots : [])
      .map(id => String(id)));
  nw.overview = overview;
  nw.history = history || [];
  nw.bookingActivity = bookingActivity || [];
  nw.importCompleteness = importCompleteness || null;
  nw.assets = assets || [];
  nw.liabilities = liabilities || [];
  nw.accounts = (accounts || []).filter(account => !linkedInvestmentAccounts.has(String(account.id)));
  nw.accountGroups = accountGroups || [];
  nw.portfolios = portfolios || [];
  nw.emergency = emergencyPref?.value && typeof emergencyPref.value === 'object' ? emergencyPref.value : {};
  nw.projection = projectionPref?.value && typeof projectionPref.value === 'object' ? projectionPref.value : {};
  nw.previewBasis = previewBasis || null;
  nw.currency = overview.currency;

  paintNetWorth();
}

// Build the whole view, then populate the management lists and (re)wire every control on fresh elements.
function paintNetWorth() {
  const host = ctx.$('#view-networth');
  if (!host) return;
  const completeness = finanzguruCompletenessNotice(nw.importCompleteness, { scope: 'wealth', lang: isDe() ? 'de' : 'en' });
  host.innerHTML = `${completeness}${buildHeroCard()}${buildAllocationCard()}${buildEmergencyCard()}${investmentsCardMarkup()}${manageMarkup()}`;

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
  void refreshWealthExtensions();

  // Loans are owned by features/loans.js. Re-bind its "add" button (our rebuilt markup replaced the
  // static one) and re-render #nw-loans so the list survives internal refreshes, not just view opens.
  bindLoans(ctx);
  renderLoans(ctx);
}

/* ---- Card 1: "Wie entwickelt sich dein Vermögen?" -------------------------------------------- */

function trendStats(history) {
  // measuredPoints(), not a Number.isFinite() filter: Number(null) is 0 and finite, so an unknown
  // point used to enter the delta as a zero and turn "we do not know yet" into a rise from nothing.
  const usable = measuredPoints(history);
  if (usable.length < 2) return { hasData: false, pct: 0, delta: 0 };
  const first = Number(usable[0].netWorth);
  const last = Number(usable.at(-1).netWorth);
  const delta = last - first;
  const pct = first !== 0 ? (delta / Math.abs(first)) * 100 : (delta !== 0 ? 100 : 0);
  // A near-zero baseline (history that starts at ~0) makes the percentage astronomical and meaningless
  // (e.g. "+698.648 %"); there the absolute change is the figure that matters, so flag when the % is worth
  // showing at all — the % badge is suppressed when the start value is a negligible fraction of the current.
  const pctMeaningful = Math.abs(first) >= Math.abs(last) * 0.05;
  return { hasData: true, pct, delta, pctMeaningful };
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
  return `${stats.pctMeaningful ? trendBadge(stats.pct, true) : ''}<div class="nw-trend-desc"><span class="nw-delta ${cls}">${sign}${ctx.money(stats.delta, nw.currency)}</span><span class="nw-window-label">${ctx.esc(currentRangeLabel())}</span></div>`;
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

function parseChartDate(value) {
  const time = Date.parse(`${String(value || '').slice(0, 10)}T00:00:00Z`);
  return Number.isFinite(time) ? time : null;
}

function selectedChartDomain(history, activity) {
  const end = parseChartDate(nw.windowMonths === -1 ? nw.customTo : localDate(new Date()));
  let start = null;
  if (nw.windowMonths === -1) {
    start = parseChartDate(nw.customFrom);
  } else if (nw.windowMonths > 0) {
    const date = new Date();
    date.setMonth(date.getMonth() - nw.windowMonths);
    start = parseChartDate(localDate(date));
  } else {
    const dates = [
      ...(history || []).map(point => parseChartDate(point.date)),
      ...(activity || []).map(point => parseChartDate(point.month))
    ].filter(Number.isFinite);
    start = dates.length ? Math.min(...dates) : end;
  }
  const safeEnd = Number.isFinite(end) ? end : Date.now();
  const safeStart = Number.isFinite(start) ? start : safeEnd;
  return { start: Math.min(safeStart, safeEnd), end: Math.max(safeStart, safeEnd) };
}

// A net worth that is not there is UNKNOWN, not zero - and `Number(null)` is 0, which is exactly how
// an unknown value sneaks in as a measurement. One helper for every reader of a net-worth figure.
function measuredValue(raw) {
  if (raw === null || raw === undefined || raw === '') return null;
  const value = Number(raw);
  return Number.isFinite(value) ? value : null;
}

// A measured history point: a netWorth of null is a deliberate "unknown", so it is dropped from the
// curve (which leaves the gap it should) instead of being drawn at the baseline.
function measuredPoints(history = nw.history) {
  return (history || []).filter(point => measuredValue(point?.netWorth) !== null);
}

function activityPoints(activity = nw.bookingActivity) {
  return (activity || []).filter(point => Number.isFinite(parseChartDate(point.month)) && Number(point.count) > 0);
}

// The forward preview shares ONE chart with the measured history: the same value scale and — as long
// as it fits — the same time scale, because a second scale would make a projection look like a
// measurement drawn next to it. Only when the horizon would squeeze the measured window into a sliver
// (10 years of preview against 12 months of history) does the forward part get capped to this share of
// the width; below the cap both halves keep identical pixels-per-day, so the axis stays uniform.
const FORECAST_MAX_WIDTH_SHARE = 0.5;

function trendChartGeometry(history, activity = nw.bookingActivity) {
  const usable = measuredPoints(history);
  const activityUsable = activityPoints(activity);
  if (!usable.length && !activityUsable.length) return null;
  const values = usable.map(point => Number(point.netWorth));
  const domain = selectedChartDomain(usable, activityUsable);
  const model = forecastModel();
  const forecast = forecastIsInWindow(model, domain) ? model : null;
  // The projected values belong in the scale, or the forward curve would run off the top of the card.
  const scaleValues = values.concat(forecast ? forecast.points.map(point => point.value) : []);
  const min = scaleValues.length ? Math.min(...scaleValues) : 0;
  const max = scaleValues.length ? Math.max(...scaleValues) : 1;
  const span = max - min || 1;
  const width = 900; const height = 200; const pad = 14; const activityBand = 24;
  const plotHeight = height - activityBand;
  const measuredSpan = Math.max(1, domain.end - domain.start);
  const forecastEnd = forecast ? parseChartDate(forecast.points.at(-1).date) : null;
  const forwardSpan = Number.isFinite(forecastEnd) ? Math.max(0, forecastEnd - domain.end) : 0;
  const forwardShare = forwardSpan > 0
    ? Math.min(FORECAST_MAX_WIDTH_SHARE, forwardSpan / (measuredSpan + forwardSpan))
    : 0;
  const splitX = width * (1 - forwardShare);
  const xForDate = value => {
    const time = parseChartDate(value);
    if (!Number.isFinite(time)) return 0;
    if (forwardSpan <= 0 || time <= domain.end)
      return Math.max(0, Math.min(splitX, ((time - domain.start) / measuredSpan) * splitX));
    return Math.max(splitX, Math.min(width, splitX + ((time - domain.end) / forwardSpan) * (width - splitX)));
  };
  const yFor = value => plotHeight - ((value - min) / span) * (plotHeight - pad * 2) - pad;
  const pts = usable.map((point, index) => ({ x: xForDate(point.date), y: yFor(values[index]) }));
  const forecastPts = forecast ? forecast.points.map(point => ({ x: xForDate(point.date), y: yFor(point.value) })) : [];
  // The dashed path starts at the last measured point so the curve is one line, not two: between that
  // point and today nothing was measured either, and dashed is exactly what that stretch is.
  const forecastPath = forecast ? (pts.length ? [pts.at(-1)] : []).concat(forecastPts) : [];
  return {
    usable, activityUsable, values, width, height, plotHeight, pts, xForDate, yFor,
    forecast, forecastPts, forecastPath,
    todayX: forecast ? xForDate(forecast.anchor.date) : null
  };
}

function bookingActivityMarkup(geometry) {
  if (!geometry?.activityUsable?.length) return '';
  const maxCount = Math.max(...geometry.activityUsable.map(point => Number(point.count) || 0), 1);
  return geometry.activityUsable.map(point => {
    const count = Number(point.count) || 0;
    const imported = Number(point.importedCount) || 0;
    const barHeight = 4 + Math.round((count / maxCount) * 14);
    const x = geometry.xForDate(point.month);
    const y = geometry.height - barHeight - 2;
    const title = `${ctx.date(point.month)}: ${count} ${t('bookingActivity')}${imported ? ` (${imported} Import)` : ''}`;
    return `<rect class="nw-chart-activity${imported ? ' imported' : ''}" x="${Math.max(0, x - 1.5).toFixed(2)}" y="${y}" width="3" height="${barHeight}" rx="1.5"><title>${ctx.esc(title)}</title></rect>`;
  }).join('');
}

function bookingHistoryHint() {
  if (!nw.bookingActivity?.length) return '';
  if (!nw.history?.length)
    return `<div class="nw-booking-history-hint"><span class="nw-booking-history-dot"></span><span>${ctx.esc(t('bookingOnlyHint'))}</span></div>`;
  const firstActivity = Math.min(...nw.bookingActivity.map(point => parseChartDate(point.month)).filter(Number.isFinite));
  const firstHistory = Math.min(...nw.history.map(point => parseChartDate(point.date)).filter(Number.isFinite));
  if (!Number.isFinite(firstActivity) || !Number.isFinite(firstHistory) || firstActivity >= firstHistory) return '';
  return `<div class="nw-booking-history-hint"><span class="nw-booking-history-dot"></span><span>${ctx.esc(t('bookingHistoryHint'))}</span></div>`;
}

function trendChartSvg(history) {
  const geometry = trendChartGeometry(history, nw.bookingActivity);
  if (!geometry) return `<div class="row-sub nw-chart-empty">${ctx.esc(t('noTrend'))}</div>`;
  const { width, height, plotHeight, pts } = geometry;
  const grid = [0.25, 0.5, 0.75].map(f => `<line class="nw-chart-grid" x1="0" y1="${(plotHeight * f).toFixed(1)}" x2="${width}" y2="${(plotHeight * f).toFixed(1)}" vector-effect="non-scaling-stroke"/>`).join('');
  const activity = bookingActivityMarkup(geometry);
  let wealth = '';
  if (pts.length) {
    const line = smoothLinePath(pts);
    const firstX = pts[0].x.toFixed(2);
    const lastX = pts.at(-1).x.toFixed(2);
    const area = `${line} L${lastX},${plotHeight} L${firstX},${plotHeight} Z`;
    wealth = `<path class="nw-chart-area" d="${area}"/><path class="nw-chart-line" d="${line}" fill="none" stroke-width="3" vector-effect="non-scaling-stroke"/>`;
  }
  // The forward preview: dashed, no area fill, and separated from the measured part by a "today"
  // divider. It is drawn on top of the measured curve so the join stays visible, and it is the only
  // part of this card the projection is allowed to touch.
  let forecast = '';
  if (geometry.forecast && geometry.forecastPath.length > 1) {
    const divider = Number.isFinite(geometry.todayX)
      ? `<line class="nw-chart-today" x1="${geometry.todayX.toFixed(2)}" y1="0" x2="${geometry.todayX.toFixed(2)}" y2="${plotHeight}" vector-effect="non-scaling-stroke"/>`
      : '';
    forecast = `${divider}<path class="nw-chart-line nw-chart-forecast" d="${smoothLinePath(geometry.forecastPath)}" fill="none" stroke-width="3" vector-effect="non-scaling-stroke"/>`;
  }
  const label = `${ctx.get('analytics.trend')}${geometry.forecast ? ` · ${t('projectionLabel')}` : ''}`;
  const empty = pts.length ? '' : `<text class="nw-chart-no-wealth" x="${width / 2}" y="${plotHeight / 2}" text-anchor="middle">${ctx.esc(t('noTrend'))}</text>`;
  return `<svg viewBox="0 0 ${width} ${height}" preserveAspectRatio="none" role="img" aria-label="${ctx.esc(label)}"><defs><linearGradient id="nw-trend-grad" x1="0" y1="0" x2="0" y2="1"><stop class="nw-trend-grad-top" offset="0%"/><stop class="nw-trend-grad-bottom" offset="100%"/></linearGradient></defs>${grid}${activity}${wealth}${forecast}${empty}</svg>${bookingHistoryHint()}`;
}

function bindNetWorthScrubber(hero) {
  const svg = hero.querySelector('.nw-chart svg');
  const valueEl = hero.querySelector('.nw-hero-value .fw-summary-value');
  const geometry = trendChartGeometry(nw.history, nw.bookingActivity);
  if (!svg || !valueEl || !geometry) return;
  const measured = geometry.usable.map((point, index) => ({
    x: geometry.pts[index].x,
    label: ctx.date(point.date),
    markers: [{ y: geometry.pts[index].y }],
    projected: false,
    date: point.date,
    value: Number(point.netWorth)
  }));
  // A projected point is not a measurement, so it gets its own marker, says "gerechnet, nicht
  // gemessen" in the preview line and in the screen-reader text, and never reaches the headline
  // figure. Index 0 of the forecast is today's anchor, which the headline already shows.
  const projected = geometry.forecast
    ? geometry.forecast.points.slice(1).map((point, index) => ({
      x: geometry.forecastPts[index + 1].x,
      label: ctx.date(point.date),
      markers: [{ y: geometry.forecastPts[index + 1].y, className: 'projected' }],
      projected: true,
      date: point.date,
      value: point.value
    }))
    : [];
  const points = measured.concat(projected);
  if (!points.length) return;
  const headline = () => { valueEl.innerHTML = ctx.money(nw.overview?.netWorth, nw.currency); };
  bindChartScrubber(svg, points, {
    initialIndex: measured.length ? measured.length - 1 : 0,
    onChange: point => {
      if (point.projected) {
        headline();
        setForecastReadout(hero, point);
        return;
      }
      valueEl.innerHTML = ctx.money(point.value, nw.currency);
      setForecastReadout(hero, null);
    },
    onReset: () => {
      headline();
      setForecastReadout(hero, null);
    },
    formatAria: point => point.projected
      ? `${t('projectionLabel')} ${point.label}: ${ctx.money(point.value, nw.currency)} (${t('projectionNotMeasured')})`
      : `${point.label}: ${ctx.money(point.value, nw.currency)}`
  });
}

// "davon gebunden": the part of the figure above that cannot be spent before retirement. It reads the
// SAME component the allocation ring uses - there is deliberately no separate "tied" total in the API,
// because two fields carrying one number drift apart. Rendered only when there is an amount: a
// "0,00 € gebunden" row would state a restriction that does not exist.
function tiedWealthLine(overview, currency) {
  const tied = num(overview?.pensionAssets?.amount);
  if (tied <= 0.005) return '';
  return `<p class="row-sub" data-tied-wealth>${ctx.esc(t('tiedLabel'))}: ${ctx.money(tied, currency)} · ${ctx.esc(t('tiedNote'))}</p>`;
}

function buildHeroCard() {
  const overview = nw.overview;
  const currency = nw.currency;
  const seg = `<div class="fw-cycle nw-windows" role="tablist" aria-label="${ctx.esc(t('window'))}">${WINDOWS.map(w =>
    `<button type="button" role="tab" data-window="${w.m}"${w.m === nw.windowMonths ? ' class="active" aria-selected="true"' : ' aria-selected="false"'}>${ctx.esc(isDe() ? w.sde : w.sen)}</button>`).join('')}</div>`;
  const custom = `<details class="nw-custom-range"${nw.windowMonths === -1 ? ' open' : ''}><summary>${ctx.esc(t('customRange'))}</summary><div class="nw-custom-range-fields"><label>${ctx.esc(t('from'))}<input type="date" data-range-from value="${ctx.esc(nw.customFrom)}"></label><label>${ctx.esc(t('to'))}<input type="date" data-range-to value="${ctx.esc(nw.customTo)}"></label><button type="button" class="secondary" data-range-apply>${ctx.esc(t('applyRange'))}</button><span class="nw-range-error" data-range-error hidden></span></div></details>`;
  const grossAssets = num(overview.totalAssets) + num(overview.accounts?.amount);
  const metrics = `<div class="nw-hero-metrics"><div><span class="nw-metric-label">${ctx.esc(ctx.get('dashboard.assets'))}</span><strong>${ctx.money(grossAssets, currency)}</strong></div><div><span class="nw-metric-label">${ctx.esc(ctx.get('dashboard.liabilities'))}</span><strong class="negative">${ctx.money(num(overview.totalLiabilities), currency)}</strong></div></div>`;
  const fx = overview.isComplete ? '' : `<p class="nw-fx">${fxIncompleteText(overview)}</p>`;
  const rates = fxRatesText(overview);
  const rateLine = rates ? `<p class="nw-fx nw-fx-rates">${rates}</p>` : '';
  const body = `<div class="nw-hero-head"><div class="nw-hero-value"><span class="fw-summary-label">${ctx.esc(ctx.get('dashboard.netWorth'))}</span><div class="fw-summary-value">${ctx.money(overview.netWorth, currency)}</div>${tiedWealthLine(overview, currency)}</div><div class="nw-hero-trend">${heroTrendInner()}</div></div>${seg}${custom}<div class="nw-chart">${trendChartSvg(nw.history)}</div>${forecastMarkup()}${metrics}${fx}${rateLine}`;
  return sectionCard(t('trendTitle'), body, { className: 'nw-hero' });
}

// A cross-currency total was a bare number: which rate produced it, and how old that rate was, were
// nowhere on the screen - and the snapshot accepts a fixing up to two weeks old for a day that has
// none, so a stale rate looked exactly like this morning's. The backend reports the rate and its
// fixing date per currency now; this is where the user finally sees it.
//
// Only currencies that were actually converted appear: an amount already in the base currency was not
// converted, so claiming a rate of 1 'as of' some date would invent provenance.
function fxRatesText(overview) {
  const used = new Map();
  for (const key of ['accounts', 'manualAssets', 'investments', 'loans', 'otherLiabilities'])
    for (const rate of overview[key]?.ratesUsed || [])
      if (!used.has(rate.currency)) used.set(rate.currency, rate);
  if (!used.size) return '';

  const parts = [...used.values()]
    .sort((a, b) => String(a.currency).localeCompare(String(b.currency)))
    .map(rate => {
      const asOf = rate.rateDate ? ` · ${t('fxRateAsOf')} ${ctx.date(rate.rateDate)}` : '';
      const stale = rate.isStale ? ` <span class="nw-fx-stale" title="${ctx.esc(t('fxRateStale'))}">!</span>` : '';
      return `${ctx.esc(rate.currency)} ${ctx.esc(fxRateValue(rate.rate))}${ctx.esc(asOf)}${stale}`;
    });
  return `${ctx.esc(t('fxRatesUsed'))}: ${parts.join(' · ')}`;
}

// A rate is not money, so it is not formatted as money. Six significant digits keep IDR (0.0000559)
// readable without printing a wall of zeros for USD (0.862).
function fxRateValue(rate) {
  const value = Number(rate);
  if (!Number.isFinite(value) || value === 0) return '—';
  const digits = Math.abs(value) >= 1 ? 4 : Math.min(8, 2 - Math.floor(Math.log10(Math.abs(value))) + 3);
  return value.toLocaleString(isDe() ? 'de-DE' : 'en-GB', { maximumFractionDigits: digits });
}

// "Gesamtsumme unvollständig" plus a flat list of currencies said that something was missing without
// saying which figure it made incomplete. Each component now reports the currencies IT could not
// convert, so the line names the value and the rate together. The flat list stays as the fallback for
// a backend that does not send the per-component breakdown.
function fxIncompleteText(overview) {
  const parts = [
    ['accounts', t('accounts')],
    ['manualAssets', t('manualAssets')],
    ['investments', t('investments')],
    ['loans', t('debt')],
    ['otherLiabilities', t('debt')]
  ]
    .map(([key, label]) => [label, (overview[key]?.missingCurrencies || []).join(', ')])
    .filter(([, currencies]) => currencies)
    .map(([label, currencies]) => `${label} (${currencies})`);
  if (parts.length) return `${ctx.esc(t('fxIncompleteWhich'))}: ${ctx.esc([...new Set(parts)].join(' · '))}`;
  const missing = (overview.missingCurrencies || []).join(', ');
  return `${ctx.esc(t('fxIncomplete'))}${missing ? ` (${ctx.esc(missing)})` : ''}`;
}

function repaintHeroTrend(hero) {
  const trendEl = hero.querySelector('.nw-hero-trend');
  if (trendEl) trendEl.innerHTML = heroTrendInner();
  // The projection's default savings rate is read off the history that is currently loaded, so it has
  // to follow the window - otherwise the preview would keep quoting a range that is no longer on
  // screen. The inputs are rebuilt here (their VALUE changes with the window), which is why this is
  // the one path that replaces the whole block instead of only the derived figures.
  const forecastEl = hero.querySelector('.nw-forecast');
  if (forecastEl) forecastEl.outerHTML = forecastMarkup();
  const chartEl = hero.querySelector('.nw-chart');
  if (chartEl) chartEl.innerHTML = trendChartSvg(nw.history);
  bindNetWorthScrubber(hero);
  wireForecast(hero);
}

function wireHero(hero) {
  bindNetWorthScrubber(hero);
  wireForecast(hero);
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
      [nw.history, nw.bookingActivity] = await Promise.all([
        loadHistory(months),
        loadBookingActivity(months)
      ]);
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
    [nw.history, nw.bookingActivity] = await Promise.all([
      loadHistory(-1, from, to),
      loadBookingActivity(-1, from, to)
    ]);
    repaintHeroTrend(hero);
  });
}

/* ---- Card 1's forward preview: the trend curve continued past today -------------------------- */

// A projection, not a forecast, and never a measurement. It is drawn as the dashed continuation of the
// measured curve inside the SAME chart - "eine variable einstellbare Vorschau wie es sich entwickeln
// könnte" is part of the trend, not a second card - and it may not touch the headline net worth, the
// metrics or anything else that reads as today's value. Every number follows from the inputs below and
// monthly compounding. The only value taken from data is the starting savings rate, read off the user's
// own history over the window currently on screen and freely overwritable: a round invented number
// would read like advice, and this is a calculator, not a recommendation. Horizon (including "off",
// stored as 0 years), savings rate, return and inflation persist in the existing `wealth.projection`
// preference.
const PROJECTION_YEARS = [5, 10, 20, 30];
const PROJECTION_FALLBACK = { returnPercent: 5, inflationPercent: 2, years: 10 };
// A 30-year horizon is 361 monthly values; drawing and scrubbing every one of them buys nothing. The
// series stays COMPOUNDED monthly - only the points that end up in the path are thinned.
const PROJECTION_MAX_POINTS = 60;
const MS_PER_MONTH = 1000 * 60 * 60 * 24 * 30.4375;

// Session-only, deliberately not a preference field: the stored value is the calculation's inputs, not
// whether a disclosure happens to be open.
let forecastSettingsOpen = false;

function monthsBetween(fromDate, toDate) {
  const from = parseChartDate(fromDate);
  const to = parseChartDate(toDate);
  if (!Number.isFinite(from) || !Number.isFinite(to)) return 0;
  return Math.max(0, Math.round((to - from) / MS_PER_MONTH));
}

// The average monthly change of the user's own net worth over the window currently shown. Returns
// null when there is not enough history to say anything - the preview then starts at zero and says
// where the number would have come from, instead of projecting a made-up rate.
function observedMonthlySavings() {
  const usable = measuredPoints();
  if (usable.length < 2) return null;
  const months = monthsBetween(usable[0].date, usable.at(-1).date);
  if (months < 1) return null;
  return Math.round((Number(usable.at(-1).netWorth) - Number(usable[0].netWorth)) / months);
}

function projectionSettings() {
  const stored = nw.projection || {};
  const observed = observedMonthlySavings();
  const savings = Number(stored.monthlySavings);
  const storedYears = Number(stored.years);
  const basis = basisSummary(nw.previewBasis);
  // The composed surplus wins unless the owner typed a number, because it is the better answer:
  // income minus what actually leaves, rather than the drift of a curve that includes market moves.
  // A basis with no configured income cannot carry it, and then the old estimate is more honest than
  // a confident negative surplus.
  const useBasis = stored.useBasis !== false && basis?.usable === true;
  return {
    basis,
    useBasis,
    growth: stored.growth && typeof stored.growth === 'object' ? stored.growth : {},
    monthlySavings: useBasis ? basis.surplus : (Number.isFinite(savings) ? savings : (observed ?? 0)),
    savingsIsObserved: !useBasis && !Number.isFinite(savings) && observed !== null,
    hasObserved: observed !== null,
    observed,
    returnPercent: Number.isFinite(Number(stored.returnPercent)) ? Number(stored.returnPercent) : PROJECTION_FALLBACK.returnPercent,
    inflationPercent: Number.isFinite(Number(stored.inflationPercent)) ? Number(stored.inflationPercent) : PROJECTION_FALLBACK.inflationPercent,
    // 0 = the preview is switched off. Anything else unknown falls back to the default horizon.
    years: (storedYears === 0 || PROJECTION_YEARS.includes(storedYears)) ? storedYears : PROJECTION_FALLBACK.years
  };
}

// value(m+1) = value(m) * (1 + r/12) + savings. Monthly compounding, because the savings arrive
// monthly; a yearly formula would silently overstate the growth on the current year's payments.
// Delegates to features/wealth-preview.js, which owns two decisions this used to get wrong.
//
// It compounded with `1 + r/12`, and `(1 + r/12)^12` is more than `1 + r`: at 7 % it came out as
// 7.229 % a year. On a 50 000 balance over thirty years that is 405 825 on screen against 380 613 in
// the account - 25 000 of growth that never happens. The twelfth root is what 7 % means, and it is
// what the bAV projection uses, so the two cannot disagree about the same rate.
//
// And the surplus is no longer one flat number for thirty years: each line of the basis grows at its
// own rate, so a salary and a rent can rise differently, which is the only way a long preview says
// anything.
function projectSeries(start, monthlySavings, returnPercent, months, basis = null, growth = null) {
  return projectPreviewSeries(start, {
    basis, growth, returnPercent, months,
    flatSurplus: basis ? null : monthlySavings
  });
}

// Calendar months, clamped at a short month end, so a curve anchored on the 31st does not drift a day
// per step (which would put a "30 years" horizon almost a year off).
function addMonthsIso(iso, months) {
  const base = parseChartDate(iso);
  if (!Number.isFinite(base)) return null;
  const from = new Date(base);
  const target = new Date(Date.UTC(from.getUTCFullYear(), from.getUTCMonth() + months, 1));
  const lastDay = new Date(Date.UTC(target.getUTCFullYear(), target.getUTCMonth() + 1, 0)).getUTCDate();
  target.setUTCDate(Math.min(from.getUTCDate(), lastDay));
  return target.toISOString().slice(0, 10);
}

// Where the forward curve starts: what is measured today. Today's own history point wins (it is the
// same figure the chart already draws, so the curves join exactly), then the overview total, then the
// last measured point. Nothing is ever anchored on an invented value.
function projectionAnchor() {
  const usable = measuredPoints();
  const last = usable.at(-1);
  const today = localDate(new Date());
  if (last && String(last.date).slice(0, 10) === today) return { date: today, value: measuredValue(last.netWorth) };
  const overviewValue = measuredValue(nw.overview?.netWorth);
  if (overviewValue !== null) return { date: today, value: overviewValue };
  if (last) return { date: String(last.date).slice(0, 10), value: measuredValue(last.netWorth) };
  return null;
}

// One model behind both the dashed curve and the figures under it, so the two can never disagree.
function forecastModel() {
  const settings = projectionSettings();
  const anchor = projectionAnchor();
  if (!anchor || !Number.isFinite(anchor.value) || settings.years <= 0) return null;
  const months = settings.years * 12;
  const basis = settings.useBasis ? nw.previewBasis : null;
  const series = projectSeries(
    anchor.value, settings.monthlySavings, settings.returnPercent, months, basis, settings.growth);
  const step = Math.max(1, Math.ceil(months / PROJECTION_MAX_POINTS));
  const points = [];
  for (let month = 0; month <= months; month += step) points.push({ date: addMonthsIso(anchor.date, month), value: series[month], monthsAhead: month });
  if (points.at(-1).monthsAhead !== months) points.push({ date: addMonthsIso(anchor.date, months), value: series[months], monthsAhead: months });
  const usable = points.filter(point => point.date && Number.isFinite(point.value));
  if (usable.length < 2) return null;
  const end = series[months];
  // What was paid in, not what one month was worth times the months: with per-line growth the
  // contribution rises, so the flat multiplication would understate it and overstate the growth.
  const contributed = basis
    ? series.reduce((sum, _value, month) => month < months
        ? sum + surplusAt(basis, settings.growth, month) : sum, 0)
    : settings.monthlySavings * months;
  return {
    settings, anchor, months, points: usable, end, contributed,
    growth: end - anchor.value - contributed,
    real: realValue(end, settings.inflationPercent, months)
  };
}

// The preview starts today, so it can only be drawn in a window that contains today. A custom range
// that ends in the past keeps its measured curve unsquashed and says why the dashed part is missing,
// instead of stretching the axis by ten years for a segment nobody asked to see there.
function forecastIsInWindow(model, domain = null) {
  if (!model) return false;
  const range = domain || selectedChartDomain(measuredPoints(), activityPoints());
  const anchor = parseChartDate(model.anchor.date);
  return Number.isFinite(anchor) && anchor >= range.start && anchor <= range.end;
}

function percentText(value) {
  const number = Number(value) || 0;
  try { return `${new Intl.NumberFormat(isDe() ? 'de-DE' : 'en-US', { maximumFractionDigits: 1 }).format(number)} %`; }
  catch { return `${number} %`; }
}

// The assumption travels with the curve: a preview whose savings rate is not on screen is a promise.
function assumptionText(settings) {
  return `${ctx.esc(t('projectionAssumption'))}: ${ctx.money(settings.monthlySavings, nw.currency)} ${ctx.esc(t('projectionPerMonth'))}`
    + ` · ${ctx.esc(percentText(settings.returnPercent))} ${ctx.esc(t('projectionPerYear'))}`;
}

function forecastReadout(model, point) {
  if (!model) return '';
  if (point) {
    return `${ctx.esc(ctx.date(point.date))}: ≈ ${ctx.money(point.value, nw.currency)}`
      + ` <span class="nw-forecast-flag">(${ctx.esc(t('projectionNotMeasured'))})</span>`;
  }
  return `${ctx.esc(t('projectionIn'))} ${model.settings.years} ${ctx.esc(t('projectionYearsLong'))}: ≈ ${ctx.money(model.end, nw.currency)}`;
}

// Hovering a projected point reads out HERE, never in the headline: the headline is today's measured
// net worth and a projection may not overwrite it.
function setForecastReadout(hero, point) {
  const el = hero?.querySelector('[data-forecast-readout]');
  if (el) el.innerHTML = forecastReadout(forecastModel(), point);
}

function forecastLeadInner(model) {
  if (!model) return '';
  const drawn = forecastIsInWindow(model);
  return `<span class="nw-forecast-swatch" aria-hidden="true"></span>`
    + `<span class="nw-forecast-label">${ctx.esc(t('projectionLabel'))}</span>`
    + `<span class="nw-forecast-value" data-forecast-readout>${forecastReadout(model, null)}</span>`
    + `<span class="nw-forecast-assumption">${assumptionText(model.settings)}</span>`
    + (drawn ? '' : `<span class="nw-forecast-note">${ctx.esc(t('projectionNotInRange'))}</span>`);
}

function forecastFigure(label, value, negative = false) {
  return `<div><span class="nw-metric-label">${ctx.esc(label)}</span><strong${negative ? ' class="negative"' : ''}>${value}</strong></div>`;
}

// What the curve itself cannot say: the same end value in today's purchasing power, and how much of it
// is money paid in versus assumed growth. Today's own total is deliberately absent - the headline
// above already carries it, and repeating it inside a projection block is exactly the confusion
// between measured and calculated that this card has to avoid.
function forecastFiguresInner(model) {
  const settings = model ? model.settings : projectionSettings();
  const hint = settings.savingsIsObserved
    ? `<p class="nw-projection-observed">${ctx.esc(t('projectionFromHistory'))} ${ctx.esc(currentRangeLabel())}</p>`
    : (settings.hasObserved
      ? `<p class="nw-projection-observed">${ctx.esc(t('projectionHistoryWas'))} ${ctx.money(settings.observed, nw.currency)}</p>`
      : `<p class="nw-projection-observed">${ctx.esc(t('projectionNoHistory'))}</p>`);
  if (!model) return hint;
  const figures = `<div class="nw-hero-metrics nw-projection-figures">`
    + forecastFigure(`${t('projectionIn')} ${model.settings.years} ${t('projectionYearsLong')}`, `≈ ${ctx.money(model.end, nw.currency)}`)
    + forecastFigure(t('projectionReal'), `≈ ${ctx.money(model.real, nw.currency)}`)
    + forecastFigure(t('projectionContributed'), ctx.money(model.contributed, nw.currency))
    + forecastFigure(t('projectionGrowth'), ctx.money(model.growth, nw.currency), model.growth < 0)
    + `</div>`;
  return hint + figures;
}

// Sits directly under the chart it belongs to: the dashed swatch and the assumption are the curve's
// legend, and the controls that move the curve are one click away behind them.
function forecastMarkup() {
  const settings = projectionSettings();
  const model = forecastModel();
  const strip = `<div class="fw-cycle nw-projection-years" role="tablist" aria-label="${ctx.esc(t('projectionHorizon'))}">`
    + [0, ...PROJECTION_YEARS].map(years => {
      const active = years === settings.years;
      const label = years === 0 ? t('projectionOff') : `${years} ${t('projectionYearsShort')}`;
      return `<button type="button" role="tab" data-projection-years="${years}"${active ? ' class="active" aria-selected="true"' : ' aria-selected="false"'}>${ctx.esc(label)}</button>`;
    }).join('')
    + `</div>`;
  const basis = settings.basis;
  const composition = settings.useBasis && basis
    ? `<div class="nw-basis">`
      + `<div class="nw-basis-head">${ctx.esc(t('basisTitle'))}</div>`
      + `<div class="nw-basis-rows">`
      + basisRow(t('basisIncome'), basis.income)
      + basisRow(t('basisFixed'), -basis.fixedCosts)
      + basisRow(`${t('basisVariable')} · ${t('basisObserved')} ${basis.observedMonths} ${t('basisMonths')}`, -basis.variableSpend)
      + basisRow(t('basisSurplus'), basis.surplus, true)
      + (basis.budgetLimit > 0
          ? `<div class="nw-basis-row nw-basis-aside"><span>${ctx.esc(t('basisBudget'))}</span><span>${ctx.money(basis.budgetLimit, nw.currency)}</span></div>`
          : '')
      + `</div>`
      + growthFields(settings)
      + `<label class="check nw-basis-switch"><input type="checkbox" data-projection-use-observed> ${ctx.esc(t('basisUseObserved'))}</label>`
      + `</div>`
    : (basis && !basis.usable ? `<p class="nw-projection-observed">${ctx.esc(t('basisNoIncome'))}</p>` : '');

  // The savings field only appears when the preview is NOT composed: with a basis the number is the
  // sum of the lines above, and an editable copy of a derived figure is a number that silently
  // stops matching what it claims to be.
  const savingsField = settings.useBasis
    ? ''
    : `<label>${ctx.esc(t('projectionSavings'))}<input type="number" step="10" inputmode="numeric" data-projection-savings value="${ctx.esc(String(settings.monthlySavings))}"></label>`;

  const fields = `<div class="nw-projection-fields">`
    + savingsField
    + `<label>${ctx.esc(t('projectionReturn'))}<input type="number" step="0.1" min="-20" max="20" inputmode="decimal" data-projection-return value="${ctx.esc(String(settings.returnPercent))}"></label>`
    + `<label>${ctx.esc(t('projectionInflation'))}<input type="number" step="0.1" min="0" max="20" inputmode="decimal" data-projection-inflation value="${ctx.esc(String(settings.inflationPercent))}"></label>`
    + `</div>`;
  return `<div class="nw-forecast"><p class="nw-forecast-lead" data-forecast-lead>${forecastLeadInner(model)}</p>`
    + `<details class="nw-forecast-settings"${forecastSettingsOpen ? ' open' : ''}><summary>${ctx.esc(t('projectionAdjust'))}</summary>`
    + `<div class="nw-forecast-controls">${strip}${composition}${fields}<div data-forecast-figures>${forecastFiguresInner(model)}</div>`
    + `<p class="nw-projection-note">${ctx.esc(t('projectionNote'))}</p></div></details></div>`;
}

function basisRow(label, amount, strong = false) {
  return `<div class="nw-basis-row${strong ? ' nw-basis-total' : ''}">`
    + `<span>${ctx.esc(label)}</span>`
    + `<span class="number ${moneyClass(amount < 0 ? MoneyVariant.Negative : MoneyVariant.Neutral)}">${ctx.money(amount, nw.currency)}</span>`
    + `</div>`;
}

// One expected increase per line, because a salary and a rent do not rise at the same rate - and
// pretending they do is what makes a thirty-year preview meaningless. The inflation assumption is the
// placeholder, so leaving a line empty is the same as saying "like everything else".
function growthFields(settings) {
  const lines = settings.basis && Array.isArray(nw.previewBasis?.lines) ? nw.previewBasis.lines : [];
  if (!lines.length) return '';
  return `<div class="nw-basis-growth"><div class="nw-basis-head">${ctx.esc(t('basisGrowth'))}</div>`
    + lines.map(line => {
      const key = lineKey(line);
      const stored = settings.growth?.[key];
      const value = Number.isFinite(Number(stored)) ? String(Number(stored)) : '';
      return `<label><span>${ctx.esc(line.kind === 'variable' ? t('basisVariable') : line.name)}</span>`
        + `<input type="number" step="0.1" min="-20" max="20" inputmode="decimal"`
        + ` data-projection-growth="${ctx.esc(key)}" value="${ctx.esc(value)}"`
        + ` placeholder="${ctx.esc(String(settings.inflationPercent))}"></label>`;
    }).join('')
    + `</div>`;
}

// Repaints what an assumption changes: the curve, its scrubber and the derived figures. The inputs and
// the horizon strip are left standing on purpose - rebuilding them would throw the caret out of the
// field the user is still typing in.
function repaintForecast(hero) {
  const chartEl = hero.querySelector('.nw-chart');
  if (chartEl) chartEl.innerHTML = trendChartSvg(nw.history);
  bindNetWorthScrubber(hero);
  const model = forecastModel();
  const lead = hero.querySelector('[data-forecast-lead]');
  if (lead) lead.innerHTML = forecastLeadInner(model);
  const figures = hero.querySelector('[data-forecast-figures]');
  if (figures) figures.innerHTML = forecastFiguresInner(model);
}

let projectionSaveTimer = null;

// Debounced: the curve recomputes on every change either way, so the PUT is only about remembering the
// inputs for the next visit and must not fire per keystroke.
function persistProjection() {
  clearTimeout(projectionSaveTimer);
  projectionSaveTimer = setTimeout(() => {
    ctx.api('api/preferences/wealth.projection', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(nw.projection || {})
    }).catch(() => { /* remembering the inputs is a convenience, not part of the calculation */ });
  }, 600);
}

function wireForecast(hero) {
  const block = hero.querySelector('.nw-forecast');
  if (!block) return;
  const details = block.querySelector('.nw-forecast-settings');
  details?.addEventListener('toggle', () => { forecastSettingsOpen = !!details.open; });

  block.querySelectorAll('[data-projection-years]').forEach(button => button.addEventListener('click', () => {
    nw.projection = { ...(nw.projection || {}), years: Number(button.dataset.projectionYears) };
    block.querySelectorAll('[data-projection-years]').forEach(other => {
      const active = other === button;
      other.classList.toggle('active', active);
      other.setAttribute('aria-selected', String(active));
    });
    persistProjection();
    repaintForecast(hero);
  }));

  // An emptied field is not a zero: `Number('')` is 0, so clearing the savings box used to commit a
  // 0 %/0 € assumption before the user had typed the new one. numberOrNull() says null instead.
  const finite = raw => {
    const value = numberOrNull(raw);
    return Number.isFinite(value) ? value : null;
  };
  const clamp = (value, low, high) => value === null ? null : Math.max(low, Math.min(high, value));
  const bind = (selector, key, parse) => {
    const input = block.querySelector(selector);
    if (!input) return;
    // `input`, not `change`: the curve is the answer to the number being typed, so it has to move with
    // it. A half-typed value ("-", "") parses to null and is simply ignored until it is a number.
    input.addEventListener('input', () => {
      const value = parse(input.value);
      if (value === null) return;
      nw.projection = { ...(nw.projection || {}), [key]: value };
      persistProjection();
      repaintForecast(hero);
    });
  };
  bind('[data-projection-savings]', 'monthlySavings', finite);
  bind('[data-projection-return]', 'returnPercent', raw => clamp(finite(raw), -20, 20));
  bind('[data-projection-inflation]', 'inflationPercent', raw => clamp(finite(raw), 0, 20));

  block.querySelectorAll('[data-projection-growth]').forEach(input => input.addEventListener('input', () => {
    const key = input.dataset.projectionGrowth;
    const parsed = clamp(finite(input.value), -20, 20);
    const growth = { ...(nw.projection?.growth || {}) };
    // An emptied field means "no opinion", which is not the same as 0 % - it falls back to the
    // inflation assumption shown as the placeholder.
    if (parsed === null) delete growth[key]; else growth[key] = parsed;
    nw.projection = { ...(nw.projection || {}), growth };
    persistProjection();
    repaintForecast(hero);
  }));

  const useObserved = block.querySelector('[data-projection-use-observed]');
  if (useObserved) useObserved.addEventListener('change', () => {
    nw.projection = { ...(nw.projection || {}), useBasis: !useObserved.checked };
    persistProjection();
    // A full repaint here, not repaintForecast: switching the basis on or off changes which controls
    // exist, not only what the curve says.
    paintNetWorth();
  });
}

/* ---- Card 2: "Verteilung deines Vermögens" --------------------------------------------------- */

// Donut geometry: a single ring whose segments are dash-slices of one radius. Keep the separation
// small and proportional for tiny slices so 1–5% positions remain visually truthful instead of
// turning into detached round dots. Colours stay on the --cat palette.
const DONUT_R = 80;
const DONUT_C = 2 * Math.PI * DONUT_R;
const DONUT_MAX_GAP = 4;

function donutSvg(segments, assetSum, label, currency) {
  let cursor = 0;
  const arcs = segments.map(segment => {
    const full = (segment.amount / assetSum) * DONUT_C;
    const gap = segments.length > 1 ? Math.min(DONUT_MAX_GAP, full * 0.2) : 0;
    const arc = Math.max(full - gap, 0.75);
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
  // Real estate as its own slice, converted by the backend with the SAME rates as the total it is a
  // subset of. This used to derive the slice from a ratio of NATIVE asset values, which is meaningless
  // across currencies: one 5.000.000 IDR asset made a 300.000 EUR house look like a rounding error.
  const realEstate = Math.min(manualTotal, num(overview.realEstateAssets?.amount));
  // The pension slice comes from the backend for the same reason, and is clamped against what is left
  // of the manual total so the ring's slices can never add up to more than the block they came from.
  const pension = Math.min(Math.max(manualTotal - realEstate, 0), num(overview.pensionAssets?.amount));
  const otherAssets = manualTotal - realEstate - pension;
  const liabilities = num(overview.totalLiabilities);

  const categories = [
    { label: t('accounts'), amount: accounts, color: 'var(--cat-2)' },
    { label: t('investments'), amount: investments, color: 'var(--cat-1)' },
    { label: t('realEstate'), amount: realEstate, color: 'var(--cat-3)' },
    { label: t('pensionAssets'), amount: pension, color: 'var(--cat-5)' },
    { label: t('otherValues'), amount: otherAssets, color: 'var(--cat-4)' }
  ];
  // A donut cannot draw a negative slice, so a category that nets negative (e.g. an overdrawn account
  // with no offsetting positive one) is left out of the ring - it is not added back in as a positive
  // amount, and it is not subtracted from the ring's own total either. That total is then honestly this
  // ring's own sum, labelled as the asset mix rather than reusing the hero's "Vermögenswerte" wording,
  // so the page never shows two different numbers under the same name.
  const segments = categories.filter(segment => segment.amount > 0.005);
  const hasHiddenNegative = categories.some(segment => segment.amount < -0.005);
  const assetSum = segments.reduce((sum, segment) => sum + segment.amount, 0);

  if (assetSum <= 0 && liabilities <= 0) {
    return sectionCard(t('allocationTitle'), emptyRow(), { className: 'nw-allocation' });
  }

  // Allocation-first: a soft donut of the asset mix leads, its legend (with shares) sits under it, and
  // debt — which is not part of the asset ring — keeps its own thin bar beneath so it stays legible.
  const donut = assetSum > 0 ? donutSvg(segments, assetSum, t('assetMix'), currency) : '';
  const debtBar = liabilities > 0
    ? `<div class="nw-alloc-block"><p class="nw-alloc-cap"><span>${ctx.esc(t('debt'))}</span><strong class="negative">${ctx.money(-liabilities, currency)}</strong></p><div class="fw-alloc nw-alloc-debt" role="img" aria-label="${ctx.esc(t('debt'))}"><span style="width:${Math.min(100, assetSum > 0 ? liabilities / assetSum * 100 : 100).toFixed(2)}%;background:var(--negative)"></span></div></div>`
    : '';

  const legendItems = segments.map(segment => legendRow(segment.label, segment.amount, segment.color, currency, false, assetSum > 0 ? segment.amount / assetSum * 100 : null));
  if (liabilities > 0) legendItems.push(legendRow(t('debt'), -liabilities, 'var(--negative)', currency, true));
  const legend = `<div class="nw-legend">${legendItems.join('')}</div>`;
  const hiddenNote = hasHiddenNegative ? `<p class="nw-alloc-note">${ctx.esc(t('assetMixNote'))}</p>` : '';

  return sectionCard(t('allocationTitle'), `${donut}${legend}${debtBar}${hiddenNote}`, { className: 'nw-allocation' });
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
  const server = nw.overview?.emergencyFund;
  if (server?.enabled === true && Number.isFinite(Number(server.currentAmount)))
    return num(server.currentAmount);
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
  const server = nw.overview?.emergencyFund;
  const target = num(server?.targetAmount ?? pref.targetAmount);
  const enabled = server?.enabled === true || pref.enabled === true;
  if (!enabled || target <= 0) return '';
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
      const row = document.createElement('div'); row.className = 'row is-drillable';
      row.dataset.accountId = account.id;
      row.setAttribute('role', 'button');
      row.tabIndex = 0;
      const balance = account.latestBalance ? ctx.money(account.latestBalance.amount, account.latestBalance.currency) : '—';
      // Same one-word label as the accounts page and the overview: available vs booked, from the
      // server's classification. A net-worth row that only shows a number cannot be checked.
      const meaning = balanceMeaningLine(account.latestBalance, key => ctx.get(key), value => ctx.esc(value));
      const sub = [account.product || account.accountType || '', maskIdentifier(account.ibanLast4)].filter(Boolean).join(' · ');
      row.innerHTML = `<span class="tx-ident-slot">${identityIcon(account.displayName || account.institutionName, {})}</span><div class="row-main"><div class="row-title">${ctx.esc(account.displayName || account.institutionName)}</div>${sub ? `<div class="row-sub">${ctx.esc(sub)}</div>` : ''}</div><div class="${moneyClass(MoneyVariant.Neutral)}">${balance}${meaning}</div>`;
      const open = event => {
        if (event?.target?.closest?.('button,a,input,select')) return;
        ctx.navScope('transactions', 'accountId=' + encodeURIComponent(account.id));
      };
      row.addEventListener('click', open);
      row.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(event); }
      });
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

// The first call site converted to ui/form-dialog.js. It used to be one 1 400-character template
// literal that re-decided the label markup, the grouping, the actions row and the error handling all
// by itself - which is what docs/UI_AUDIT.md measured 76 times over. What is left here is what this
// dialog actually knows: which fields an asset has, which of them carry their weight on a phone, and
// what to send.
//
// Growth rate and notes moved behind the disclosure. "In Gesamtvermögen einbeziehen" did not, even
// though it is one line: it changes whether this value counts at all, and a default-on switch hidden
// behind "Mehr" is a switch nobody knows they have.
function openAssetForm(kind, existing) {
  const asset = existing || {};
  const selectedKind = existing?.kind || kind || 'other';
  const currency = asset.currency || lastOverview?.currency || 'EUR';

  const form = openFormDialog({
    title: ctx.get(existing ? 'networth.editAsset' : 'networth.newAsset'),
    subtitle: t(selectedKind),
    closeLabel: ctx.get('common.close'),
    advancedLabel: t('moreDetails'),
    fallbackError: ctx.get('common.error'),
    create: html => ctx.dialog(html),
    fields: [
      { name: 'name', kind: FieldKind.Text, label: ctx.get('common.name'), required: true, maxLength: 160 },
      { name: 'value', kind: FieldKind.Money, label: ctx.get('networth.value'), required: true, min: 0, group: 'amount' },
      { name: 'currency', kind: FieldKind.Text, label: ctx.get('purchases.currency'), required: true, minLength: 3, maxLength: 3, group: 'amount' },
      // The as-of date stays visible: a value without one is a value nobody can date.
      { name: 'valuedAt', kind: FieldKind.Date, label: ctx.get('networth.valuedAt') },
      { name: 'include', kind: FieldKind.Check, label: ctx.get('networth.includeInNetWorth') },
      { name: 'growth', kind: FieldKind.Number, label: ctx.get('networth.growth'), step: '0.01', advanced: true },
      { name: 'notes', kind: FieldKind.Textarea, label: ctx.get('contracts.notes'), maxLength: 1000, advanced: true }
    ],
    values: {
      name: asset.name || '',
      value: asset.currentValue ?? '',
      currency,
      valuedAt: dateValue(asset.valuedAt),
      include: asset.includeInNetWorth !== false,
      growth: asset.annualGrowthRate ?? '',
      notes: asset.notes || ''
    },
    actions: [
      { name: 'cancel', label: ctx.get('common.cancel'), role: 'secondary', onClick: ({ close }) => close() },
      { name: 'save', label: ctx.get(existing ? 'common.apply' : 'common.create'), role: 'primary', submit: true }
    ],
    onSubmit: async ({ values, setFormError, close }) => {
      // An empty number field reads as null here, not as 0 - so "no growth rate given" stays absent
      // instead of becoming a stated 0 % per year.
      const body = {
        name: values.name, kind: selectedKind,
        currentValue: values.value, currency: String(values.currency || 'EUR').toUpperCase(),
        valuedAt: values.valuedAt, annualGrowthRate: values.growth,
        includeInNetWorth: values.include, notes: values.notes
      };
      try {
        const created = await ctx.api(existing ? `api/assets/${existing.id}` : 'api/assets', jsonBody(body, existing ? 'PUT' : 'POST'));
        close('saved');
        ctx.toast(ctx.get('common.saved'));
        await renderNetWorth(ctx);
        if (!existing && selectedKind === 'real_estate' && created?.id) await openRealEstateDetail(ctx, created, () => renderNetWorth(ctx));
      } catch (error) {
        // A rejected save is not one field's fault, so it is not pinned to one field - and it stays
        // in the dialog rather than becoming a toast that outlives what it refers to.
        setFormError(error.message || ctx.get('common.error'));
      }
    }
  });
  return form;
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

// The twin of openAssetForm, converted for the same reason and on the same screen: leaving one of the
// two as a 1 200-character template literal is worse than either state, because the two dialogs a user
// opens from the same page then look and behave differently.
//
// The balance is the figure a liability is about, so it stays visible with its currency and with the
// payment it is serviced by. The interest rate, the dates and the notes are refinements. "In
// Gesamtvermögen einbeziehen" stays visible for the third time for the same reason: it decides whether
// the number counts at all.
function openLiabilityDialog(existing) {
  const item = existing || {};
  const choice = (list, prefix) => list.map(value => ({ value, label: ctx.get(prefix + value) }));

  return openFormDialog({
    title: ctx.get(existing ? 'networth.editLiability' : 'networth.newLiability'),
    closeLabel: ctx.get('common.close'),
    advancedLabel: t('moreDetails'),
    fallbackError: ctx.get('common.error'),
    create: html => ctx.dialog(html),
    fields: [
      { name: 'name', kind: FieldKind.Text, label: ctx.get('common.name'), required: true, maxLength: 160 },
      { name: 'kind', kind: FieldKind.Select, label: ctx.get('networth.kind'), options: choice(LIABILITY_KINDS, 'networth.liabilityKind_') },
      { name: 'balance', kind: FieldKind.Money, label: ctx.get('networth.balance'), required: true, min: 0, group: 'sum' },
      { name: 'currency', kind: FieldKind.Text, label: ctx.get('purchases.currency'), required: true, minLength: 3, maxLength: 3, group: 'sum' },
      { name: 'payment', kind: FieldKind.Money, label: ctx.get('networth.payment'), group: 'rate' },
      { name: 'cycle', kind: FieldKind.Select, label: ctx.get('contracts.billingCycle'), group: 'rate', options: choice(CYCLES, 'contracts.cycle_') },
      { name: 'include', kind: FieldKind.Check, label: ctx.get('networth.includeInNetWorth') },
      { name: 'interest', kind: FieldKind.Number, label: ctx.get('networth.interestRate'), step: '0.001', advanced: true },
      { name: 'nextDue', kind: FieldKind.Date, label: ctx.get('contracts.nextDue'), advanced: true, group: 'dates' },
      { name: 'end', kind: FieldKind.Date, label: ctx.get('contracts.endDate'), advanced: true, group: 'dates' },
      { name: 'notes', kind: FieldKind.Textarea, label: ctx.get('contracts.notes'), maxLength: 1000, advanced: true }
    ],
    values: {
      name: item.name || '',
      kind: item.kind || 'loan',
      balance: item.currentBalance ?? '',
      currency: item.currency || lastOverview?.currency || 'EUR',
      payment: item.regularPayment ?? '',
      cycle: item.paymentCycle || 'monthly',
      include: item.includeInNetWorth !== false,
      interest: item.interestRate ?? '',
      nextDue: dateValue(item.nextDueDate),
      end: dateValue(item.endDate),
      notes: item.notes || ''
    },
    actions: [
      { name: 'cancel', label: ctx.get('common.cancel'), role: 'secondary', onClick: ({ close }) => close() },
      { name: 'save', label: ctx.get(existing ? 'common.apply' : 'common.create'), role: 'primary', submit: true }
    ],
    onSubmit: async ({ values, setFormError, close }) => {
      // Every empty number field reads as null, not 0 - so "no interest rate given" stays absent
      // instead of becoming a stated 0 % per year.
      const body = {
        name: values.name, kind: values.kind,
        currentBalance: values.balance, currency: String(values.currency || 'EUR').toUpperCase(),
        interestRate: values.interest, regularPayment: values.payment, paymentCycle: values.cycle,
        nextDueDate: values.nextDue, endDate: values.end,
        includeInNetWorth: values.include, notes: values.notes
      };
      try {
        await ctx.api(existing ? 'api/liabilities/' + existing.id : 'api/liabilities', jsonBody(body, existing ? 'PUT' : 'POST'));
        close('saved');
        ctx.toast(ctx.get('common.saved'));
        await renderNetWorth(ctx);
      } catch (error) {
        setFormError(error.message || ctx.get('common.error'));
      }
    }
  });
}
function assetToWrite(item) { return { name: item.name, kind: item.kind || 'other', currentValue: item.currentValue, currency: item.currency, valuedAt: item.valuedAt || null, annualGrowthRate: item.annualGrowthRate ?? null, includeInNetWorth: item.includeInNetWorth !== false, notes: item.notes || null }; }
function liabilityToWrite(item) { return { name: item.name, kind: item.kind || 'other', currentBalance: item.currentBalance, currency: item.currency, interestRate: item.interestRate ?? null, regularPayment: item.regularPayment ?? null, paymentCycle: item.paymentCycle || 'monthly', nextDueDate: item.nextDueDate || null, endDate: item.endDate || null, includeInNetWorth: item.includeInNetWorth !== false, notes: item.notes || null }; }
function jsonBody(body, method = 'POST') { return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }; }
function emptyRow() { return `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`; }
function dateValue(value) { return value ? String(value).slice(0, 10) : ''; }
function localDate(value) { return `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, '0')}-${String(value.getDate()).padStart(2, '0')}`; }
function numberOrNull(value) { const text = String(value ?? '').trim(); return text === '' ? null : Number(text); }
