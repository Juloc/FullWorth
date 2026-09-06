import { state } from '../core/state.js';
import { ButtonRole, buttonClass } from '../ui/buttons.js';

let ctx;

function use(context) {
  ctx = context;
  return context;
}

function coachLabel() {
  const translated = ctx.get('coach.title');
  return translated === 'coach.title' ? 'Coach' : translated;
}

export async function renderBudgets(context) {
  use(context);
  const currency = state.space?.baseCurrency || 'EUR';
  const status = await ctx.api('api/analytics/budget-status');
  const items = status.items || [];

  const totalBudgeted = items.reduce((sum, item) => sum + Number(item.amount || 0), 0);
  const totalSpent = items.reduce((sum, item) => sum + Number(item.spent || 0), 0);

  ctx.$('#budget-total').textContent = ctx.money(totalBudgeted, currency);
  ctx.$('#budget-spent').textContent = ctx.money(totalSpent, currency);
  ctx.$('#budget-remaining').textContent = ctx.money(totalBudgeted - totalSpent, currency);

  const root = ctx.$('#budgets-list');
  root.innerHTML = '';
  if (!items.length) {
    ctx.empty(root);
    return;
  }

  for (const item of items) {
    const percent = Math.max(0, Number(item.percent || 0));
    const clamped = Math.min(100, percent);
    const statusKey = percent > 100 ? 'over' : percent >= 85 ? 'near' : 'ontrack';
    const cycleLabel = item.period && item.period !== 'monthly'
      ? `${ctx.esc(ctx.get('budgets.period_' + item.period) || item.period)} · ${ctx.date(item.periodStart)}–${ctx.date(item.periodEnd)} · `
      : '';

    root.insertAdjacentHTML('beforeend', `
      <div class="budget-card" role="button" tabindex="0" data-id="${ctx.esc(item.budgetId || item.id)}">
        <div class="budget-card-head">
          <div class="row-title">${ctx.esc(item.name)}</div>
          <div class="budget-card-head-actions">
            <button type="button" class="${buttonClass(ButtonRole.Secondary, 'budget-coach')}" data-coach>${ctx.esc(coachLabel())}</button>
            <span class="budget-status ${statusKey}">${ctx.esc(ctx.get('budgets.status_' + statusKey))}</span>
          </div>
        </div>
        <div class="progress ${statusKey}"><span data-w="${clamped}"></span></div>
        <div class="budget-card-foot">
          <span>${cycleLabel}${ctx.money(item.spent, currency)} / ${ctx.money(item.amount, currency)}</span>
          <span>${ctx.esc(ctx.get('budgets.remaining'))}: ${ctx.money(item.remaining, currency)}</span>
        </div>
      </div>`);
  }

  if (status.incomplete) {
    root.insertAdjacentHTML('afterbegin', `<div class="fx-incomplete">${ctx.esc(ctx.get('common.fxIncomplete'))}</div>`);
  }

  root.querySelectorAll('.progress > span[data-w]').forEach(element => {
    element.style.width = element.dataset.w + '%';
  });

  root.querySelectorAll('.budget-card[data-id]').forEach(card => {
    const item = items.find(value => String(value.budgetId || value.id) === String(card.dataset.id));
    const open = () => openBudgetDetail(ctx, card.dataset.id);

    card.querySelector('[data-coach]')?.addEventListener('click', event => {
      event.stopPropagation();
      if (!item) return;
      window.dispatchEvent(new CustomEvent('fullworth:coach-open', {
        detail: {
          entityType: 'budget',
          entityId: item.budgetId || item.id,
          entityLabel: item.name,
          details: {
            amount: String(item.amount ?? ''),
            currency,
            status: item.percent > 100 ? 'over' : item.percent >= 85 ? 'near' : 'ontrack'
          }
        }
      }));
    });

    card.addEventListener('click', event => {
      if (!event.target.closest('button')) open();
    });
    card.addEventListener('keydown', event => {
      if (!event.target.closest('button') && (event.key === 'Enter' || event.key === ' ')) {
        event.preventDefault();
        open();
      }
    });
  });
}

