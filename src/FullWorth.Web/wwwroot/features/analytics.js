// Analytics home (UX rework §6 / Phase C). A card-based analysis home driven by ONE global cycle
// selector (Woche / Monat / Quartal / Jahr) with prev/next window navigation. Every card obeys the
// selected window — the old behaviour where category & merchant panels always meant the current
// calendar month is gone. The custom chart builder + saved analyses are demoted below the cards under
// a collapsed "Erweitert / Eigene Analyse" section. All money is privacy-masked via ctx.money(); SVG
// bar widths are set from JS (no source inline style) to keep the CSP audit at one.

import { cycleWindow, CYCLES, sectionCard, trendBadge, identityIcon, categoryIconInner, esc, ensureOfficialBrandCatalog } from '../ui/ux-kit.js';
import { bindChartScrubber } from '../ui/chart-scrubber.js';

let ctx = null;
// Kept for backwards compatibility with the builder period presets (and the required export).
const PERIODS = ['7d', 'month', 'quarter', 'year', '1y', '5y', 'all'];

// Global cycle state (UX rework §6): the chosen cycle + how many whole windows we've paged back.
let cycle = 'month';
let offset = 0;
let activeWindow = null;

const CYCLE_LABELS = { week: ['Woche', 'Week'], month: ['Monat', 'Month'], quarter: ['Quartal', 'Quarter'], year: ['Jahr', 'Year'] };

function isDe() { return !document.documentElement.lang?.startsWith('en'); }
function t(d, e) { return isDe() ? d : e; }

export function bindAnalytics(context) {
  // The whole view is (re)built by renderAnalytics each time it is shown, so there are no stable static
  // controls to wire here — just remember the context. Real wiring happens on the freshly-built DOM.
  ctx = context;
}

export async function renderAnalytics(context) {
  ctx = context;
  await ensureOfficialBrandCatalog(ctx.api);
  injectCss();
  const view = ctx.$('#view-analytics');
  if (!view) return;

  const lang = isDe() ? 'de' : 'en';
  const win = cycleWindow(cycle, offset, lang);
  activeWindow = win;

  view.innerHTML = shellHtml(win);
  wireControls(view);

  const from = win.from, to = win.to, gran = win.granularity;
  const prev = cycleWindow(cycle, offset - 1, lang);
  const cmp = '&comparison=previous-period';

  // All card queries share the selected window. overviewPrev backs the income/spending trend badges.
  const [overview, overviewPrev, history, categories, merchants, forecast, catList] = await Promise.all([
    ctx.api(`api/analytics/overview?from=${from}&to=${to}&granularity=${gran}`).catch(() => null),
    ctx.api(`api/analytics/overview?from=${prev.from}&to=${prev.to}&granularity=${gran}`).catch(() => null),
    ctx.api(`api/net-worth/history?from=${from}&to=${to}`).catch(() => []),
    ctx.api(`api/analytics/categories?from=${from}&to=${to}&granularity=${gran}${cmp}`).catch(() => null),
    ctx.api(`api/analytics/merchants?from=${from}&to=${to}&granularity=${gran}&top=10${cmp}`).catch(() => null),
    ctx.api('api/analytics/forecast?months=12').catch(() => null),
    ctx.api('api/categories').catch(() => []),
  ]);
  // Map categoryId -> icon key (user emoji Icon, else semantic Key) so the category list can show the
  // same icon it uses elsewhere. Flatten in case the endpoint nests children under parents.
  const catIcon = new Map();
  // Prefer a real emoji Icon; ignore the "cat-N" colour placeholder some categories store in Icon and
  // fall back to the semantic Key (which resolves to a line-art glyph).
  (function walk(list) { (list || []).forEach(c => { if (c && c.id) catIcon.set(c.id, (c.icon && !/^cat-\d/.test(c.icon)) ? c.icon : c.key); if (c && c.children) walk(c.children); }); })(catList);

  const cur = overview?.currency || history?.[0]?.currency || 'EUR';
  fillSpending(ctx.$('#an-spending'), overview, overviewPrev);
  fillInout(ctx.$('#an-inout'), overview, overviewPrev);
  fillCategory(ctx.$('#an-category'), categories, catIcon);
  fillMerchant(ctx.$('#an-merchant'), merchants);
  fillNetWorth(ctx.$('#an-networth'), history, cur);
  fillForecast(ctx.$('#an-forecast'), forecast);
  loadSavingsBenchmark();
}

function savingsPct(value) {
  return new Intl.NumberFormat(isDe() ? 'de-DE' : 'en-US', {
    style: 'percent',
    minimumFractionDigits: 0,
    maximumFractionDigits: 1,
  }).format(Number(value) || 0);
}

function savingsMonthLabel(value) {
  if (!/^\d{4}-\d{2}$/.test(String(value || ''))) return String(value || '');
  const [year, month] = String(value).split('-').map(Number);
  return new Intl.DateTimeFormat(isDe() ? 'de-DE' : 'en-US', {
    month: 'long',
    year: 'numeric',
  }).format(new Date(year, month - 1, 1));
}

