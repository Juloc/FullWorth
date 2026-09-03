// Loans / credit liabilities (UI_UX_SPEC §13 loans + §14.4 amortization). Loans are a first-class
// liability with full amortization: the detail drawer shows the payoff estimate, total expected
// interest, principal/interest split and a remaining-balance curve, all computed server-side
// (GET /api/loans/{id}/amortization). When the loan cannot be projected reliably the drawer says so
// instead of a misleading number. Loans render as a panel inside the net-worth screen.

let ctx = null;
const FREQ = ['monthly', 'quarterly', 'yearly', 'weekly'];

export function bindLoans(context) {
  ctx = context;
  ctx.$('[data-action="new-loan"]')?.addEventListener('click', () => openLoanDialog());
}

export async function renderLoans(context) {
  ctx = context;
  const el = ctx.$('#nw-loans');
  if (!el) return;
  let loans;
  try { loans = await ctx.api('api/loans'); } catch { el.innerHTML = errorRow(); return; }
  loans = (loans || []).filter(l => l.isActive);
  if (!loans.length) { el.innerHTML = emptyRow(); return; }
  el.innerHTML = '';
  const frag = document.createDocumentFragment();
  for (const l of loans) {
    const row = document.createElement('button');
    row.type = 'button';
    row.className = 'row loan-row';
    row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(l.name)}</div><div class="row-sub">${ctx.esc(ctx.get('loans.rate'))} ${Number(l.nominalInterestRate)}% · ${ctx.esc(ctx.get('loans.payment'))} ${ctx.money(l.paymentAmount, l.currency)}</div></div><div class="amount">${ctx.money(l.currentBalance, l.currency)}</div>`;
    row.addEventListener('click', () => openAmortization(l));
    frag.appendChild(row);
  }
  el.appendChild(frag);
}

async function openAmortization(loan) {
  let data, error;
  try { data = await ctx.api(`api/loans/${loan.id}/amortization`); }
  catch (err) { error = err.message || ctx.get('loans.notEnough'); }

  const body = error
    ? `<div class="row-sub">${ctx.esc(ctx.get('loans.notEnough'))}</div>`
    : amortizationBody(loan, data);

  const dlg = ctx.dialog(`<div class="dialog-card loan-detail">
    <div class="panel-head"><h2>${ctx.esc(loan.name)}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    ${body}
    <div class="dialog-actions"><button type="button" data-edit>${ctx.esc(ctx.get('loans.edit'))}</button></div>
  </div>`);
  dlg.querySelectorAll('.loan-split-bar > span[data-w]').forEach(s => { s.style.width = s.dataset.w + '%'; });
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-edit]').onclick = () => { dlg.close(); openLoanDialog(loan); };
  dlg.showModal();
}

function amortizationBody(loan, data) {
  const cur = data.currency || loan.currency;
  const periods = data.periods || [];
  const totalInterest = Number(data.totalExpectedInterest || 0);
  const totalPrincipal = Number(data.totalPrincipal || 0);
  const total = totalInterest + totalPrincipal;
  const interestPct = total > 0 ? Math.round((totalInterest / total) * 100) : 0;

  const meta = [
    [ctx.get('loans.payoffDate'), data.estimatedPayoffDate ? ctx.date(data.estimatedPayoffDate) : '—'],
    [ctx.get('loans.payments'), String(data.periodCount ?? periods.length)],
    [ctx.get('loans.totalInterest'), ctx.money(totalInterest, cur)],
    [ctx.get('loans.totalPrincipal'), ctx.money(totalPrincipal, cur)]
  ].map(([k, v]) => `<div class="detail-item"><span class="detail-k">${ctx.esc(k)}</span><span class="detail-v">${ctx.esc(v)}</span></div>`).join('');

  // Principal vs interest split bar (shape only; amounts shown in the metrics above and masked there).
  const splitBar = `<div class="loan-split"><div class="loan-split-head"><span>${ctx.esc(ctx.get('loans.principal'))} ${ctx.isPrivate() ? '' : (100 - interestPct) + '%'}</span><span>${ctx.esc(ctx.get('loans.interest'))} ${ctx.isPrivate() ? '' : interestPct + '%'}</span></div><div class="loan-split-bar"><span class="loan-split-principal" data-w="${100 - interestPct}"></span><span class="loan-split-interest" data-w="${interestPct}"></span></div></div>`;

  return `<div class="detail-grid">${meta}</div>${splitBar}<div class="loan-chart">${balanceChart(periods)}</div>`;
}

// Remaining-balance decline over the schedule as an SVG polyline.
function balanceChart(periods) {
  if (periods.length < 2) return '';
  const vals = periods.map(p => Number(p.remainingBalance));
  const max = Math.max(...vals, 1), w = 600, h = 120;
  const pts = periods.map((p, i) => `${(i / (periods.length - 1)) * w},${h - (Number(p.remainingBalance) / max) * (h - 12) - 6}`).join(' ');
  return `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${ctx.esc(ctx.get('loans.balanceCurve'))}"><polyline points="${pts}" fill="none" stroke="currentColor" stroke-width="2" vector-effect="non-scaling-stroke"/></svg>`;
}