export async function openBudgetDetail(context, id) {
  use(context);
  let budgetStatus;
  try {
    budgetStatus = await ctx.api(`api/budgets/${id}/status`);
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }

  if (!budgetStatus) {
    ctx.toast(ctx.get('common.error'));
    return;
  }

  const currency = budgetStatus.currency || state.space?.baseCurrency || 'EUR';
  const percent = Math.max(0, Number(budgetStatus.percentUsed || 0));
  const clamped = Math.min(100, percent);
  const barStatus = percent > 100 ? 'over' : percent >= 85 ? 'near' : 'ontrack';
  const projectedPercent = Number(budgetStatus.budgetAmount) > 0
    ? (Number(budgetStatus.projectedEndSpend || 0) / Number(budgetStatus.budgetAmount)) * 100
    : 0;
  const forecastPercent = Math.max(0, Math.min(100, projectedPercent) - clamped);
  const trend = budgetStatus.trend || 'NoData';
  const trendKey = 'budgets.trend_' + trend.toLowerCase();
  const projectedOverUnder = Number(budgetStatus.projectedOverUnder || 0);

  const forecastLine = trend === 'NoData' ? '' : `
    <div class="budget-detail-forecast">
      <div class="kv">
        <span>${ctx.esc(ctx.get('budgets.projectedEnd'))}</span>
        <strong class="amount">${ctx.money(budgetStatus.projectedEndSpend, currency)}</strong>
      </div>
      <div class="kv">
        <span>${ctx.esc(ctx.get(projectedOverUnder > 0 ? 'budgets.projectedOver' : 'budgets.projectedUnder'))}</span>
        <strong class="amount ${projectedOverUnder > 0 ? 'negative' : 'positive'}">${ctx.money(Math.abs(projectedOverUnder), currency)}</strong>
      </div>
    </div>`;

  const rows = (budgetStatus.contributing || []).map(transaction => `
    <div class="row">
      <div class="row-main">
        <div class="row-title">${ctx.esc(transaction.counterparty || '—')}</div>
        <div class="row-sub">${transaction.bookingDate ? ctx.date(transaction.bookingDate) : ''}${transaction.category ? ` · ${ctx.esc(transaction.category)}` : ''}</div>
      </div>
      <div class="amount negative">${ctx.money(-Math.abs(Number(transaction.amount || 0)), transaction.currency || currency)}</div>
    </div>`).join('');

  const cycleLabel = budgetStatus.period && budgetStatus.period !== 'monthly'
    ? `${ctx.esc(ctx.get('budgets.period_' + budgetStatus.period) || budgetStatus.period)} · `
    : '';

  const dlg = ctx.dialog(`<div class="dialog-card budget-detail">
    <div class="panel-head">
      <h2>${ctx.esc(budgetStatus.name)}</h2>
      <div class="panel-head-actions">
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-edit>${ctx.esc(ctx.get('common.edit'))}</button>
      </div>
    </div>
    <div class="row-sub">${cycleLabel}${ctx.date(budgetStatus.periodStart)}–${ctx.date(budgetStatus.periodEnd)}</div>
    <div class="budget-detail-stats">
      <div class="kv"><span>${ctx.esc(ctx.get('budgets.spent'))}</span><strong class="amount">${ctx.money(budgetStatus.spent, currency)}</strong></div>
      <div class="kv"><span>${ctx.esc(ctx.get('budgets.budget'))}</span><strong class="amount">${ctx.money(budgetStatus.budgetAmount, currency)}</strong></div>
      <div class="kv"><span>${ctx.esc(ctx.get('budgets.remaining'))}</span><strong class="amount${Number(budgetStatus.remaining) < 0 ? ' negative' : ''}">${ctx.money(budgetStatus.remaining, currency)}</strong></div>
    </div>
    <div class="progress ${barStatus}">
      <span data-w="${clamped}"></span>
      <span class="forecast" data-w="${forecastPercent}"></span>
    </div>
    <div class="budget-detail-trend">
      <span class="budget-status ${barStatus}">${ctx.esc(Math.round(percent))}%</span>
      <span>${ctx.esc(ctx.get(trendKey))}</span>
    </div>
    ${forecastLine}
    <div class="row-group">${ctx.esc(ctx.get('budgets.contributing'))}</div>
    <div class="budget-detail-rows">${rows || `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`}</div>
  </div>`);

  dlg.querySelectorAll('.progress > span[data-w]').forEach(element => {
    element.style.width = element.dataset.w + '%';
  });

  const coach = document.createElement('button');
  coach.type = 'button';
  coach.className = buttonClass(ButtonRole.Secondary);
  coach.textContent = coachLabel();
  coach.addEventListener('click', () => {
    dlg.close();
    window.dispatchEvent(new CustomEvent('fullworth:coach-open', {
      detail: {
        entityType: 'budget',
        entityId: budgetStatus.budgetId,
        entityLabel: budgetStatus.name,
        details: {
          amount: String(budgetStatus.budgetAmount ?? ''),
          currency,
          status: barStatus,
          count: String((budgetStatus.contributing || []).length)
        }
      }
    }));
  });

  dlg.querySelector('.panel-head-actions')?.prepend(coach);
  dlg.querySelector('[data-edit]')?.addEventListener('click', () => openBudgetEdit(budgetStatus.budgetId, () => dlg.close()));
  dlg.showModal();
}