async function loadSavingsBenchmark() {
  const box = ctx.$('#an-cloud-savings');
  if (!box) return;
  try {
    const result = await ctx.api('api/intelligence/benchmarks/savings');
    if (!result?.available) {
      box.hidden = true;
      box.innerHTML = '';
      return;
    }

    const local = Number(result.localSavingsRate) || 0;
    const median = Number(result.median) || 0;
    const points = (local - median) * 100;
    const relation = Math.abs(points) < 0.1
      ? t('nahe am Median', 'near median')
      : points > 0
        ? t(Math.abs(points).toFixed(1) + ' Prozentpunkte über Median', Math.abs(points).toFixed(1) + ' pp above median')
        : t(Math.abs(points).toFixed(1) + ' Prozentpunkte unter Median', Math.abs(points).toFixed(1) + ' pp below median');

    const filter = result.peerFilter === 'country_income'
      ? t('Land + Einkommensbereich', 'country + income band')
      : result.peerFilter === 'income'
        ? t('Einkommensbereich', 'income band')
        : result.peerFilter === 'country'
          ? t('Land', 'country')
          : t('alle verfügbaren', 'all available');

    const body = `<div class="an-card-foot">
      ${kpi(savingsPct(local), esc(t('Deine Sparquote', 'Your savings rate')))}
      ${kpi(savingsPct(median), esc(t('Cloud-Median', 'Cloud median')))}
      ${kpi(savingsPct(result.p25) + '–' + savingsPct(result.p75), esc(t('mittlere 50 %', 'middle 50%')))}
    </div>
    <div class="row-sub">${esc(relation)} · ${result.distinctInstanceCount} ${esc(t('Instanzen', 'instances'))} · ${esc(filter)}</div>`;

    box.hidden = false;
    box.innerHTML = sectionCard(
      t('Sparquote im FullWorth-Cloud-Vergleich', 'Savings rate compared with FullWorth Cloud'),
      body,
      { sub: savingsMonthLabel(result.observedMonth) + ' · ' + t('nur aggregierte Werte ab 20 Instanzen', 'aggregates only from 20+ instances') });
  } catch {
    box.hidden = true;
    box.innerHTML = '';
  }
}

// ---- View shell -----------------------------------------------------------------------------------

function shellHtml(win) {
  const cycleBtns = CYCLES.map(c => {
    const [de, en] = CYCLE_LABELS[c];
    return `<button type="button" role="tab" data-cycle="${c}" aria-selected="${c === cycle}" class="${c === cycle ? 'active' : ''}">${esc(t(de, en))}</button>`;
  }).join('');
  const nextDisabled = offset >= 0 ? ' disabled' : '';
  const cyclebar = `<div class="fw-cyclebar">
    <div class="fw-cycle" role="tablist" aria-label="${esc(t('Zeitraum', 'Cycle'))}">${cycleBtns}</div>
    <div class="fw-window">
      <button type="button" data-nav="prev" aria-label="${esc(t('Vorheriger Zeitraum', 'Previous window'))}">‹</button>
      <span class="fw-window-label" aria-live="polite">${esc(win.label)}</span>
      <button type="button" data-nav="next"${nextDisabled} aria-label="${esc(t('Nächster Zeitraum', 'Next window'))}">›</button>
    </div>
  </div>`;

  const loading = `<div class="row-sub">${esc(ctx.get('common.loading'))}</div>`;
  const card = (id, title, sub) => sectionCard(title, `<div id="${id}" class="an-card-body">${loading}</div>`, { sub });

  const cards = `<div class="fw-analysis-grid">
    ${card('an-spending', t('Ausgabenentwicklung', 'Spending development'), t('Ausgaben je Periode', 'Spending per period'))}
    ${card('an-inout', ctx.get('analytics.incomeExpense'), t('Einnahmen und Ausgaben', 'Income and expenses'))}
    ${card('an-category', ctx.get('analytics.categories'), t('Größte Kategorien', 'Top categories'))}
    ${card('an-merchant', ctx.get('analytics.merchants'), t('Größte Händler', 'Top merchants'))}
    ${card('an-networth', ctx.get('analytics.trend'), t('Vermögen über die Zeit', 'Net worth over time'))}
    ${card('an-forecast', ctx.get('analytics.forecast'), t('Geschätzte Entwicklung', 'Estimated trajectory'))}
  </div>`;

  return cyclebar + cards + '<div id="an-cloud-savings" hidden></div>' + advancedHtml();
}

// Advanced / custom builder (UX rework §6): demoted into a collapsed <details> so it is neither the
// first nor the largest element. Reuses the original builder ids so readBuilderConfig et al. still work.
function advancedHtml() {
  const opt = (v, key, sel) => `<option value="${v}"${v === sel ? ' selected' : ''}>${esc(ctx.get(key))}</option>`;
  const controls = `<div class="an-builder-controls">
    <label class="field"><span>${esc(ctx.get('analytics.builder.measure'))}</span><select id="an-measure">${opt('spend', 'analytics.builder.measure_spend')}${opt('income', 'analytics.builder.measure_income')}${opt('net', 'analytics.builder.measure_net')}${opt('count', 'analytics.builder.measure_count')}</select></label>
    <label class="field"><span>${esc(ctx.get('analytics.builder.dimension'))}</span><select id="an-dimension">${opt('month', 'analytics.builder.dimension_month')}${opt('category', 'analytics.builder.dimension_category')}${opt('merchant', 'analytics.builder.dimension_merchant')}${opt('none', 'analytics.builder.dimension_none')}</select></label>
    <label class="field"><span>${esc(ctx.get('analytics.period'))}</span><select id="an-cperiod">${PERIODS.map(p => opt(p, 'analytics.period_' + p, '1y')).join('')}</select></label>
    <label class="field"><span>${esc(ctx.get('analytics.builder.type'))}</span><select id="an-ctype">${opt('bar', 'analytics.builder.type_bar')}${opt('line', 'analytics.builder.type_line')}${opt('hbar', 'analytics.builder.type_hbar')}${opt('donut', 'analytics.builder.type_donut')}</select></label>
  </div>`;
  return `<details class="fw-card an-advanced">
    <summary>${esc(t('Erweitert / Eigene Analyse', 'Advanced / Custom analysis'))}</summary>
    <p class="fw-card-sub">${esc(ctx.get('analytics.builder.title'))}</p>
    ${controls}
    <div class="an-builder-actions"><button type="button" id="an-save" class="ghost">${esc(ctx.get('analytics.builder.save'))}</button><button type="button" id="an-run">${esc(ctx.get('analytics.builder.run'))}</button></div>
    <div id="an-builder-chart" class="chart-empty"></div>
    <div id="an-saved" class="rows"></div>
  </details>`;
}

