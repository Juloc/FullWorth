import { emitAppEvent } from '../core/event-bus.js';

const VIEW_API = { current: 'active', completed: 'resolved', hidden: 'hidden' };
let dashboardRenderVersion = 0;

function pick(value, ...keys) {
  if (!value || typeof value !== 'object') return undefined;
  for (const key of keys) if (value[key] !== undefined && value[key] !== null) return value[key];
}

function tr(ctx, key, fallback) {
  const value = ctx.get(key);
  return value === key ? fallback : value;
}

function amount(ctx, value, currency) {
  const n = Number(value);
  if (!Number.isFinite(n)) return '';
  return ctx.isPrivate() ? '••••' : ctx.money(n, currency || 'EUR');
}

function percent(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return '';
  return (n > 0 ? '+' : '') + n.toLocaleString(undefined, { maximumFractionDigits: 1 }) + ' %';
}

function title(ctx, signal) {
  return tr(ctx, signal.titleKey, tr(ctx, 'insights.generic', 'Financial insight'));
}

function summary(ctx, signal) {
  const e = signal.evidence || {};
  const currency = signal.impactCurrency || pick(e, 'currency', 'Currency') || 'EUR';
  const pair = (before, after) => {
    const a = amount(ctx, before, currency), b = amount(ctx, after, currency);
    return a && b ? a + ' → ' + b : '';
  };
  switch (signal.titleKey) {
    case 'insights.spending.totalIncreased':
    case 'insights.spending.totalDecreased':
      return [pair(pick(e, 'previous', 'Previous'), pick(e, 'current', 'Current')), percent(pick(e, 'percent', 'Percent'))].filter(Boolean).join(' · ');
    case 'insights.spending.categoryIncreased':
      return [pick(e, 'category', 'Category') || tr(ctx, 'common.uncategorized', 'Uncategorized'), pair(pick(e, 'previous', 'Previous'), pick(e, 'current', 'Current')), percent(pick(e, 'percent', 'Percent'))].filter(Boolean).join(' · ');
    case 'insights.spending.merchantIncreased':
      return [pick(e, 'merchant', 'Merchant'), pair(pick(e, 'previous', 'Previous'), pick(e, 'current', 'Current')), percent(pick(e, 'percent', 'Percent'))].filter(Boolean).join(' · ');
    case 'insights.budget.over':
    case 'insights.budget.projectedOver': {
      const projected = signal.titleKey.endsWith('projectedOver');
      return [pick(e, 'budget', 'Budget'), pair(pick(e, 'target', 'Target'), projected ? pick(e, 'projectedEndSpend', 'ProjectedEndSpend') : pick(e, 'spent', 'Spent'))].filter(Boolean).join(' · ');
    }
    case 'insights.savings.decreased':
    case 'insights.savings.increased':
      return [pair(pick(e, 'previousAverageMonthlySavings', 'PreviousAverageMonthlySavings'), pick(e, 'currentAverageMonthlySavings', 'CurrentAverageMonthlySavings')), percent(pick(e, 'percent', 'Percent'))].filter(Boolean).join(' · ');
    case 'insights.data.uncategorizedRecent': {
      const count = pick(e, 'uncategorizedRecentTransactionCount', 'UncategorizedRecentTransactionCount');
      const total = pick(e, 'recentTransactionCount', 'RecentTransactionCount');
      return tr(ctx, 'insights.uncategorizedCount', '{count} of {total} recent transactions').replace('{count}', String(count ?? '—')).replace('{total}', String(total ?? '—'));
    }
    case 'insights.transfer.candidate': {
      const first = pick(e, 'first', 'First') || {}, second = pick(e, 'second', 'Second') || {};
      const money = amount(ctx, Math.abs(Number(pick(first, 'amount', 'Amount') || signal.impactAmount || 0)), pick(first, 'currency', 'Currency') || currency);
      const a = pick(first, 'account', 'Account') || '', b = pick(second, 'account', 'Account') || '';
      return [money, a && b ? a + ' ↔ ' + b : ''].filter(Boolean).join(' · ');
    }
    case 'insights.contract.paymentAccountChanged':
      return [pick(e, 'provider', 'Provider'), tr(ctx, 'insights.accountChangeSummary', 'Payment history continues on another account')].filter(Boolean).join(' · ');
    case 'insights.contract.possibleDuplicate':
      return [pick(e, 'provider', 'Provider'), tr(ctx, 'insights.continuitySummary', 'Non-overlapping payment histories look continuous')].filter(Boolean).join(' · ');
    case 'insights.contract.priceIncreased':
    case 'insights.contract.priceDecreased':
      return [pick(e, 'contractName', 'ContractName'), pair(pick(e, 'oldAmount', 'OldAmount'), pick(e, 'newAmount', 'NewAmount')), percent(pick(e, 'percentChange', 'PercentChange'))].filter(Boolean).join(' · ');
    case 'insights.data.incomplete':
      return tr(ctx, 'insights.dataIncompleteSummary', 'Some financial components are incomplete.');
    default:
      return signal.impactAmount == null ? '' : tr(ctx, 'insights.impact', 'Impact') + ': ' + amount(ctx, signal.impactAmount, currency);
  }
}

