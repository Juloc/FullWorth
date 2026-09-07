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
  const carryIn = Number(budgetStatus.carryIn || 0);
  const rolloverLine = Math.abs(carryIn) > 0.004
    ? `<div class="row-sub budget-rollover-summary">${ctx.esc(ctx.get('budgets.baseAmount'))}: ${ctx.money(budgetStatus.baseBudgetAmount ?? budgetStatus.budgetAmount, currency)} · ${ctx.esc(ctx.get('budgets.carryIn'))}: ${carryIn > 0 ? '+' : ''}${ctx.money(carryIn, currency)}</div>`
    : '';

  const dlg = ctx.dialog(`<div class="dialog-card budget-detail">
    <div class="panel-head">
      <h2>${ctx.esc(budgetStatus.name)}</h2>
      <div class="panel-head-actions">
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-edit>${ctx.esc(ctx.get('common.edit'))}</button>
      </div>
    </div>
    <div class="row-sub">${cycleLabel}${ctx.date(budgetStatus.periodStart)}–${ctx.date(budgetStatus.periodEnd)}</div>
    ${rolloverLine}
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

  const selectedPeriod = existing?.period || 'monthly';
  const periods = ['daily','weekly','biweekly','monthly','quarterly','yearly','paycycle','custom']
    .map(period => `<option value="${period}"${selectedPeriod === period ? ' selected' : ''}>${ctx.esc(ctx.get('budgets.period_' + period))}</option>`)
    .join('');

  const rollover = !existing?.carryOver
    ? 'reset'
    : existing?.carryOverOverspend === false
      ? 'positive'
      : 'full';
  const rolloverOptions = ['reset','positive','full']
    .map(mode => `<option value="${mode}"${rollover === mode ? ' selected' : ''}>${ctx.esc(ctx.get('budgets.rollover_' + mode))}</option>`)
    .join('');

  const presets = !existing ? `
    <div class="budget-wizard-presets">
      <div class="row-sub">${ctx.esc(ctx.get('budgets.quickStart'))}</div>
      <div class="budget-preset-row">
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-budget-preset="weekly-groceries">${ctx.esc(ctx.get('budgets.preset_weeklyGroceries'))}</button>
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-budget-preset="monthly">${ctx.esc(ctx.get('budgets.preset_monthly'))}</button>
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-budget-preset="paycycle">${ctx.esc(ctx.get('budgets.preset_paycycle'))}</button>
      </div>
    </div>` : '';

  const dlg = ctx.dialog(`<form class="dialog-card budget-wizard">
    <h2>${ctx.esc(ctx.get(existing ? 'budgets.edit' : 'budgets.new'))}</h2>
    ${presets}
    <div class="budget-wizard-section">
      <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="120" value="${ctx.esc(existing?.name || '')}"></label>
      <div class="form-grid">
        <label>${ctx.esc(ctx.get('transactions.amount'))}<input name="amount" type="number" min="0.01" step="0.01" inputmode="decimal" required value="${existing ? ctx.esc(String(existing.amount)) : ''}"></label>
        <label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(currency)}" maxlength="3" required></label>
      </div>
      <label>${ctx.esc(ctx.get('budgets.period'))}<select name="period">${periods}</select></label>
      <div class="form-grid budget-cycle-fields">
        <label data-budget-start>${ctx.esc(ctx.get('budgets.anchorDate'))}<input name="startDate" type="date" value="${ctx.esc(existing?.startDate || '')}"><small class="row-sub" data-budget-anchor-hint></small></label>
        <label data-budget-end>${ctx.esc(ctx.get('budgets.endDate'))}<input name="endDate" type="date" value="${ctx.esc(existing?.endDate || '')}"></label>
      </div>
    </div>
    <div class="budget-wizard-section">
      <label>${ctx.esc(ctx.get('transactions.category'))}<select name="category"><option value="">${ctx.esc(ctx.get('common.all'))}</option>${options}</select></label>
      <label>${ctx.esc(ctx.get('budgets.rollover'))}<select name="rollover">${rolloverOptions}</select><small class="row-sub" data-rollover-hint></small></label>
    </div>
    <div class="dialog-actions">
      ${existing ? `<button type="button" class="${buttonClass(ButtonRole.Danger)}" data-delete>${ctx.esc(ctx.get('common.delete'))}</button>` : ''}
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button>
      <button type="submit" class="${buttonClass(ButtonRole.Primary)}">${ctx.esc(ctx.get(existing ? 'common.save' : 'common.create'))}</button>
    </div>
  </form>`);

  const form = dlg.querySelector('form');
  const periodSelect = form.querySelector('[name="period"]');
  const rolloverSelect = form.querySelector('[name="rollover"]');
  const startWrap = form.querySelector('[data-budget-start]');
  const endWrap = form.querySelector('[data-budget-end]');
  const startInput = form.querySelector('[name="startDate"]');
  const endInput = form.querySelector('[name="endDate"]');
  const anchorHint = form.querySelector('[data-budget-anchor-hint]');
  const rolloverHint = form.querySelector('[data-rollover-hint]');

  const localIso = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
  const mondayIso = () => {
    const date = new Date();
    date.setHours(12, 0, 0, 0);
    date.setDate(date.getDate() - ((date.getDay() + 6) % 7));
    return localIso(date);
  };

  const syncCycleFields = () => {
    const period = periodSelect.value;
    const needsAnchor = ['weekly','biweekly','paycycle','custom'].includes(period);
    startWrap.hidden = !needsAnchor;
    endWrap.hidden = period !== 'custom';
    startInput.required = period === 'custom';
    endInput.required = period === 'custom';
    anchorHint.textContent = ctx.get(
      period === 'paycycle'
        ? 'budgets.anchorHint_paycycle'
        : period === 'custom'
          ? 'budgets.anchorHint_custom'
          : 'budgets.anchorHint_week');
    if (period === 'paycycle' && !startInput.value) startInput.value = localIso(new Date());
  };

  const syncRolloverHint = () => {
    rolloverHint.textContent = ctx.get('budgets.rolloverHint_' + rolloverSelect.value);
  };

  periodSelect.addEventListener('change', syncCycleFields);
  rolloverSelect.addEventListener('change', syncRolloverHint);

  form.querySelectorAll('[data-budget-preset]').forEach(button => {
    button.addEventListener('click', () => {
      const preset = button.dataset.budgetPreset;
      const name = form.querySelector('[name="name"]');

      if (preset === 'weekly-groceries') {
        if (!name.value) name.value = ctx.get('budgets.presetName_weeklyGroceries');
        periodSelect.value = 'weekly';
        rolloverSelect.value = 'positive';
        startInput.value = mondayIso();
      } else if (preset === 'paycycle') {
        if (!name.value) name.value = ctx.get('budgets.presetName_paycycle');
        periodSelect.value = 'paycycle';
        rolloverSelect.value = 'full';
        startInput.value = localIso(new Date());
      } else {
        if (!name.value) name.value = ctx.get('budgets.presetName_monthly');
        periodSelect.value = 'monthly';
        rolloverSelect.value = 'reset';
      }

      syncCycleFields();
      syncRolloverHint();
      form.querySelector('[name="amount"]').focus();
    });
  });

  syncCycleFields();
  syncRolloverHint();

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

  form.onsubmit = async event => {
    event.preventDefault();
    const values = new FormData(event.currentTarget);
    const period = String(values.get('period') || 'monthly');
    const rolloverMode = String(values.get('rollover') || 'reset');
    const usesAnchor = ['weekly','biweekly','paycycle','custom'].includes(period);
    const body = ctx.jsonBody({
      name: values.get('name'),
      categoryId: values.get('category') || null,
      amount: Number(values.get('amount')),
      currency: values.get('currency'),
      period,
      carryOver: rolloverMode !== 'reset',
      carryOverOverspend: rolloverMode === 'full',
      isActive: true,
      startDate: usesAnchor ? (values.get('startDate') || null) : null,
      endDate: period === 'custom' ? (values.get('endDate') || null) : null
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