function wireControls(view) {
  view.querySelectorAll('.fw-cycle [data-cycle]').forEach(b => b.addEventListener('click', () => {
    cycle = b.dataset.cycle; offset = 0; renderAnalytics(ctx);
  }));
  const prev = view.querySelector('[data-nav="prev"]');
  const next = view.querySelector('[data-nav="next"]');
  prev?.addEventListener('click', () => { offset -= 1; renderAnalytics(ctx); });
  next?.addEventListener('click', () => { if (offset < 0) { offset += 1; renderAnalytics(ctx); } });

  view.querySelector('#an-run')?.addEventListener('click', () => runBuilder(ctx));
  view.querySelector('#an-save')?.addEventListener('click', () => saveAnalysis(ctx));
  // Lazily populate the advanced builder the first time it is expanded (avoids extra calls per render).
  const adv = view.querySelector('.an-advanced');
  adv?.addEventListener('toggle', () => {
    if (adv.open && !adv.dataset.loaded) { adv.dataset.loaded = '1'; loadSavedAnalyses(ctx); runBuilder(ctx); }
  });
}

// ---- Card renderers -------------------------------------------------------------------------------

function pct(cur, prev) { cur = Number(cur) || 0; prev = Number(prev) || 0; if (!prev) return 0; return ((cur - prev) / Math.abs(prev)) * 100; }

function kpi(valueHtml, label) { return `<div class="an-kpi"><span class="k">${valueHtml}</span><span class="l">${label}</span></div>`; }

function monthLabel(row) {
  if (row?.start) {
    const start = new Date(String(row.start).slice(0, 10) + 'T12:00:00');
    if (!Number.isNaN(start.getTime())) {
      if (activeWindow?.granularity === 'week')
        return new Intl.DateTimeFormat(isDe() ? 'de-DE' : 'en-US', { day: '2-digit', month: 'short', year: 'numeric' }).format(start);
      if (activeWindow?.granularity === 'month')
        return new Intl.DateTimeFormat(isDe() ? 'de-DE' : 'en-US', { month: 'short', year: 'numeric' }).format(start);
    }
  }
  if (row?.label && activeWindow?.granularity !== 'month') return String(row.label);
  const year = Number(row?.year), month = Number(row?.month);
  if (!Number.isFinite(year) || !Number.isFinite(month) || month < 1 || month > 12) return String(row?.label || row?.month || '');
  return new Intl.DateTimeFormat(isDe() ? 'de-DE' : 'en-US', { month: 'short', year: 'numeric' }).format(new Date(year, month - 1, 1));
}

function axisPeriodLabel(row) {
  const granularity = activeWindow?.granularity || 'month';
  if (granularity === 'quarter' || granularity === 'year') return String(row?.label || '');
  if (row?.start) {
    const start = new Date(String(row.start).slice(0, 10) + 'T12:00:00');
    if (!Number.isNaN(start.getTime())) {
      if (granularity === 'week')
        return new Intl.DateTimeFormat(isDe() ? 'de-DE' : 'en-US', { day: '2-digit', month: '2-digit' }).format(start);
      if (granularity === 'month') return String(start.getMonth() + 1).padStart(2, '0');
    }
  }
  return String(row?.month ?? row?.label ?? '');
}

function analyticsTxScope(extra = '') {
  const p = new URLSearchParams();
  if (activeWindow?.from) p.set('from', activeWindow.from);
  if (activeWindow?.to) p.set('to', activeWindow.to);
  p.set('status', 'booked');
  if (extra) {
    const more = new URLSearchParams(extra);
    more.forEach((value, key) => p.set(key, value));
  }
  return p.toString();
}

function periodRange(row) {
  const raw = String(row?.start || '').slice(0, 10);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(raw)) return { from: activeWindow?.from || '', to: activeWindow?.to || '' };
  const start = new Date(raw + 'T12:00:00');
  const end = new Date(start);
  const granularity = activeWindow?.granularity || 'month';
  if (granularity === 'week') end.setDate(end.getDate() + 6);
  else if (granularity === 'quarter') end.setMonth(end.getMonth() + 3, 0);
  else if (granularity === 'year') end.setFullYear(end.getFullYear(), 11, 31);
  else end.setMonth(end.getMonth() + 1, 0);
  const iso = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
  let from = raw, to = iso(end);
  if (activeWindow?.from && from < activeWindow.from) from = activeWindow.from;
  if (activeWindow?.to && to > activeWindow.to) to = activeWindow.to;
  return { from, to };
}

function bindPeriodDrills(el, rows, defaultDirection = '') {
  if (!el || !rows?.length) return;
  el.querySelectorAll('[data-period-index]').forEach(target => {
    const index = Number(target.dataset.periodIndex);
    const row = rows[index];
    if (!row) return;
    const go = () => {
      const range = periodRange(row);
      const direction = target.dataset.direction || defaultDirection;
      const extra = new URLSearchParams();
      if (range.from) extra.set('from', range.from);
      if (range.to) extra.set('to', range.to);
      if (direction) extra.set('direction', direction);
      window.fwNavScope && window.fwNavScope('transactions', analyticsTxScope(extra.toString()));
    };
    target.addEventListener('click', event => { event.stopPropagation(); go(); });
    target.addEventListener('keydown', event => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        event.stopPropagation();
        go();
      }
    });
  });
}

function bindSpendingScrubber(el, rows, currency) {
  const svg = el.querySelector('svg.an-chart');
  const valueEl = el.querySelector('.an-card-foot .an-kpi .k');
  const labelEl = el.querySelector('.an-card-foot .an-kpi .l');
  if (!svg || !valueEl || !rows.length) return;
  const originalValue = valueEl.innerHTML;
  const originalLabel = labelEl?.innerHTML || '';
  const vals = rows.map(row => Math.abs(Number(row.expenses) || 0));
  const max = Math.max(1, ...vals), w = 900, h = 200, baseY = h - 20;
  const points = rows.map((row, index) => {
    const value = vals[index];
    return {
      x: (index / (rows.length - 1 || 1)) * w,
      label: monthLabel(row),
      markers: [{ y: baseY - (value / max) * (h - 30), className: 'expense' }],
      data: { row, value }
    };
  });
  bindChartScrubber(svg, points, {
    initialIndex: points.length - 1,
    onChange: point => {
      valueEl.innerHTML = ctx.money(point.data.value, currency);
      if (labelEl) labelEl.textContent = point.label;
    },
    onReset: () => {
      valueEl.innerHTML = originalValue;
      if (labelEl) labelEl.innerHTML = originalLabel;
    },
    formatAria: point => `${point.label}: ${ctx.money(point.data.value, currency)}`
  });
}

