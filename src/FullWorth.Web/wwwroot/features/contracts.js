// Contracts & recurring costs (UI_UX_SPEC §13). Contracts come from detection (candidates the owner
// confirms) or manual creation. The list separates active from archived; each row opens a detail drawer
// showing value mode (automatic/manual), linked payments + payment trend, next expected payment,
// annualized cost, start/end and notes, with edit / cancel (archive) / reactivate. A "detected
// subscriptions" section surfaces recurring-payment candidates with one-click accept. All money and
// cadence come from the backend (annualization/next-due are computed server-side, §30).

import { identityIcon, sectionCard, esc } from '../ui/ux-kit.js';

let ctx = null;
const CYCLES = ['monthly', 'quarterly', 'yearly', 'weekly'];
const KINDS = ['subscription', 'contract', 'insurance', 'loan', 'other'];
// Populated on every render so the price-change panel can show a contract's name/currency from its id.
let contractsById = new Map();
// The full contract set plus best-effort id→name maps, kept at module scope so the filter/sort chips
// can re-render the list without refetching (UX rework §7: small datasets filter/sort client-side).
let allContracts = [];
let categoryNames = new Map();
let accountNames = new Map();
// Filter/sort state persisted across renders so it survives an edit / cancel / detect round-trip.
const view = { kind: '', status: 'active', sort: 'due', order: 'asc' };

// A few labels the rework introduces have no existing i18n key, so fall back to inline DE/EN.
function lang() { return !document.documentElement.lang || !document.documentElement.lang.startsWith('en'); }
function t(de, en) { return lang() ? de : en; }

// bindContracts only stashes ctx now — the view (and every control) is rebuilt by renderContracts, and
// the page-header primary action button (app.js #primary-action) is what invokes newContract.
export function bindContracts(context) { ctx = context; }

// Used by the page-header primary action.
export function newContract(context) { if (context) ctx = context; return openContractDialog(); }

export async function renderContracts(context) {
  ctx = context;
  injectCss();
  const host = ctx.$('#view-contracts');
  let rows = [];
  try { rows = (await ctx.api('api/contracts')) || []; }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); rows = []; }
  contractsById = new Map(rows.map(c => [c.id, c]));
  allContracts = rows;
  // The list DTO only carries category/account ids; resolve their names best-effort for the row context
  // line and for account/category sorting. A failed lookup just omits that label — never blocks render.
  [categoryNames, accountNames] = await Promise.all([
    ctx.api('api/categories').then(cs => new Map((cs || []).map(c => [c.id, c.name]))).catch(() => new Map()),
    ctx.api('api/accounts').then(as => new Map((as || []).map(a => [a.id, a.displayName || a.institutionName]))).catch(() => new Map()),
  ]);

  host.innerHTML = viewHtml();
  wireControls(host);
  renderList(host);
  // Contextual alerts: detected subscriptions + price-change suggestions load quietly and surface only
  // when the backend actually has candidates, so they never dominate the header (UX rework §7).
  loadDetected(false);
  loadPriceChanges(false);
}

