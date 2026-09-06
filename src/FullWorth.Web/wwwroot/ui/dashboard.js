// Configurable, persisted dashboard (UI_UX_SPEC §6-8). A user's widget set + order is stored per
// user + FullWorth Space via /api/preferences (§22); the desktop uses a responsive grid, mobile a
// single ordered full-width list (§6.3). Edit mode adds/removes/reorders with an accessible
// move-up/down fallback (§25). Widgets render real backend data with loading/empty/error states.
import { money, converted, maskIdentifier } from './money.js';
import { isPrivate } from './privacy.js';
import { identityIcon } from './ux-kit.js';
import { bindChartScrubber } from './chart-scrubber.js';

// Catalog: id -> { titleKey, width (default desktop cols 4/6/8/12) }. Kept small and mapped to
// endpoints that already exist. Per-widget title + width are user-configurable (§7); scope/period/
// visualization/forecast selectors arrive with the widgets that can honor them.
const CATALOG = {
  'net-worth':     { title: 'widgets.netWorth', width: 8 },
  'available':     { title: 'widgets.available', width: 4 },
  'accounts':      { title: 'dashboard.accounts', width: 12 },
  'income-expense':{ title: 'widgets.incomeExpense', width: 6 },
  'budget-focus':  { title: 'dashboard.budget', width: 6 },
  'upcoming':      { title: 'dashboard.upcoming', width: 4 },
  'recent-tx':     { title: 'widgets.recent', width: 8 },
};
const DEFAULT_LAYOUT = [
  { id: 'w1', type: 'net-worth' }, { id: 'w2', type: 'available' },
  { id: 'w3', type: 'accounts' },
  { id: 'w4', type: 'income-expense' }, { id: 'w5', type: 'budget-focus' },
  { id: 'w6', type: 'upcoming' }, { id: 'w7', type: 'recent-tx' },
];

const WIDTHS = [4, 6, 8, 12]; // desktop grid spans offered per widget (LayoutDesktop, §7)

// Per-widget config (§7): only controls a widget can actually honor. `cfg` lives in the layout
// preference next to title/width. Each entry lists the controls that type exposes.
const CONFIGURABLE = {
  'income-expense': { period: true, chart: ['summary', 'bars'] },
  'recent-tx': { limit: [6, 10, 15, 25] },
  'upcoming': { limit: [3, 6, 10] },
  'budget-focus': { limit: [3, 4, 6] },
};
const CFG_PERIODS = ['7d', 'month', 'quarter', 'year', '1y', 'all'];

// Clamp a raw cfg to what the widget type supports, dropping unknown/invalid values so hand-edited or
// stale prefs stay safe (mirrors the WIDTHS/CATALOG guards). Returns undefined when nothing is set.
function normalizeCfg(type, cfg) {
  const spec = CONFIGURABLE[type];
  if (!spec || !cfg) return undefined;
  const out = {};
  if (spec.period && CFG_PERIODS.includes(cfg.period)) out.period = cfg.period;
  if (spec.chart && spec.chart.includes(cfg.chart)) out.chart = cfg.chart;
  if (spec.limit && spec.limit.includes(Number(cfg.limit))) out.limit = Number(cfg.limit);
  return Object.keys(out).length ? out : undefined;
}

// today-relative {from,to} ISO range for a period preset ('all' → unbounded). Local-day serialized to
// avoid the UTC off-by-one (same reason as analytics.js isoLocal).
function periodToRange(period) {
  const end = new Date();
  const start = new Date();
  switch (period) {
    case '7d': start.setDate(end.getDate() - 6); break;
    case 'month': start.setDate(1); break;
    case 'quarter': start.setMonth(Math.floor(end.getMonth() / 3) * 3, 1); break;
    case 'year': start.setMonth(0, 1); break;
    case '1y': start.setFullYear(end.getFullYear() - 1); break;
    default: return { from: null, to: null };
  }
  const iso = z => `${z.getFullYear()}-${String(z.getMonth() + 1).padStart(2, '0')}-${String(z.getDate()).padStart(2, '0')}`;
  return { from: iso(start), to: iso(end) };
}