function bindInoutScrubber(el, rows, currency) {
  const svg = el.querySelector('svg.an-chart');
  const valueEls = [...el.querySelectorAll('.an-card-foot .an-kpi .k')];
  if (!svg || valueEls.length < 3 || !rows.length) return;
  const originals = valueEls.map(node => node.innerHTML);
  const max = Math.max(1, ...rows.map(row => Math.max(Number(row.income) || 0, Number(row.expenses) || 0)));
  const w = 900, h = 200, pad = 24, slot = (w - pad * 2) / rows.length;
  const points = rows.map((row, index) => {
    const income = Number(row.income) || 0, expenses = Number(row.expenses) || 0, net = income - expenses;
    const x = pad + slot * index + slot / 2;
    return {
      x,
      label: monthLabel(row),
      markers: [
        { y: h - 20 - (income / max) * (h - 30), className: 'income' },
        { y: h - 20 - (expenses / max) * (h - 30), className: 'expense' }
      ],
      data: { income, expenses, net }
    };
  });
  bindChartScrubber(svg, points, {
    initialIndex: points.length - 1,
    onChange: point => {
      valueEls[0].innerHTML = ctx.money(point.data.income, currency);
      valueEls[1].innerHTML = ctx.money(point.data.expenses, currency);
      const cls = point.data.net > 0 ? 'positive' : point.data.net < 0 ? 'negative' : '';
      valueEls[2].innerHTML = `<span class="${cls}">${ctx.money(point.data.net, currency)}</span>`;
    },
    onReset: () => valueEls.forEach((node, index) => { node.innerHTML = originals[index]; }),
    formatAria: point => `${point.label}: ${ctx.get('transactions.income')} ${ctx.money(point.data.income, currency)}, ${ctx.get('transactions.expenses')} ${ctx.money(point.data.expenses, currency)}`
  });
}

function bindNetWorthHistoryScrubber(el, history, currency) {
  const svg = el.querySelector('svg.an-chart');
  const valueEl = el.querySelector('.an-card-foot .an-kpi .k');
  const labelEl = el.querySelector('.an-card-foot .an-kpi .l');
  if (!svg || !valueEl || !history.length) return;
  const originalValue = valueEl.innerHTML;
  const originalLabel = labelEl?.innerHTML || '';
  const vals = history.map(point => Number(point.netWorth) || 0);
  const min = Math.min(...vals), max = Math.max(...vals), span = (max - min) || 1;
  const w = 900, h = 200, baseY = h - 10;
  const points = history.map((point, index) => ({
    x: (index / (history.length - 1 || 1)) * w,
    label: ctx.date(point.date),
    markers: [{ y: baseY - ((vals[index] - min) / span) * (h - 20) }],
    data: { value: vals[index] }
  }));
  bindChartScrubber(svg, points, {
    initialIndex: points.length - 1,
    onChange: point => {
      valueEl.innerHTML = ctx.money(point.data.value, currency);
      if (labelEl) labelEl.textContent = point.label;
    },
    onReset: () => {
      valueEl.innerHTML = originalValue;
      if (labelEl) labelEl.innerHTML = originalLabel;
    },
    formatAria: point => `${point.label}: ${ctx.money(point.data.value, currency)}`
  });
}

function bindBuilderScrubber(el, series, fmt, chartType) {
  const svg = el.querySelector('svg.an-chart');
  const readout = el.querySelector('.an-chart-current');
  if (!svg || !readout || !series.length) return;
  const vals = series.map(point => Number(point.value) || 0);
  const w = 900, h = 200;
  let points;
  if (chartType === 'bar') {
    const max = Math.max(1, ...vals.map(Math.abs)), pad = 24, slot = (w - pad * 2) / series.length;
    points = series.map((point, index) => {
      const value = vals[index], x = pad + slot * index + slot / 2;
      return { x, label: String(point.label || ''), markers: [{ y: h - 20 - (Math.abs(value) / max) * (h - 30), className: value < 0 ? 'expense' : 'income' }], data: point };
    });
  } else {
    const min = Math.min(0, ...vals), max = Math.max(1, ...vals), span = (max - min) || 1, baseY = h - 10;
    points = series.map((point, index) => ({ x: (index / (series.length - 1 || 1)) * w, label: String(point.label || ''), markers: [{ y: baseY - ((vals[index] - min) / span) * (h - 20) }], data: point }));
  }
  const original = readout.innerHTML;
  bindChartScrubber(svg, points, {
    initialIndex: points.length - 1,
    onChange: point => { readout.textContent = `${point.label}: ${fmt(point.data.value)}`; },
    onReset: () => { readout.innerHTML = original; },
    formatAria: point => `${point.label}: ${fmt(point.data.value)}`
  });
}

// 1) Spending development — expenses over the window as a line, with total + trend vs previous window.
function fillSpending(el, o, oPrev) {
  if (!el) return;
  const cur = o?.currency || 'EUR';
  const rows = o?.byPeriod || o?.byMonth || [];
  if (!rows.length) { el.innerHTML = fxMarker(o?.incomplete) + emptyRow(); return; }
  const trend = pct(Math.abs(o?.expenses || 0), Math.abs(oPrev?.expenses || 0));
  el.innerHTML = fxMarker(o?.incomplete) + chart(() => spendingLine(rows)) +
    `<div class="an-card-foot">${kpi(ctx.money(o?.expenses || 0, cur), esc(t('Ausgaben gesamt', 'Total spending')))}${trendBadge(trend, false)}</div>`;
  bindSpendingScrubber(el, rows, cur);
  bindPeriodDrills(el, rows, 'expense');
}