// Whole-view markup: top summary card (sum of monthlyEquivalent / annualizedAmount over active
// contracts, computed server-side), the detected/price-change alert slots, then the filter + list card.
function viewHtml() {
  const active = allContracts.filter(c => c.isActive);
  const sumMonthly = active.reduce((s, c) => s + (Number(c.monthlyEquivalent) || 0), 0);
  const sumAnnual = active.reduce((s, c) => s + (Number(c.annualizedAmount) || 0), 0);
  const cur = (active.find(c => c.currency) || {}).currency || 'EUR';
  const summaryBody = `<div class="fw-summary">
    <div class="fw-summary-item"><span class="fw-summary-label">${esc(t('Kosten pro Monat', 'Cost per month'))}</span><span class="fw-summary-value">${ctx.money(sumMonthly, cur)}</span></div>
    <div class="fw-summary-item"><span class="fw-summary-label">${esc(t('Pro Jahr', 'Per year'))}</span><span class="fw-summary-value">${ctx.money(sumAnnual, cur)}</span></div>
  </div>`;
  const summary = sectionCard(ctx.get('contracts.title'), summaryBody, {
    sub: t(`${active.length} aktive Verträge`, `${active.length} active contracts`),
    className: 'contracts-summary',
  });

  const kindChip = (val, label) => `<button type="button" class="fw-chip${view.kind === val ? ' active' : ''}" data-kind="${val}">${esc(label)}</button>`;
  const typeChips = `<div class="fw-chips" data-type-chips>${kindChip('', ctx.get('common.all'))}${KINDS.map(k => kindChip(k, ctx.get('contracts.kind_' + k))).join('')}</div>`;
  const statusChip = (val, label) => `<button type="button" class="fw-chip${view.status === val ? ' active' : ''}" data-status="${val}">${esc(label)}</button>`;
  const sortOpt = (val, label) => `<option value="${val}"${view.sort === val ? ' selected' : ''}>${esc(label)}</option>`;
  const controls = `<div class="contracts-controls">
    <div class="fw-chips" data-status-chips>${statusChip('active', t('Aktiv', 'Active'))}${statusChip('archived', ctx.get('contracts.archived'))}${statusChip('all', ctx.get('common.all'))}</div>
    <label class="contracts-sort"><span>${esc(t('Sortieren', 'Sort'))}</span>
      <select data-sort>${sortOpt('due', ctx.get('contracts.nextDue'))}${sortOpt('monthly', t('Monatlich', 'Monthly'))}${sortOpt('annual', ctx.get('contracts.annualized'))}${sortOpt('account', ctx.get('contracts.account'))}${sortOpt('category', t('Kategorie', 'Category'))}${sortOpt('name', ctx.get('common.name'))}</select>
      <button type="button" class="fw-chip contracts-order" data-order aria-label="${esc(t('Reihenfolge umkehren', 'Reverse order'))}">${view.order === 'desc' ? '↓' : '↑'}</button>
    </label>
    <button type="button" class="fw-chip contracts-detect" data-detect>${esc(ctx.get('contracts.detect'))}</button>
  </div>`;

  const listCard = sectionCard('', `${typeChips}${controls}<div class="contracts-list" data-list></div>`, { className: 'contracts-listcard' });

  return `<div class="contracts-ux">
    ${summary}
    <div id="contracts-price-changes" class="detected-panel" hidden></div>
    <div id="contracts-detected" class="detected-panel" hidden></div>
    ${listCard}
  </div>`;
}

function wireControls(host) {
  host.querySelector('[data-type-chips]')?.addEventListener('click', e => {
    const btn = e.target.closest('[data-kind]'); if (!btn) return;
    view.kind = btn.dataset.kind; setActive(host, '[data-type-chips] .fw-chip', btn); renderList(host);
  });
  host.querySelector('[data-status-chips]')?.addEventListener('click', e => {
    const btn = e.target.closest('[data-status]'); if (!btn) return;
    view.status = btn.dataset.status; setActive(host, '[data-status-chips] .fw-chip', btn); renderList(host);
  });
  host.querySelector('[data-sort]')?.addEventListener('change', e => { view.sort = e.target.value; renderList(host); });
  host.querySelector('[data-order]')?.addEventListener('click', e => {
    view.order = view.order === 'desc' ? 'asc' : 'desc';
    e.currentTarget.textContent = view.order === 'desc' ? '↓' : '↑';
    renderList(host);
  });
  // The detect action stays, but as a subtle contextual control rather than the header's focal point.
  host.querySelector('[data-detect]')?.addEventListener('click', () => { loadDetected(true); loadPriceChanges(true); });
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
  for (const c of shown) frag.appendChild(rowFor(c));
  box.appendChild(frag);
}

function accountLabel(c) { return c.accountId ? (accountNames.get(c.accountId) || '') : ''; }
function categoryLabel(c) { return c.categoryId ? (categoryNames.get(c.categoryId) || '') : ''; }

function filterContracts(list) {
  return list.filter(c => {
    if (view.kind && (c.kind || '') !== view.kind) return false;
    if (view.status === 'active' && !c.isActive) return false;
    if (view.status === 'archived' && c.isActive) return false;
    return true;
  });
}