async function openLoanDialog(existing) {
  const l = existing || {};
  const currency = l.currency || 'EUR';
  let categories, accounts;
  try { categories = await ctx.categoryOptions(l.categoryId); accounts = (await ctx.api('api/accounts')) || []; }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }
  const freqOpts = FREQ.map(f => `<option value="${f}"${(l.paymentFrequency || 'monthly') === f ? ' selected' : ''}>${ctx.esc(ctx.get('contracts.cycle_' + f))}</option>`).join('');
  const accountOpts = accounts.map(a => `<option value="${a.id}"${l.accountId === a.id ? ' selected' : ''}>${ctx.esc(a.displayName || a.institutionName)}</option>`).join('');
  const dv = v => v ? String(v).slice(0, 10) : '';

  const dlg = ctx.dialog(`<form class="dialog-card">
    <div class="panel-head"><h2>${ctx.esc(ctx.get(existing ? 'loans.edit' : 'loans.new'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="160" value="${ctx.esc(l.name || '')}"></label>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('loans.principalOriginal'))}<input name="principal" type="number" step="0.01" min="0" required value="${l.originalPrincipal ?? ''}"></label>
      <label>${ctx.esc(ctx.get('loans.balance'))}<input name="balance" type="number" step="0.01" min="0" required value="${l.currentBalance ?? ''}"></label>
    </div>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('loans.payment'))}<input name="payment" type="number" step="0.01" min="0" required value="${l.paymentAmount ?? ''}"></label>
      <label>${ctx.esc(ctx.get('loans.rate'))}<input name="rate" type="number" step="0.001" min="0" required value="${l.nominalInterestRate ?? ''}"></label>
    </div>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('loans.frequency'))}<select name="frequency">${freqOpts}</select></label>
      <label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(currency)}" maxlength="3" required></label>
    </div>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('contracts.startDate'))}<input name="start" type="date" required value="${dv(l.startDate) || dv(new Date().toISOString())}"></label>
      <label>${ctx.esc(ctx.get('loans.fees'))}<input name="fees" type="number" step="0.01" min="0" value="${l.fees ?? 0}"></label>
    </div>
    <label>${ctx.esc(ctx.get('contracts.account'))}<select name="account"><option value="">—</option>${accountOpts}</select></label>
    <label>${ctx.esc(ctx.get('transactions.category'))}<select name="category"><option value="">${ctx.esc(ctx.get('common.all'))}</option>${categories}</select></label>
    <div class="dialog-actions">${existing ? `<button type="button" class="ghost danger" data-delete>${ctx.esc(ctx.get('common.delete'))}</button>` : ''}<button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get(existing ? 'common.apply' : 'common.create'))}</button></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('[data-delete]')?.addEventListener('click', async () => {
    if (!await ctx.confirm(ctx.get('loans.deleteConfirm').replace('{name}', () => existing.name), { destructive: true, confirmLabel: ctx.get('common.delete') })) return;
    try { await ctx.api(`api/loans/${existing.id}`, { method: 'DELETE' }); dlg.close(); ctx.toast(ctx.get('common.deleted')); await renderLoans(ctx); }
    catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    const body = {
      name: fd.get('name'), originalPrincipal: Number(fd.get('principal')), currentBalance: Number(fd.get('balance')),
      paymentAmount: Number(fd.get('payment')), nominalInterestRate: Number(fd.get('rate')),
      startDate: fd.get('start'), endDate: null, fixedTermMonths: null, fees: Number(fd.get('fees') || 0),
      paymentFrequency: fd.get('frequency'), currency: (fd.get('currency') || 'EUR').toUpperCase(),
      categoryId: fd.get('category') || null, accountId: fd.get('account') || null, isActive: existing ? existing.isActive !== false : true
    };
    try {
      await ctx.api(existing ? `api/loans/${existing.id}` : 'api/loans', { method: existing ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      dlg.close(); ctx.toast(ctx.get('common.saved')); await renderLoans(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

function emptyRow() { return `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`; }
function errorRow() { return `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.error'))}</div></div>`; }