function spendingLine(rows) {
  const vals = rows.map(r => Math.abs(Number(r.expenses) || 0));
  const max = Math.max(1, ...vals), w = 900, h = 200, baseY = h - 20;
  const pts = vals.map((v, i) => [(i / (vals.length - 1 || 1)) * w, baseY - (v / max) * (h - 30)]);
  const line = smoothPath(pts);
  const area = line ? `${line} L${w},${baseY} L0,${baseY} Z` : '';
  const base = `<line x1="0" y1="${baseY}" x2="${w}" y2="${baseY}" class="an-zero"></line>`;
  const labels = rows.map((r, i) => `<text x="${((i / (rows.length - 1 || 1)) * w).toFixed(1)}" y="${h - 6}" class="an-axis" text-anchor="middle">${esc(axisPeriodLabel(r))}</text>`).join('');
  const slot = w / Math.max(1, rows.length);
  const hits = rows.map((r, i) => {
    const x = i * slot;
    return `<rect class="an-period-hit" x="${x.toFixed(1)}" y="0" width="${slot.toFixed(1)}" height="${h}" data-period-index="${i}" role="button" tabindex="0" aria-label="${esc(t('Buchungen öffnen: ', 'Open transactions: ') + monthLabel(r))}" fill="transparent"></rect>`;
  }).join('');
  return `<svg viewBox="0 0 ${w} ${h}" class="an-chart" role="img" aria-label="${esc(t('Ausgabenentwicklung', 'Spending development'))}">${areaGradient('an-grad-neg', 'an-g-neg')}${base}<path d="${area}" class="an-area" fill="url(#an-grad-neg)"></path><path d="${line}" class="an-line-expense"></path>${labels}${hits}</svg>`;
}

// 2) Income vs expenses — grouped bars per period, with income/expense/net numbers and both trends.
function fillInout(el, o, oPrev) {
  if (!el) return;
  const cur = o?.currency || 'EUR';
  const rows = o?.byPeriod || o?.byMonth || [];
  if (!rows.length) { el.innerHTML = fxMarker(o?.incomplete) + emptyRow(); return; }
  const incTrend = pct(o?.income || 0, oPrev?.income || 0);
  const expTrend = pct(Math.abs(o?.expenses || 0), Math.abs(oPrev?.expenses || 0));
  const net = o?.net ?? ((o?.income || 0) - (o?.expenses || 0));
  const netCls = net > 0 ? 'positive' : net < 0 ? 'negative' : '';
  el.innerHTML = fxMarker(o?.incomplete) + chart(() => inoutBars(rows)) +
    `<div class="an-card-foot"><div class="an-kpi-group">` +
    kpi(ctx.money(o?.income || 0, cur), `${esc(ctx.get('transactions.income'))} ${trendBadge(incTrend, true)}`) +
    kpi(ctx.money(o?.expenses || 0, cur), `${esc(ctx.get('transactions.expenses'))} ${trendBadge(expTrend, false)}`) +
    `</div>` + kpi(`<span class="${netCls}">${ctx.money(net, cur)}</span>`, esc(ctx.get('analytics.net'))) + `</div>`;
  bindInoutScrubber(el, rows, cur);
  bindPeriodDrills(el, rows);
}

function inoutBars(rows) {
  const max = Math.max(1, ...rows.map(r => Math.max(Number(r.income) || 0, Number(r.expenses) || 0)));
  const w = 900, h = 200, pad = 24, n = rows.length, slot = (w - pad * 2) / n;
  const bw = Math.max(4, Math.min(18, slot / 3));
  let bars = '';
  rows.forEach((r, i) => {
    const cx = pad + slot * i + slot / 2;
    const ih = ((Number(r.income) || 0) / max) * (h - 30);
    const eh = ((Number(r.expenses) || 0) / max) * (h - 30);
    bars += `<rect x="${(cx - bw - 1).toFixed(1)}" y="${(h - 20 - ih).toFixed(1)}" width="${bw.toFixed(1)}" height="${Math.max(2, ih).toFixed(1)}" class="bar-income an-period-bar" rx="6" data-period-index="${i}" data-direction="income" role="button" tabindex="0" aria-label="${esc(ctx.get('transactions.income') + ': ' + monthLabel(r))}"></rect>`;
    bars += `<rect x="${(cx + 1).toFixed(1)}" y="${(h - 20 - eh).toFixed(1)}" width="${bw.toFixed(1)}" height="${Math.max(2, eh).toFixed(1)}" class="bar-expense an-period-bar" rx="6" data-period-index="${i}" data-direction="expense" role="button" tabindex="0" aria-label="${esc(ctx.get('transactions.expenses') + ': ' + monthLabel(r))}"></rect>`;
    bars += `<text x="${cx.toFixed(1)}" y="${h - 6}" class="an-axis" text-anchor="middle">${esc(axisPeriodLabel(r))}</text>`;
  });
  const baseline = `<line x1="0" y1="${h - 20}" x2="${w}" y2="${h - 20}" class="an-zero"></line>`;
  return `<div class="an-legend"><span class="lg lg-income">${esc(ctx.get('transactions.income'))}</span><span class="lg lg-expense">${esc(ctx.get('transactions.expenses'))}</span></div>
    <svg viewBox="0 0 ${w} ${h}" class="an-chart" role="img" aria-label="${esc(ctx.get('analytics.incomeExpense'))}">${baseline}${bars}</svg>`;
}

