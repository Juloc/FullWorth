// Transactions list + detail (UI_UX_SPEC §9). List shows compact markers (pending, transfer,
// excluded, receipt); a detail drawer (right-side on desktop, full-screen on mobile via CSS) edits
// category, exclude-from-statistics and transfer status through the existing classification PATCH,
// and links to a receipt when a purchase is attached.

import { attachCategoryPicker } from '../ui/category-picker.js';
import { identityIcon } from '../ui/ux-kit.js';

let ctx = null;
export function bindTransactions(context) {
  ctx = context;
  ctx.$('#tx-apply').addEventListener('click', applySearch);
  ctx.$('#tx-query').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); applySearch(); } });
  ctx.$('#tx-filter')?.addEventListener('click', openFilterSheet);
  ctx.$('#tx-detect').addEventListener('click', detectTransfers);
  ctx.$('#tx-add').addEventListener('click', openBookingDialog);
}

// The URL is the SINGLE source of truth for the free-text search term, so the search box, the scope
// banner and the actual list filter can never diverge — a merchant drill (?query=…) and a manual search
// both flow through it. Applying a search writes the term into the URL (preserving the account/group/
// category scope) and re-renders.
function applySearch() {
  const params = new URLSearchParams(location.search);
  const v = ctx.$('#tx-query').value.trim();
  if (v) params.set('query', v); else params.delete('query');
  const qs = params.toString();
  history.replaceState({ view: 'transactions' }, '', qs ? `/transactions?${qs}` : '/transactions');
  renderTransactions(ctx);
}

function deLabel(de, en) { return document.documentElement.lang?.startsWith('en') ? en : de; }
function txReplaceUrl(params) {
  const qs = params.toString();
  history.replaceState({ view: 'transactions' }, '', qs ? `/transactions?${qs}` : '/transactions');
}

// Active-filter count badge on the Filter button (§5): direction, flags, date range and category.
function updateFilterBadge(f) {
  const btn = ctx.$('#tx-filter'); if (!btn) return;
  const n = (f.direction ? 1 : 0) + (f.flags ? 1 : 0) + (f.from || f.to ? 1 : 0) + (f.categoryId ? 1 : 0);
  let badge = btn.querySelector('.tx-filter-count');
  if (n) { if (!badge) { badge = document.createElement('span'); badge.className = 'tx-filter-count'; btn.appendChild(badge); } badge.textContent = String(n); }
  else badge?.remove();
}

// Filter sheet (§5): a compact drawer (bottom sheet on mobile via .drawer CSS) for the advanced filters
// that don't fit the toolbar — direction, date range, category and the transfer/excluded flags. Applied
// through the URL (single source of truth) so filters are restorable and agree with the scope banner.
async function openFilterSheet() {
  const params = new URLSearchParams(location.search);
  let catOptions = '';
  try { catOptions = await ctx.categoryOptions(params.get('categoryId') || undefined); } catch { /* category filter optional */ }
  const dir = ctx.$('#tx-direction').value, flags = ctx.$('#tx-flags').value;
  const dlg = ctx.dialog(`<form class="dialog-card drawer tx-filter-sheet" method="dialog">
    <div class="panel-head"><h2>${ctx.esc(deLabel('Filter', 'Filters'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('transactions.type'))}<select name="direction"><option value="">${ctx.esc(ctx.get('common.all'))}</option><option value="income"${dir === 'income' ? ' selected' : ''}>${ctx.esc(ctx.get('transactions.income'))}</option><option value="expense"${dir === 'expense' ? ' selected' : ''}>${ctx.esc(ctx.get('transactions.expenses'))}</option></select></label>
    <div class="tx-filter-range"><label>${ctx.esc(deLabel('Von', 'From'))}<input type="date" name="from" value="${ctx.esc(params.get('from') || '')}"></label><label>${ctx.esc(deLabel('Bis', 'To'))}<input type="date" name="to" value="${ctx.esc(params.get('to') || '')}"></label></div>
    <label>${ctx.esc(ctx.get('transactions.category'))}<select name="category"><option value="">${ctx.esc(ctx.get('common.all'))}</option>${catOptions}</select></label>
    <label class="check"><input type="checkbox" name="fpending"${flags === 'pending' ? ' checked' : ''}>${ctx.esc(ctx.get('transactions.pendingOnly'))}</label>
    <label class="check"><input type="checkbox" name="ftransfers"${flags === 'transfers' ? ' checked' : ''}>${ctx.esc(ctx.get('transactions.transfersOnly'))}</label>
    <label class="check"><input type="checkbox" name="fignored"${flags === 'ignored' ? ' checked' : ''}>${ctx.esc(ctx.get('transactions.excludedOnly'))}</label>
    <div class="dialog-actions"><button type="button" data-reset class="ghost">${ctx.esc(deLabel('Zurücksetzen', 'Reset'))}</button><button type="button" data-apply>${ctx.esc(ctx.get('common.apply'))}</button></div>
  </form>`);
  dlg.classList.add('drawer');
  const cat = () => dlg.querySelector('[name="category"]');
  if (cat() && params.get('categoryId')) cat().value = params.get('categoryId');
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-reset]').onclick = () => {
    ctx.$('#tx-direction').value = ''; ctx.$('#tx-flags').value = '';
    const p = new URLSearchParams(location.search); ['from', 'to', 'categoryId', 'includeDescendants'].forEach(k => p.delete(k));
    txReplaceUrl(p); dlg.close(); renderTransactions(ctx);
  };
  dlg.querySelector('[data-apply]').onclick = () => {
    const fd = new FormData(dlg.querySelector('form'));
    ctx.$('#tx-direction').value = fd.get('direction') || '';
    ctx.$('#tx-flags').value = fd.get('ftransfers') ? 'transfers' : fd.get('fignored') ? 'ignored' : fd.get('fpending') ? 'pending' : '';
    const p = new URLSearchParams(location.search);
    const setOrDel = (k, v) => { if (v) p.set(k, v); else p.delete(k); };
    setOrDel('from', fd.get('from')); setOrDel('to', fd.get('to'));
    const c = fd.get('category');
    if (c) { p.set('categoryId', c); p.set('includeDescendants', 'true'); } else { p.delete('categoryId'); p.delete('includeDescendants'); }
    txReplaceUrl(p); dlg.close(); renderTransactions(ctx);
  };
  dlg.showModal();
}

