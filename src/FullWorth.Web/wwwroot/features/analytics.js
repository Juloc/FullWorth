// Analytics home (UI_UX_SPEC §15.1). A period selector (§7.2 presets) drives the reports the backend
// can serve today: income vs expenses (header + monthly grouped bars), expenses by category
// (horizontal bars), net-worth history (trend) and the forecast (clearly labeled as an estimate,
// §7.4). The guided chart builder (§15.2) needs a generic measure/scope query engine and arrives
// separately. All money is privacy-masked via money(); bar widths are set from JS (no source inline
// style) to keep the CSP audit at one.

let ctx = null;
const PERIODS = ['7d', 'month', 'quarter', 'year', '1y', '5y', 'all'];

export function bindAnalytics(context) {
  ctx = context;
  ctx.$('#an-period')?.addEventListener('change', () => renderAnalytics(ctx));
  // Chart builder (§15.2): explicit Run (no auto-refetch while configuring); Save stores a named config.
  ctx.$('#an-run')?.addEventListener('click', () => runBuilder(ctx));
  ctx.$('#an-save')?.addEventListener('click', () => saveAnalysis(ctx));
}

function range(period) {
  const end = new Date();
  const start = new Date();
  switch (period) {
    case '7d': start.setDate(end.getDate() - 6); break;
    case 'month': start.setDate(1); break;
    case 'quarter': start.setMonth(Math.floor(end.getMonth() / 3) * 3, 1); break;
    case 'year': start.setMonth(0, 1); break;
    case '1y': start.setFullYear(end.getFullYear() - 1); break;
    case '5y': start.setFullYear(end.getFullYear() - 5); break;
    case 'all': return { from: null, to: null };
    default: start.setDate(1);
  }
  return { from: isoLocal(start), to: isoLocal(end) };
}

// Serialize a Date by its LOCAL calendar day. Using toISOString() would convert to UTC and, in
// positive-offset timezones during the early-morning window, shift both bounds a day earlier —
// dropping the current day (or, for the month preset, the whole current month).
function isoLocal(z) {
  return `${z.getFullYear()}-${String(z.getMonth() + 1).padStart(2, '0')}-${String(z.getDate()).padStart(2, '0')}`;
}

export async function renderAnalytics(context) {
  ctx = context;
  const period = ctx.$('#an-period')?.value || 'month';
  const { from, to } = range(period);
  const qs = from ? `?from=${from}&to=${to}` : '';

  // Category trend and Merchant spending are inherently single-calendar-month reports (they carry
  // their own trailing 3/6/12-month averages) — they always show the CURRENT month regardless of the
  // page's date-range selector above, which drives the other panels.
  const [overview, forecast, history, categories, merchants] = await Promise.all([
    ctx.api(`api/analytics/overview${qs}`).catch(() => null),
    ctx.api('api/analytics/forecast?months=12').catch(() => null),
    ctx.api(`api/net-worth/history${qs}`).catch(() => []),
    ctx.api('api/analytics/categories').catch(() => null),
    ctx.api('api/analytics/merchants?top=10').catch(() => null),
  ]);

  renderHeader(overview);
  renderMonthly(overview);
  renderCategories(categories);
  renderTrend(history, overview?.currency || 'EUR');
  renderForecast(forecast);
  renderMerchants(merchants);
  loadSavedAnalyses(ctx);
}

function renderHeader(o) {
  const cur = o?.currency || 'EUR';
  ctx.$('#an-income').textContent = ctx.money(o?.income ?? 0, cur);
  ctx.$('#an-expenses').textContent = ctx.money(o?.expenses ?? 0, cur);
  const net = ctx.$('#an-net');
  net.textContent = ctx.money(o?.net ?? 0, cur);
  net.className = (o?.net ?? 0) > 0 ? 'positive' : (o?.net ?? 0) < 0 ? 'negative' : '';
}

