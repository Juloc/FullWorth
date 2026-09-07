// Contracts & recurring costs (UI_UX_SPEC §13). Contracts come from detection (candidates the owner
// confirms) or manual creation. The list separates active from archived; each row opens a detail drawer
// showing value mode (automatic/manual), linked payments + payment trend, next expected payment,
// annualized cost, start/end and notes, with edit / cancel (archive) / reactivate. A "detected
// subscriptions" section surfaces recurring-payment candidates with one-click accept. All money and
// cadence come from the backend (annualization/next-due are computed server-side, §30).

import { identityIcon, sectionCard, esc, ensureOfficialBrandCatalog } from '../ui/ux-kit.js';

let ctx = null;
const CYCLES = ['monthly', 'quarterly', 'yearly', 'weekly'];
const KINDS = ['subscription', 'contract', 'insurance', 'loan', 'other'];
const DETECTED_BATCH_SIZE = 3;
let detectedVisibleCount = DETECTED_BATCH_SIZE;
// Populated on every render so the price-change panel can show a contract's name/currency from its id.
let contractsById = new Map();
// The full contract set plus best-effort id→name maps, kept at module scope so the filter/sort chips
// can re-render the list without refetching (UX rework §7: small datasets filter/sort client-side).
let allContracts = [];
let categoryNames = new Map();
let categoryIcons = new Map();
let accountNames = new Map();
// Filter/sort state is URL-backed so the contracts view is restorable and shareable.
const view = { kind: '', status: 'active', account: '', category: '', cycle: '', sort: 'cycle', order: 'asc' };

function loadViewState() {
  const p = new URLSearchParams(location.search);
  view.kind = p.get('kind') || '';
  view.status = p.get('status') || 'active';
  view.account = p.get('accountId') || '';
  view.category = p.get('categoryId') || '';
  view.cycle = p.get('cycle') || '';
  view.sort = p.get('sort') || 'cycle';
  view.order = p.get('order') === 'desc' ? 'desc' : 'asc';
}
function syncViewState() {
  const p = new URLSearchParams();
  if (view.kind) p.set('kind', view.kind);
  if (view.status && view.status !== 'active') p.set('status', view.status);
  if (view.account) p.set('accountId', view.account);
  if (view.category) p.set('categoryId', view.category);
  if (view.cycle) p.set('cycle', view.cycle);
  if (view.sort && view.sort !== 'cycle') p.set('sort', view.sort);
  if (view.order === 'desc') p.set('order', 'desc');
  const qs = p.toString();
  history.replaceState({ view: 'contracts' }, '', qs ? '/contracts?' + qs : '/contracts');
}

// A few labels the rework introduces have no existing i18n key, so fall back to inline DE/EN.
function lang() { return !document.documentElement.lang || !document.documentElement.lang.startsWith('en'); }
function t(de, en) { return lang() ? de : en; }

const CONTRACT_LEGAL_SUFFIXES = new Set(['AG', 'GMBH', 'KG', 'OHG', 'SE', 'SA', 'SAS', 'BV', 'NV', 'INC', 'LTD', 'LLC', 'PLC', 'AB']);
function contractIdentityKey(contract) {
  const raw = String(contract?.providerName || contract?.name || '').trim().toUpperCase()
    .replaceAll('Ä', 'AE').replaceAll('Ö', 'OE').replaceAll('Ü', 'UE').replaceAll('ẞ', 'SS').replaceAll('ß', 'SS');
  const tokens = raw.replace(/[^A-Z0-9]+/g, ' ').trim().split(/\s+/).filter(Boolean);
  while (tokens.length > 1 && CONTRACT_LEGAL_SUFFIXES.has(tokens[tokens.length - 1])) tokens.pop();
  return tokens.join(' ');
}
function sameExpectedAmount(a, b) {
  const left = Math.abs(Number(a?.amount) || 0);
  const right = Math.abs(Number(b?.amount) || 0);
  const tolerance = Math.max(0.02, Math.max(left, right) * 0.02);
  return Math.abs(left - right) <= tolerance;
}
function likelyDuplicateGroups() {
  const groups = [];
  const candidates = allContracts.filter(contract => contract.autoDetected && contract.isActive !== false);
  for (const contract of candidates) {
    const key = contractIdentityKey(contract);
    if (!key) continue;
    let group = groups.find(item =>
      item.key === key &&
      item.currency === String(contract.currency || '').toUpperCase() &&
      item.cycle === String(contract.billingCycle || 'monthly') &&
      item.interval === Number(contract.interval || 1) &&
      sameExpectedAmount(item.contracts[0], contract));
    if (!group) {
      group = {
        key,
        currency: String(contract.currency || '').toUpperCase(),
        cycle: String(contract.billingCycle || 'monthly'),
        interval: Number(contract.interval || 1),
        contracts: []
      };
      groups.push(group);
    }
    group.contracts.push(contract);
  }
  return groups.filter(group => {
    if (group.contracts.length < 2) return false;
    const accountIds = new Set(group.contracts.map(contract => contract.accountId).filter(Boolean));
    return accountIds.size >= 2;
  });
}

const CANCELLED_STATES = new Set(['sent', 'confirmed', 'cancelled']);
function cancellationStatus(c) { return c?.cancellationStatus || 'none'; }
function lifecycleStatus(c) {
  if (!c?.isActive) return 'archived';
  const status = cancellationStatus(c.cancellation);
  if (CANCELLED_STATES.has(status)) return 'cancelled';
  if (status === 'planned') return 'planned';
  return 'active';
}
function cancellationStatusLabel(status) {
  const key = 'contracts.cancelStatus_' + (status || 'none');
  const label = ctx.get(key);
  return label === key ? (status || '—') : label;
}
function periodLabel(value, unit) {
  if (value == null || !unit) return '—';
  return `${value} ${ctx.get('contracts.period_' + unit)}`;
}

// Monochrome line glyph for the sort bottom-sheet (matches the shared `.more-sheet` icon language).
function sortIcon(paths) {
  return `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">${paths}</svg>`;
}
// The sort dimensions offered in the bottom-sheet — same keys the list `sortContracts()` understands and
// the same i18n labels the old <select> used, so behaviour is unchanged; only the presentation is new.
function sortOptions() {
  return [
    { key: 'cycle', label: t('Turnus', 'Billing cycle'), icon: sortIcon('<path d="M20 8a8 8 0 0 0-14-4L3 7"/><path d="M3 3.5V7h3.5"/><path d="M4 16a8 8 0 0 0 14 4l3-3"/><path d="M21 20.5V17h-3.5"/>') },
    { key: 'annual', label: t('Kosten pro Jahr', 'Cost per year'), icon: sortIcon('<path d="M12 7c3.9 0 7 1.3 7 3s-3.1 3-7 3-7-1.3-7-3 3.1-3 7-3Z"/><path d="M5 10v6c0 1.7 3.1 3 7 3s7-1.3 7-3v-6"/>') },
    { key: 'account', label: ctx.get('contracts.account'), icon: sortIcon('<path d="M3 10 12 4l9 6"/><path d="M5 10v9M19 10v9M9 10v9M15 10v9"/><path d="M3 20h18"/>') },
    { key: 'due', label: t('Nächste Fälligkeit', 'Next due date'), icon: sortIcon('<path d="M4 5h16v15H4z"/><path d="M4 9h16"/><path d="M8 3v4M16 3v4"/>') },
    { key: 'category', label: t('Kategorie', 'Category'), icon: sortIcon('<path d="M4 4h7l9 9-7 7-9-9V4Z"/><path d="M8.5 8.5h.01"/>') },
    { key: 'name', label: ctx.get('common.name'), icon: sortIcon('<path d="M7 4v14M7 18l-3-3M7 18l3-3"/><path d="M13 6h7M13 11h5M13 16h3"/>') },
  ];
}
function sortLabel() { const o = sortOptions().find(x => x.key === view.sort); return o ? o.label : ''; }

// Sort bottom-sheet (mirrors the Finanzguru "Sortierung" sheet): one tap per dimension + a direction
// segment. Selecting a dimension applies it and closes; the ascending/descending toggle applies live.
function openSortSheet(host) {
  const rows = sortOptions().map(o => `
    <button type="button" class="contracts-sortopt${view.sort === o.key ? ' active' : ''}" data-sort-opt="${o.key}" aria-pressed="${view.sort === o.key}">
      <span class="contracts-sortopt-ic">${o.icon}</span>
      <span class="contracts-sortopt-label">${esc(o.label)}</span>
      <span class="contracts-sortopt-radio" aria-hidden="true"></span>
    </button>`).join('');
  const dir = `<div class="contracts-sortdir" data-order-seg role="group" aria-label="${esc(t('Reihenfolge', 'Order'))}">
      <button type="button" class="${view.order === 'asc' ? 'active' : ''}" data-order-val="asc" aria-pressed="${view.order === 'asc'}">${esc(t('Aufsteigend', 'Ascending'))}</button>
      <button type="button" class="${view.order === 'desc' ? 'active' : ''}" data-order-val="desc" aria-pressed="${view.order === 'desc'}">${esc(t('Absteigend', 'Descending'))}</button>
    </div>`;
  const dlg = ctx.dialog(`<div class="dialog-card contracts-sortsheet">
    <div class="panel-head"><h2>${esc(t('Sortierung', 'Sort by'))}</h2><button type="button" data-close aria-label="${esc(ctx.get('common.close'))}">×</button></div>
    <div class="contracts-sortlist">${rows}</div>
    ${dir}
  </div>`);
  dlg.classList.add('contracts-sortsheet-dlg');
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelectorAll('[data-sort-opt]').forEach(b => b.addEventListener('click', () => {
    view.sort = b.dataset.sortOpt;
    syncViewState();
    const cur = host.querySelector('[data-sort-current]'); if (cur) cur.textContent = sortLabel();
    renderList(host);
    dlg.close();
  }));
  dlg.querySelector('[data-order-seg]')?.addEventListener('click', e => {
    const b = e.target.closest('[data-order-val]'); if (!b) return;
    view.order = b.dataset.orderVal;
    syncViewState();
    dlg.querySelectorAll('[data-order-val]').forEach(x => { const on = x === b; x.classList.toggle('active', on); x.setAttribute('aria-pressed', on); });
    renderList(host);
  });
  dlg.showModal();
}