// Manual booking (UI_UX_SPEC §9.4): hand-enter an income/expense on a MANUAL account. Only manual
// accounts are offered; the server rejects booking on a synced account. Currency follows the account.
async function openBookingDialog() {
  let accounts, options;
  try {
    accounts = (await ctx.api('api/accounts')).filter(a => a.provider === 'manual' && !a.bankConnectionId);
    options = await ctx.categoryOptions();
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }
  if (!accounts.length) { ctx.toast(ctx.get('transactions.needManualAccount')); return; }

  const today = new Date().toISOString().slice(0, 10);
  const accountOptions = accounts.map(a => `<option value="${a.id}" data-currency="${ctx.esc(a.currency)}">${ctx.esc(a.displayName || a.institutionName)}</option>`).join('');
  const dlg = ctx.dialog(`<form class="dialog-card" method="dialog">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('transactions.addTitle'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.cancel'))}">×</button></div>
    <label>${ctx.esc(ctx.get('transactions.account'))}<select name="account" required>${accountOptions}</select></label>
    <label>${ctx.esc(ctx.get('transactions.type'))}<select name="direction"><option value="expense">${ctx.esc(ctx.get('transactions.expenses'))}</option><option value="income">${ctx.esc(ctx.get('transactions.income'))}</option></select></label>
    <label>${ctx.esc(ctx.get('transactions.amount'))}<input name="amount" type="number" step="0.01" min="0" inputmode="decimal" required placeholder="0,00"></label>
    <label>${ctx.esc(ctx.get('transactions.date'))}<input name="date" type="date" value="${today}" required></label>
    <label>${ctx.esc(ctx.get('transactions.counterparty'))}<input name="counterparty" required maxlength="200" placeholder="${ctx.esc(ctx.get('transactions.counterpartyPlaceholder'))}"></label>
    <label>${ctx.esc(ctx.get('transactions.category'))}<span class="field-inline"><select name="category"><option value="">${ctx.esc(ctx.get('common.uncategorized'))}</option>${options}</select></span></label>
    <label>${ctx.esc(ctx.get('transactions.note'))}<input name="note" maxlength="500"></label>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get('common.create'))}</button></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    const amount = Number(fd.get('amount'));
    if (!(amount > 0)) { ctx.toast(ctx.get('transactions.amountRequired')); return; }
    const payload = {
      accountId: fd.get('account'),
      amount,
      direction: fd.get('direction'),
      date: fd.get('date') || null,
      counterparty: fd.get('counterparty'),
      categoryId: fd.get('category') || null,
      note: fd.get('note') || null,
    };
    try {
      await ctx.api('api/transactions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
      dlg.close();
      ctx.toast(ctx.get('transactions.created'));
      await renderTransactions(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  attachCategoryPicker(ctx, dlg.querySelector('select[name="category"]'));
  dlg.showModal();
}

export async function renderTransactions(context) {
  ctx = context;
  const body = ctx.$('#transactions-body');
  // URL scope (UX rework §3): ?accountId= one account, ?groupId= every account in that group. The
  // group is resolved to accessible accounts server-side (backend param accountGroupId) — the browser
  // never sends a raw account-id list, so the scope stays authorization-safe.
  const params = new URLSearchParams(location.search);
  const accountId = params.get('accountId') || '';
  const groupId = params.get('groupId') || '';
  const categoryId = params.get('categoryId') || '';
  const includeDescendants = params.get('includeDescendants') === 'true';
  const fromDate = params.get('from') || '';
  const toDate = params.get('to') || '';
  // The search term lives in the URL (?query=), set by a merchant drill or by applySearch(); reflect it
  // into the box and use it for the request so the box, the scope banner and the list always agree.
  const urlQuery = params.get('query') || '';
  ctx.$('#tx-query').value = urlQuery;
  const q = new URLSearchParams({ limit: '500' });
  const text = urlQuery;
  const dir = ctx.$('#tx-direction').value;
  const flags = ctx.$('#tx-flags').value;
  if (text) q.set('query', text);
  if (dir) q.set('direction', dir);
  if (flags === 'transfers') q.set('transfersOnly', 'true');
  if (flags === 'ignored') q.set('includeIgnored', 'true');
  if (accountId) q.set('accountId', accountId);
  if (groupId) q.set('accountGroupId', groupId);
  if (categoryId) { q.set('categoryId', categoryId); if (includeDescendants) q.set('includeDescendants', 'true'); }
  if (fromDate) q.set('from', fromDate);
  if (toDate) q.set('to', toDate);
  updateFilterBadge({ direction: dir, flags, from: fromDate, to: toDate, categoryId });
  await renderScope({ accountId, groupId, categoryId, query: urlQuery });
  const data = await ctx.api(`api/transactions?${q}`);
  let items = data.items || [];
  // 'pending'/'ignored' refine client-side over the fetched page (list endpoint has no pending filter).
  if (flags === 'pending') items = items.filter(x => x.status === 'PDNG');
  if (flags === 'ignored') items = items.filter(x => x.isIgnored);

  body.innerHTML = '';
  let lastDate = null;
  for (const x of items) {
    // Date-grouped rows with a lightweight sticky header (UX rework §4); on mobile the table collapses
    // to identity cards via CSS. Items arrive newest-first, so a header opens each new booking day.
    const day = String(x.bookingDate || '').slice(0, 10);
    if (day !== lastDate) {
      lastDate = day;
      const head = document.createElement('tr');
      head.className = 'tx-date-head';
      head.innerHTML = `<td colspan="5"><span>${ctx.esc(dateHeading(day))}</span></td>`;
      body.appendChild(head);
    }
    const name = x.merchantDisplayName || x.counterparty || '—';
    const cat = x.categoryName || x.category || ctx.get('common.uncategorized');
    const tr = document.createElement('tr');
    tr.className = 'tx-row' + (x.isIgnored ? ' tx-ignored' : '');
    tr.tabIndex = 0;
    tr.dataset.txId = x.id;
    tr.innerHTML =
      `<td class="tx-date-cell">${ctx.date(x.bookingDate)}</td>` +
      `<td class="tx-cp"><span class="tx-ident-slot">${identityIcon(name, { logoAssetPath: x.logoAssetPath, categoryIconKey: x.categoryIconKey, isTransfer: x.isTransfer })}</span><span class="tx-cp-main"><strong>${ctx.esc(name)}</strong>${markers(x)}<span class="row-sub">${ctx.esc(x.description || cat)}</span></span></td>` +
      `<td class="tx-cat">${ctx.esc(cat)}</td>` +
      `<td class="tx-acct">${ctx.esc(x.account || '')}</td>` +
      `<td class="number amount ${x.amount < 0 ? 'negative' : 'positive'}">${ctx.money(x.amount, x.currency)}</td>`;
    tr.addEventListener('click', () => openDetail(x));
    tr.addEventListener('keydown', e => { if (e.key === 'Enter') openDetail(x); });
    body.appendChild(tr);
  }
  if (!items.length) body.innerHTML = `<tr><td colspan="5" class="tx-empty">${ctx.esc(ctx.get('common.empty'))}</td></tr>`;
}

// Booking-date header: Heute / Gestern / localized date (UX rework §4).
function dateHeading(day) {
  if (!day) return '—';
  const iso = d => { const t = new Date(); t.setHours(12, 0, 0, 0); t.setDate(t.getDate() + d); return t.toISOString().slice(0, 10); };
  const today = ctx.get('common.today'), yesterday = ctx.get('common.yesterday');
  if (day === iso(0) && today !== 'common.today') return today;
  if (day === iso(-1) && yesterday !== 'common.yesterday') return yesterday;
  return ctx.date(day);
}

// Scope banner (UX rework §3/§6): shows the active account / group / category / merchant-search scope
// with its name and a clear back path (accounts for an account/group drill, all-bookings otherwise).
async function renderScope(scope) {
  const { accountId, groupId, categoryId, query } = scope;
  const view = ctx.$('#view-transactions');
  let bar = view.querySelector('#tx-scopebar');
  if (!accountId && !groupId && !categoryId && !query) { bar?.remove(); return; }
  let label = '';
  try {
    if (accountId) { const a = (await ctx.api('api/accounts')).find(a => String(a.id) === String(accountId)); label = a?.displayName || a?.institutionName || ''; }
    else if (groupId) { const g = (await ctx.api('api/account-groups').catch(() => [])).find(g => String(g.id) === String(groupId)); label = g?.name || ''; }
    else if (categoryId) { const c = (await ctx.api('api/categories').catch(() => [])).find(c => String(c.id) === String(categoryId)); label = c?.name || ''; }
    else if (query) { label = query; }
  } catch { /* label is best-effort; the list itself is already scoped server-side */ }
  if (!bar) { bar = document.createElement('div'); bar.id = 'tx-scopebar'; bar.className = 'tx-scopebar'; view.prepend(bar); }
  const backTo = (accountId || groupId) ? 'accounts' : 'transactions';
  bar.innerHTML = `<button type="button" class="tx-scope-back" data-back aria-label="${ctx.esc(ctx.get('common.back'))}">←</button><span class="tx-scope-label">${ctx.esc(label || ctx.get('nav.transactions'))}</span>`;
  bar.querySelector('[data-back]').onclick = () => { if (window.fwNavScope) window.fwNavScope(backTo, ''); };
  const title = ctx.$('#page-title'); if (title && label) title.textContent = label;
}

// Markers are grey word-label pills hanging on the name (Design System §10): monochrome, never a
// colour emoji or bare symbol, and never more than two per row (the rest live in the detail view).
function marker(cls, label) {
  return `<span class="tx-marker ${cls}" title="${ctx.esc(label)}">${ctx.esc(label)}</span>`;
}
function markers(x) {
  const m = [];
  if (x.status === 'PDNG') m.push(marker('pending', ctx.get('transactions.pending')));
  if (x.isTransfer) m.push(marker('transfer', ctx.get('transactions.transfer')));
  if (x.isIgnored) m.push(marker('ignored', ctx.get('transactions.excluded')));
  if (x.purchaseCount > 0) m.push(marker('receipt', ctx.get('transactions.receiptLinked')));
  return m.length ? ` ${m.slice(0, 2).join('')}` : '';
}

// Transfer detection review (UI_UX_SPEC §9.7 Flow D): suggestions are never applied automatically —
// the user confirms (or leaves unchecked to reject) each pair before it becomes a real link.
async function detectTransfers() {
  let pairs;
  try { pairs = await ctx.api('api/transfers/candidates'); }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }
  if (!pairs || !pairs.length) { ctx.toast(ctx.get('transactions.detectNone')); return; }

  const rows = pairs.map((p, i) => `<label class="check candidate-row"><input type="checkbox" checked data-pair="${i}"><span class="row-main"><span class="row-title">${ctx.esc(p.first.account)} ⇄ ${ctx.esc(p.second.account)}</span><span class="row-sub">${ctx.esc(ctx.date(p.first.bookingDate))} · ${ctx.money(Math.abs(p.first.amount), p.first.currency)}${p.first.counterparty ? ' · ' + ctx.esc(p.first.counterparty) : ''}</span></span></label>`).join('');
  const dlg = ctx.dialog(`<div class="dialog-card drawer">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('transactions.detectTitle'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <p class="row-sub">${ctx.esc(ctx.get('transactions.detectHint'))}</p>
    <div class="refund-candidates">${rows}</div>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="button" data-confirm>${ctx.esc(ctx.get('transactions.detectConfirm'))}</button></div>
  </div>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('[data-confirm]').addEventListener('click', async () => {
    const checked = [...dlg.querySelectorAll('[data-pair]:checked')].map(el => pairs[Number(el.dataset.pair)]);
    dlg.close();
    let linked = 0;
    for (const pair of checked) {
      try {
        await ctx.api(`api/transactions/${pair.first.id}/transfer-link`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ otherTransactionId: pair.second.id }) });
        linked++;
      } catch { /* one rejected pair (e.g. already linked meanwhile) must not block the rest */ }
    }
    ctx.toast(ctx.get('transactions.detectResult').replace('{n}', linked));
    await renderTransactions(ctx);
  });
  dlg.showModal();
}