// Monthly income vs expenses as a grouped bar chart. Two bars per month; heights scale to the max.
function renderMonthly(o) {
  const el = ctx.$('#an-monthly');
  const rows = (o?.byMonth || []);
  if (!rows.length) { el.innerHTML = fxMarker(o?.incomplete) + emptyRow(); return; }
  const max = Math.max(1, ...rows.map(r => Math.max(Number(r.income) || 0, Number(r.expenses) || 0)));
  const w = 900, h = 200, pad = 24, n = rows.length;
  const slot = (w - pad * 2) / n;
  const bw = Math.max(4, Math.min(18, slot / 3));
  let bars = '';
  rows.forEach((r, i) => {
    const cx = pad + slot * i + slot / 2;
    const ih = ((Number(r.income) || 0) / max) * (h - 30);
    const eh = ((Number(r.expenses) || 0) / max) * (h - 30);
    bars += `<rect x="${(cx - bw - 1).toFixed(1)}" y="${(h - 20 - ih).toFixed(1)}" width="${bw.toFixed(1)}" height="${ih.toFixed(1)}" class="bar-income" rx="2"></rect>`;
    bars += `<rect x="${(cx + 1).toFixed(1)}" y="${(h - 20 - eh).toFixed(1)}" width="${bw.toFixed(1)}" height="${eh.toFixed(1)}" class="bar-expense" rx="2"></rect>`;
    bars += `<text x="${cx.toFixed(1)}" y="${h - 6}" class="an-axis" text-anchor="middle">${String(r.month).padStart(2, '0')}</text>`;
  });
  const baseline = `<line x1="0" y1="${h - 20}" x2="${w}" y2="${h - 20}" class="an-zero"></line>`;
  el.innerHTML = fxMarker(o?.incomplete) + `<div class="an-legend"><span class="lg lg-income">${ctx.esc(ctx.get('transactions.income'))}</span><span class="lg lg-expense">${ctx.esc(ctx.get('transactions.expenses'))}</span></div>
    <svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${ctx.esc(ctx.get('analytics.incomeExpense'))}">${baseline}${bars}</svg>`;
}

// Stable 1-8 palette index for a category (§13 category colours), from its id/name so the same
// category always keeps the same tone across renders without needing a server-side colour.
function categoryColorIndex(key) {
  const s = String(key || '');
  let hash = 0;
  for (let i = 0; i < s.length; i++) hash = (hash * 31 + s.charCodeAt(i)) >>> 0;
  return (hash % 8) + 1;
}

// Category trend (§15.1): current month's spend per category with a share-of-largest bar, trend vs
// last month, and the trailing 3-month average — a subtree roll-up, so a parent's figure already
// includes its children. Amounts are privacy-masked.
function renderCategories(result) {
  const el = ctx.$('#category-analysis');
  const rows = (result?.categories || []).slice(0, 10);
  const cur = result?.currency || 'EUR';
  if (!rows.length) { el.innerHTML = fxMarker(result?.incomplete) + emptyRow(); return; }
  const max = Math.max(1, ...rows.map(r => Math.abs(Number(r.current) || 0)));
  el.innerHTML = fxMarker(result?.incomplete) + rows.map(r => {
    const pct = Math.round((Math.abs(Number(r.current) || 0) / max) * 100);
    const cat = categoryColorIndex(r.categoryId || r.name);
    const trendClass = r.trendPercent > 0 ? 'negative' : r.trendPercent < 0 ? 'positive' : '';
    const arrow = r.trendPercent > 0 ? '▲' : r.trendPercent < 0 ? '▼' : '';
    return `<div class="an-cat"><div class="an-cat-head"><span class="row-title"><span class="cat-dot" data-cat="${cat}"></span>${ctx.esc(r.name)}</span><span class="amount">${ctx.money(r.current, cur)}</span></div>
      <div class="progress"><span class="bar-fill" data-cat="${cat}" data-w="${pct}"></span></div>
      <div class="row-sub"><span class="amount ${trendClass}">${arrow} ${Math.abs(Math.round(r.trendPercent))}%</span> · Ø3 ${ctx.money(r.average3, cur)}${r.hasItemBreakdown ? ' · ' + ctx.esc(ctx.get('analytics.itemLevel')) : ''}</div></div>`;
  }).join('');
  el.querySelectorAll('.bar-fill[data-w]').forEach(s => { s.style.width = s.dataset.w + '%'; });
}