// The selected sort dimension also defines the visual grouping where that improves scanning.
function dueBucket(c) {
  const value = String(c.nextDueDate || '');
  if (!value) return { key: 'none', label: t('Ohne Fälligkeit', 'No due date'), rank: 5 };
  const now = new Date();
  const today = [now.getFullYear(), String(now.getMonth() + 1).padStart(2, '0'), String(now.getDate()).padStart(2, '0')].join('-');
  const ym = today.slice(0, 7);
  const dueYm = value.slice(0, 7);
  if (value < today) return { key: 'overdue', label: t('Überfällig', 'Overdue'), rank: 0 };
  if (dueYm === ym) return { key: 'month', label: t('Diesen Monat', 'This month'), rank: 1 };
  const next = new Date(now.getFullYear(), now.getMonth() + 1, 1);
  const nextYm = [next.getFullYear(), String(next.getMonth() + 1).padStart(2, '0')].join('-');
  if (dueYm === nextYm) return { key: 'next', label: t('Nächsten Monat', 'Next month'), rank: 2 };
  return { key: 'later', label: t('Später', 'Later'), rank: 3 };
}
function groupKeyFor() { return ['cycle', 'account', 'category', 'due'].includes(view.sort) ? view.sort : null; }
function groupBucket(c) {
  if (view.sort === 'cycle') {
    const key = c.billingCycle || 'monthly';
    const rank = ({ monthly: 0, quarterly: 1, yearly: 2, weekly: 3 })[key] ?? 4;
    return { key, label: ctx.get('contracts.cycle_' + key), rank };
  }
  if (view.sort === 'account') return { key: c.accountId || '', label: accountLabel(c) || t('Ohne Konto', 'No account'), rank: 0 };
  if (view.sort === 'due') return dueBucket(c);
  return { key: c.categoryId || '', label: categoryLabel(c) || t('Ohne Kategorie', 'No category'), rank: 0 };
}
function groupMonthly(items) { return items.reduce((sum, contract) => sum + (Number(contract.monthlyEquivalent) || 0), 0); }
function groupHead(label, items, cur) {
  const el = document.createElement('div');
  el.className = 'contracts-group-head';
  const suffix = view.sort === 'due' ? '' : `<span class="contracts-group-sum">${ctx.money(groupMonthly(items), cur)}<small>${esc(t('mtl.', 'mo.'))}</small></span>`;
  el.innerHTML = `<div class="contracts-group-id"><span class="contracts-group-name">${esc(label)}</span><span class="contracts-group-count">(${items.length})</span></div>${suffix}`;
  return el;
}

// bindContracts only stashes ctx now — the view (and every control) is rebuilt by renderContracts, and
// the page-header primary action button (app.js #primary-action) is what invokes newContract.
export function bindContracts(context) {
  ctx = context;
  window.addEventListener('fullworth:open-contract', event => { if (event.detail?.id) openDetail(event.detail.id); });
}
function askCoachAboutContract(contract, activity = null) {
  window.dispatchEvent(new CustomEvent('fullworth:coach-open', { detail: {
    entityType: 'contract',
    entityId: contract.id,
    entityLabel: contract.name,
    details: {
      amount: String(contract.amount ?? ''),
      currency: contract.currency || '',
      kind: contract.kind || '',
      status: contract.isActive ? 'active' : 'archived',
      nextDueDate: String(activity?.nextExpected || contract.nextDueDate || '').slice(0, 10),
      monthlyEquivalent: String(contract.monthlyEquivalent ?? ''),
      annualized: String(activity?.annualizedAmount ?? '')
    }
  }}));
}

// Used by the page-header primary action.
export function newContract(context) { if (context) ctx = context; return openContractDialog(); }

export async function renderContracts(context) {
  ctx = context;
  loadViewState();
  detectedVisibleCount = DETECTED_BATCH_SIZE;
  await ensureOfficialBrandCatalog(ctx.api);
  injectCss();
  const host = ctx.$('#view-contracts');
  let rows = [];
  try { rows = (await ctx.api('api/contracts')) || []; }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); rows = []; }
  try {
    const cancellationRows = (await ctx.api('api/contract-parity/cancellations')) || [];
    const cancellationById = new Map(cancellationRows.map(item => [item.contractId, item]));
    rows.forEach(contract => { contract.cancellation = cancellationById.get(contract.id) || null; });
  } catch {
    rows.forEach(contract => { contract.cancellation = null; });
  }
  contractsById = new Map(rows.map(c => [c.id, c]));
  allContracts = rows;
  // The list DTO only carries category/account ids; resolve their names best-effort for the row context
  // line and for account/category sorting. A failed lookup just omits that label — never blocks render.
  const [categoryRows, accountRows] = await Promise.all([
    ctx.api('api/categories').catch(() => []),
    ctx.api('api/accounts').catch(() => []),
  ]);
  categoryNames = new Map((categoryRows || []).map(category => [category.id, category.name]));
  categoryIcons = new Map((categoryRows || []).map(category => [
    category.id,
    (category.icon && !/^cat-\d/.test(category.icon)) ? category.icon : category.key
  ]));
  accountNames = new Map((accountRows || []).map(account => [account.id, account.displayName || account.institutionName]));

  host.innerHTML = viewHtml();
  wireControls(host);
  renderList(host);
  loadCloudBenchmarks();
  // Contextual alerts: detected subscriptions + price-change suggestions load quietly and surface only
  // when the backend actually has candidates, so they never dominate the header (UX rework §7).
  loadDetected(false);
  loadPriceChanges(false);
}

// Whole-view markup: top summary card (sum of monthlyEquivalent / annualizedAmount over active
// contracts, computed server-side), the detected/price-change alert slots, then the filter + list card.
function contractFilterCount() {
  return [
    view.status && view.status !== 'active' ? view.status : '',
    view.kind, view.account, view.category, view.cycle
  ].filter(Boolean).length;
}

function openContractFilterSheet(host) {
  const option = (value, label, selected) => `<option value="${esc(value)}"${selected === value ? ' selected' : ''}>${esc(label)}</option>`;
  const accountOptions = [...accountNames.entries()].map(([id, label]) => option(id, label, view.account)).join('');
  const categoryOptions = [...categoryNames.entries()].map(([id, label]) => option(id, label, view.category)).join('');
  const cycleOptions = CYCLES.map(value => option(value, ctx.get('contracts.cycle_' + value), view.cycle)).join('');
  const kindOptions = KINDS.map(value => option(value, ctx.get('contracts.kind_' + value), view.kind)).join('');
  const statusOptions = [
    ['', ctx.get('common.all')],
    ['active', ctx.get('contracts.status_active')],
    ['cancelled', ctx.get('contracts.status_cancelled')],
    ['archived', ctx.get('contracts.archived')],
  ].map(([value, label]) => option(value, label, view.status)).join('');

  const dlg = ctx.dialog(`<form class="dialog-card contracts-sortsheet contract-filter-sheet" method="dialog">
    <div class="panel-head"><h2>${esc(t('Verträge filtern', 'Filter contracts'))}</h2><button type="button" data-close aria-label="${esc(ctx.get('common.close'))}">×</button></div>
    <label>${esc(t('Status', 'Status'))}<select name="status">${statusOptions}</select></label>
    <label>${esc(t('Art', 'Type'))}<select name="kind"><option value="">${esc(ctx.get('common.all'))}</option>${kindOptions}</select></label>
    <label>${esc(ctx.get('contracts.account'))}<select name="account"><option value="">${esc(ctx.get('common.all'))}</option>${accountOptions}</select></label>
    <label>${esc(t('Kategorie', 'Category'))}<select name="category"><option value="">${esc(ctx.get('common.all'))}</option>${categoryOptions}</select></label>
    <label>${esc(t('Turnus', 'Billing cycle'))}<select name="cycle"><option value="">${esc(ctx.get('common.all'))}</option>${cycleOptions}</select></label>
    <div class="dialog-actions"><button type="button" class="btn btn-secondary" data-reset>${esc(t('Zurücksetzen', 'Reset'))}</button><button type="button" class="btn btn-primary" data-apply>${esc(ctx.get('common.apply'))}</button></div>
  </form>`);
  dlg.classList.add('contracts-sortsheet-dlg');
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-reset]').onclick = () => {
    view.status = 'active'; view.kind = ''; view.account = ''; view.category = ''; view.cycle = '';
    syncViewState(); dlg.close();
    host.innerHTML = viewHtml(); wireControls(host); renderList(host);
  };
  dlg.querySelector('[data-apply]').onclick = () => {
    const fd = new FormData(dlg.querySelector('form'));
    view.status = String(fd.get('status') || '');
    view.kind = String(fd.get('kind') || '');
    view.account = String(fd.get('account') || '');
    view.category = String(fd.get('category') || '');
    view.cycle = String(fd.get('cycle') || '');
    syncViewState(); dlg.close();
    host.innerHTML = viewHtml(); wireControls(host); renderList(host);
  };
  dlg.showModal();
}

function duplicateReviewHtml() {
  const groups = likelyDuplicateGroups();
  if (!groups.length) return '';

  const rows = groups.map(group => {
    const ordered = group.contracts.slice().sort((a, b) =>
      String(a.createdAt || '').localeCompare(String(b.createdAt || '')));
    const target = ordered[ordered.length - 1];
    const sourceIds = ordered.slice(0, -1).map(contract => contract.id);
    const accounts = new Set(ordered.map(contract => contract.accountId).filter(Boolean));
    const cycle = ctx.get('contracts.cycle_' + (target.billingCycle || 'monthly'));
    return `<div class="fw-row">
      <div class="fw-row-main">
        <div class="fw-row-title">${esc(target.providerName || target.name)}</div>
        <div class="fw-row-sub">${esc(t(
          `${ordered.length} erkannte Einträge · ${accounts.size} Zahlungskonten`,
          `${ordered.length} detected entries · ${accounts.size} payment accounts`
        ))}</div>
      </div>
      <div class="fw-row-amt">${ctx.money(target.amount, target.currency)}<small>${esc(cycle)}</small></div>
      <button type="button" class="btn btn-secondary" data-duplicate-merge="${target.id}" data-duplicate-sources="${sourceIds.join(',')}">${esc(t('Prüfen', 'Review'))}</button>
    </div>`;
  }).join('');

  return sectionCard(
    t('Mögliche doppelte Verträge', 'Possible duplicate contracts'),
    `<div class="row-sub">${esc(t(
      'Diese automatisch erkannten Verträge sehen nach einem Kontowechsel aus. Prüfe sie und führe sie bei Bedarf zu einem Vertrag zusammen.',
      'These automatically detected contracts look like an account change. Review them and merge them into one contract if appropriate.'
    ))}</div><div class="rows">${rows}</div>`,
    { className: 'contracts-duplicate-review' }
  );
}