// 3) Spend by category — top categories as share-of-largest bars with a per-row trend badge + total.
function fillCategory(el, result, catIcon) {
  if (!el) return;
  const cats = result?.categories || [];
  const rows = cats.slice(0, 6);
  const cur = result?.currency || 'EUR';
  if (!rows.length) { el.innerHTML = fxMarker(result?.incomplete) + emptyRow(); return; }
  const max = Math.max(1, ...rows.map(r => Math.abs(Number(r.current) || 0)));
  // True window spend = sum of ROOT categories only (each root's `current` already rolls up its whole
  // subtree and roots are disjoint) + the Uncategorized row — NOT the sum of the top-N rows, which would
  // double-count a parent together with its children (the backend emits both parent and child rows).
  const total = cats.filter(c => !c.parentId).reduce((s, r) => s + Math.abs(Number(r.current) || 0), 0);
  // Drill-down (UX rework §6): tapping a category opens its bookings, scoped to the category subtree so
  // the list matches the card's rolled-up figure. Uncategorized (no id) is not navigable.
  const list = rows.map(r => {
    const pctW = Math.round((Math.abs(Number(r.current) || 0) / max) * 100);
    const cat = categoryColorIndex(r.categoryId || r.name);
    const drill = r.categoryId ? ` data-cat-id="${esc(r.categoryId)}" role="button" tabindex="0"` : '';
    return `<div class="an-catrow${r.categoryId ? ' is-drillable' : ''}"${drill}><div class="an-catrow-head"><span class="row-title"><span class="tx-cat-ic" data-cat="${cat}">${categoryIconInner(catIcon?.get(r.categoryId)) || ''}</span>${esc(r.name)}</span><span class="amount">${ctx.money(r.current, cur)}</span>${trendBadge(r.trendPercent, false)}</div>
      <div class="progress"><span class="bar-fill" data-cat="${cat}" data-w="${pctW}"></span></div></div>`;
  }).join('');
  // Screenshot parity: a soft category donut sits above the list, sharing its per-category palette; the
  // list below doubles as the legend. Wrapped in chart() so privacy mode swaps its (leaking) geometry.
  const donutHtml = categoryDonut(cats, total, cur);
  const donut = donutHtml ? chart(() => donutHtml) : '';
  el.innerHTML = fxMarker(result?.incomplete) + donut + list + `<div class="an-card-foot">${kpi(ctx.money(total, cur), esc(t('Ausgaben gesamt', 'Total spending')))}</div>`;
  el.querySelectorAll('.bar-fill[data-w]').forEach(s => { s.style.width = s.dataset.w + '%'; });
  el.querySelectorAll('.an-catrow[data-cat-id]').forEach(row => {
    const go = () => window.fwNavScope && window.fwNavScope('transactions', analyticsTxScope(`direction=expense&categoryId=${encodeURIComponent(row.dataset.catId)}&includeDescendants=true`));
    row.addEventListener('click', go);
    row.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(); } });
  });
}

// Soft category donut (screenshot parity): shares of the window's spend across ROOT categories only
// (roots are disjoint and each rolls up its subtree, matching `total`), coloured from the same per-
// category palette as the list. Rounded caps + a small inter-segment gap keep it in the soft FullWorth
// style; the center carries the total, the list underneath serves as the legend. Returns '' when a donut
// wouldn't say anything (fewer than two slices).
function categoryDonut(cats, total, cur) {
  const roots = cats.filter(c => !c.parentId)
    .map(c => ({ val: Math.abs(Number(c.current) || 0), cat: categoryColorIndex(c.categoryId || c.name) }))
    .filter(s => s.val > 0).sort((a, b) => b.val - a.val);
  if (roots.length < 2 || total <= 0) return '';
  const r = 64, cx = 80, cy = 80, circ = 2 * Math.PI * r, gap = 8;
  let offset = 0, arcs = '';
  for (const s of roots) {
    const len = (s.val / total) * circ;
    const dash = Math.max(0.75, len - gap);
    arcs += `<circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke-width="16" stroke-linecap="round" class="donut-seg" data-cat="${s.cat}" stroke-dasharray="${dash.toFixed(2)} ${(circ - dash).toFixed(2)}" stroke-dashoffset="${(-offset).toFixed(2)}" transform="rotate(-90 ${cx} ${cy})"></circle>`;
    offset += len;
  }
  return `<div class="an-donut"><svg viewBox="0 0 160 160" class="an-donut-svg" role="img" aria-label="${esc(ctx.get('analytics.categories'))}">${arcs}</svg><div class="an-donut-center"><span class="k">${ctx.money(total, cur)}</span><span class="l">${esc(ctx.get('transactions.expenses'))}</span></div></div>`;
}

// 4) Spend by merchant — top merchants with brand identity, count/average, spend + per-row trend.
function fillMerchant(el, result) {
  if (!el) return;
  const rows = (result?.merchants || []).slice(0, 6);
  const cur = result?.currency || 'EUR';
  if (!rows.length) { el.innerHTML = fxMarker(result?.incomplete) + emptyRow(); return; }
  const total = rows.reduce((s, r) => s + Math.abs(Number(r.currentSpend) || 0), 0);
  // Drill-down (UX rework §6): a merchant has no stored FK on transactions, so scope by the merchant name
  // as a counterparty search (the tx list ILIKEs the counterparty) — the pragmatic equivalent of a
  // merchant filter without a backend change.
  const list = rows.map(r => `<div class="an-mrow is-drillable" role="button" tabindex="0" data-merchant="${esc(r.merchant || '')}">${identityIcon(r.merchant, { logoAssetPath: r.logoAssetPath })}<div class="row-main"><div class="row-title">${esc(r.merchant)}</div><div class="row-sub">${Number(r.currentCount) || 0} × · Ø ${ctx.money(r.currentAverage, cur)}</div></div><div class="an-mrow-side"><span class="amount">${ctx.money(r.currentSpend, cur)}</span>${trendBadge(r.trendPercent, false)}</div></div>`).join('');
  el.innerHTML = fxMarker(result?.incomplete) + list + `<div class="an-card-foot">${kpi(ctx.money(total, cur), esc(t('Top-Ausgaben', 'Top spending')))}</div>`;
  el.querySelectorAll('.an-mrow[data-merchant]').forEach(row => {
    const q = row.dataset.merchant;
    if (!q) return;
    const go = () => window.fwNavScope && window.fwNavScope('transactions', analyticsTxScope(`direction=expense&merchant=${encodeURIComponent(q)}`));
    row.addEventListener('click', go);
    row.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(); } });
  });
}

// 5) Net-worth development — history as a trend line with the latest value + change over the window.
function fillNetWorth(el, history, currency) {
  if (!el) return;
  if (!history || !history.length) { el.innerHTML = emptyRow(); return; }
  const vals = history.map(x => Number(x.netWorth) || 0);
  const last = vals[vals.length - 1], first = vals[0];
  el.innerHTML = chart(() => nwLine(vals)) + `<div class="an-card-foot">${kpi(ctx.money(last, currency), esc(t('Aktuelles Vermögen', 'Current net worth')))}${trendBadge(pct(last, first), true)}</div>`;
  bindNetWorthHistoryScrubber(el, history, currency);
}