function stateLabel(ctx, signal) {
  if (signal.state === 'dismissed') return tr(ctx, 'insights.state.dismissed', 'Hidden');
  if (signal.state === 'snoozed') return tr(ctx, 'insights.state.snoozed', 'Snoozed');
  if (signal.resolvedAt) return tr(ctx, 'insights.state.completed', 'Completed');
  if (signal.state === 'read') return tr(ctx, 'insights.state.read', 'Read');
  return tr(ctx, 'insights.state.new', 'New');
}

function row(ctx, signal, compact) {
  const s = summary(ctx, signal);
  const meta = compact ? '' : '<span class="insight-meta">' + ctx.esc(stateLabel(ctx, signal)) + ' · ' + ctx.esc(ctx.dateTime(signal.updatedAt || signal.detectedAt)) + '</span>';
  return '<button type="button" class="insight-row insight-' + ctx.esc(signal.severity || 'info') + (signal.state === 'unread' ? ' is-unread' : '') + '" data-insight-id="' + ctx.esc(signal.id) + '">' +
    '<span class="insight-severity" aria-hidden="true"></span><span class="insight-copy"><strong>' + ctx.esc(title(ctx, signal)) + '</strong>' +
    (s ? '<span>' + ctx.esc(s) + '</span>' : '') + meta + '</span><span class="insight-chevron" aria-hidden="true">›</span></button>';
}

async function load(ctx, view, limit, abortSignal) {
  const path = 'api/insights?view=' + encodeURIComponent(VIEW_API[view] || 'active') + '&limit=' + limit;
  return ctx.api(path, abortSignal ? { signal: abortSignal } : undefined);
}

function unavailable(ctx, isError) {
  const key = isError ? 'insights.loadError' : 'insights.unavailable';
  const fallback = isError ? 'Insights could not be loaded.' : 'Insights are currently unavailable.';
  return '<article class="panel insights-panel"><div class="state-empty"><div class="row-sub">' + ctx.esc(tr(ctx, key, fallback)) + '</div></div></article>';
}

export async function renderDashboardInsights(ctx) {
  const renderVersion = ++dashboardRenderVersion;
  const root = ctx.$('#dashboard-insights');
  if (!root) return;
  root.hidden = false;
  root.innerHTML = '<article class="panel insights-panel insights-dashboard"><div class="insights-head"><div><h2>' + ctx.esc(tr(ctx, 'insights.important', 'Important for you')) + '</h2></div></div><div class="state-empty"><div class="row-sub">' + ctx.esc(tr(ctx, 'common.loading', 'Loading…')) + '</div></div></article>';
  let signals;
  try {
    signals = await load(ctx, 'current', 3);
    if (renderVersion !== dashboardRenderVersion) return;
  } catch (error) {
    if (renderVersion !== dashboardRenderVersion) return;
    if (error?.status === 404) {
      root.hidden = true; root.innerHTML = ''; root.onclick = null; return;
    }
    root.innerHTML = unavailable(ctx, true); root.onclick = null; return;
  }

  const body = signals.length
    ? signals.map(signal => row(ctx, signal, true)).join('')
    : '<div class="insights-empty"><strong>' + ctx.esc(tr(ctx, 'insights.emptyTitle', 'Nothing needs your attention')) + '</strong><span>' + ctx.esc(tr(ctx, 'insights.emptyCurrent', 'New relevant changes will appear here.')) + '</span></div>';
  root.innerHTML = '<article class="panel insights-panel insights-dashboard"><div class="insights-head"><div><h2>' + ctx.esc(tr(ctx, 'insights.important', 'Important for you')) + '</h2><span>' + ctx.esc(tr(ctx, 'insights.dashboardHint', 'Changes and open items FullWorth noticed.')) + '</span></div><button type="button" class="ghost insights-all" data-insights-all>' + ctx.esc(tr(ctx, 'insights.showAll', 'Show all')) + '</button></div><div class="insight-list">' + body + '</div></article>';

  root.onclick = event => {
    if (event.target.closest('[data-insights-all]')) { ctx.showView('insights'); return; }
    const hit = event.target.closest('[data-insight-id]');
    if (!hit) return;
    const signal = signals.find(item => item.id === hit.dataset.insightId);
    if (signal) openDetail(ctx, signal, () => renderDashboardInsights(ctx));
  };
}