function viewHtml() {
  const active = allContracts.filter(contract => contract.isActive);
  const sumMonthly = active.reduce((sum, contract) => sum + (Number(contract.monthlyEquivalent) || 0), 0);
  const sumAnnual = active.reduce((sum, contract) => sum + (Number(contract.annualizedAmount) || 0), 0);
  const cur = (active.find(contract => contract.currency) || {}).currency || 'EUR';
  const filterCount = contractFilterCount();

  const summary = `<section class="fw-card contracts-summary">
    <button type="button" class="contracts-summary-open" data-contract-analysis>
      <span class="contracts-summary-copy">
        <span class="contracts-summary-label">${esc(t('Ausgaben für Verträge', 'Contract spending'))}</span>
        <span class="contracts-summary-value">Ø ${ctx.money(sumMonthly, cur)} <small>/ ${esc(t('Monat', 'month'))}</small></span>
        <span class="contracts-summary-meta">${ctx.money(sumAnnual, cur)} ${esc(t('pro Jahr', 'per year'))} · ${active.length} ${esc(t('aktiv', 'active'))}</span>
      </span>
      <span class="contracts-summary-link">${esc(t('Analyse', 'Analysis'))} <span aria-hidden="true">›</span></span>
    </button>
  </section>`;

  const toolbar = `<div class="contracts-toolbar">
    <button type="button" class="contracts-sortbar" data-sort-open aria-haspopup="dialog">
      <span>${esc(t('Sortieren nach', 'Sort by'))}</span>
      <strong data-sort-current>${esc(sortLabel())}</strong>
      <span class="contracts-sortbar-caret" aria-hidden="true">↕</span>
    </button>
    <button type="button" class="contracts-filter-open${filterCount ? ' active' : ''}" data-filter-open aria-label="${esc(t('Filter', 'Filters'))}">
      ${sortIcon('<path d="M4 6h16M7 12h10M10 18h4"/>')}
      ${filterCount ? `<span>${filterCount}</span>` : ''}
    </button>
  </div>`;

  const duplicateReview = duplicateReviewHtml();

  return `<div class="contracts-ux">
    ${summary}
    ${duplicateReview}
    <div id="contracts-price-changes" class="detected-panel" hidden></div>
    <div id="contracts-detected" class="detected-panel" hidden></div>
    <div class="contracts-listcard">
      ${toolbar}
      <div class="contracts-list" data-list></div>
    </div>
    <div id="contracts-cloud-benchmarks" hidden></div>
  </div>`;
}

function benchmarkLabel(metricKey) {
  return ({
    'contract.energy.monthly_cost': t('Strom', 'Electricity'),
    'contract.internet.monthly_cost': t('Internet & Telefon', 'Internet & phone'),
    'contract.insurance.monthly_cost': t('Versicherung', 'Insurance'),
    'contract.insurance.health.monthly_cost': t('Krankenversicherung', 'Health insurance'),
  })[metricKey] || metricKey;
}

async function loadCloudBenchmarks() {
  const box = ctx.$('#contracts-cloud-benchmarks');
  if (!box) return;
  try {
    const result = await ctx.api('api/intelligence/benchmarks/contracts');
    if (!result?.available || !result.items?.length) {
      box.hidden = true;
      box.innerHTML = '';
      return;
    }

    const rows = result.items.map(item => {
      const local = Number(item.localMedian);
      const median = Number(item.median);
      const delta = median > 0 ? ((local - median) / median) * 100 : null;
      const relation = delta == null
        ? ''
        : delta > 2
          ? t(Math.round(delta) + ' % über Median', Math.round(delta) + '% above median')
          : delta < -2
            ? t(Math.abs(Math.round(delta)) + ' % unter Median', Math.abs(Math.round(delta)) + '% below median')
            : t('nahe am Median', 'near median');

      return `<div class="fw-row">
        <div class="fw-row-main">
          <div class="fw-row-title">${esc(benchmarkLabel(item.metricKey))}</div>
          <div class="fw-row-sub">${esc(t('Dein Vertragsmedian', 'Your contract median'))}: ${ctx.money(local, item.currency)} · ${esc(relation)}</div>
          <div class="fw-row-sub">${esc(t('Cloud-Spanne', 'Cloud range'))}: ${ctx.money(item.p25, item.currency)}–${ctx.money(item.p75, item.currency)} · ${item.distinctInstanceCount} ${esc(t('Instanzen', 'instances'))}</div>
        </div>
        <div class="fw-row-amt">${ctx.money(median, item.currency)}<small>${esc(t('Median / Monat', 'median / month'))}</small></div>
      </div>`;
    }).join('');

    box.hidden = false;
    box.innerHTML = sectionCard(
      t('Vergleich mit FullWorth Cloud', 'Compare with FullWorth Cloud'),
      `<div class="rows">${rows}</div><div class="row-sub">${esc(t('Nur aggregierte Werte ab mindestens 20 Instanzen.', 'Aggregates only, from at least 20 instances.'))}</div>`,
      { className: 'contracts-benchmarks' });
  } catch {
    box.hidden = true;
    box.innerHTML = '';
  }
}

function wireControls(host) {
  host.querySelector('[data-sort-open]')?.addEventListener('click', () => openSortSheet(host));
  host.querySelector('[data-filter-open]')?.addEventListener('click', () => openContractFilterSheet(host));
  host.querySelector('[data-contract-analysis]')?.addEventListener('click', () => openContractAnalysis());
  host.querySelectorAll('[data-duplicate-merge]').forEach(button => button.addEventListener('click', () => {
    const target = contractsById.get(button.dataset.duplicateMerge);
    if (!target) return;
    const sourceIds = String(button.dataset.duplicateSources || '').split(',').filter(Boolean);
    openMergeDialog(target, sourceIds);
  }));
}

function setActive(host, selector, activeEl) {
  host.querySelectorAll(selector).forEach(el => el.classList.toggle('active', el === activeEl));
}

function renderList(host) {
  const box = host.querySelector('[data-list]');
  if (!box) return;
  const shown = sortContracts(filterContracts(allContracts));
  if (!shown.length) {
    box.innerHTML = `<div class="contracts-empty">${esc(ctx.get('common.empty'))}</div>`;
    return;
  }

  box.innerHTML = '';
  const frag = document.createDocumentFragment();
  if (!groupKeyFor()) {
    const card = document.createElement('div');
    card.className = 'contracts-row-card';
    for (const contract of shown) card.appendChild(rowFor(contract));
    frag.appendChild(card);
    box.appendChild(frag);
    return;
  }

  const groups = new Map();
  for (const contract of shown) {
    const bucket = groupBucket(contract);
    if (!groups.has(bucket.key)) groups.set(bucket.key, { ...bucket, items: [] });
    groups.get(bucket.key).items.push(contract);
  }

  const cur = (allContracts.find(contract => contract.currency) || {}).currency || 'EUR';
  const ordered = [...groups.values()].sort((a, b) => {
    if (view.sort === 'cycle' || view.sort === 'due') return (a.rank ?? 0) - (b.rank ?? 0);
    return groupMonthly(b.items) - groupMonthly(a.items) || String(a.label).localeCompare(String(b.label));
  });
  for (const group of ordered) {
    frag.appendChild(groupHead(group.label, group.items, cur));
    const card = document.createElement('div');
    card.className = 'contracts-row-card';
    for (const contract of group.items) card.appendChild(rowFor(contract));
    frag.appendChild(card);
  }
  box.appendChild(frag);
}

function accountLabel(c) { return c.accountId ? (accountNames.get(c.accountId) || '') : ''; }
function categoryLabel(c) { return c.categoryId ? (categoryNames.get(c.categoryId) || '') : ''; }
function categoryIconKey(c) { return c.categoryId ? (categoryIcons.get(c.categoryId) || c.kind || '') : (c.kind || ''); }

function filterContracts(list) {
  return list.filter(c => {
    if (view.kind && (c.kind || '') !== view.kind) return false;
    if (view.account && String(c.accountId || '') !== view.account) return false;
    if (view.category && String(c.categoryId || '') !== view.category) return false;
    if (view.cycle && String(c.billingCycle || '') !== view.cycle) return false;
    const lifecycle = lifecycleStatus(c);
    if (view.status === 'active' && !['active', 'planned'].includes(lifecycle)) return false;
    if (view.status === 'cancelled' && lifecycle !== 'cancelled') return false;
    if (view.status === 'archived' && lifecycle !== 'archived') return false;
    return true;
  });
}

function sortContracts(list) {
  const dir = view.order === 'desc' ? -1 : 1;
  const byName = (a, b) => String(a.name || '').localeCompare(String(b.name || ''));
  const dueKey = contract => contract.nextDueDate ? String(contract.nextDueDate) : '9999-12-31';
  const cycleRank = contract => ({ monthly: 0, quarterly: 1, yearly: 2, weekly: 3 })[contract.billingCycle || 'monthly'] ?? 4;
  const cmp = {
    cycle: (a, b) => cycleRank(a) - cycleRank(b),
    due: (a, b) => dueKey(a).localeCompare(dueKey(b)),
    annual: (a, b) => (Number(a.annualizedAmount) || 0) - (Number(b.annualizedAmount) || 0),
    account: (a, b) => accountLabel(a).localeCompare(accountLabel(b)),
    category: (a, b) => categoryLabel(a).localeCompare(categoryLabel(b)),
    name: byName,
  }[view.sort] || (() => 0);
  return list.slice().sort((a, b) => dir * cmp(a, b) || byName(a, b));
}