function sortContracts(list) {
  const dir = view.order === 'desc' ? -1 : 1;
  const byName = (a, b) => String(a.name || '').localeCompare(String(b.name || ''));
  const dueKey = c => (c.nextDueDate ? String(c.nextDueDate) : '9999-12-31'); // nulls sort last
  const cmp = {
    due: (a, b) => dueKey(a).localeCompare(dueKey(b)),
    monthly: (a, b) => (Number(a.monthlyEquivalent) || 0) - (Number(b.monthlyEquivalent) || 0),
    annual: (a, b) => (Number(a.annualizedAmount) || 0) - (Number(b.annualizedAmount) || 0),
    account: (a, b) => accountLabel(a).localeCompare(accountLabel(b)),
    category: (a, b) => categoryLabel(a).localeCompare(categoryLabel(b)),
    name: byName,
  }[view.sort] || (() => 0);
  return list.slice().sort((a, b) => dir * cmp(a, b) || byName(a, b));
}

// Injected once (no app.css edits). Everything else reuses the shared `.fw-*` and app.css classes.
function injectCss() {
  if (document.getElementById('contracts-ux-css')) return;
  const style = document.createElement('style');
  style.id = 'contracts-ux-css';
  style.textContent = `
    .contracts-ux .detected-panel:not([hidden]){margin-bottom:var(--s4)}
    .contracts-controls{display:flex;flex-wrap:wrap;align-items:center;gap:var(--s3);margin:var(--s3) 0}
    .contracts-sort{display:inline-flex;align-items:center;gap:var(--s2);color:var(--muted);font-size:12px;margin-left:auto}
    .contracts-sort select{border:1px solid var(--line);background:var(--surface);color:var(--text);border-radius:var(--radius-pill);padding:5px 10px;font-size:12px;font-weight:600}
    .contracts-order{min-width:32px;text-align:center;line-height:1}
    .contracts-detect{border-style:dashed}
    .contract-row.contract-archived{opacity:.6}
    .contracts-empty{color:var(--muted);padding:var(--s4) 4px;text-align:center;font-size:13px}
  `;
  document.head.appendChild(style);
}

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
        <div class="row-side"><button type="button" class="ghost danger" data-ignore>${ctx.esc(ctx.get('priceChanges.ignore'))}</button><button type="button" class="ghost" data-confirm>${ctx.esc(ctx.get('priceChanges.confirm'))}</button></div>
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
  row.className = 'fw-row contract-row' + (c.isActive ? '' : ' contract-archived');
  row.tabIndex = 0;
  row.setAttribute('role', 'button');
  const cycle = ctx.get('contracts.cycle_' + (c.billingCycle || 'monthly'));
  const kind = ctx.get('contracts.kind_' + (c.kind || 'contract'));
  const cat = categoryLabel(c);
  const due = (c.isActive && c.nextDueDate) ? `${ctx.get('contracts.nextDue')}: ${ctx.date(c.nextDueDate)}` : '';
  const marker = c.isActive ? '' : ` <span class="tx-marker">${ctx.esc(ctx.get('contracts.archived'))}</span>`;
  const sub = [kind, cat, due].filter(Boolean).map(p => ctx.esc(p)).join(' · ');
  row.innerHTML = `${identityIcon(c.name, { logoAssetPath: c.logoAssetPath })}
    <div class="fw-row-main">
      <div class="fw-row-title">${ctx.esc(c.name)}${marker}</div>
      <div class="fw-row-sub">${sub}</div>
    </div>
    <div class="fw-row-amt">${ctx.money(c.amount, c.currency)}<small>${ctx.esc(cycle)}</small></div>`;
  const open = () => openDetail(c.id);
  row.addEventListener('click', open);
  row.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } });
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
  const items = candidates.map((cand, i) => `
    <div class="detected-row" data-i="${i}">
      <div class="row-main">
        <div class="row-title">${ctx.esc(cand.counterparty)}</div>
        <div class="row-sub">${ctx.esc(ctx.get('contracts.cycle_' + (cand.billingCycle || 'monthly')))} · ${ctx.esc(ctx.get('contracts.confidence'))} ${Math.round((cand.confidence || 0) * 100)}%</div>
      </div>
      <div class="row-side"><span class="amount">${ctx.money(cand.typicalAmount, cand.currency)}</span><button type="button" class="ghost danger" data-dismiss>${ctx.esc(ctx.get('contracts.dismiss'))}</button><button type="button" class="ghost" data-accept>${ctx.esc(ctx.get('contracts.accept'))}</button></div>
    </div>`).join('');
  box.innerHTML = `<div class="panel-head"><h3>${ctx.esc(ctx.get('contracts.detectedTitle'))}</h3></div>${items}`;
  box.querySelectorAll('.detected-row').forEach(el => {
    el.querySelector('[data-accept]').addEventListener('click', () => acceptCandidate(candidates[Number(el.dataset.i)]));
    el.querySelector('[data-dismiss]').addEventListener('click', () => dismissCandidate(candidates[Number(el.dataset.i)]));
  });
}