export async function mountInsights(ctx) {
  const root = ctx.$('#insights-root');
  if (!root) return;
  const controller = new AbortController();
  let view = 'current', signals = [];
  let renderVersion = 0;

  const render = async () => {
    const version = ++renderVersion;
    root.innerHTML = surface(ctx, view, null, true);
    try {
      const result = await load(ctx, view, 100, controller.signal);
      if (controller.signal.aborted || version !== renderVersion) return;
      signals = result;
      root.innerHTML = surface(ctx, view, signals, false);
    } catch (error) {
      if (controller.signal.aborted || version !== renderVersion) return;
      root.innerHTML = '<div class="insights-view-wrap"><div class="insights-back-row"><button type="button" class="ghost" data-insights-back>← ' + ctx.esc(tr(ctx, 'common.back', 'Back')) + '</button></div>' + unavailable(ctx, error?.status !== 404) + '</div>';
    }
  };

  root.onclick = event => {
    if (event.target.closest('[data-insights-back]')) { ctx.showView('dashboard'); return; }
    const tab = event.target.closest('[data-insights-tab]');
    if (tab && VIEW_API[tab.dataset.insightsTab]) { view = tab.dataset.insightsTab; render(); return; }
    const hit = event.target.closest('[data-insight-id]');
    if (!hit) return;
    const signal = signals.find(item => item.id === hit.dataset.insightId);
    if (signal) openDetail(ctx, signal, render);
  };

  await render();
  return () => { controller.abort(); root.onclick = null; };
}

function surface(ctx, selected, signals, loading) {
  const tabs = ['current', 'completed', 'hidden'].map(view =>
    '<button type="button" data-insights-tab="' + view + '" class="' + (selected === view ? 'active' : '') + '" aria-pressed="' + (selected === view) + '">' + ctx.esc(tr(ctx, 'insights.tabs.' + view, view)) + '</button>'
  ).join('');
  let body;
  if (loading) body = '<div class="state-empty"><div class="row-sub">' + ctx.esc(tr(ctx, 'common.loading', 'Loading…')) + '</div></div>';
  else if (signals?.length) body = signals.map(signal => row(ctx, signal, false)).join('');
  else {
    const key = selected === 'current' ? 'insights.emptyCurrent' : selected === 'completed' ? 'insights.emptyCompleted' : 'insights.emptyHidden';
    body = '<div class="insights-empty"><strong>' + ctx.esc(tr(ctx, 'insights.emptyTitle', 'Nothing here')) + '</strong><span>' + ctx.esc(tr(ctx, key, 'No insights in this view.')) + '</span></div>';
  }
  return '<div class="insights-view-wrap"><div class="insights-back-row"><button type="button" class="ghost" data-insights-back>← ' + ctx.esc(tr(ctx, 'common.back', 'Back')) + '</button></div><article class="panel insights-panel"><div class="insights-head insights-view-head"><div><h2>' + ctx.esc(tr(ctx, 'insights.title', 'Insights')) + '</h2><span>' + ctx.esc(tr(ctx, 'insights.subtitle', 'Deterministic signals from your FullWorth data.')) + '</span></div></div><div class="insights-tabs" role="group" aria-label="' + ctx.esc(tr(ctx, 'insights.filterLabel', 'Insight status')) + '">' + tabs + '</div><div class="insight-list">' + body + '</div></article></div>';
}

function targetView(signal) {
  if (signal.subjectType === 'contract' || signal.subjectType === 'contract-pair') return 'contracts';
  if (signal.subjectType === 'budget') return 'budgets';
  if (signal.subjectType === 'transaction-pair' || signal.subjectType === 'recent-transactions') return 'transactions';
  if (signal.subjectType === 'category' || signal.subjectType === 'merchant' || signal.subjectType === 'cashflow') return 'analytics';
  return 'dashboard';
}