// Injected once (no app.css edits). Everything else reuses the shared `.fw-*` and app.css classes.
// No-op: the contracts layout CSS lives in app.css (the app CSP blocks injected inline <style>).
function injectCss() { }

// Price-change suggestions (UI_UX_SPEC §13): detected jumps in a subscription's recurring amount, shown
// for the owner to accept (apply the new price to the contract) or dismiss. `detect` runs a fresh scan
// first; the passive path only lists existing pending suggestions. Not-owner access returns 404 → the
// panel stays hidden quietly. Joined to contractsById for the contract name and currency.
async function loadPriceChanges(detect) {
  const box = ctx.$('#contracts-price-changes');
  if (!box) return;
  try {
    if (detect) await ctx.api('api/contracts/price-changes/detect', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ detectedOn: new Date().toISOString().slice(0, 10) }) });
    const pending = ((await ctx.api('api/contracts/price-changes')) || []).filter(s => s.status === 'pending');
    if (!pending.length) {
      box.hidden = !detect;
      box.innerHTML = detect ? `<div class="panel-head"><h3>${ctx.esc(ctx.get('priceChanges.title'))}</h3></div><div class="row-sub">${ctx.esc(ctx.get('priceChanges.none'))}</div>` : '';
      return;
    }
    box.hidden = false;
    const items = pending.map(s => {
      const c = contractsById.get(s.contractId);
      const pct = `${s.percentChange > 0 ? '+' : ''}${Math.round(s.percentChange)}%`;
      return `<div class="detected-row" data-id="${s.id}">
        <div class="row-main">
          <div class="row-title">${ctx.esc(c ? c.name : ctx.get('priceChanges.title'))}</div>
          <div class="row-sub">${ctx.money(s.oldAmount, c?.currency)} → ${ctx.money(s.newAmount, c?.currency)} · ${ctx.esc(pct)} · ${ctx.esc(ctx.date(s.detectedOn))}</div>
        </div>
        <div class="row-side"><button type="button" class="btn btn-secondary" data-ignore>${ctx.esc(ctx.get('priceChanges.ignore'))}</button><button type="button" class="btn btn-primary" data-confirm>${ctx.esc(ctx.get('priceChanges.confirm'))}</button></div>
      </div>`;
    }).join('');
    box.innerHTML = `<div class="panel-head"><h3>${ctx.esc(ctx.get('priceChanges.title'))}</h3></div>${items}`;
    box.querySelectorAll('.detected-row').forEach(el => {
      el.querySelector('[data-confirm]').addEventListener('click', () => resolvePriceChange(el.dataset.id, 'confirm'));
      el.querySelector('[data-ignore]').addEventListener('click', () => resolvePriceChange(el.dataset.id, 'ignore'));
    });
  } catch (err) {
    if (detect) ctx.toast(err.message || ctx.get('common.error'));
    box.hidden = true; box.innerHTML = '';
  }
}

async function resolvePriceChange(id, action) {
  try {
    await ctx.api(`api/contracts/price-changes/${id}/${action}`, { method: 'POST' });
    ctx.toast(ctx.get(action === 'confirm' ? 'priceChanges.confirmed' : 'priceChanges.ignored'));
    await renderContracts(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

// Compact identity row (UX rework §7): brand/monogram icon, provider/contract name, type + category as
// secondary context, amount with its recurrence, and the next due date when it is still meaningful.
function rowFor(c) {
  const row = document.createElement('div');
  const lifecycle = lifecycleStatus(c);
  const dueBucketInfo = dueBucket(c);
  row.className = 'fw-row contract-row' +
    (lifecycle === 'archived' ? ' contract-archived' : '') +
    (dueBucketInfo.key === 'overdue' && c.isActive ? ' contract-overdue' : '');
  row.tabIndex = 0;
  row.setAttribute('role', 'button');

  const cycleKey = c.billingCycle || 'monthly';
  const cycle = ctx.get('contracts.cycle_' + cycleKey);
  const kind = ctx.get('contracts.kind_' + (c.kind || 'contract'));
  const category = categoryLabel(c) || kind;
  const account = accountLabel(c);
  const statusMarker = lifecycle === 'archived'
    ? ctx.get('contracts.archived')
    : lifecycle === 'cancelled'
      ? ctx.get('contracts.status_cancelled')
      : lifecycle === 'planned'
        ? ctx.get('contracts.status_planned')
        : '';
  const marker = statusMarker ? ` <span class="tx-marker">${ctx.esc(statusMarker)}</span>` : '';

  let secondary = category;
  if (view.sort === 'category' && account) secondary = account;
  else if (view.sort === 'name' || view.sort === 'annual') secondary = [category, cycle].filter(Boolean).join(' · ');

  let amount = ctx.money(c.amount, c.currency);
  let amountSub = cycle;
  if (view.sort === 'annual') {
    amount = ctx.money(c.annualizedAmount, c.currency);
    amountSub = t('pro Jahr', 'per year');
  } else if (view.sort === 'due') {
    amountSub = c.nextDueDate ? ctx.date(c.nextDueDate) : t('keine Fälligkeit', 'no due date');
  } else if (view.sort === 'cycle') {
    amountSub = '';
  }

  const alert = dueBucketInfo.key === 'overdue' && c.isActive
    ? `<div class="contract-row-alert">${esc(t('Fälligkeit überschritten', 'Past due'))}</div>`
    : lifecycle === 'planned' && c.cancellation?.cancellationDeadline
      ? `<div class="contract-row-alert">${esc(t('Kündigungsfrist', 'Cancellation deadline'))}: ${ctx.esc(ctx.date(c.cancellation.cancellationDeadline))}</div>`
      : '';

  row.innerHTML = `${identityIcon(c.providerName || c.name, { logoAssetPath: c.logoAssetPath, categoryIconKey: categoryIconKey(c) })}
    <div class="fw-row-main">
      <div class="fw-row-title">${ctx.esc(c.name)}${marker}</div>
      <div class="fw-row-sub">${ctx.esc(secondary)}</div>
      ${alert}
    </div>
    <div class="fw-row-amt">${amount}${amountSub ? `<small>${ctx.esc(amountSub)}</small>` : ''}</div>
    <span class="contract-row-chevron" aria-hidden="true">›</span>`;

  const open = () => openDetail(c.id);
  row.addEventListener('click', event => { if (!event.target.closest('button')) open(); });
  row.addEventListener('keydown', event => {
    if (!event.target.closest('button') && (event.key === 'Enter' || event.key === ' ')) {
      event.preventDefault();
      open();
    }
  });
  return row;
}

async function loadDetected(interactive) {
  const box = ctx.$('#contracts-detected');
  if (!box) return;
  let candidates;
  try { candidates = await ctx.api('api/contracts/detection'); }
  catch (err) { if (interactive) ctx.toast(err.message || ctx.get('common.error')); box.hidden = true; return; }
  candidates = candidates || [];
  if (!candidates.length) {
    box.hidden = !interactive;
    box.innerHTML = interactive ? `<div class="panel-head"><h3>${ctx.esc(ctx.get('contracts.detectedTitle'))}</h3></div><div class="row-sub">${ctx.esc(ctx.get('contracts.detectedNone'))}</div>` : '';
    return;
  }
  box.hidden = false;
  const visible = candidates.slice(0, detectedVisibleCount);
  const items = visible.map((cand, i) => {
    const due = cand.nextDueDate ? ` · ${ctx.esc(ctx.get('contracts.nextDue'))}: ${ctx.esc(ctx.date(cand.nextDueDate))}` : '';
    return `
    <div class="detected-row" data-i="${i}">
      <div class="row-main detected-main">
        ${identityIcon(cand.counterparty)}
        <div class="detected-copy">
          <div class="row-title">${ctx.esc(cand.counterparty)}</div>
          <div class="row-sub">${ctx.esc(ctx.get('contracts.cycle_' + (cand.billingCycle || 'monthly')))}${due}</div>
        </div>
      </div>
      <div class="row-side detected-side">
        <span class="amount">${ctx.money(cand.typicalAmount, cand.currency)}</span>
        <div class="detected-actions">
          <button type="button" class="btn btn-secondary" data-dismiss>${ctx.esc(ctx.get('contracts.dismiss'))}</button>
          <button type="button" class="btn btn-primary" data-accept>${ctx.esc(ctx.get('contracts.accept'))}</button>
        </div>
      </div>
    </div>`;
  }).join('');
  const remaining = Math.max(0, candidates.length - visible.length);
  const more = remaining
    ? `<button type="button" class="btn btn-secondary contracts-more-suggestions" data-detected-more>${ctx.esc(t(`Weitere ${remaining} anzeigen`, `Show ${remaining} more`))}</button>`
    : '';
  box.innerHTML = `<div class="panel-head"><h3>${ctx.esc(ctx.get('contracts.detectedTitle'))}</h3></div>${items}${more}`;
  box.querySelectorAll('.detected-row').forEach(el => {
    el.querySelector('[data-accept]').addEventListener('click', () => acceptCandidate(visible[Number(el.dataset.i)], el));
    el.querySelector('[data-dismiss]').addEventListener('click', () => dismissCandidate(visible[Number(el.dataset.i)], el));
  });
  box.querySelector('[data-detected-more]')?.addEventListener('click', () => {
    detectedVisibleCount += DETECTED_BATCH_SIZE;
    loadDetected(false);
  });
}

async function acceptCandidate(candidate, row) {
  const buttons = row ? [...row.querySelectorAll('button')] : [];
  buttons.forEach(button => { button.disabled = true; });
  try {
    await ctx.api('api/contracts/detection/accept', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(candidate) });
    row?.remove();
    ctx.toast(ctx.get('common.saved'));
    await renderContracts(ctx);
  } catch (err) {
    buttons.forEach(button => { button.disabled = false; });
    ctx.toast(err.message || ctx.get('common.error'));
  }
}

// Reject a detected candidate so it stops reappearing in future detection runs.
async function dismissCandidate(candidate) {
  try {
    await ctx.api('api/contracts/detection/dismiss', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ counterparty: candidate.counterparty, currency: candidate.currency }) });
    ctx.toast(ctx.get('contracts.dismissed'));
    await renderContracts(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}


function editableDetailRow(label, value, field) {
  return `<button type="button" class="contract-field-row" data-quick-edit="${field}">
    <span>${ctx.esc(label)}</span>
    <strong>${ctx.esc(value || '—')}</strong>
    <span class="contract-field-edit" aria-hidden="true">✎</span>
  </button>`;
}

async function saveContractPatch(contract, patch) {
  await ctx.api(`api/contracts/${contract.id}`, jsonBody({ ...contractToWrite(contract), ...patch }, 'PUT'));
  ctx.toast(ctx.get('common.saved'));
  await renderContracts(ctx);
}

async function openQuickEdit(contract, field) {
  let title = '';
  let control = '';
  let readValue = null;

  if (field === 'amount') {
    title = t('Betrag bearbeiten', 'Edit amount');
    control = `<label>${ctx.esc(ctx.get('transactions.amount'))}<input name="value" type="number" step="0.01" inputmode="decimal" required value="${Number(contract.amount) || 0}"></label>`;
    readValue = fd => ({ amount: Number(fd.get('value')) });
  } else if (field === 'cycle') {
    title = t('Turnus bearbeiten', 'Edit billing cycle');
    control = `<label>${ctx.esc(ctx.get('contracts.billingCycle'))}<select name="value">${CYCLES.map(value => `<option value="${value}"${value === (contract.billingCycle || 'monthly') ? ' selected' : ''}>${ctx.esc(ctx.get('contracts.cycle_' + value))}</option>`).join('')}</select></label>`;
    readValue = fd => ({ billingCycle: String(fd.get('value') || 'monthly') });
  } else if (field === 'category') {
    title = t('Kategorie bearbeiten', 'Edit category');
    control = `<label>${ctx.esc(t('Kategorie', 'Category'))}<select name="value"><option value="">—</option>${[...categoryNames.entries()].map(([id, label]) => `<option value="${id}"${id === contract.categoryId ? ' selected' : ''}>${ctx.esc(label)}</option>`).join('')}</select></label>`;
    readValue = fd => ({ categoryId: String(fd.get('value') || '') || null });
  } else if (field === 'account') {
    title = t('Zahlungskonto bearbeiten', 'Edit payment account');
    control = `<label>${ctx.esc(ctx.get('contracts.account'))}<select name="value"><option value="">—</option>${[...accountNames.entries()].map(([id, label]) => `<option value="${id}"${id === contract.accountId ? ' selected' : ''}>${ctx.esc(label)}</option>`).join('')}</select></label>`;
    readValue = fd => ({ accountId: String(fd.get('value') || '') || null });
  } else if (field === 'nextDue') {
    title = t('Nächste Fälligkeit bearbeiten', 'Edit next due date');
    control = `<label>${ctx.esc(t('Nächste Fälligkeit', 'Next due date'))}<input name="value" type="date" value="${String(contract.nextDueDate || '').slice(0, 10)}"></label>`;
    readValue = fd => ({ nextDueDate: String(fd.get('value') || '') || null });
  } else if (field === 'name') {
    title = t('Name bearbeiten', 'Edit name');
    control = `<label>${ctx.esc(ctx.get('common.name'))}<input name="value" maxlength="160" required value="${ctx.esc(contract.name || '')}"></label>`;
    readValue = fd => ({ name: String(fd.get('value') || '').trim() });
  } else {
    return;
  }

  const dlg = ctx.dialog(`<form class="dialog-card contracts-sortsheet contract-quick-edit">
    <div class="panel-head"><h2>${ctx.esc(title)}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    ${control}
    <div class="dialog-actions"><button type="button" class="btn btn-secondary" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit" class="btn btn-primary">${ctx.esc(ctx.get('common.apply'))}</button></div>
  </form>`);
  dlg.classList.add('contracts-sortsheet-dlg');
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const submit = event.currentTarget.querySelector('[type="submit"]');
    submit.disabled = true;
    try {
      await saveContractPatch(contract, readValue(new FormData(event.currentTarget)));
      dlg.close();
      await openDetail(contract.id);
    } catch (err) {
      submit.disabled = false;
      ctx.toast(err.message || ctx.get('common.error'));
    }
  };
  dlg.showModal();
}