let editing = false;

export async function renderDashboard(ctx) {
  const grid = ctx.$('#dashboard-grid');
  const bar = ctx.$('#dashboard-editbar');
  bar.hidden = !editing;
  const layout = await loadLayout(ctx);

  grid.innerHTML = '';
  if (!layout.length) {
    grid.innerHTML = `<div class="panel widget span-6"><div class="state-empty"><div class="row-sub">${ctx.esc(ctx.get('dashboard.emptyLayout'))}</div><button id="dash-add-empty">${ctx.esc(ctx.get('dashboard.addWidget'))}</button></div></div>`;
    grid.querySelector('#dash-add-empty')?.addEventListener('click', () => openCatalog(ctx));
    return;
  }

  // Fetch shared data once so widgets don't each round-trip for the same dashboard/analytics call.
  const data = await gatherData(ctx);

  layout.forEach((inst, index) => {
    const meta = CATALOG[inst.type];
    if (!meta) return;
    const width = WIDTHS.includes(inst.w) ? inst.w : meta.width;
    const title = inst.title ? inst.title : ctx.get(meta.title);
    const card = document.createElement('article');
    card.className = `panel widget span-${width}`;
    card.dataset.id = inst.id;
    const controls = editing
      ? `<div class="widget-controls"><button data-config aria-label="${ctx.esc(ctx.get('dashboard.configure'))}" title="${ctx.esc(ctx.get('dashboard.configure'))}">⚙</button><button data-move="up" ${index === 0 ? 'disabled' : ''} aria-label="${ctx.esc(ctx.get('dashboard.moveUp'))}">↑</button><button data-move="down" ${index === layout.length - 1 ? 'disabled' : ''} aria-label="${ctx.esc(ctx.get('dashboard.moveDown'))}">↓</button><button data-remove aria-label="${ctx.esc(ctx.get('dashboard.remove'))}">×</button></div>`
      : '';
    card.innerHTML = `<div class="panel-head"><h2>${ctx.esc(title)}</h2>${controls}</div><div class="widget-body"></div>`;
    grid.appendChild(card);
    try { renderWidget(inst.type, ctx, card.querySelector('.widget-body'), data, inst.cfg); }
    catch { card.querySelector('.widget-body').innerHTML = errorState(ctx); }

    if (editing) {
      card.querySelector('[data-config]').addEventListener('click', () => openWidgetConfig(ctx, inst, meta));
      card.querySelector('[data-remove]').addEventListener('click', () => mutate(ctx, l => l.filter(x => x.id !== inst.id)));
      card.querySelector('[data-move="up"]').addEventListener('click', () => mutate(ctx, l => swap(l, index, index - 1)));
      card.querySelector('[data-move="down"]').addEventListener('click', () => mutate(ctx, l => swap(l, index, index + 1)));
    }
  });
}

export function bindDashboard(ctx) {
  ctx.$('#dash-add').addEventListener('click', () => openCatalog(ctx));
  ctx.$('#dash-done').addEventListener('click', () => { editing = false; renderDashboard(ctx); });
  ctx.$('#dash-reset').addEventListener('click', async () => {
    if (!await ctx.confirm(ctx.get('dashboard.resetConfirm'), { destructive: true, confirmLabel: ctx.get('dashboard.reset') })) return;
    await saveLayout(ctx, DEFAULT_LAYOUT);
    renderDashboard(ctx);
  });
}

// The header primary action on the dashboard toggles edit mode.
export function toggleDashboardEdit(ctx) { editing = !editing; return renderDashboard(ctx); }
export function isEditing() { return editing; }

function swap(list, a, b) { const c = list.slice(); [c[a], c[b]] = [c[b], c[a]]; return c; }
async function mutate(ctx, fn) { const next = fn(await loadLayout(ctx)); await saveLayout(ctx, next); renderDashboard(ctx); }