function openDetail(ctx, initialSignal, refresh) {
  let signal = initialSignal;
  const s = summary(ctx, signal);
  const hidden = signal.state === 'dismissed' || signal.state === 'snoozed';
  const manage = signal.resolvedAt ? '' :
    '<div class="insight-detail-section"><span class="insight-section-label">' + ctx.esc(tr(ctx, 'insights.manage', 'Manage')) + '</span><div class="insight-detail-actions">' +
    (hidden
      ? '<button type="button" class="ghost" data-action="read">' + ctx.esc(tr(ctx, 'insights.restore', 'Show again')) + '</button>'
      : signal.state !== 'read'
        ? '<button type="button" class="ghost" data-action="read">' + ctx.esc(tr(ctx, 'insights.markRead', 'Mark as read')) + '</button>'
        : '') +
    (!hidden ? '<button type="button" class="ghost" data-action="dismiss">' + ctx.esc(tr(ctx, 'insights.dismiss', 'Hide')) + '</button>' : '') +
    '</div>' +
    (!hidden ? '<div class="insight-snooze"><select data-snooze-days aria-label="' + ctx.esc(tr(ctx, 'insights.snooze', 'Snooze')) + '"><option value="1">' + ctx.esc(tr(ctx, 'insights.snooze1', '1 day')) + '</option><option value="7" selected>' + ctx.esc(tr(ctx, 'insights.snooze7', '7 days')) + '</option><option value="30">' + ctx.esc(tr(ctx, 'insights.snooze30', '30 days')) + '</option></select><button type="button" class="ghost" data-action="snooze">' + ctx.esc(tr(ctx, 'insights.snooze', 'Snooze')) + '</button></div>' : '') +
    '</div>';
  const html = '<form method="dialog" class="dialog-card insight-detail"><div class="panel-head"><div><h2>' + ctx.esc(title(ctx, signal)) + '</h2><div class="row-sub">' + ctx.esc(stateLabel(ctx, signal)) + '</div></div><button value="cancel" data-close aria-label="' + ctx.esc(tr(ctx, 'common.close', 'Close')) + '">×</button></div>' +
    (s ? '<p class="insight-detail-summary">' + ctx.esc(s) + '</p>' : '') +
    '<div class="insight-detail-meta"><span>' + ctx.esc(tr(ctx, 'insights.detected', 'Detected')) + '</span><strong>' + ctx.esc(ctx.dateTime(signal.detectedAt)) + '</strong></div>' +
    manage +
    '<div class="insight-detail-section"><span class="insight-section-label">' + ctx.esc(tr(ctx, 'insights.feedback', 'Was this useful?')) + '</span><div class="insight-feedback"><button type="button" class="ghost ' + (signal.feedback === 'useful' ? 'active' : '') + '" data-feedback="useful" aria-pressed="' + (signal.feedback === 'useful') + '">' + ctx.esc(tr(ctx, 'insights.useful', 'Useful')) + '</button><button type="button" class="ghost ' + (signal.feedback === 'irrelevant' ? 'active' : '') + '" data-feedback="irrelevant" aria-pressed="' + (signal.feedback === 'irrelevant') + '">' + ctx.esc(tr(ctx, 'insights.irrelevant', 'Not relevant')) + '</button></div></div>' +
    '<button type="button" class="insight-open-target" data-open-target>' + ctx.esc(tr(ctx, 'insights.openAffected', 'Open affected area')) + '<span aria-hidden="true">›</span></button></form>';
  const dlg = ctx.dialog(html, { mobileMode: 'sheet' });
  let busy = false;

  const mutate = async (action, payload) => {
    if (busy) return false;
    busy = true;
    dlg.querySelectorAll('button,select').forEach(el => { el.disabled = true; });
    try {
      await ctx.api('api/insights/' + encodeURIComponent(signal.id) + '/' + action, payload === undefined ? { method: 'POST' } : ctx.jsonBody(payload));
      return true;
    } catch (error) {
      ctx.toast(error?.message || tr(ctx, 'common.error', 'Could not load data'));
      return false;
    } finally {
      busy = false;
      dlg.querySelectorAll('button,select').forEach(el => { el.disabled = false; });
    }
  };

  dlg.querySelector('[data-open-target]')?.addEventListener('click', async () => {
    dlg.close();
    const view = targetView(signal);
    await ctx.showView(view, { query: '' });
    if (signal.subjectType === 'contract') emitAppEvent('contract:open', { id: signal.subjectId });
    if (signal.subjectType === 'budget') emitAppEvent('budget:open', { id: signal.subjectId });
  });
  dlg.querySelectorAll('[data-action]').forEach(button => button.addEventListener('click', async () => {
    const action = button.dataset.action;
    let payload;
    if (action === 'snooze') {
      const days = Math.max(1, Math.min(365, Number(dlg.querySelector('[data-snooze-days]')?.value) || 7));
      payload = { until: new Date(Date.now() + days * 86400000).toISOString() };
    }
    if (!await mutate(action, payload)) return;
    dlg.close();
    await refresh();
  }));
  dlg.querySelectorAll('[data-feedback]').forEach(button => button.addEventListener('click', async () => {
    const feedback = button.dataset.feedback;
    if (!await mutate('feedback', { feedback })) return;
    signal = { ...signal, feedback };
    dlg.querySelectorAll('[data-feedback]').forEach(item => {
      const active = item.dataset.feedback === feedback;
      item.classList.toggle('active', active);
      item.setAttribute('aria-pressed', String(active));
    });
    ctx.toast(tr(ctx, 'insights.feedbackSaved', 'Feedback saved.'));
  }));
  dlg.showModal();
}
