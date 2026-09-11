import { state } from '../core/state.js';
import { ButtonRole, buttonClass } from '../ui/buttons.js';
import { MoneyVariant, moneyClass } from '../ui/money.js';
import { openFormDialog, FieldKind } from '../ui/form-dialog.js';

// The disclosure label is new with the form-dialog conversion and has no i18n key yet.
function lang() { return !document.documentElement.lang || !document.documentElement.lang.startsWith('en'); }
function bilingual(de, en) { return lang() ? de : en; }

let ctx;

function use(context) {
  ctx = context;
  return context;
}

// Which periods are anchored to a date rather than to the calendar. The dialog asks this twice -
// once to decide what to show, once to decide what to send - and the two must not drift apart.
const USES_ANCHOR = ['weekly', 'biweekly', 'paycycle', 'custom'];

function coachLabel() {
  const translated = ctx.get('coach.title');
  return translated === 'coach.title' ? 'Coach' : translated;
}

export async function renderBudgets(context) {
  use(context);
  const currency = state.space?.baseCurrency || 'EUR';
  const status = await ctx.api('api/analytics/budget-status');
  const items = status.items || [];

  const windows = new Set(items.map(item => `${item.periodStart || ''}|${item.periodEnd || ''}`));
  const comparableWindow = windows.size <= 1;
  const totalBudgeted = comparableWindow ? items.reduce((sum, item) => sum + Number(item.amount || 0), 0) : null;
  const totalSpent = comparableWindow ? items.reduce((sum, item) => sum + Number(item.spent || 0), 0) : null;

  ctx.$('#budget-total').textContent = totalBudgeted == null ? '—' : ctx.money(totalBudgeted, currency);
  ctx.$('#budget-spent').textContent = totalSpent == null ? '—' : ctx.money(totalSpent, currency);
  ctx.$('#budget-remaining').textContent = totalBudgeted == null ? '—' : ctx.money(totalBudgeted - totalSpent, currency);

  const root = ctx.$('#budgets-list');
  root.innerHTML = '';
  if (!comparableWindow && items.length) {
    root.insertAdjacentHTML('beforeend', `<div class="row-sub budget-period-note">${ctx.esc(document.documentElement.lang?.startsWith('en')
      ? 'Budgets use different active periods, so no misleading combined total is shown.'
      : 'Budgets haben unterschiedliche aktive Zeiträume – deshalb wird kein irreführender Gesamtwert addiert.')}</div>`);
  }
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
        <strong class="${moneyClass(projectedOverUnder > 0 ? MoneyVariant.Danger : MoneyVariant.Neutral)}">${ctx.money(Math.abs(projectedOverUnder), currency)}</strong>
      </div>
    </div>`;

  const rows = (budgetStatus.contributing || []).map(transaction => `
    <div class="row">
      <div class="row-main">
        <div class="row-title">${ctx.esc(transaction.counterparty || '—')}</div>
        <div class="row-sub">${transaction.bookingDate ? ctx.date(transaction.bookingDate) : ''}${transaction.category ? ` · ${ctx.esc(transaction.category)}` : ''}</div>
      </div>
      <div class="${moneyClass(MoneyVariant.Neutral)}">${ctx.money(-Math.abs(Number(transaction.amount || 0)), transaction.currency || currency)}</div>
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
      <div class="kv"><span>${ctx.esc(ctx.get('budgets.remaining'))}</span><strong class="${moneyClass(Number(budgetStatus.remaining) < 0 ? MoneyVariant.Danger : MoneyVariant.Neutral)}">${ctx.money(budgetStatus.remaining, currency)}</strong></div>
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

  // Visible: what a budget IS - a name, an amount, how often, and for what. The anchor date, the end
  // date and the carry-over are settings you touch once, so they sit in the disclosure.
  //
  // The catch is that two of the hidden ones become REQUIRED for some periods, and a required field
  // inside a collapsed <details> cannot be focused: the browser then refuses to submit and reports
  // nothing a user can act on. So the disclosure opens itself as soon as the chosen period needs a
  // date - hidden is allowed to mean "not relevant yet", never "relevant but unreachable".
  const handles = openFormDialog({
    title: ctx.get(existing ? 'budgets.edit' : 'budgets.new'),
    closeLabel: ctx.get('common.close'),
    advancedLabel: bilingual('Weitere Einstellungen', 'More settings'),
    fallbackError: ctx.get('common.error'),
    className: 'budget-wizard',
    create: html => ctx.dialog(html),
    fields: [
      { name: 'name', kind: FieldKind.Text, label: ctx.get('common.name'), required: true, maxLength: 120 },
      { name: 'amount', kind: FieldKind.Money, label: ctx.get('transactions.amount'), required: true, min: '0.01', group: 'sum' },
      { name: 'currency', kind: FieldKind.Text, label: ctx.get('purchases.currency'), required: true, minLength: 3, maxLength: 3, group: 'sum' },
      { name: 'period', kind: FieldKind.Select, label: ctx.get('budgets.period'), rawOptions: periods },
      { name: 'category', kind: FieldKind.Select, label: ctx.get('transactions.category'),
        rawOptions: `<option value="">${ctx.esc(ctx.get('common.all'))}</option>${options}` },
      { name: 'startDate', kind: FieldKind.Date, label: ctx.get('budgets.anchorDate'), advanced: true, group: 'cycle',
        hint: ctx.get('budgets.anchorHint_week') },
      { name: 'endDate', kind: FieldKind.Date, label: ctx.get('budgets.endDate'), advanced: true, group: 'cycle' },
      // 'reset' is the default, so counting it would make an untouched form announce a setting nobody made.
      { name: 'rollover', kind: FieldKind.Select, label: ctx.get('budgets.rollover'), advanced: true,
        rawOptions: rolloverOptions, emptyValue: 'reset', hint: ctx.get('budgets.rolloverHint_' + rollover) }
    ],
    values: {
      name: existing?.name || '',
      amount: existing ? String(existing.amount) : '',
      currency,
      period: selectedPeriod,
      category: existing?.categoryId || '',
      startDate: existing?.startDate || '',
      endDate: existing?.endDate || '',
      rollover
    },
    actions: [
      ...(existing ? [{ name: 'delete', label: ctx.get('common.delete'), role: 'danger', onClick: () => remove() }] : []),
      { name: 'cancel', label: ctx.get('common.cancel'), role: 'secondary', onClick: ({ close }) => close() },
      { name: 'save', label: ctx.get(existing ? 'common.save' : 'common.create'), role: 'primary', submit: true }
    ],
    onSubmit: ({ values, setFormError, close }) => save(values, setFormError, close)
  });

  const form = handles.form;
  const periodSelect = form.elements.namedItem('period');
  const rolloverSelect = form.elements.namedItem('rollover');
  const startInput = form.elements.namedItem('startDate');
  const nameInput = form.elements.namedItem('name');
  const disclosure = form.querySelector('details');
  const hintOf = name => handles.field(name)?.querySelector('.fw-field-hint');

  const localIso = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
  const mondayIso = () => {
    const date = new Date();
    date.setHours(12, 0, 0, 0);
    date.setDate(date.getDate() - ((date.getDay() + 6) % 7));
    return localIso(date);
  };

  const syncCycleFields = () => {
    const period = periodSelect.value;
    const needsAnchor = USES_ANCHOR.includes(period);
    const needsEnd = period === 'custom';
    handles.field('startDate').hidden = !needsAnchor;
    handles.field('endDate').hidden = !needsEnd;
    startInput.required = needsEnd;
    form.elements.namedItem('endDate').required = needsEnd;
    // See above: a required field the user cannot reach is worse than a long form.
    if (needsAnchor || needsEnd) disclosure?.setAttribute('open', '');
    const hint = hintOf('startDate');
    if (hint) hint.textContent = ctx.get(
      period === 'paycycle'
        ? 'budgets.anchorHint_paycycle'
        : needsEnd
          ? 'budgets.anchorHint_custom'
          : 'budgets.anchorHint_week');
    if (period === 'paycycle' && !startInput.value) startInput.value = localIso(new Date());
  };

  const syncRolloverHint = () => {
    const hint = hintOf('rollover');
    if (hint) hint.textContent = ctx.get('budgets.rolloverHint_' + rolloverSelect.value);
  };

  periodSelect.addEventListener('change', syncCycleFields);
  rolloverSelect.addEventListener('change', syncRolloverHint);

  // The presets belong above the fields they fill, which is before everything the primitive renders.
  if (presets) {
    form.querySelector('.panel-head')?.insertAdjacentHTML('afterend', presets);
    form.querySelectorAll('[data-budget-preset]').forEach(button => {
      button.addEventListener('click', () => {
        const preset = button.dataset.budgetPreset;
        if (preset === 'weekly-groceries') {
          if (!nameInput.value) nameInput.value = ctx.get('budgets.presetName_weeklyGroceries');
          periodSelect.value = 'weekly';
          rolloverSelect.value = 'positive';
          startInput.value = mondayIso();
        } else if (preset === 'paycycle') {
          if (!nameInput.value) nameInput.value = ctx.get('budgets.presetName_paycycle');
          periodSelect.value = 'paycycle';
          rolloverSelect.value = 'full';
          startInput.value = localIso(new Date());
        } else {
          if (!nameInput.value) nameInput.value = ctx.get('budgets.presetName_monthly');
          periodSelect.value = 'monthly';
          rolloverSelect.value = 'reset';
        }
        syncCycleFields();
        syncRolloverHint();
        form.elements.namedItem('amount').focus();
      });
    });
  }

  syncCycleFields();
  syncRolloverHint();

  async function remove() {
    if (!await ctx.confirm(
      ctx.get('budgets.deleteConfirm').replace('{name}', existing.name),
      { destructive: true, confirmLabel: ctx.get('common.delete') })) return;

    try {
      await ctx.api(`api/budgets/${existing.id}`, { method: 'DELETE' });
      handles.close('deleted');
      ctx.toast(ctx.get('common.deleted'));
      await renderBudgets(ctx);
    } catch (error) {
      handles.setFormError(error.message || ctx.get('common.error'));
    }
  }

  async function save(values, setFormError, close) {
    const period = String(values.period || 'monthly');
    const rolloverMode = String(values.rollover || 'reset');
    // A hidden date still holds whatever a previous period put there, so the period decides what is
    // sent - otherwise switching from "custom" to "monthly" silently keeps an end date the screen no
    // longer shows.
    const usesAnchor = USES_ANCHOR.includes(period);
    const body = ctx.jsonBody({
      name: values.name,
      categoryId: values.category || null,
      amount: values.amount,
      currency: String(values.currency || 'EUR').toUpperCase(),
      period,
      carryOver: rolloverMode !== 'reset',
      carryOverOverspend: rolloverMode === 'full',
      isActive: true,
      startDate: usesAnchor ? (values.startDate || null) : null,
      endDate: period === 'custom' ? (values.endDate || null) : null
    });

    try {
      await ctx.api(
        existing ? `api/budgets/${existing.id}` : 'api/budgets',
        existing ? { ...body, method: 'PUT' } : body);
      close('saved');
      ctx.toast(ctx.get('common.saved'));
      await renderBudgets(ctx);
    } catch (error) {
      setFormError(error.message || ctx.get('common.error'));
    }
  }
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