function nwLine(vals) {
  const min = Math.min(...vals), max = Math.max(...vals), span = (max - min) || 1;
  const w = 900, h = 200, baseY = h - 10;
  const pts = vals.map((v, i) => [(i / (vals.length - 1 || 1)) * w, baseY - ((v - min) / span) * (h - 20)]);
  const line = smoothPath(pts);
  const area = line ? `${line} L${w},${baseY} L0,${baseY} Z` : '';
  return `<svg viewBox="0 0 ${w} ${h}" class="an-chart" role="img" aria-label="${esc(ctx.get('analytics.trend'))}">${areaGradient('an-grad-nw', 'an-g-nw')}<line x1="0" y1="${baseY}" x2="${w}" y2="${baseY}" class="an-zero"></line><path d="${area}" class="an-area" fill="url(#an-grad-nw)"></path><path d="${line}" class="an-line-networth"></path></svg>`;
}

// 6) Forecast (optional) — kept as an explicitly-labelled estimate (§7.4). Independent of the cycle.
function fillForecast(el, forecast) {
  if (!el) return;
  const points = (forecast?.points || []).slice(0, 6);
  const cur = forecast?.currency || 'EUR';
  if (!points.length) { el.innerHTML = fxMarker(forecast?.incomplete) + emptyRow(); return; }
  el.innerHTML = fxMarker(forecast?.incomplete) + `<div class="row-sub an-estimate">${esc(ctx.get('analytics.estimateHint'))}</div>` +
    points.map(p => `<div class="row"><div class="row-main"><div class="row-title">${esc(ctx.date(p.date))}</div><div class="row-sub">${esc(ctx.get('analytics.estimate'))}</div></div><div class="amount">${ctx.money(p.estimatedNetWorth, cur)}</div></div>`).join('');
}

// ---- Shared small helpers -------------------------------------------------------------------------

// Stable 1-8 palette index for a category (§13 category colours), from its id/name so the same
// category always keeps the same tone across renders without needing a server-side colour.
function categoryColorIndex(key) {
  const s = String(key || '');
  let hash = 0;
  for (let i = 0; i < s.length; i++) hash = (hash * 31 + s.charCodeAt(i)) >>> 0;
  return (hash % 8) + 1;
}

function emptyRow() { return `<div class="row state-empty"><div class="row-sub">${esc(ctx.get('common.empty'))}</div></div>`; }

// Catmull-Rom → cubic-bézier smoothing: turns [[x,y],…] into a soft SVG path `d` so the line/area charts
// read as gentle curves instead of hard polylines (paired with stroke-linecap/-linejoin:round in CSS).
function smoothPath(pts) {
  if (!pts.length) return '';
  if (pts.length < 3) return 'M' + pts.map(p => `${p[0].toFixed(1)},${p[1].toFixed(1)}`).join(' L');
  let d = `M${pts[0][0].toFixed(1)},${pts[0][1].toFixed(1)}`;
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[i - 1] || pts[i], p1 = pts[i], p2 = pts[i + 1], p3 = pts[i + 2] || p2;
    const c1x = p1[0] + (p2[0] - p0[0]) / 6, c1y = p1[1] + (p2[1] - p0[1]) / 6;
    const c2x = p2[0] - (p3[0] - p1[0]) / 6, c2y = p2[1] - (p3[1] - p1[1]) / 6;
    d += ` C${c1x.toFixed(1)},${c1y.toFixed(1)} ${c2x.toFixed(1)},${c2y.toFixed(1)} ${p2[0].toFixed(1)},${p2[1].toFixed(1)}`;
  }
  return d;
}

// A soft vertical fade for area fills — a token colour up top, transparent at the bottom — referenced by
// the returned <path fill="url(#id)">. Defining the gradient inside the SVG markup is allowed (it is not
// an injected <style>); the stop colours/opacities live in the .an-g-* classes in app.css.
function areaGradient(id, cls) {
  return `<defs><linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="${cls}-0"></stop><stop offset="1" class="${cls}-1"></stop></linearGradient></defs>`;
}

// §18: an amber note when foreign amounts couldn't be converted (a missing FX rate) so the figures are
// understood as partial rather than silently dropping money.
function fxMarker(incomplete) { return incomplete ? `<div class="fx-incomplete">${esc(ctx.get('common.fxIncomplete'))}</div>` : ''; }

// Privacy mode (§5): a chart's geometry is derived from the real amounts, so it would visually leak the
// masked figures. When privacy is on, swap the chart body for a neutral placeholder (as the contracts
// sparkline does). KPI numbers are already masked through ctx.money().
function chart(build) { return ctx.isPrivate() ? `<div class="an-chart an-chart-private" aria-hidden="true">•••</div>` : build(); }

// One-time CSS for the analytics-only layout primitives (card feet, KPI, merchant/category rows,
// coloured trend lines, advanced disclosure). Everything else comes from app.css .fw-*/token classes.
// No-op: the analytics layout CSS lives in app.css (the app CSP blocks injected inline <style>, so this
// used to be dead). Kept as a stub so the renderAnalytics call site needs no change.
function injectCss() { }