function openCatalog(ctx) {
  const current = new Set();
  const options = Object.entries(CATALOG).map(([type, m]) =>
    `<button type="button" data-add="${type}"><strong>${ctx.esc(ctx.get(m.title))}</strong></button>`).join('');
  const dlg = ctx.dialog(`<form method="dialog" class="dialog-card"><div class="panel-head"><h2>${ctx.esc(ctx.get('dashboard.addWidget'))}</h2><button value="cancel" data-close>×</button></div><div class="choice-grid widget-catalog">${options}</div></form>`);
  dlg.querySelectorAll('[data-add]').forEach(b => b.addEventListener('click', async () => {
    dlg.close();
    await mutate(ctx, l => [...l, { id: 'w' + Date.now().toString(36), type: b.dataset.add }]);
  }));
  dlg.showModal();
}

// Per-widget configuration (UI_UX_SPEC §7): a real title override and desktop width. Only controls
// that actually take effect are offered — scope/period/visualization selectors arrive with the
// widgets that can honor them, to avoid controls that imply behavior they don't have.
function openWidgetConfig(ctx, inst, meta) {
  const currentWidth = WIDTHS.includes(inst.w) ? inst.w : meta.width;
  const widthOpts = WIDTHS.map(w => `<option value="${w}"${w === currentWidth ? ' selected' : ''}>${ctx.esc(ctx.get('dashboard.width_' + w))}</option>`).join('');
  const spec = CONFIGURABLE[inst.type];
  const cfg = inst.cfg || {};
  // Only the controls this widget type can honor are shown (§7 — no controls that imply behaviour they
  // don't have). "Default" first option reverts that field to the widget's built-in behaviour.
  let extra = '';
  if (spec?.period) {
    const opts = CFG_PERIODS.map(p => `<option value="${p}"${cfg.period === p ? ' selected' : ''}>${ctx.esc(ctx.get('analytics.period_' + p))}</option>`).join('');
    extra += `<label>${ctx.esc(ctx.get('dashboard.period'))}<select name="period"><option value="">${ctx.esc(ctx.get('dashboard.period_default'))}</option>${opts}</select></label>`;
  }
  if (spec?.chart) {
    const opts = spec.chart.map(c => `<option value="${c}"${cfg.chart === c ? ' selected' : ''}>${ctx.esc(ctx.get('dashboard.chart_' + c))}</option>`).join('');
    extra += `<label>${ctx.esc(ctx.get('dashboard.chartType'))}<select name="chart">${opts}</select></label>`;
  }
  if (spec?.limit) {
    const opts = spec.limit.map(l => `<option value="${l}"${Number(cfg.limit) === l ? ' selected' : ''}>${l}</option>`).join('');
    extra += `<label>${ctx.esc(ctx.get('dashboard.rowLimit'))}<select name="limit"><option value="">${ctx.esc(ctx.get('dashboard.rowLimit_default'))}</option>${opts}</select></label>`;
  }
  const dlg = ctx.dialog(`<form class="dialog-card"><div class="panel-head"><h2>${ctx.esc(ctx.get('dashboard.configure'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('dashboard.widgetTitle'))}<input name="title" maxlength="60" placeholder="${ctx.esc(ctx.get(meta.title))}" value="${ctx.esc(inst.title || '')}"></label>
    <label>${ctx.esc(ctx.get('dashboard.width'))}<select name="width">${widthOpts}</select></label>
    ${extra}
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get('common.apply'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    const title = (fd.get('title') || '').trim();
    const w = Number(fd.get('width'));
    const period = fd.get('period') || undefined;
    // chart only matters with a period; limit is standalone. normalizeCfg drops anything invalid/empty.
    const nextCfg = normalizeCfg(inst.type, { period, chart: period ? (fd.get('chart') || undefined) : undefined, limit: fd.get('limit') || undefined });
    dlg.close();
    await mutate(ctx, l => l.map(x => x.id === inst.id
      ? { ...x, title: title || undefined, w: WIDTHS.includes(w) ? w : undefined, cfg: nextCfg }
      : x));
  };
  dlg.showModal();
}

let cachedLayout = null;
async function loadLayout(ctx) {
  if (cachedLayout) return cachedLayout;
  try {
    const pref = await ctx.api('api/preferences/dashboard.layout');
    const widgets = pref?.value?.widgets;
    cachedLayout = Array.isArray(widgets) && widgets.length
      ? widgets.filter(w => CATALOG[w.type]).map(w => ({ ...w, cfg: normalizeCfg(w.type, w.cfg) }))
      : DEFAULT_LAYOUT.slice();
  } catch { cachedLayout = DEFAULT_LAYOUT.slice(); }
  return cachedLayout;
}
async function saveLayout(ctx, layout) {
  cachedLayout = layout;
  try { await ctx.api('api/preferences/dashboard.layout', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ widgets: layout }) }); }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}
export function invalidateLayout() { cachedLayout = null; }

async function gatherData(ctx) {
  const [dashboard, accounts, budgets, groups] = await Promise.all([
    ctx.api('api/analytics/dashboard').catch(() => null),
    ctx.api('api/accounts').catch(() => []),
    ctx.api('api/analytics/budget-status').catch(() => ({ items: [] })),
    ctx.api('api/account-groups').catch(() => []),
  ]);
  return { dashboard, accounts, budgets, groups };
}

// Scoped-navigation helper for drillable widget rows (Overview → scoped bookings, UX rework §3).
const DASH_FOLDER = '<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h7l2 2h9v10a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2Z"/></svg>';
// Muted drill affordance on grouped account headers (mirrors the reference overview's ">"): monochrome,
// only signals the header opens the group's bookings — never a strong hue.
const DASH_CHEVRON = '<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m9 6 6 6-6 6"/></svg>';
function bindDrill(el, queryFn) {
  const go = () => window.fwNavScope && window.fwNavScope('transactions', queryFn());
  el.addEventListener('click', go);
  el.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(); } });
}

function errorState(ctx) { return `<div class="state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.error'))}</div></div>`; }
function emptyState(ctx, key) { return `<div class="state-empty"><div class="row-sub">${ctx.esc(ctx.get(key || 'common.empty'))}</div></div>`; }

function renderWidget(type, ctx, body, data, cfg) {
  const d = data.dashboard;
  const cur = d?.currency || 'EUR';
  if (type === 'net-worth') {
    if (!d) { body.innerHTML = emptyState(ctx); return; }
    body.innerHTML = `<div class="widget-metric dash-metric"><strong>${money(d.netWorth, cur)}</strong></div><div class="widget-split dash-metric-split"><span>${ctx.esc(ctx.get('dashboard.assets'))}: ${money(d.assets, cur)}</span><span>${ctx.esc(ctx.get('dashboard.liabilities'))}: ${money(d.liabilities, cur)}</span></div>${d.incomplete ? `<div class="fx-incomplete">${ctx.esc(ctx.get('common.fxIncomplete'))}</div>` : ''}`;
    return;
  }
  if (type === 'available') {
    if (!d) { body.innerHTML = emptyState(ctx); return; }
    body.innerHTML = `<div class="widget-metric dash-metric"><strong>${money(d.accounts, cur)}</strong></div><div class="row-sub">${ctx.esc(ctx.get('widgets.availableHint'))}</div>`;
    return;
  }
  if (type === 'accounts') {
    // Grouped account cards (UX rework §3): each group header opens all its bookings (?groupId=), each
    // account row opens that account (?accountId=) — so group AND account drill-down work from Overview.
    const a = (data.accounts || []).filter(x => x.isActive !== false);
    if (!a.length) { body.innerHTML = emptyState(ctx); return; }
    const groups = (data.groups || []).slice().sort((g1, g2) => (g1.sortOrder - g2.sortOrder) || (g1.name || '').localeCompare(g2.name || ''));
    const byGroup = new Map();
    for (const x of a) { const k = x.groupId || ''; if (!byGroup.has(k)) byGroup.set(k, []); byGroup.get(k).push(x); }
    // The group subtotal is in the SPACE base currency: foreign accounts carry a converted `baseValue`,
    // base-currency accounts have baseValue==null and their native amount IS the base amount. Derive the
    // base currency from the accounts themselves (baseCurrency), never from the analytics response — that
    // endpoint always reports EUR and would drop non-EUR base accounts from the sum and mislabel it.
    const baseCur = a.find(x => x.baseCurrency)?.baseCurrency || cur;
    const groupTotal = accts => accts.reduce((s, x) => x.baseValue != null ? s + Number(x.baseValue) : (x.latestBalance && x.latestBalance.currency === baseCur ? s + Number(x.latestBalance.amount) : s), 0);
    const acctRow = x => `<div class="fw-row is-drillable" role="button" tabindex="0" data-acct="${ctx.esc(x.id)}"><span class="tx-ident-slot">${identityIcon(x.displayName || x.institutionName, {})}</span><div class="fw-row-main"><div class="fw-row-title">${ctx.esc(x.displayName || x.institutionName)}</div><div class="fw-row-sub">${[ctx.esc(x.institutionName || ''), ctx.esc(x.product || x.accountType || ''), maskIdentifier(x.ibanLast4)].filter(Boolean).join(' · ')}</div></div><div class="fw-row-amt">${x.latestBalance ? money(x.latestBalance.amount, x.latestBalance.currency) : '—'}</div></div>`;
    const groupHead = (g, accts) => `<div class="fw-row dash-group-head is-drillable" role="button" tabindex="0" data-group="${ctx.esc(g.id)}"><span class="dash-group-icon">${DASH_FOLDER}</span><div class="fw-row-main"><div class="fw-row-title">${ctx.esc(g.name)}</div><div class="fw-row-sub">${accts.length} · ${ctx.esc(ctx.get('nav.accounts'))}</div></div><div class="fw-row-amt">${money(groupTotal(accts), baseCur)}</div><span class="dash-drill-chevron">${DASH_CHEVRON}</span></div>`;
    // Each group is its own calm block: a header row (folder monogram · name · subtotal · drill chevron)
    // over its account rows, blocks spaced by whitespace rather than heavy rules (reference overview §3).
    const groupBlock = (headHtml, accts) => `<section class="dash-group">${headHtml}<div class="dash-group-rows">${accts.map(acctRow).join('')}</div></section>`;
    let html = '';
    if (groups.length) {
      for (const g of groups) { const accts = byGroup.get(g.id) || []; if (accts.length) html += groupBlock(groupHead(g, accts), accts); }
      const ung = byGroup.get('') || []; if (ung.length) html += `<section class="dash-group">${ung.map(acctRow).join('')}</section>`;
    } else {
      html = `<section class="dash-group">${a.map(acctRow).join('')}</section>`;
    }
    body.innerHTML = `<div class="dash-accounts">${html}</div>`;
    body.querySelectorAll('[data-acct]').forEach(r => bindDrill(r, () => 'accountId=' + encodeURIComponent(r.dataset.acct)));
    body.querySelectorAll('[data-group]').forEach(r => bindDrill(r, () => 'groupId=' + encodeURIComponent(r.dataset.group)));
    return;
  }
  if (type === 'income-expense') {
    // With a configured period, fetch the overview for that range (and optionally a monthly bar chart);
    // the default (no cfg) keeps the shared current-month snapshot with zero extra round-trips.
    if (cfg?.period) { renderIncomeExpensePeriod(ctx, body, cfg); return; }
    if (!d) { body.innerHTML = emptyState(ctx); return; }
    body.innerHTML = `<div class="widget-split big"><div><div class="row-sub">${ctx.esc(ctx.get('transactions.income'))}</div><strong class="amount positive">${money(d.income ?? 0, cur)}</strong></div><div><div class="row-sub">${ctx.esc(ctx.get('transactions.expenses'))}</div><strong class="amount negative">${money(d.expenses ?? 0, cur)}</strong></div></div>`;
    return;
  }
  if (type === 'budget-focus') {
    const items = (data.budgets?.items || []).slice(0, cfg?.limit || 4);
    body.innerHTML = items.length ? items.map(x => {
      const raw = Number(x.percent || 0);
      const status = raw > 100 ? 'over' : raw >= 85 ? 'near' : 'ontrack';
      const clamped = Math.max(0, Math.min(100, raw));
      return `<div class="row"><div class="row-main"><div class="row-title">${ctx.esc(x.name)}</div><div class="progress ${status}"><span data-w="${clamped}"></span></div></div><div class="row-side"><span class="budget-status ${status}">${ctx.esc(ctx.get('budgets.status_' + status))}</span><div class="amount">${isPrivate() ? '••%' : Math.round(raw) + '%'}</div></div></div>`;
    }).join('') : emptyState(ctx);
    // Set bar widths via JS (avoids a source inline style; keeps the CSP inline-style budget at one).
    body.querySelectorAll('.progress > span[data-w]').forEach(s => { s.style.width = s.dataset.w + '%'; });
    return;
  }
  if (type === 'upcoming') {
    // Contracts due soon, with the shared brand/category identity (UX rework §4/Phase B).
    const items = d?.upcoming || [];
    body.innerHTML = items.length ? items.slice(0, cfg?.limit || 6).map(x => `<div class="fw-row fw-row-plain"><span class="tx-ident-slot">${identityIcon(x.name, { logoAssetPath: x.logoAssetPath })}</span><div class="fw-row-main"><div class="fw-row-title">${ctx.esc(x.name)}</div><div class="fw-row-sub">${ctx.date(x.nextDueDate)}</div></div><div class="fw-row-amt amount negative">${money(x.amount, x.currency)}</div></div>`).join('') : emptyState(ctx, 'dashboard.noUpcoming');
    return;
  }
  if (type === 'recent-tx') {
    // Recent bookings share the transaction identity system (UX rework §4/Phase B): brand logo →
    // category-tinted monogram → transfer glyph. Tapping opens the full booking list.
    body.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('widgets.recentHint'))}</div>`;
    ctx.api(`api/transactions?limit=${cfg?.limit || 6}`).then(tx => {
      const items = tx.items || [];
      body.innerHTML = items.length ? items.map(x => {
        const name = x.merchantDisplayName || x.counterparty || '—';
        const cat = x.categoryName || x.category || ctx.get('common.uncategorized');
        return `<div class="fw-row is-drillable" role="button" tabindex="0" data-recent-tx><span class="tx-ident-slot">${identityIcon(name, { logoAssetPath: x.logoAssetPath, categoryIconKey: x.categoryIconKey, isTransfer: x.isTransfer })}</span><div class="fw-row-main"><div class="fw-row-title">${ctx.esc(name)}</div><div class="fw-row-sub">${ctx.date(x.bookingDate)} · ${ctx.esc(cat)}</div></div><div class="fw-row-amt amount ${x.amount < 0 ? 'negative' : 'positive'}">${money(x.amount, x.currency)}</div></div>`;
      }).join('') : emptyState(ctx);
      body.querySelectorAll('[data-recent-tx]').forEach(r => bindDrill(r, () => ''));
    }).catch(() => { body.innerHTML = errorState(ctx); });
    return;
  }
}

// Income vs expenses for a configured period (§7.2). Fetches the overview for the range; optionally
// draws a monthly bar chart instead of the two-value summary.
function renderIncomeExpensePeriod(ctx, body, cfg) {
  body.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('common.loading'))}</div>`;
  const { from, to } = periodToRange(cfg.period);
  const qs = from ? `?from=${from}&to=${to}` : '';
  ctx.api(`api/analytics/overview${qs}`).then(o => {
    const cur = o?.currency || 'EUR';
    const marker = o?.incomplete ? `<div class="fx-incomplete">${ctx.esc(ctx.get('common.fxIncomplete'))}</div>` : '';
    // Privacy: a bar chart's geometry would leak the masked figures, so hide the chart body in privacy mode.
    if (cfg.chart === 'bars') {
      const rows = o?.byMonth || [];
      body.innerHTML = (isPrivate() ? `<div class="widget-chart an-chart-private" aria-hidden="true">•••</div>` : monthlyBars(ctx, rows, cur)) + marker;
      if (!isPrivate()) bindMonthlyBarsScrubber(ctx, body, rows, cur);
      return;
    }
    body.innerHTML = `<div class="widget-split big"><div><div class="row-sub">${ctx.esc(ctx.get('transactions.income'))}</div><strong class="amount positive">${money(o?.income ?? 0, cur)}</strong></div><div><div class="row-sub">${ctx.esc(ctx.get('transactions.expenses'))}</div><strong class="amount negative">${money(o?.expenses ?? 0, cur)}</strong></div></div>${marker}`;
  }).catch(() => { body.innerHTML = errorState(ctx); });
}

// Compact monthly income/expense bar chart (two bars per month), scaled to the max. Uses the shared
// .bar-income/.bar-expense tokens; no charting dependency, no source inline style.
function monthlyBars(ctx, rows, cur) {
  if (!rows.length) return emptyState(ctx);
  const max = Math.max(1, ...rows.map(r => Math.max(Number(r.income) || 0, Number(r.expenses) || 0)));
  const w = 320, h = 96, pad = 6, n = rows.length, slot = (w - pad * 2) / n;
  const bw = Math.max(3, Math.min(12, slot / 3));
  let bars = '';
  rows.forEach((r, i) => {
    const cx = pad + slot * i + slot / 2;
    const ih = ((Number(r.income) || 0) / max) * (h - 12);
    const eh = ((Number(r.expenses) || 0) / max) * (h - 12);
    bars += `<rect x="${(cx - bw - 1).toFixed(1)}" y="${(h - 4 - ih).toFixed(1)}" width="${bw.toFixed(1)}" height="${ih.toFixed(1)}" class="bar-income" rx="1"></rect>`;
    bars += `<rect x="${(cx + 1).toFixed(1)}" y="${(h - 4 - eh).toFixed(1)}" width="${bw.toFixed(1)}" height="${eh.toFixed(1)}" class="bar-expense" rx="1"></rect>`;
  });
  const last = rows[rows.length - 1];
  return `<div class="row-sub widget-chart-current">${ctx.esc(dashboardMonthLabel(last))} · ${ctx.esc(ctx.get('transactions.income'))} ${money(last.income || 0, cur)} · ${ctx.esc(ctx.get('transactions.expenses'))} ${money(last.expenses || 0, cur)}</div><svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${ctx.esc(ctx.get('analytics.incomeExpense'))}" class="widget-chart">${bars}</svg>`;
}

function dashboardMonthLabel(row) {
  const year = Number(row?.year), month = Number(row?.month);
  if (!Number.isFinite(year) || !Number.isFinite(month) || month < 1 || month > 12) return String(row?.month || '');
  const locale = (document.documentElement.lang || '').startsWith('en') ? 'en-US' : 'de-DE';
  return new Intl.DateTimeFormat(locale, { month: 'short', year: 'numeric' }).format(new Date(year, month - 1, 1));
}

function bindMonthlyBarsScrubber(ctx, body, rows, cur) {
  const svg = body.querySelector('svg.widget-chart');
  const readout = body.querySelector('.widget-chart-current');
  if (!svg || !readout || !rows.length) return;
  const max = Math.max(1, ...rows.map(row => Math.max(Number(row.income) || 0, Number(row.expenses) || 0)));
  const w = 320, h = 96, pad = 6, slot = (w - pad * 2) / rows.length;
  const points = rows.map((row, index) => {
    const income = Number(row.income) || 0, expenses = Number(row.expenses) || 0;
    const x = pad + slot * index + slot / 2;
    return {
      x,
      label: dashboardMonthLabel(row),
      markers: [
        { y: h - 4 - (income / max) * (h - 12), className: 'income' },
        { y: h - 4 - (expenses / max) * (h - 12), className: 'expense' }
      ],
      data: { income, expenses }
    };
  });
  const original = readout.innerHTML;
  bindChartScrubber(svg, points, {
    initialIndex: points.length - 1,
    onChange: point => {
      readout.textContent = `${point.label} · ${ctx.get('transactions.income')} ${money(point.data.income, cur)} · ${ctx.get('transactions.expenses')} ${money(point.data.expenses, cur)}`;
    },
    onReset: () => { readout.innerHTML = original; },
    formatAria: point => `${point.label}: ${ctx.get('transactions.income')} ${money(point.data.income, cur)}, ${ctx.get('transactions.expenses')} ${money(point.data.expenses, cur)}`
  });
}