function openPaymentsDialog(contract, payments) {
  const rows = (payments || []).map(payment => `<div class="contract-payment-row">
    <div><strong>${ctx.esc(ctx.date(payment.date))}</strong><span>${ctx.esc(contract.providerName || contract.name)}</span></div>
    <strong>${ctx.money(payment.amount, payment.currency)}</strong>
  </div>`).join('');
  const dlg = ctx.dialog(`<div class="dialog-card contract-payments-dialog">
    <div class="panel-head"><div><h2>${ctx.esc(t('Buchungen', 'Payments'))}</h2><div class="row-sub">${ctx.esc(contract.name)}</div></div><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <div class="contract-payment-list">${rows || `<div class="row-sub">${ctx.esc(ctx.get('contracts.noPayments'))}</div>`}</div>
  </div>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.showModal();
}

async function openDetail(id) {
  let contract, activity, cancellation, cloudBenchmark, mergedSources;
  try {
    [contract, activity, cancellation, cloudBenchmark, mergedSources] = await Promise.all([
      ctx.api(`api/contracts/${id}`),
      ctx.api(`api/contracts/${id}/activity`),
      ctx.api(`api/contract-parity/${id}/cancellation`).catch(() => null),
      ctx.api(`api/intelligence/benchmarks/contracts/${id}`).catch(() => null),
      ctx.api(`api/contracts/${id}/merged-sources`).catch(() => [])
    ]);
  } catch (err) {
    ctx.toast(err.message || ctx.get('common.error'));
    return;
  }

  cancellation ||= {};
  contract.cancellation = cancellation;
  const lifecycle = lifecycleStatus(contract);
  const payments = activity?.payments || [];
  const previewPayments = payments.slice(0, 4);
  const cycle = ctx.get('contracts.cycle_' + (contract.billingCycle || 'monthly'));
  const category = categoryLabel(contract) || ctx.get('contracts.kind_' + (contract.kind || 'contract'));
  const account = accountLabel(contract) || '—';
  const next = activity?.nextExpected || contract.nextDueDate;
  const annualized = activity?.annualizedAmount ?? contract.annualizedAmount ?? 0;
  const statusMarker = lifecycle === 'archived'
    ? ctx.get('contracts.archived')
    : lifecycle === 'cancelled'
      ? ctx.get('contracts.status_cancelled')
      : lifecycle === 'planned'
        ? ctx.get('contracts.status_planned')
        : '';

  const paymentRows = previewPayments.length
    ? previewPayments.map((payment, index) => `<div class="contract-payment-row">
        <div><strong>${ctx.esc(ctx.date(payment.date))}</strong><span>${index === 0 ? ctx.esc(t('Letzte Zahlung', 'Last payment')) : ctx.esc(category)}</span></div>
        <strong>${ctx.money(payment.amount, payment.currency)}</strong>
      </div>`).join('')
    : `<div class="row-sub">${ctx.esc(ctx.get('contracts.noPayments'))}</div>`;

  const extraData = [
    contract.startDate ? [ctx.get('contracts.startDate'), ctx.date(contract.startDate)] : null,
    contract.endDate ? [ctx.get('contracts.endDate'), ctx.date(contract.endDate)] : null,
    cancellation.minimumTermEnd ? [ctx.get('contracts.minimumTermEnd'), ctx.date(cancellation.minimumTermEnd)] : null,
    cancellation.cancellationDeadline ? [ctx.get('contracts.cancellationDeadline'), ctx.date(cancellation.cancellationDeadline)] : null,
    cancellation.customerNumber ? [ctx.get('contracts.customerNumber'), cancellation.customerNumber] : null,
    [t('Kosten pro Jahr', 'Cost per year'), ctx.money(annualized, contract.currency)],
    [t('Erkennung', 'Detection'), contract.autoDetected ? t('Automatisch', 'Automatic') : t('Manuell', 'Manual')]
  ].filter(Boolean).map(([label, value]) => `<div class="contract-data-row"><span>${ctx.esc(label)}</span><strong>${ctx.esc(value)}</strong></div>`).join('');

  let benchmark = '';
  if (cloudBenchmark?.available) {
    const median = Number(cloudBenchmark.median) || 0;
    const local = Number(cloudBenchmark.localMonthly) || Number(contract.monthlyEquivalent) || 0;
    const delta = median > 0 ? Math.round(((local - median) / median) * 100) : 0;
    const relation = Math.abs(delta) <= 2
      ? t('Nahe am Vergleich', 'Near benchmark')
      : delta > 0
        ? t(`${delta} % über Vergleich`, `${delta}% above benchmark`)
        : t(`${Math.abs(delta)} % unter Vergleich`, `${Math.abs(delta)}% below benchmark`);
    benchmark = `<section class="contract-detail-card">
      <div class="contract-insight-row"><span>${ctx.esc(t('Kostenvergleich', 'Cost comparison'))}</span><strong>${ctx.esc(relation)}</strong><span aria-hidden="true">›</span></div>
    </section>`;
  }

  const sources = (mergedSources || []).map(source => {
    const sourceAccount = source.accountId ? (accountNames.get(source.accountId) || ctx.get('contracts.account')) : t('Ohne festes Konto', 'No fixed account');
    return `<div class="contract-source-row"><div><strong>${ctx.esc(source.name)}</strong><span>${ctx.esc(sourceAccount)}</span></div><button type="button" data-unmerge="${source.id}">${ctx.esc(ctx.get('contracts.unmerge'))}</button></div>`;
  }).join('');

  const dlg = ctx.dialog(`<div class="dialog-card contract-detail contract-detail-v2">
    <div class="contract-detail-topbar">
      <button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button>
      <button type="button" data-edit-all aria-label="${ctx.esc(ctx.get('contracts.edit'))}">•••</button>
    </div>

    <div class="contract-detail-hero">
      <div class="contract-detail-logo">${identityIcon(contract.providerName || contract.name, { logoAssetPath: contract.logoAssetPath, categoryIconKey: categoryIconKey(contract) })}</div>
      <div class="contract-detail-name">
        <h2>${ctx.esc(contract.name)}${statusMarker ? ` <span class="tx-marker">${ctx.esc(statusMarker)}</span>` : ''}</h2>
        <button type="button" data-quick-edit="name" aria-label="${ctx.esc(t('Name bearbeiten', 'Edit name'))}">✎</button>
      </div>
      ${contract.providerName && contract.providerName !== contract.name ? `<div class="contract-detail-provider">${ctx.esc(contract.providerName)}</div>` : ''}
      <div class="contract-detail-price">${ctx.money(activity?.expectedAmount ?? contract.amount, contract.currency)} <small>/ ${ctx.esc(cycle)}</small></div>
      <div class="contract-detail-due">${next ? `${ctx.esc(t('Nächste Zahlung', 'Next payment'))} ${ctx.esc(ctx.date(next))}` : ctx.esc(t('Keine Fälligkeit hinterlegt', 'No due date set'))}</div>
    </div>

    <section class="contract-detail-card contract-main-fields">
      ${editableDetailRow(ctx.get('transactions.amount'), ctx.money(contract.amount, contract.currency), 'amount')}
      ${editableDetailRow(ctx.get('contracts.billingCycle'), cycle, 'cycle')}
      ${editableDetailRow(t('Kategorie', 'Category'), category, 'category')}
      ${editableDetailRow(t('Zahlungskonto', 'Payment account'), account, 'account')}
      ${editableDetailRow(t('Nächste Fälligkeit', 'Next due date'), next ? ctx.date(next) : '—', 'nextDue')}
    </section>

    ${benchmark}

    <div class="contract-section-label">${ctx.esc(t('Buchungen', 'Payments'))}</div>
    <section class="contract-detail-card">
      <div class="contract-payment-list">${paymentRows}</div>
      ${payments.length > 4 ? `<button type="button" class="contract-card-link" data-all-payments>${ctx.esc(t(`Alle ${payments.length} Buchungen anzeigen`, `Show all ${payments.length} payments`))}<span>›</span></button>` : ''}
    </section>

    <div class="contract-section-label">${ctx.esc(t('Vertrag', 'Contract'))}</div>
    <section class="contract-detail-card contract-actions-card">
      <button type="button" data-cancellation><span>${ctx.esc(t('Laufzeit & Kündigung', 'Term & cancellation'))}</span><span>›</span></button>
      ${contract.notes
        ? `<div class="contract-note"><span>${ctx.esc(ctx.get('contracts.notes'))}</span><p>${ctx.esc(contract.notes)}</p></div>`
        : `<button type="button" data-edit-all><span>${ctx.esc(t('Notiz hinzufügen', 'Add note'))}</span><span>›</span></button>`}
      ${sources ? `<details class="contract-sources"><summary>${ctx.esc(t('Zahlungskonten & Historie', 'Payment accounts & history'))} <small>${mergedSources.length}</small></summary>${sources}</details>` : ''}
      <button type="button" data-merge><span>${ctx.esc(t('Ähnliche Verträge zusammenführen', 'Merge similar contracts'))}</span><span>›</span></button>
    </section>

    <details class="contract-detail-card contract-more-data">
      <summary>${ctx.esc(t('Weitere Vertragsdaten', 'More contract details'))}<span>›</span></summary>
      <div>${extraData}</div>
    </details>

    <div class="contract-section-label">${ctx.esc(t('Einstellungen', 'Settings'))}</div>
    <section class="contract-detail-card contract-actions-card">
      <button type="button" data-edit-all><span>${ctx.esc(t('Alle Daten bearbeiten', 'Edit all details'))}</span><span>›</span></button>
      <button type="button" data-coach><span>${ctx.esc(t('Coach fragen', 'Ask Coach'))}</span><span>›</span></button>
      ${contract.isActive
        ? `<button type="button" class="btn btn-danger contract-action-danger" data-archive><span>${ctx.esc(ctx.get('contracts.archive'))}</span><span>›</span></button>`
        : `<button type="button" data-reactivate><span>${ctx.esc(ctx.get('contracts.reactivate'))}</span><span>›</span></button>`}
    </section>
  </div>`);
  dlg.classList.add('contracts-detail-dlg');

  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelectorAll('[data-quick-edit]').forEach(button => button.addEventListener('click', () => {
    dlg.close();
    openQuickEdit(contract, button.dataset.quickEdit);
  }));
  dlg.querySelectorAll('[data-edit-all]').forEach(button => button.addEventListener('click', () => {
    dlg.close();
    openContractDialog(contract);
  }));
  dlg.querySelector('[data-all-payments]')?.addEventListener('click', () => openPaymentsDialog(contract, payments));
  dlg.querySelector('[data-cancellation]')?.addEventListener('click', () => {
    dlg.close();
    openCancellationDialog(contract, cancellation);
  });
  dlg.querySelector('[data-merge]')?.addEventListener('click', () => {
    dlg.close();
    openMergeDialog(contract);
  });
  dlg.querySelector('[data-coach]')?.addEventListener('click', () => {
    dlg.close();
    askCoachAboutContract(contract, activity);
  });
  dlg.querySelectorAll('[data-unmerge]').forEach(button => button.addEventListener('click', async () => {
    button.disabled = true;
    try {
      await ctx.api(`api/contract-parity/merge/${id}/${button.dataset.unmerge}`, { method: 'DELETE' });
      dlg.close();
      ctx.toast(ctx.get('contracts.unmergedToast'));
      await renderContracts(ctx);
      await openDetail(id);
    } catch (err) {
      button.disabled = false;
      ctx.toast(err.message || ctx.get('common.error'));
    }
  }));
  dlg.querySelector('[data-archive]')?.addEventListener('click', async () => {
    if (!await ctx.confirm(ctx.get('contracts.archiveConfirm').replace('{name}', contract.name), { destructive: true, confirmLabel: ctx.get('contracts.archive') })) return;
    try {
      await ctx.api(`api/contracts/${id}`, { method: 'DELETE' });
      dlg.close();
      ctx.toast(ctx.get('contracts.archivedToast'));
      await renderContracts(ctx);
    } catch (err) {
      ctx.toast(err.message || ctx.get('common.error'));
    }
  });
  dlg.querySelector('[data-reactivate]')?.addEventListener('click', async () => {
    try {
      await ctx.api(`api/contracts/${id}`, jsonBody({ ...contractToWrite(contract), isActive: true }, 'PUT'));
      dlg.close();
      ctx.toast(ctx.get('common.saved'));
      await renderContracts(ctx);
    } catch (err) {
      ctx.toast(err.message || ctx.get('common.error'));
    }
  });
  dlg.showModal();
}
function mergeCandidateScore(primary, candidate) {
  let score = 0;
  const primaryIdentity = contractIdentityKey(primary);
  const candidateIdentity = contractIdentityKey(candidate);
  if (primaryIdentity && candidateIdentity && primaryIdentity === candidateIdentity) score += 12;
  if ((primary.currency || '') === (candidate.currency || '')) score += 4;
  if (Math.abs(Number(primary.amount || 0) - Number(candidate.amount || 0)) < 0.01) score += 6;
  if ((primary.billingCycle || 'monthly') === (candidate.billingCycle || 'monthly')) score += 3;
  if (primary.accountId && candidate.accountId && primary.accountId !== candidate.accountId) score += 1;
  return score;
}

async function openMergeDialog(contract, preselectedIds = []) {
  const preselected = new Set(preselectedIds);
  const candidates = allContracts
    .filter(candidate => candidate.id !== contract.id && (candidate.currency || '') === (contract.currency || ''))
    .slice()
    .sort((a, b) => {
      const score = mergeCandidateScore(contract, b) - mergeCandidateScore(contract, a);
      return score || String(a.name || '').localeCompare(String(b.name || ''));
    });

  if (!candidates.length) {
    ctx.toast(ctx.get('contracts.mergeNone'));
    return;
  }

  const rows = candidates.map(candidate => {
    const account = candidate.accountId ? (accountNames.get(candidate.accountId) || ctx.get('contracts.account')) : t('Ohne festes Konto', 'No fixed account');
    const archived = candidate.isActive === false ? ` · ${ctx.get('contracts.archived')}` : '';
    return `<label class="check contract-merge-option">
      <input type="checkbox" name="sourceContractId" value="${candidate.id}"${preselected.has(candidate.id) ? ' checked' : ''}>
      <span><strong>${ctx.esc(candidate.name)}</strong><span class="row-sub">${ctx.esc(account)} · ${ctx.money(candidate.amount, candidate.currency)} · ${ctx.esc(ctx.get('contracts.cycle_' + (candidate.billingCycle || 'monthly')))}${ctx.esc(archived)}</span></span>
    </label>`;
  }).join('');

  const dlg = ctx.dialog(`<form class="dialog-card contract-dialog">
    <div class="panel-head"><div><h2>${ctx.esc(ctx.get('contracts.mergeTitle'))}</h2><div class="row-sub">${ctx.esc(contract.name)}</div></div><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <div class="row-sub">${ctx.esc(ctx.get('contracts.mergeHint'))}</div>
    <div class="contract-merge-options">${rows}</div>
    <div class="dialog-actions"><button type="button" class="btn btn-secondary" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit" class="btn btn-primary" data-merge-submit disabled>${ctx.esc(ctx.get('contracts.mergeConfirm'))}</button></div>
  </form>`);

  const submit = dlg.querySelector('[data-merge-submit]');
  const updateSubmit = () => { submit.disabled = !dlg.querySelector('input[name="sourceContractId"]:checked'); };
  dlg.querySelectorAll('input[name="sourceContractId"]').forEach(input => input.addEventListener('change', updateSubmit));
  updateSubmit();
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const sourceContractIds = [...dlg.querySelectorAll('input[name="sourceContractId"]:checked')].map(input => input.value);
    if (!sourceContractIds.length) return;
    submit.disabled = true;
    try {
      await ctx.api('api/contract-parity/merge', jsonBody({
        contractIds: [contract.id, ...sourceContractIds],
        targetContractId: contract.id
      }, 'POST'));
      dlg.close();
      ctx.toast(ctx.get('contracts.mergedToast'));
      await renderContracts(ctx);
    } catch (err) {
      submit.disabled = false;
      ctx.toast(err.message || ctx.get('common.error'));
    }
  };
  dlg.showModal();
}

async function openCancellationDialog(contract, existing) {
  let details = existing;
  if (!details) {
    try { details = await ctx.api(`api/contract-parity/${contract.id}/cancellation`); }
    catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }
  }
  details ||= {};
  const dv = value => value ? String(value).slice(0, 10) : '';
  const statusOptions = ['none', 'planned', 'sent', 'confirmed', 'cancelled']
    .map(status => `<option value='${status}'${cancellationStatus(details) === status ? ' selected' : ''}>${ctx.esc(cancellationStatusLabel(status))}</option>`).join('');
  const unitOptions = selected => ['days', 'weeks', 'months']
    .map(unit => `<option value='${unit}'${selected === unit ? ' selected' : ''}>${ctx.esc(ctx.get('contracts.period_' + unit))}</option>`).join('');

  const dlg = ctx.dialog(`<form class='dialog-card contract-dialog'>
    <div class='panel-head'><div><h2>${ctx.esc(ctx.get('contracts.manageCancellation'))}</h2><div class='row-sub'>${ctx.esc(contract.name)}</div></div><button type='button' data-close aria-label='${ctx.esc(ctx.get('common.close'))}'>×</button></div>
    <label>${ctx.esc(ctx.get('contracts.status'))}<select name='status'>${statusOptions}</select></label>
    <label>${ctx.esc(ctx.get('contracts.minimumTermEnd'))}<input name='minimumTermEnd' type='date' value='${dv(details.minimumTermEnd)}'></label>
    <label>${ctx.esc(ctx.get('contracts.cancelEffectiveDate'))}<input name='effectiveEndDate' type='date' value='${dv(contract.endDate)}'></label>
    <div class='rule-grid'>
      <label>${ctx.esc(ctx.get('contracts.noticePeriod'))}<input name='noticeValue' type='number' min='0' value='${details.noticePeriodValue ?? ''}'></label>
      <label>${ctx.esc(ctx.get('contracts.periodUnit'))}<select name='noticeUnit'>${unitOptions(details.noticePeriodUnit || 'months')}</select></label>
    </div>
    <label>${ctx.esc(ctx.get('contracts.cancellationDeadline'))}<input name='deadline' type='date' value='${dv(details.cancellationDeadline)}'></label>
    <div class='rule-grid'>
      <label>${ctx.esc(ctx.get('contracts.renewalPeriod'))}<input name='renewalValue' type='number' min='0' value='${details.renewalPeriodValue ?? ''}'></label>
      <label>${ctx.esc(ctx.get('contracts.periodUnit'))}<select name='renewalUnit'>${unitOptions(details.renewalPeriodUnit || 'months')}</select></label>
    </div>
    <label class='check'><input name='autoRenews' type='checkbox'${details.autoRenews ? ' checked' : ''}> ${ctx.esc(ctx.get('contracts.autoRenews'))}</label>
    <label>${ctx.esc(ctx.get('contracts.customerNumber'))}<input name='customerNumber' maxlength='160' value='${ctx.esc(details.customerNumber || '')}'></label>
    <label>${ctx.esc(ctx.get('contracts.providerContact'))}<textarea name='providerContact' maxlength='500' rows='2'>${ctx.esc(details.providerContact || '')}</textarea></label>
    ${details.cancellationSentAt ? `<div class='row-sub'>${ctx.esc(ctx.get('contracts.cancelledOn'))}: ${ctx.esc(ctx.dateTime(details.cancellationSentAt))}</div>` : ''}
    ${details.cancellationConfirmedAt ? `<div class='row-sub'>${ctx.esc(ctx.get('contracts.confirmedOn'))}: ${ctx.esc(ctx.dateTime(details.cancellationConfirmedAt))}</div>` : ''}
    <div class='dialog-actions'><button type='button' class='btn btn-secondary' data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type='submit' class='btn btn-primary'>${ctx.esc(ctx.get('common.apply'))}</button></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const fd = new FormData(event.currentTarget);
    const numberOrNull = name => fd.get(name) === '' ? null : Number(fd.get(name));
    const effectiveEndDate = fd.get('effectiveEndDate') || null;
    const body = {
      minimumTermEnd: fd.get('minimumTermEnd') || null,
      noticePeriodValue: numberOrNull('noticeValue'),
      noticePeriodUnit: fd.get('noticeUnit') || null,
      renewalPeriodValue: numberOrNull('renewalValue'),
      renewalPeriodUnit: fd.get('renewalUnit') || null,
      autoRenews: fd.get('autoRenews') === 'on',
      cancellationDeadline: fd.get('deadline') || null,
      cancellationStatus: fd.get('status') || 'none',
      customerNumber: (fd.get('customerNumber') || '').trim() || null,
      providerContact: (fd.get('providerContact') || '').trim() || null
    };
    try {
      await ctx.api(`api/contract-parity/${contract.id}/cancellation`, jsonBody(body, 'PUT'));
      if (effectiveEndDate !== (contract.endDate || null)) {
        await ctx.api(`api/contracts/${contract.id}`, jsonBody({ ...contractToWrite(contract), endDate: effectiveEndDate }, 'PUT'));
      }
      dlg.close();
      ctx.toast(ctx.get('common.saved'));
      await renderContracts(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

// Payment trend as a small SVG polyline (oldest→newest). Masked in privacy mode by hiding the line.
function sparkline(payments) {
  if (!payments || payments.length < 2) return '';
  const vals = payments.slice().reverse().map(p => Number(p.amount));
  const min = Math.min(...vals), max = Math.max(...vals), span = (max - min) || 1;
  const w = 600, h = 90;
  const pts = vals.map((v, i) => `${(i / (vals.length - 1)) * w},${h - ((v - min) / span) * (h - 16) - 8}`).join(' ');
  if (ctx.isPrivate()) return `<div class="contract-trend private"><div class="row-sub">${ctx.esc(ctx.get('privacy.hidden'))}</div></div>`;
  return `<div class="contract-trend"><svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${ctx.esc(ctx.get('contracts.trend'))}"><polyline points="${pts}" fill="none" stroke="currentColor" stroke-width="2" vector-effect="non-scaling-stroke"/></svg></div>`;
}



function contractMonthKey(value) {
  return String(value instanceof Date
    ? `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, '0')}`
    : value || '').slice(0, 7);
}

function contractAnalysisBars(months) {
  const max = Math.max(1, ...months.map(month => month.value));
  const width = 720, height = 180, baseline = 148;
  const slot = width / Math.max(1, months.length);
  const bars = months.map((month, index) => {
    const barHeight = Math.max(2, (month.value / max) * 116);
    const x = index * slot + slot * .18;
    const barWidth = slot * .64;
    const y = baseline - barHeight;
    const label = new Intl.DateTimeFormat(lang() ? 'de-DE' : 'en-US', { month: 'short' })
      .format(new Date(month.key + '-01T12:00:00'));
    return `<g><rect class="contract-analysis-bar" x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${barWidth.toFixed(1)}" height="${barHeight.toFixed(1)}" rx="5"></rect><text class="contract-analysis-axis" x="${(x + barWidth / 2).toFixed(1)}" y="172" text-anchor="middle">${ctx.esc(label)}</text></g>`;
  }).join('');
  return `<svg class="contract-analysis-chart" viewBox="0 0 ${width} ${height}" role="img" aria-label="${ctx.esc(t('Vertragskosten im Verlauf', 'Contract cost history'))}"><line x1="0" y1="${baseline}" x2="${width}" y2="${baseline}" class="contract-analysis-zero"></line>${bars}</svg>`;
}

function contractAnalysisDonut(rows, total) {
  if (!rows.length || total <= 0) return '';
  let offset = 0;
  const segments = rows.slice(0, 8).map((row, index) => {
    const share = Math.max(0, Math.min(100, row.value / total * 100));
    const segment = `<circle class="contract-analysis-segment contract-analysis-seg-${index % 8}" cx="50" cy="50" r="40" pathLength="100" stroke-dasharray="${share} ${100 - share}" stroke-dashoffset="${-offset}" />`;
    offset += share;
    return segment;
  }).join('');
  return `<svg viewBox="0 0 100 100" class="contract-analysis-donut" aria-hidden="true"><circle class="contract-analysis-donut-bg" cx="50" cy="50" r="40"/>${segments}</svg>`;
}

function contractDateParam(value) {
  const year = value.getFullYear();
  const month = String(value.getMonth() + 1).padStart(2, '0');
  const day = String(value.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

async function openContractAnalysis() {
  const active = allContracts.filter(contract => contract.isActive);
  const currency = (active.find(contract => contract.currency) || {}).currency || 'EUR';
  const monthly = active.reduce((sum, contract) => sum + (Number(contract.monthlyEquivalent) || 0), 0);
  const annual = active.reduce((sum, contract) => sum + (Number(contract.annualizedAmount) || 0), 0);
  const reserve = active
    .filter(contract => (contract.billingCycle || 'monthly') !== 'monthly')
    .reduce((sum, contract) => sum + (Number(contract.monthlyEquivalent) || 0), 0);

  const categories = new Map();
  for (const contract of active) {
    const label = categoryLabel(contract) || ctx.get('contracts.kind_' + (contract.kind || 'contract'));
    categories.set(label, (categories.get(label) || 0) + (Number(contract.monthlyEquivalent) || 0));
  }
  const categoryRows = [...categories.entries()]
    .map(([label, value]) => ({ label, value }))
    .sort((a, b) => b.value - a.value);

  const dlg = ctx.dialog(`<div class="dialog-card contract-analysis-dialog">
    <div class="panel-head"><h2>${ctx.esc(t('Vertragsanalyse', 'Contract analysis'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <div class="contract-analysis-loading">${ctx.esc(t('Buchungen werden ausgewertet …', 'Analyzing payments …'))}</div>
  </div>`);
  dlg.classList.add('contracts-analysis-dlg');
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.showModal();

  const now = new Date();
  const firstMonth = new Date(now.getFullYear(), now.getMonth() - 11, 1);
  const lastMonth = new Date(now.getFullYear(), now.getMonth() + 1, 0);
  const months = [];
  const monthMap = new Map();
  for (let offset = 11; offset >= 0; offset--) {
    const date = new Date(now.getFullYear(), now.getMonth() - offset, 1);
    const key = contractMonthKey(date);
    const month = { key, value: 0 };
    months.push(month);
    monthMap.set(key, month);
  }

  const overviewPromise = ctx.api(`api/analytics/overview?from=${contractDateParam(firstMonth)}&to=${contractDateParam(lastMonth)}&granularity=month`).catch(() => null);
  const activities = [];
  const analysisContracts = active.slice(0, 60);
  for (let index = 0; index < analysisContracts.length; index += 6) {
    const batch = await Promise.allSettled(
      analysisContracts.slice(index, index + 6).map(contract => ctx.api(`api/contracts/${contract.id}/activity`))
    );
    activities.push(...batch);
  }
  const overview = await overviewPromise;

  for (const result of activities) {
    if (result.status !== 'fulfilled') continue;
    for (const payment of result.value?.payments || []) {
      const target = monthMap.get(contractMonthKey(payment.date));
      if (target) target.value += Number(payment.amount) || 0;
    }
  }

  const periods = overview?.byPeriod || overview?.byMonth || [];
  const incomeMonths = periods.map(period => Number(period.income) || 0).filter(value => value > 0);
  const averageIncome = incomeMonths.length ? incomeMonths.reduce((sum, value) => sum + value, 0) / incomeMonths.length : null;
  const available = averageIncome == null ? null : averageIncome - monthly;
  const share = averageIncome && averageIncome > 0 ? Math.round(monthly / averageIncome * 100) : null;

  const maxCategory = Math.max(1, ...categoryRows.map(row => row.value));
  const categoriesHtml = categoryRows.map((row, index) => `<div class="contract-analysis-category">
    <span class="contract-analysis-category-dot contract-analysis-bg-${index % 8}"></span>
    <strong>${ctx.esc(row.label)}</strong>
    <span>${ctx.money(row.value, currency)}</span>
    <progress max="${maxCategory}" value="${row.value}"></progress>
  </div>`).join('');

  const body = dlg.querySelector('.contract-analysis-dialog');
  if (!body) return;
  body.innerHTML = `
    <div class="panel-head"><h2>${ctx.esc(t('Vertragsanalyse', 'Contract analysis'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>

    <section class="contract-analysis-card contract-analysis-balance">
      <h3>${ctx.esc(t('Durchschnittlich pro Monat', 'Average per month'))}</h3>
      ${averageIncome == null ? '' : `<div><span>${ctx.esc(t('Einnahmen', 'Income'))}</span><strong class="positive">${ctx.money(averageIncome, currency)}</strong></div>`}
      <div><span>${ctx.esc(t('Verträge', 'Contracts'))}${share == null ? '' : ` <small>${share} %</small>`}</span><strong>−${ctx.money(monthly, currency)}</strong></div>
      ${available == null ? '' : `<div class="contract-analysis-available"><span>${ctx.esc(t('Frei verfügbar', 'Available'))}</span><strong class="${available >= 0 ? 'positive' : 'negative'}">${ctx.money(available, currency)}</strong></div>`}
      <small>${ctx.money(annual, currency)} ${ctx.esc(t('Vertragskosten pro Jahr', 'contract cost per year'))}</small>
    </section>

    <section class="contract-analysis-card">
      <h3>${ctx.esc(t('Verträge pro Kategorie', 'Contracts by category'))}</h3>
      <div class="contract-analysis-donut-wrap">
        ${contractAnalysisDonut(categoryRows, monthly)}
        <div class="contract-analysis-donut-center"><strong>${ctx.money(monthly, currency)}</strong><span>${ctx.esc(t('monatlich', 'monthly'))}</span></div>
      </div>
      <div class="contract-analysis-categories">${categoriesHtml || `<div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div>`}</div>
    </section>

    <section class="contract-analysis-card">
      <h3>${ctx.esc(t('Vertragskosten im Verlauf', 'Contract cost history'))}</h3>
      <p>${ctx.esc(t('Erkannte Vertragszahlungen der letzten 12 Monate.', 'Detected contract payments over the last 12 months.'))}</p>
      ${contractAnalysisBars(months)}
      ${reserve > 0 ? `<div class="contract-analysis-tip"><strong>${ctx.esc(t('Tipp', 'Tip'))}</strong><span>${ctx.esc(t('Für nicht-monatliche Verträge monatlich zurücklegen:', 'Set aside monthly for non-monthly contracts:'))} ${ctx.money(reserve, currency)}</span></div>` : ''}
    </section>`;
  body.querySelector('[data-close]').onclick = () => dlg.close();
}

function contractToWrite(c) {
  return {
    name: c.name, providerName: c.providerName || null, kind: c.kind || 'contract',
    categoryId: c.categoryId || null, accountId: c.accountId || null,
    amount: c.amount, currency: c.currency, billingCycle: c.billingCycle || 'monthly',
    interval: c.interval || 1, startDate: c.startDate || null, endDate: c.endDate || null,
    nextDueDate: c.nextDueDate || null, isActive: c.isActive !== false, notes: c.notes || null
  };
}

function jsonBody(body, method) {
  return { method: method || 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) };
}


async function openContractDialog(existing) {
  const contract = existing || {};
  const currency = contract.currency || 'EUR';
  let categories, accounts;
  try {
    categories = await ctx.categoryOptions(contract.categoryId);
    accounts = (await ctx.api('api/accounts')) || [];
  } catch (err) {
    ctx.toast(err.message || ctx.get('common.error'));
    return;
  }

  const option = (list, selected, prefix) => list.map(value =>
    `<option value="${value}"${selected === value ? ' selected' : ''}>${ctx.esc(ctx.get(prefix + value))}</option>`
  ).join('');
  const accountOptions = accounts.map(account =>
    `<option value="${account.id}"${contract.accountId === account.id ? ' selected' : ''}>${ctx.esc(account.displayName || account.institutionName)}</option>`
  ).join('');
  const dateValue = value => value ? String(value).slice(0, 10) : '';

  const dlg = ctx.dialog(`<form class="dialog-card contract-dialog contract-edit-v2">
    <div class="panel-head"><h2>${ctx.esc(ctx.get(existing ? 'contracts.edit' : 'contracts.new'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>

    <fieldset>
      <legend>${ctx.esc(t('Basisdaten', 'Basics'))}</legend>
      <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="160" value="${ctx.esc(contract.name || '')}"></label>
      <label>${ctx.esc(ctx.get('contracts.provider'))}<input name="provider" maxlength="160" value="${ctx.esc(contract.providerName || '')}"></label>
      <label>${ctx.esc(ctx.get('contracts.kind'))}<select name="kind">${option(KINDS, contract.kind || 'subscription', 'contracts.kind_')}</select></label>
    </fieldset>

    <fieldset>
      <legend>${ctx.esc(t('Zahlung', 'Payment'))}</legend>
      <div class="rule-grid">
        <label>${ctx.esc(ctx.get('transactions.amount'))}<input name="amount" type="number" step="0.01" inputmode="decimal" required value="${contract.amount ?? ''}"></label>
        <label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(currency)}" maxlength="3" required></label>
      </div>
      <label>${ctx.esc(ctx.get('contracts.billingCycle'))}<select name="cycle">${option(CYCLES, contract.billingCycle || 'monthly', 'contracts.cycle_')}</select></label>
      <label>${ctx.esc(t('Kategorie', 'Category'))}<select name="category"><option value="">—</option>${categories}</select></label>
      <label>${ctx.esc(t('Zahlungskonto', 'Payment account'))}<select name="account"><option value="">—</option>${accountOptions}</select></label>
    </fieldset>

    <details class="contract-edit-more">
      <summary>${ctx.esc(t('Weitere Vertragsdaten', 'More contract details'))}</summary>
      <label>${ctx.esc(ctx.get('contracts.nextDue'))}<input name="nextDue" type="date" value="${dateValue(contract.nextDueDate)}"></label>
      <div class="rule-grid">
        <label>${ctx.esc(ctx.get('contracts.startDate'))}<input name="start" type="date" value="${dateValue(contract.startDate)}"></label>
        <label>${ctx.esc(ctx.get('contracts.endDate'))}<input name="end" type="date" value="${dateValue(contract.endDate)}"></label>
      </div>
      <label>${ctx.esc(ctx.get('contracts.notes'))}<textarea name="notes" maxlength="1000" rows="3">${ctx.esc(contract.notes || '')}</textarea></label>
    </details>

    <div class="dialog-actions"><button type="button" class="btn btn-secondary" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit" class="btn btn-primary">${ctx.esc(ctx.get(existing ? 'common.apply' : 'common.create'))}</button></div>
  </form>`);

  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const fd = new FormData(event.currentTarget);
    const body = {
      name: fd.get('name'),
      providerName: fd.get('provider') || null,
      kind: fd.get('kind'),
      categoryId: fd.get('category') || null,
      accountId: fd.get('account') || null,
      amount: Number(fd.get('amount')),
      currency: (fd.get('currency') || 'EUR').toUpperCase(),
      billingCycle: fd.get('cycle'),
      interval: existing?.interval || 1,
      startDate: fd.get('start') || null,
      endDate: fd.get('end') || null,
      nextDueDate: fd.get('nextDue') || null,
      isActive: existing ? existing.isActive !== false : true,
      notes: (fd.get('notes') || '').trim() || null
    };

    const submit = event.currentTarget.querySelector('[type="submit"]');
    submit.disabled = true;
    try {
      const endpoint = existing ? `api/contracts/${existing.id}` : 'api/contracts';
      const saved = await ctx.api(endpoint, jsonBody(body, existing ? 'PUT' : 'POST'));
      dlg.close();
      ctx.toast(ctx.get('common.saved'));
      await renderContracts(ctx);
      if (saved?.id) await openDetail(saved.id);
    } catch (err) {
      submit.disabled = false;
      ctx.toast(err.message || ctx.get('common.error'));
    }
  };
  dlg.showModal();
}