// Manual transfer link (UI_UX_SPEC §9.7 Flow D): pick a specific counterpart transaction outside the
// auto-detector's date window or when the automatic pairing missed it.
async function openTransferPicker(t) {
  let candidates;
  try {
    const direction = t.amount < 0 ? 'income' : 'expense';
    const res = await ctx.api(`api/transactions?direction=${direction}&limit=200`);
    candidates = (res.items || []).filter(x => x.id !== t.id && !x.isTransfer && x.currency === t.currency && x.amount === -t.amount);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }

  const rows = candidates.length
    ? candidates.map(x => `<button type="button" class="row candidate-row" data-id="${x.id}"><div class="row-main"><div class="row-title">${ctx.esc(x.account || '')}</div><div class="row-sub">${ctx.esc(ctx.date(x.bookingDate))} · ${ctx.esc(x.counterparty || '')}</div></div><div class="amount">${ctx.money(x.amount, x.currency)}</div></button>`).join('')
    : `<div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div>`;
  const dlg = ctx.dialog(`<div class="dialog-card drawer">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('transactions.transferLink'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <p class="row-sub">${ctx.esc(ctx.get('transactions.transferPick'))}</p>
    <div class="refund-candidates">${rows}</div>
  </div>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelectorAll('.candidate-row').forEach(row => row.addEventListener('click', async () => {
    try {
      await ctx.api(`api/transactions/${t.id}/transfer-link`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ otherTransactionId: row.dataset.id }) });
      dlg.close();
      ctx.toast(ctx.get('common.saved'));
      await renderTransactions(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  }));
  dlg.showModal();
}

async function openDetail(listItem) {
  let detail, options;
  try {
    detail = await ctx.api(`api/transactions/${listItem.id}`);
    options = await ctx.categoryOptions(detail.transaction?.categoryId);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }
  const t = detail.transaction || listItem;
  const counterpart = detail.transferCounterpart || null;
  const purchases = detail.purchases || [];
  const receiptPurchase = purchases.find(p => p.receiptImagePath || p.hasReceipt);

  // Identity header (Finanzguru-style): brand logo / category icon / monogram + merchant name.
  const name = listItem.merchantDisplayName || listItem.counterparty || t.counterparty || ctx.get('transactions.title');
  const identity = identityIcon(name, { logoAssetPath: listItem.logoAssetPath, categoryIconKey: listItem.categoryIconKey, isTransfer: t.isTransfer });
  // Transfer direction: money leaving THIS account → Von = this account, An = counterpart; else reversed.
  const outgoing = Number(t.amount) < 0;
  const vonAcct = outgoing ? (t.account || '') : (counterpart?.account || '');
  const anAcct = outgoing ? (counterpart?.account || '') : (t.account || '');
  const purposeOpts = ['', 'savings', 'vacation', 'reserve', 'other'].map(p => `<option value="${p}"${(t.transferPurpose || '') === p ? ' selected' : ''}>${p === '' ? ctx.esc(ctx.get('transactions.purposeNone')) : ctx.esc(ctx.get('transactions.purpose_' + p))}</option>`).join('');
  // Inside the transfer block: a tappable Von→An counter-booking when linked, else a "choose" button.
  const transferInner = counterpart
    ? `${counterpart.id ? `<button type="button" class="tx-vonan" data-open-counterpart><span class="tx-vonan-leg"><span class="tx-vonan-label">${ctx.esc(deLabel('Von', 'From'))}</span><span class="tx-vonan-acct">${ctx.esc(vonAcct)}</span></span><span class="tx-vonan-arrow" aria-hidden="true">→</span><span class="tx-vonan-leg"><span class="tx-vonan-label">${ctx.esc(deLabel('An', 'To'))}</span><span class="tx-vonan-acct">${ctx.esc(anAcct)}</span></span><span class="tx-vonan-go" aria-hidden="true">›</span></button>` : ''}<button type="button" class="ghost danger tx-transfer-unpair" data-transfer-unpair>${ctx.esc(ctx.get('transactions.unpair'))}</button>`
    : `<button type="button" class="tx-choose-counter" data-transfer-link>${ctx.esc(deLabel('Gegenbuchung wählen', 'Choose counter-booking'))}</button>`;
  const dlg = ctx.dialog(`<form class="dialog-card tx-detail" method="dialog">
    <div class="panel-head tx-detail-head"><div class="tx-detail-id"><span class="tx-ident-slot">${identity}</span><span class="tx-detail-idmain"><h2>${ctx.esc(name)}</h2><span class="tx-detail-sub">${ctx.date(t.bookingDate)} · ${ctx.esc(t.account || '')}</span></span></div><button type="button" class="icon-button tx-close" data-close aria-label="${ctx.esc(ctx.get('common.close'))}"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg></button></div>
    <div class="tx-amount amount ${t.amount < 0 ? 'negative' : 'positive'}">${ctx.money(t.amount, t.currency)}</div>
    ${t.description ? `<div class="row-sub tx-detail-desc">${ctx.esc(t.description)}</div>` : ''}
    <label>${ctx.esc(ctx.get('transactions.category'))}<span class="field-inline"><select name="category"><option value="">${ctx.esc(ctx.get('common.uncategorized'))}</option>${options}</select></span></label>
    <label class="fw-toggle-row"><span>${ctx.esc(ctx.get('transactions.excludeFromStats'))}</span><span class="fw-toggle"><input type="checkbox" name="ignored" ${t.isIgnored ? 'checked' : ''}><span class="fw-toggle-track"></span></span></label>
    <label class="fw-toggle-row"><span>${ctx.esc(ctx.get('transactions.markTransfer'))}</span><span class="fw-toggle"><input type="checkbox" name="transfer" ${t.isTransfer ? 'checked' : ''}><span class="fw-toggle-track"></span></span></label>
    <div class="tx-transfer"${t.isTransfer ? '' : ' hidden'}><label class="tx-purpose">${ctx.esc(ctx.get('transactions.transferPurpose'))}<select name="purpose">${purposeOpts}</select></label>${transferInner}</div>
    ${t.amount > 0 ? `<div class="tx-refund"><div class="row-main"><div class="row-title">${ctx.esc(ctx.get('transactions.refund'))}</div><div class="row-sub">${t.refundOfTransactionId ? ctx.esc(ctx.get(t.refundCategoryId ? 'transactions.refundLinkedItem' : 'transactions.refundLinked')) : ctx.esc(ctx.get('transactions.refundHint'))}</div></div><div class="row-side"><button type="button" class="ghost" data-refund-link>${ctx.esc(ctx.get('transactions.refundLink'))}</button>${t.refundOfTransactionId ? `<button type="button" class="ghost" data-refund-clear>${ctx.esc(ctx.get('transactions.refundClear'))}</button>` : ''}</div></div>` : ''}
    ${receiptPurchase ? `<a class="row settings-link" href="/bff/backend/api/purchases/${receiptPurchase.id}/receipt?fullWorthSpaceId=${encodeURIComponent(spaceId())}" target="_blank" rel="noopener"><div class="row-main"><div class="row-title">${ctx.esc(ctx.get('transactions.viewReceipt'))}</div></div><span aria-hidden="true">↗</span></a>` : ''}
    <label class="tx-note">${ctx.esc(ctx.get('transactions.note'))}<input name="note" maxlength="500" value="${ctx.esc(t.userNote || '')}"></label>
    <div class="dialog-actions">${t.isManual ? `<button type="button" class="ghost danger" data-delete>${ctx.esc(ctx.get('transactions.delete'))}</button>` : ''}<button type="button" class="ghost" data-split>${ctx.esc(ctx.get('transactions.split'))}</button><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="button" data-save>${ctx.esc(ctx.get('common.apply'))}</button></div>
  </form>`);
  dlg.classList.add('drawer');
  dlg.querySelector('[data-split]').addEventListener('click', () => openSplitDialog(t));
  dlg.querySelector('[data-delete]')?.addEventListener('click', async () => {
    if (!await ctx.confirm(ctx.get('transactions.deleteConfirm'), { destructive: true, confirmLabel: ctx.get('transactions.delete') })) return;
    try {
      await ctx.api(`api/transactions/${t.id}`, { method: 'DELETE' });
      dlg.close();
      ctx.toast(ctx.get('transactions.deleted'));
      await renderTransactions(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });
  dlg.querySelector('[data-refund-link]')?.addEventListener('click', () => { dlg.close(); openRefundPicker(t); });
  dlg.querySelector('[data-refund-clear]')?.addEventListener('click', async () => {
    try { await setRefund(t.id, null); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderTransactions(ctx); }
    catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });
  dlg.querySelector('[data-transfer-link]')?.addEventListener('click', () => { dlg.close(); openTransferPicker(t); });
  dlg.querySelector('[data-transfer-unpair]')?.addEventListener('click', async () => {
    if (!await ctx.confirm(ctx.get('transactions.unpairConfirm'), { destructive: true, confirmLabel: ctx.get('transactions.unpair') })) return;
    try { await ctx.api(`api/transactions/${t.id}/transfer-link`, { method: 'DELETE' }); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderTransactions(ctx); }
    catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });
  const sel = dlg.querySelector('select[name="category"]');
  if (t.categoryId) sel.value = t.categoryId;
  attachCategoryPicker(ctx, sel);
  const transferBox = dlg.querySelector('[name="transfer"]');
  const transferSection = dlg.querySelector('.tx-transfer');
  // The transfer options (purpose select + "Gegenbuchung wählen" / the Von→An counter-booking) appear
  // only while "Als Umbuchung markieren" is on (§9.7).
  transferBox.addEventListener('change', () => { transferSection.hidden = !transferBox.checked; });
  // Tapping the Von→An block opens the linked counter-booking's own detail.
  dlg.querySelector('[data-open-counterpart]')?.addEventListener('click', () => { dlg.close(); openDetail(counterpart); });
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('[data-save]').onclick = async () => {
    const isTransfer = transferBox.checked;
    const payload = {
      categoryId: sel.value || null,
      isIgnored: dlg.querySelector('[name="ignored"]').checked,
      isTransfer,
      transferPurpose: isTransfer ? (dlg.querySelector('[name="purpose"]').value || null) : null,
      userNote: dlg.querySelector('[name="note"]').value.trim() || null,
    };
    try {
      await ctx.api(`api/transactions/${t.id}/classification`, { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
      dlg.close();
      ctx.toast(ctx.get('common.saved'));
      await renderTransactions(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

// A transaction split can be a generic category split, a concrete purchased article, or a mix of
// both. Product rows preserve their receipt sign and signed adjustment rows (discount/coupon) reverse
// the ledger direction: for an expense, a +15 receipt product becomes -15 while a -2 coupon becomes +2.
// The optional PurchaseItemId preserves item identity without changing the accounting sum.
async function openSplitDialog(t) {
  let current, options, detail;
  try {
    [current, options, detail] = await Promise.all([
      ctx.api(`api/transactions/${t.id}/allocations`),
      ctx.categoryOptions(),
      ctx.api(`api/transactions/${t.id}`)
    ]);
  }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }

  const purchases = detail?.purchases || [];
  const articleRows = purchases.flatMap(purchase => (purchase.items || [])
    .filter(item => Number(item.totalPrice || 0) !== 0)
    .map(item => ({
      ...item,
      purchaseId: purchase.id,
      purchaseMerchant: purchase.merchant || ctx.get('purchases.receipt'),
      purchaseDate: purchase.purchaseDate
    })));
  const articleById = new Map(articleRows.map(item => [item.id, item]));
  const lines = (current?.lines || []).map(line => ({
    categoryId: line.categoryId || '',
    amount: line.amount,
    note: line.note || '',
    purchaseItemId: line.purchaseItemId || '',
    articleName: line.articleName || ''
  }));
  const langDe = (document.documentElement.lang || 'de').toLowerCase().startsWith('de');
  const articleLabel = langDe ? 'Konkreter Artikel' : 'Specific item';
  const categorySplitLabel = langDe ? 'Kategorie-Aufteilung' : 'Category split';
  const adjustmentLabel = langDe ? 'Rabatt/Gutschrift' : 'Discount/credit';
  const articleHint = langDe
    ? 'Artikel stammen nur aus verknüpften Käufen. Rabatte und Coupons erscheinen als Gegenbuchung und reduzieren den Ausgabenanteil.'
    : 'Items only come from linked purchases. Discounts and coupons are offset lines and reduce the expense allocation.';

  const articleOptions = () => {
    if (!articleRows.length) return '';
    const groups = new Map();
    for (const item of articleRows) {
      const key = `${item.purchaseId}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(item);
    }
    return [...groups.values()].map(items => {
      const first = items[0];
      const groupLabel = `${first.purchaseMerchant}${first.purchaseDate ? ` · ${ctx.date(first.purchaseDate)}` : ''}`;
      return `<optgroup label="${ctx.esc(groupLabel)}">${items.map(item => {
        const lineType = String(item.lineType || '').toLowerCase();
        const suffix = ['discount', 'coupon'].includes(lineType) ? ` · ${adjustmentLabel}` : '';
        return `<option value="${item.id}">${ctx.esc(item.name || articleLabel)}${ctx.esc(suffix)} · ${ctx.money(item.totalPrice, item.currency || t.currency)}</option>`;
      }).join('')}</optgroup>`;
    }).join('');
  };

  const dlg = ctx.dialog(`<form class="dialog-card tx-split tx-split-articles" method="dialog">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('transactions.split'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <div class="row-sub">${ctx.esc(ctx.get('transactions.total'))}: ${ctx.money(t.amount, t.currency)}</div>
    ${articleRows.length ? `<div class="row-sub">${ctx.esc(articleHint)}</div>` : ''}
    <div class="split-lines" data-lines></div>
    <button type="button" class="ghost" data-add-line>${ctx.esc(ctx.get('transactions.addLine'))}</button>
    <div class="split-remaining" data-remaining></div>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="button" data-save>${ctx.esc(ctx.get('common.apply'))}</button></div>
  </form>`);
  dlg.classList.add('drawer');
  const linesBox = dlg.querySelector('[data-lines]');
  const remainingEl = dlg.querySelector('[data-remaining]');

  function applyArticleState(row, selectedId, suggestAmount = false) {
    const category = row.querySelector('.split-cat');
    const note = row.querySelector('.split-note');
    const amount = row.querySelector('.split-amt');
    const article = selectedId ? articleById.get(selectedId) : null;
    row.dataset.purchaseItemId = article?.id || '';
    if (!article) {
      category.disabled = false;
      return;
    }
    category.value = article.categoryId || '';
    category.disabled = true;
    if (!note.value.trim()) note.value = article.name || '';
    if (suggestAmount && !String(amount.value || '').trim()) {
      const ledgerDirection = Number(t.amount) < 0 ? -1 : 1;
      amount.value = String(Math.round(Number(article.totalPrice || 0) * ledgerDirection * 100) / 100);
    }
  }

  const rowFor = line => {
    const row = document.createElement('div');
    row.className = 'split-line split-line-article';
    row.innerHTML = `<select class="split-article" aria-label="${ctx.esc(articleLabel)}">
        <option value="">${ctx.esc(categorySplitLabel)}</option>${articleOptions()}
      </select>
      <input class="split-amt" type="number" step="0.01" inputmode="decimal" value="${line.amount ?? ''}" aria-label="${ctx.esc(ctx.get('transactions.amount'))}">
      <select class="split-cat" aria-label="${ctx.esc(ctx.get('transactions.category'))}"><option value="">${ctx.esc(ctx.get('common.uncategorized'))}</option>${options}</select>
      <input class="split-note" maxlength="500" value="${ctx.esc(line.note || '')}" aria-label="${ctx.esc(ctx.get('transactions.note'))}">
      <button type="button" class="icon-button" data-remove aria-label="${ctx.esc(ctx.get('dashboard.remove'))}">×</button>`;
    const articleSelect = row.querySelector('.split-article');
    const category = row.querySelector('.split-cat');
    category.value = line.categoryId || '';
    if (line.purchaseItemId && articleById.has(line.purchaseItemId)) articleSelect.value = line.purchaseItemId;
    applyArticleState(row, articleSelect.value, false);
    articleSelect.addEventListener('change', () => { applyArticleState(row, articleSelect.value, true); recompute(); });
    row.querySelector('.split-amt').addEventListener('input', recompute);
    row.querySelector('[data-remove]').addEventListener('click', () => { row.remove(); recompute(); });
    attachCategoryPicker(ctx, category);
    return row;
  };

  function recompute() {
    let sum = 0;
    linesBox.querySelectorAll('.split-amt').forEach(input => {
      const value = Number(input.value);
      if (!Number.isNaN(value)) sum += value;
    });
    const remaining = Math.round((Number(t.amount) - sum) * 100) / 100;
    remainingEl.textContent = `${ctx.get('transactions.remaining')}: ${ctx.money(remaining, t.currency)}`;
    remainingEl.className = 'split-remaining' + (Math.abs(remaining) <= 0.01 ? ' ok' : ' warn');
  }

  const addLine = line => {
    linesBox.appendChild(rowFor(line || { amount: '', categoryId: '', note: '', purchaseItemId: '' }));
    recompute();
  };

  if (lines.length) lines.forEach(addLine); else recompute();
  dlg.querySelector('[data-add-line]').addEventListener('click', () => addLine());
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('[data-save]').onclick = async () => {
    const payload = [...linesBox.querySelectorAll('.split-line')].map(row => ({
      categoryId: row.querySelector('.split-cat').value || null,
      amount: Number(row.querySelector('.split-amt').value || 0),
      note: row.querySelector('.split-note').value.trim() || null,
      purchaseItemId: row.dataset.purchaseItemId || null,
    }));
    try {
      await ctx.api(`api/transactions/${t.id}/allocations`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
      dlg.close();
      ctx.toast(ctx.get('common.saved'));
      await renderTransactions(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

function setRefund(refundId, originalTransactionId, refundCategoryId = null) {
  return ctx.api(`api/transactions/${refundId}/refund`, { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ originalTransactionId, refundCategoryId }) });
}

// Refund picker (UI_UX_SPEC §9.6): link a positive transaction to an original expense so analytics
// reduce that expense's category instead of counting the refund as income. If the chosen original has
// more than one split line, a second step lets the user attribute the refund to ONE item (or the whole
// transaction) — a single-category original skips straight to a whole-transaction link.
async function openRefundPicker(t) {
  let candidates;
  try {
    const res = await ctx.api('api/transactions?direction=expense&limit=50');
    candidates = (res.items || []).filter(x => x.id !== t.id);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }

  const dlg = ctx.dialog(`<div class="dialog-card drawer">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('transactions.refundLink'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <div data-step></div>
  </div>`);
  const step = dlg.querySelector('[data-step]');
  dlg.querySelector('[data-close]').onclick = () => dlg.close();

  const link = async (originalId, categoryId) => {
    try { await setRefund(t.id, originalId, categoryId); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderTransactions(ctx); }
    catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };

  // Step 2 — which split line does the refund cover? Only shown when the original has >1 category.
  const chooseTarget = async original => {
    let lines = [];
    try { const alloc = await ctx.api(`api/transactions/${original.id}/allocations`); lines = (alloc.lines || []).filter(l => l.categoryId); }
    catch { /* no split info available → whole-transaction link */ }
    const distinct = [...new Map(lines.map(l => [l.categoryId, l])).values()];
    if (distinct.length <= 1) { await link(original.id, null); return; }

    let names = new Map();
    try { const cats = await ctx.api('api/categories'); names = new Map(cats.map(c => [c.id, c.name])); } catch { /* names optional */ }
    const opts = distinct.map(l => `<button type="button" class="row candidate-row" data-cat="${l.categoryId}"><div class="row-main"><div class="row-title">${ctx.esc(names.get(l.categoryId) || ctx.get('common.uncategorized'))}</div></div><div class="amount negative">${ctx.money(l.amount, original.currency)}</div></button>`).join('');
    step.innerHTML = `<p class="row-sub">${ctx.esc(ctx.get('transactions.refundCategoryPick'))}</p>
      <div class="refund-candidates"><button type="button" class="row candidate-row" data-cat=""><div class="row-main"><div class="row-title">${ctx.esc(ctx.get('transactions.refundWhole'))}</div></div></button>${opts}</div>`;
    step.querySelectorAll('.candidate-row').forEach(row => row.addEventListener('click', () => link(original.id, row.dataset.cat || null)));
  };

  // Step 1 — pick the original expense.
  const rows = candidates.length
    ? candidates.map(x => `<button type="button" class="row candidate-row" data-id="${x.id}"><div class="row-main"><div class="row-title">${ctx.esc(x.counterparty || '—')}</div><div class="row-sub">${ctx.esc(ctx.date(x.bookingDate))} · ${ctx.esc(x.category || ctx.get('common.uncategorized'))}</div></div><div class="amount negative">${ctx.money(x.amount, x.currency)}</div></button>`).join('')
    : `<div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div>`;
  step.innerHTML = `<p class="row-sub">${ctx.esc(ctx.get('transactions.refundPick'))}</p><div class="refund-candidates">${rows}</div>`;
  step.querySelectorAll('.candidate-row').forEach(row => row.addEventListener('click', () => chooseTarget(candidates.find(c => c.id === row.dataset.id))));

  dlg.showModal();
}

function spaceId() {
  // The receipt link needs the space param; read it from the persisted selection the app stores.
  return localStorage.getItem('finance.space') || '';
}