async function acceptCandidate(candidate) {
  try {
    await ctx.api('api/contracts/detection/accept', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(candidate) });
    ctx.toast(ctx.get('common.saved'));
    await renderContracts(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

// Reject a detected candidate so it stops reappearing in future detection runs.
async function dismissCandidate(candidate) {
  try {
    await ctx.api('api/contracts/detection/dismiss', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ counterparty: candidate.counterparty, currency: candidate.currency }) });
    ctx.toast(ctx.get('contracts.dismissed'));
    await renderContracts(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

async function openDetail(id) {
  let contract, activity;
  try {
    contract = await ctx.api(`api/contracts/${id}`);
    activity = await ctx.api(`api/contracts/${id}/activity`);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }

  const valueMode = ctx.get('contracts.mode_' + (activity?.valueMode || 'manual'));
  const next = activity?.nextExpected ? ctx.date(activity.nextExpected) : '—';
  const lastPay = activity?.lastPayment ? ctx.date(activity.lastPayment) : '—';
  const payments = activity?.payments || [];
  const trend = sparkline(payments);
  const paymentRows = payments.length
    ? payments.map(p => `<div class="preview-row"><span class="preview-label">${ctx.esc(ctx.date(p.date))}</span><span class="preview-amt">${ctx.money(p.amount, p.currency)}</span></div>`).join('')
    : `<div class="row-sub">${ctx.esc(ctx.get('contracts.noPayments'))}</div>`;

  const meta = [
    [ctx.get('contracts.mode'), valueMode],
    [ctx.get('contracts.expected'), ctx.money(activity?.expectedAmount ?? contract.amount, contract.currency)],
    [ctx.get('contracts.annualized'), ctx.money(activity?.annualizedAmount ?? 0, contract.currency)],
    [ctx.get('contracts.nextExpected'), next],
    [ctx.get('contracts.lastPayment'), lastPay],
    [ctx.get('contracts.billingCycle'), ctx.get('contracts.cycle_' + (contract.billingCycle || 'monthly'))],
    [ctx.get('contracts.kind'), ctx.get('contracts.kind_' + (contract.kind || 'contract'))],
    [ctx.get('contracts.startDate'), contract.startDate ? ctx.date(contract.startDate) : '—'],
    [ctx.get('contracts.endDate'), contract.endDate ? ctx.date(contract.endDate) : '—']
  ].map(([k, v]) => `<div class="detail-item"><span class="detail-k">${ctx.esc(k)}</span><span class="detail-v">${ctx.esc(v)}</span></div>`).join('');

  const dlg = ctx.dialog(`<div class="dialog-card contract-detail">
    <div class="panel-head"><h2>${ctx.esc(contract.name)}${contract.isActive ? '' : ` <span class="tx-marker">${ctx.esc(ctx.get('contracts.archived'))}</span>`}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    ${contract.providerName ? `<div class="row-sub">${ctx.esc(contract.providerName)}</div>` : ''}
    <div class="detail-grid">${meta}</div>
    ${trend}
    <div class="detail-section"><h3>${ctx.esc(ctx.get('contracts.payments'))}</h3>${paymentRows}</div>
    ${contract.notes ? `<div class="detail-section"><h3>${ctx.esc(ctx.get('contracts.notes'))}</h3><div class="row-sub">${ctx.esc(contract.notes)}</div></div>` : ''}
    <div class="dialog-actions">
      <button type="button" data-edit>${ctx.esc(ctx.get('contracts.edit'))}</button>
      ${contract.isActive
        ? `<button type="button" class="danger" data-cancel-contract>${ctx.esc(ctx.get('contracts.cancel'))}</button>`
        : `<button type="button" data-reactivate>${ctx.esc(ctx.get('contracts.reactivate'))}</button>`}
    </div>
  </div>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-edit]').onclick = () => { dlg.close(); openContractDialog(contract); };
  dlg.querySelector('[data-cancel-contract]')?.addEventListener('click', async () => {
    if (!await ctx.confirm(ctx.get('contracts.cancelConfirm').replace('{name}', contract.name), { destructive: true, confirmLabel: ctx.get('contracts.cancel') })) return;
    try { await ctx.api(`api/contracts/${id}`, { method: 'DELETE' }); dlg.close(); ctx.toast(ctx.get('contracts.cancelled')); await renderContracts(ctx); }
    catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });
  dlg.querySelector('[data-reactivate]')?.addEventListener('click', async () => {
    try { await ctx.api(`api/contracts/${id}`, jsonBody({ ...contractToWrite(contract), isActive: true }, 'PUT')); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderContracts(ctx); }
    catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });
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
  const c = existing || {};
  const currency = c.currency || 'EUR';
  let categories, accounts;
  try {
    categories = await ctx.categoryOptions(c.categoryId);
    accounts = (await ctx.api('api/accounts')) || [];
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }
  const opt = (list, sel, prefix) => list.map(v => `<option value="${v}"${sel === v ? ' selected' : ''}>${ctx.esc(ctx.get(prefix + v))}</option>`).join('');
  const accountOpts = accounts.map(a => `<option value="${a.id}"${c.accountId === a.id ? ' selected' : ''}>${ctx.esc(a.displayName || a.institutionName)}</option>`).join('');
  const dv = v => v ? String(v).slice(0, 10) : '';

  const dlg = ctx.dialog(`<form class="dialog-card contract-dialog">
    <div class="panel-head"><h2>${ctx.esc(ctx.get(existing ? 'contracts.edit' : 'contracts.new'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="160" value="${ctx.esc(c.name || '')}"></label>
    <label>${ctx.esc(ctx.get('contracts.provider'))}<input name="provider" maxlength="160" value="${ctx.esc(c.providerName || '')}"></label>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('contracts.kind'))}<select name="kind">${opt(KINDS, c.kind || 'subscription', 'contracts.kind_')}</select></label>
      <label>${ctx.esc(ctx.get('contracts.billingCycle'))}<select name="cycle">${opt(CYCLES, c.billingCycle || 'monthly', 'contracts.cycle_')}</select></label>
    </div>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('transactions.amount'))}<input name="amount" type="number" step="0.01" inputmode="decimal" required value="${c.amount ?? ''}"></label>
      <label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(currency)}" maxlength="3" required></label>
    </div>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('contracts.interval'))}<input name="interval" type="number" min="1" value="${c.interval || 1}"></label>
      <label>${ctx.esc(ctx.get('contracts.nextDue'))}<input name="nextDue" type="date" value="${dv(c.nextDueDate)}"></label>
    </div>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('contracts.startDate'))}<input name="start" type="date" value="${dv(c.startDate)}"></label>
      <label>${ctx.esc(ctx.get('contracts.endDate'))}<input name="end" type="date" value="${dv(c.endDate)}"></label>
    </div>
    <label>${ctx.esc(ctx.get('transactions.category'))}<select name="category"><option value="">${ctx.esc(ctx.get('common.all'))}</option>${categories}</select></label>
    <label>${ctx.esc(ctx.get('contracts.account'))}<select name="account"><option value="">—</option>${accountOpts}</select></label>
    <label>${ctx.esc(ctx.get('contracts.notes'))}<textarea name="notes" maxlength="1000" rows="2">${ctx.esc(c.notes || '')}</textarea></label>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get(existing ? 'common.apply' : 'common.create'))}</button></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    const body = {
      name: fd.get('name'), providerName: fd.get('provider') || null, kind: fd.get('kind'),
      categoryId: fd.get('category') || null, accountId: fd.get('account') || null,
      amount: Number(fd.get('amount')), currency: (fd.get('currency') || 'EUR').toUpperCase(),
      billingCycle: fd.get('cycle'), interval: Number(fd.get('interval') || 1),
      startDate: fd.get('start') || null, endDate: fd.get('end') || null, nextDueDate: fd.get('nextDue') || null,
      isActive: existing ? existing.isActive !== false : true, notes: (fd.get('notes') || '').trim() || null
    };
    try {
      const path = existing ? `api/contracts/${existing.id}` : 'api/contracts';
      await ctx.api(path, jsonBody(body, existing ? 'PUT' : 'POST'));
      dlg.close(); ctx.toast(ctx.get('common.saved')); await renderContracts(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}
