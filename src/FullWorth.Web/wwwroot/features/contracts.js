// Contracts & recurring costs (UI_UX_SPEC §13). Contracts come from detection (candidates the owner
// confirms) or manual creation. The list separates active from archived; each row opens a detail drawer
// showing value mode (automatic/manual), linked payments + payment trend, next expected payment,
// annualized cost, start/end and notes, with edit / cancel (archive) / reactivate. A "detected
// subscriptions" section surfaces recurring-payment candidates with one-click accept. All money and
// cadence come from the backend (annualization/next-due are computed server-side, §30).

let ctx = null;
const CYCLES = ['monthly', 'quarterly', 'yearly', 'weekly'];
const KINDS = ['subscription', 'contract', 'insurance', 'loan', 'other'];
// Populated on every render so the price-change panel can show a contract's name/currency from its id.
let contractsById = new Map();

export function bindContracts(context) {
  ctx = context;
  ctx.$('[data-action="new-contract"]').addEventListener('click', () => openContractDialog());
  // One "Detect" action scans for both new subscriptions and price changes on existing contracts.
  ctx.$('#contracts-detect')?.addEventListener('click', () => { loadDetected(true); loadPriceChanges(true); });
  ctx.$('#contracts-archived')?.addEventListener('change', () => renderContracts(ctx));
}

// Used by the page-header primary action.
export function newContract(context) { if (context) ctx = context; return openContractDialog(); }

export async function renderContracts(context) {
  ctx = context;
  const showArchived = !!ctx.$('#contracts-archived')?.checked;
  const rows = (await ctx.api('api/contracts')) || [];
  contractsById = new Map(rows.map(c => [c.id, c]));
  const shown = showArchived ? rows : rows.filter(r => r.isActive);

  const list = ctx.$('#contracts-list');
  list.innerHTML = '';
  if (!shown.length) {
    list.innerHTML = `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`;
  } else {
    const frag = document.createDocumentFragment();
    for (const c of shown) frag.appendChild(rowFor(c));
    list.appendChild(frag);
  }
  // Refresh detected candidates + price-change suggestions quietly (no error toast on the passive load).
  loadDetected(false);
  loadPriceChanges(false);
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

function rowFor(c) {
  const row = document.createElement('button');
  row.type = 'button';
  row.className = 'row contract-row' + (c.isActive ? '' : ' contract-archived');
  const cycle = ctx.get('contracts.cycle_' + (c.billingCycle || 'monthly'));
  const due = c.nextDueDate ? ctx.date(c.nextDueDate) : '—';
  row.innerHTML = `
    <div class="row-main">
      <div class="row-title">${ctx.esc(c.name)}${c.isActive ? '' : ` <span class="tx-marker">${ctx.esc(ctx.get('contracts.archived'))}</span>`}</div>
      <div class="row-sub">${ctx.esc(cycle)} · ${ctx.esc(ctx.get('contracts.nextDue'))}: ${ctx.esc(due)}</div>
    </div>
    <div class="amount">${ctx.money(c.amount, c.currency)}</div>`;
  row.addEventListener('click', () => openDetail(c.id));
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