// ---- Chart builder (§15.2): a bounded measure×dimension query rendered with the existing chart
// techniques, plus saved analyses persisted in a preference. Demoted below the cards (advancedHtml). ----

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
// positive-offset timezones during the early-morning window, shift both bounds a day earlier.
function isoLocal(z) {
  return `${z.getFullYear()}-${String(z.getMonth() + 1).padStart(2, '0')}-${String(z.getDate()).padStart(2, '0')}`;
}

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
  if (!el) return;
  el.innerHTML = `<div class="row-sub">${esc(ctx.get('common.loading'))}</div>`;
  // "all" in the builder means the widest the bounded engine allows (~5 years), not unbounded — send an
  // explicit 5-year range so it doesn't fall through to the endpoint's 12-month default.
  let win;
  if (cfg.period === 'all') { const s = new Date(); s.setFullYear(s.getFullYear() - 5); win = { from: isoLocal(s), to: isoLocal(new Date()) }; }
  else win = range(cfg.period);
  const { from, to } = win;
  const qs = `?measure=${cfg.measure}&dimension=${cfg.dimension}${from ? `&from=${from}&to=${to}` : ''}`;
  let r;
  try { r = await ctx.api('api/analytics/chart' + qs); }
  catch (err) { el.innerHTML = `<div class="row-sub">${esc(err.message || ctx.get('common.error'))}</div>`; return; }
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
  if (cfg.chartType === 'line') { el.innerHTML = marker + lineChart(series, fmt); bindBuilderScrubber(el, series, fmt, 'line'); return; }
  if (cfg.chartType === 'donut') { el.innerHTML = marker + donutChart(series, fmt); return; }
  el.innerHTML = marker + barChart(series, fmt, cfg.measure);
  bindBuilderScrubber(el, series, fmt, 'bar');
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
    out += `<rect x="${(cx - bw / 2).toFixed(1)}" y="${(h - 20 - bh).toFixed(1)}" width="${bw.toFixed(1)}" height="${bh.toFixed(1)}" class="${cls}" rx="6"></rect>`;
    out += `<text x="${cx.toFixed(1)}" y="${h - 6}" class="an-axis" text-anchor="middle">${esc(shortLabel(p.label))}</text>`;
  });
  const last = series[series.length - 1];
  return `<svg viewBox="0 0 ${w} ${h}" class="an-chart" role="img" aria-label="${esc(ctx.get('analytics.builder.title'))}">${out}</svg><div class="row-sub an-chart-current">${esc(last.label)}: ${fmt(last.value)}</div>`;
}

// Horizontal bars (share of the largest), reusing the category-bar technique.
function hbarChart(series, fmt) {
  const max = Math.max(1, ...series.map(p => Math.abs(Number(p.value) || 0)));
  return series.map(p => {
    const pct = Math.round((Math.abs(Number(p.value) || 0) / max) * 100);
    const cat = categoryColorIndex(p.key || p.label);
    return `<div class="an-cat"><div class="an-cat-head"><span class="row-title"><span class="cat-dot" data-cat="${cat}"></span>${esc(p.label)}</span><span class="amount">${fmt(p.value)}</span></div><div class="progress"><span class="bar-fill" data-cat="${cat}" data-w="${pct}"></span></div></div>`;
  }).join('');
}

function lineChart(series, fmt) {
  const vals = series.map(p => Number(p.value) || 0);
  const min = Math.min(0, ...vals), max = Math.max(1, ...vals), span = (max - min) || 1;
  const w = 900, h = 200, baseY = h - 10;
  const pts = vals.map((v, i) => [(i / (vals.length - 1 || 1)) * w, baseY - ((v - min) / span) * (h - 20)]);
  const line = smoothPath(pts);
  const area = line ? `${line} L${w},${baseY} L0,${baseY} Z` : '';
  const last = series[series.length - 1];
  return `<svg viewBox="0 0 ${w} ${h}" class="an-chart" role="img" aria-label="${esc(ctx.get('analytics.builder.title'))}">${areaGradient('an-grad-line', 'an-g-line')}<path d="${area}" class="an-area" fill="url(#an-grad-line)"></path><path d="${line}" class="an-line-builder"></path></svg><div class="row-sub an-chart-current">${esc(last.label)}: ${fmt(last.value)}</div>`;
}

// Donut of the positive-valued slices (a donut of mixed signs is meaningless). Arc lengths via
// stroke-dasharray ATTRIBUTES (no source inline style).
function donutChart(series, fmt) {
  const positives = series.filter(p => Number(p.value) > 0);
  const total = positives.reduce((s, p) => s + Number(p.value), 0);
  if (!total) return emptyRow();
  const r = 60, cx = 90, cy = 90, circ = 2 * Math.PI * r, gap = 8;
  let offset = 0, arcs = '';
  for (const p of positives) {
    const len = (Number(p.value) / total) * circ;
    // Soft rounded arcs: round caps + shrink each dash by a few px so a small gap opens between segments.
    const dash = Math.max(0.75, len - gap);
    arcs += `<circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke-width="18" stroke-linecap="round" class="donut-seg" data-cat="${categoryColorIndex(p.key || p.label)}" stroke-dasharray="${dash.toFixed(2)} ${(circ - dash).toFixed(2)}" stroke-dashoffset="${(-offset).toFixed(2)}" transform="rotate(-90 ${cx} ${cy})"></circle>`;
    offset += len;
  }
  const legend = positives.map(p => `<div class="row-sub"><span class="cat-dot" data-cat="${categoryColorIndex(p.key || p.label)}"></span>${esc(p.label)} · ${fmt(p.value)}</div>`).join('');
  return `<div class="donut-wrap"><svg viewBox="0 0 180 180" role="img" aria-label="${esc(ctx.get('analytics.builder.title'))}">${arcs}</svg><div class="donut-legend">${legend}</div></div>`;
}

async function loadSavedAnalyses(context) {
  if (context) ctx = context;
  const el = ctx.$('#an-saved');
  if (!el) return;
  let items;
  try { const pref = await ctx.api('api/preferences/analytics.savedAnalyses'); items = pref?.value?.items || []; }
  catch { items = []; }
  if (!items.length) { el.innerHTML = `<div class="row-sub">${esc(ctx.get('analytics.builder.empty'))}</div>`; return; }
  el.innerHTML = `<div class="row-group">${esc(ctx.get('analytics.builder.saved'))}</div>`;
  for (const it of items) {
    const row = document.createElement('div');
    row.className = 'row';
    row.innerHTML = `<button type="button" class="ghost saved-open" data-id="${esc(it.id)}">${esc(it.name)}</button><div class="row-side"><button type="button" class="ghost danger" data-del="${esc(it.id)}">${esc(ctx.get('common.delete'))}</button></div>`;
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
  const dlg = ctx.dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(ctx.get('analytics.builder.save'))}</h2><button type="button" data-close aria-label="${esc(ctx.get('common.close'))}">×</button></div>
    <label>${esc(ctx.get('analytics.builder.saveName'))}<input name="name" required maxlength="80" autocomplete="off"></label>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(ctx.get('common.cancel'))}</button><button type="submit">${esc(ctx.get('common.save'))}</button></div></form>`);
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