// Merchant spending (§15.1): top merchants this month by total spend, with visit count and average.
function renderMerchants(result) {
  const el = ctx.$('#merchant-analysis');
  if (!el) return;
  const rows = result?.merchants || [];
  const cur = result?.currency || 'EUR';
  if (!rows.length) { el.innerHTML = fxMarker(result?.incomplete) + emptyRow(); return; }
  el.innerHTML = fxMarker(result?.incomplete) + rows.map(r => `<div class="row"><div class="row-main"><div class="row-title">${ctx.esc(r.merchant)}</div><div class="row-sub">${r.currentCount} × · Ø ${ctx.money(r.currentAverage, cur)}</div></div><div class="amount">${ctx.money(r.currentSpend, cur)}</div></div>`).join('');
}

function renderTrend(history, currency) {
  const el = ctx.$('#trend-chart');
  if (!history || !history.length) { el.innerHTML = emptyRow(); return; }
  const vals = history.map(x => Number(x.netWorth));
  const min = Math.min(...vals), max = Math.max(...vals), span = (max - min) || 1;
  const w = 900, h = 200;
  const pts = vals.map((v, i) => `${(i / (vals.length - 1 || 1)) * w},${h - ((v - min) / span) * (h - 20) - 10}`).join(' ');
  el.innerHTML = `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${ctx.esc(ctx.get('analytics.trend'))}"><line x1="0" y1="${h - 10}" x2="${w}" y2="${h - 10}" class="an-zero"></line><polyline points="${pts}" fill="none" stroke="currentColor" stroke-width="2.4" vector-effect="non-scaling-stroke"/></svg><div class="row-sub">${ctx.money(vals[vals.length - 1], currency)}</div>`;
}

function renderForecast(forecast) {
  const el = ctx.$('#forecast-list');
  const points = forecast?.points || [];
  const cur = forecast?.currency || 'EUR';
  if (!points.length) { el.innerHTML = fxMarker(forecast?.incomplete) + emptyRow(); return; }
  el.innerHTML = fxMarker(forecast?.incomplete) + `<div class="row-sub an-estimate">${ctx.esc(ctx.get('analytics.estimateHint'))}</div>` +
    points.map(p => `<div class="row"><div class="row-main"><div class="row-title">${ctx.esc(ctx.date(p.date))}</div><div class="row-sub">${ctx.esc(ctx.get('analytics.estimate'))}</div></div><div class="amount">${ctx.money(p.estimatedNetWorth, cur)}</div></div>`).join('');
}

function emptyRow() { return `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`; }

// §18: an amber note when foreign amounts couldn't be converted (a missing FX rate) so the figures are
// understood as partial rather than silently dropping money.
function fxMarker(incomplete) { return incomplete ? `<div class="fx-incomplete">${ctx.esc(ctx.get('common.fxIncomplete'))}</div>` : ''; }

// ---- Chart builder (§15.2): a bounded measure×dimension query rendered with the existing chart
// techniques, plus saved analyses persisted in a preference. ----

function readBuilderConfig() {
  return {
    measure: ctx.$('#an-measure')?.value || 'spend',
    dimension: ctx.$('#an-dimension')?.value || 'month',
    period: ctx.$('#an-cperiod')?.value || '1y',
    chartType: ctx.$('#an-ctype')?.value || 'bar',
  };
}
function applyBuilderConfig(cfg) {
  const set = (id, v) => { const el = ctx.$(id); if (el && v != null) el.value = v; };
  set('#an-measure', cfg.measure); set('#an-dimension', cfg.dimension); set('#an-cperiod', cfg.period); set('#an-ctype', cfg.chartType);
}