export async function newBudget(context) {
  use(context);
  return openBudgetDialog();
}

async function openBudgetDialog(existing) {
  const currency = existing?.currency || state.space?.baseCurrency || 'EUR';
  let options;
  try {
    options = await ctx.categoryOptions(existing?.categoryId || undefined);
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }

  const periods = ['monthly', 'weekly', 'biweekly', 'paycycle']
    .map(period => `<option value="${period}"${existing?.period === period ? ' selected' : ''}>${ctx.esc(ctx.get('budgets.period_' + period))}</option>`)
    .join('');

  const dlg = ctx.dialog(`<form class="dialog-card">
    <h2>${ctx.esc(ctx.get(existing ? 'budgets.edit' : 'budgets.new'))}</h2>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="120" value="${ctx.esc(existing?.name || '')}"></label>
    <label>${ctx.esc(ctx.get('transactions.amount'))}<input name="amount" type="number" step="0.01" inputmode="decimal" required value="${existing ? ctx.esc(String(existing.amount)) : ''}"></label>
    <label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(currency)}" maxlength="3" required></label>
    <label>${ctx.esc(ctx.get('budgets.period'))}<select name="period">${periods}</select></label>
    <label>${ctx.esc(ctx.get('transactions.category'))}<select name="category"><option value="">${ctx.esc(ctx.get('common.all'))}</option>${options}</select></label>
    <label class="check"><input name="carryOver" type="checkbox"${existing?.carryOver ? ' checked' : ''}>${ctx.esc(ctx.get('budgets.carryOver'))}</label>
    <div class="dialog-actions">
      ${existing ? `<button type="button" class="${buttonClass(ButtonRole.Danger)}" data-delete>${ctx.esc(ctx.get('common.delete'))}</button>` : ''}
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button>
      <button type="submit" class="${buttonClass(ButtonRole.Primary)}">${ctx.esc(ctx.get(existing ? 'common.save' : 'common.create'))}</button>
    </div>
  </form>`);

  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();

  dlg.querySelector('[data-delete]')?.addEventListener('click', async () => {
    if (!await ctx.confirm(
      ctx.get('budgets.deleteConfirm').replace('{name}', existing.name),
      { destructive: true, confirmLabel: ctx.get('common.delete') })) return;

    try {
      await ctx.api(`api/budgets/${existing.id}`, { method: 'DELETE' });
      dlg.close();
      ctx.toast(ctx.get('common.deleted'));
      await renderBudgets(ctx);
    } catch (error) {
      ctx.toast(error.message || ctx.get('common.error'));
    }
  });

  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const body = ctx.jsonBody({
      name: form.get('name'),
      categoryId: form.get('category') || null,
      amount: Number(form.get('amount')),
      currency: form.get('currency'),
      period: form.get('period'),
      carryOver: form.get('carryOver') === 'on',
      isActive: true,
      startDate: null,
      endDate: null
    });

    try {
      await ctx.api(
        existing ? `api/budgets/${existing.id}` : 'api/budgets',
        existing ? { ...body, method: 'PUT' } : body);
      dlg.close();
      ctx.toast(ctx.get('common.saved'));
      await renderBudgets(ctx);
    } catch (error) {
      ctx.toast(error.message || ctx.get('common.error'));
    }
  };

  dlg.showModal();
}

async function openBudgetEdit(id, closeDrawer) {
  let budget;
  try {
    budget = await ctx.api(`api/budgets/${id}`);
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }

  closeDrawer?.();
  openBudgetDialog(budget);
}