async function runBuilder(context) {
  if (context) ctx = context;
  const cfg = readBuilderConfig();
  const el = ctx.$('#an-builder-chart');
  el.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('common.loading'))}</div>`;
  // "all" in the builder means the widest the bounded engine allows (~5 years), not unbounded — send an
  // explicit 5-year range so it doesn't fall through to the endpoint's 12-month default.
  let win;
  if (cfg.period === 'all') { const s = new Date(); s.setFullYear(s.getFullYear() - 5); win = { from: isoLocal(s), to: isoLocal(new Date()) }; }
  else win = range(cfg.period);
  const { from, to } = win;
  const qs = `?measure=${cfg.measure}&dimension=${cfg.dimension}${from ? `&from=${from}&to=${to}` : ''}`;
  let r;
  try { r = await ctx.api('api/analytics/chart' + qs); }
  catch (err) { el.innerHTML = `<div class="row-sub">${ctx.esc(err.message || ctx.get('common.error'))}</div>`; return; }
  renderSeries(el, r, cfg);
}

function renderSeries(el, r, cfg) {
  const series = r?.series || [];
  const marker = r?.incomplete ? fxMarker(true) : '';
  if (!series.length) { el.innerHTML = marker + emptyRow(); return; }
  const isMoney = cfg.measure !== 'count';
  const fmt = v => isMoney ? ctx.money(v, r.currency || 'EUR') : String(Math.round(Number(v) || 0));
  if (cfg.chartType === 'hbar') {
    el.innerHTML = marker + hbarChart(series, fmt);
    el.querySelectorAll('.bar-fill[data-w]').forEach(s => { s.style.width = s.dataset.w + '%'; });
    return;
  }
  if (cfg.chartType === 'line') { el.innerHTML = marker + lineChart(series, fmt); return; }
  if (cfg.chartType === 'donut') { el.innerHTML = marker + donutChart(series, fmt); return; }
  el.innerHTML = marker + barChart(series, fmt, cfg.measure);
}

function shortLabel(s) { const t = String(s || ''); return t.length > 10 ? t.slice(0, 9) + '…' : t; }

// Vertical bars, one per series point. Colour by the MEASURE (spend=expense/red, income=income/green,
// count=neutral-income), except net colours each bar by its own sign.
function barChart(series, fmt, measure) {
  const barClass = value => measure === 'net' ? ((Number(value) || 0) < 0 ? 'bar-expense' : 'bar-income') : (measure === 'spend' ? 'bar-expense' : 'bar-income');
  const max = Math.max(1, ...series.map(p => Math.abs(Number(p.value) || 0)));
  const w = 900, h = 200, pad = 24, n = series.length, slot = (w - pad * 2) / n;
  const bw = Math.max(6, Math.min(40, slot * 0.55));
  let out = `<line x1="0" y1="${h - 20}" x2="${w}" y2="${h - 20}" class="an-zero"></line>`;
  series.forEach((p, i) => {
    const cx = pad + slot * i + slot / 2;
    const bh = (Math.abs(Number(p.value) || 0) / max) * (h - 30);
    const cls = barClass(p.value);
    out += `<rect x="${(cx - bw / 2).toFixed(1)}" y="${(h - 20 - bh).toFixed(1)}" width="${bw.toFixed(1)}" height="${bh.toFixed(1)}" class="${cls}" rx="2"></rect>`;
    out += `<text x="${cx.toFixed(1)}" y="${h - 6}" class="an-axis" text-anchor="middle">${ctx.esc(shortLabel(p.label))}</text>`;
  });
  return `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${ctx.esc(ctx.get('analytics.builder.title'))}">${out}</svg>`;
}

// Horizontal bars (share of the largest), reusing the category-bar technique.
function hbarChart(series, fmt) {
  const max = Math.max(1, ...series.map(p => Math.abs(Number(p.value) || 0)));
  return series.map(p => {
    const pct = Math.round((Math.abs(Number(p.value) || 0) / max) * 100);
    const cat = categoryColorIndex(p.key || p.label);
    return `<div class="an-cat"><div class="an-cat-head"><span class="row-title"><span class="cat-dot" data-cat="${cat}"></span>${ctx.esc(p.label)}</span><span class="amount">${fmt(p.value)}</span></div><div class="progress"><span class="bar-fill" data-cat="${cat}" data-w="${pct}"></span></div></div>`;
  }).join('');
}

function lineChart(series, fmt) {
  const vals = series.map(p => Number(p.value) || 0);
  const min = Math.min(0, ...vals), max = Math.max(1, ...vals), span = (max - min) || 1;
  const w = 900, h = 200;
  const pts = vals.map((v, i) => `${(i / (vals.length - 1 || 1)) * w},${(h - ((v - min) / span) * (h - 20) - 10).toFixed(1)}`).join(' ');
  const last = series[series.length - 1];
  return `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${ctx.esc(ctx.get('analytics.builder.title'))}"><polyline points="${pts}" fill="none" stroke="currentColor" stroke-width="2.4" vector-effect="non-scaling-stroke"/></svg><div class="row-sub">${ctx.esc(last.label)}: ${fmt(last.value)}</div>`;
}

// Donut of the positive-valued slices (a donut of mixed signs is meaningless). Arc lengths via
// stroke-dasharray ATTRIBUTES (no source inline style).
function donutChart(series, fmt) {
  const positives = series.filter(p => Number(p.value) > 0);
  const total = positives.reduce((s, p) => s + Number(p.value), 0);
  if (!total) return emptyRow();
  const r = 60, cx = 90, cy = 90, circ = 2 * Math.PI * r;
  let offset = 0, arcs = '';
  for (const p of positives) {
    const len = (Number(p.value) / total) * circ;
    arcs += `<circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke-width="22" class="donut-seg" data-cat="${categoryColorIndex(p.key || p.label)}" stroke-dasharray="${len.toFixed(2)} ${(circ - len).toFixed(2)}" stroke-dashoffset="${(-offset).toFixed(2)}" transform="rotate(-90 ${cx} ${cy})"></circle>`;
    offset += len;
  }
  const legend = positives.map(p => `<div class="row-sub"><span class="cat-dot" data-cat="${categoryColorIndex(p.key || p.label)}"></span>${ctx.esc(p.label)} · ${fmt(p.value)}</div>`).join('');
  return `<div class="donut-wrap"><svg viewBox="0 0 180 180" role="img" aria-label="${ctx.esc(ctx.get('analytics.builder.title'))}">${arcs}</svg><div class="donut-legend">${legend}</div></div>`;
}

async function loadSavedAnalyses(context) {
  if (context) ctx = context;
  const el = ctx.$('#an-saved');
  if (!el) return;
  let items;
  try { const pref = await ctx.api('api/preferences/analytics.savedAnalyses'); items = pref?.value?.items || []; }
  catch { items = []; }
  if (!items.length) { el.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('analytics.builder.empty'))}</div>`; return; }
  el.innerHTML = `<div class="row-group">${ctx.esc(ctx.get('analytics.builder.saved'))}</div>`;
  for (const it of items) {
    const row = document.createElement('div');
    row.className = 'row';
    row.innerHTML = `<button type="button" class="ghost saved-open" data-id="${ctx.esc(it.id)}">${ctx.esc(it.name)}</button><div class="row-side"><button type="button" class="ghost danger" data-del="${ctx.esc(it.id)}">${ctx.esc(ctx.get('common.delete'))}</button></div>`;
    row.querySelector('.saved-open').addEventListener('click', () => { applyBuilderConfig(it.config || {}); runBuilder(ctx); });
    row.querySelector('[data-del]').addEventListener('click', () => deleteSaved(it.id));
    el.appendChild(row);
  }
}

async function persistSaved(items) {
  await ctx.api('api/preferences/analytics.savedAnalyses', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ items }) });
}
async function fetchSaved() {
  try { const pref = await ctx.api('api/preferences/analytics.savedAnalyses'); return pref?.value?.items || []; }
  catch { return []; }
}
async function deleteSaved(id) {
  try { await persistSaved((await fetchSaved()).filter(x => x.id !== id)); await loadSavedAnalyses(ctx); }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

function saveAnalysis(context) {
  if (context) ctx = context;
  const dlg = ctx.dialog(`<form class="dialog-card"><div class="panel-head"><h2>${ctx.esc(ctx.get('analytics.builder.save'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('analytics.builder.saveName'))}<input name="name" required maxlength="80" autocomplete="off"></label>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get('common.save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const name = (new FormData(e.currentTarget).get('name') || '').trim();
    if (!name) return;
    try {
      const items = await fetchSaved();
      items.push({ id: (crypto.randomUUID ? crypto.randomUUID() : 's' + Date.now().toString(36)), name, config: readBuilderConfig() });
      await persistSaved(items);
      dlg.close(); ctx.toast(ctx.get('common.saved')); await loadSavedAnalyses(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

export { PERIODS };
