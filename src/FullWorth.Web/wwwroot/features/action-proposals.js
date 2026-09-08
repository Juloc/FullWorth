import { confirmDialog } from '../ui/confirm.js';

function pick(value, ...keys) {
  if (!value || typeof value !== 'object') return undefined;
  for (const key of keys) if (value[key] !== undefined && value[key] !== null) return value[key];
}

function tr(ctx, key, fallback) {
  const value = ctx.get(key);
  return value === key ? fallback : value;
}

function money(ctx, value, currency) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) return '';
  return ctx.isPrivate() ? '••••' : ctx.money(numeric, currency || 'EUR');
}

function transferIds(signal) {
  if (signal?.subjectType !== 'transaction-pair') return [];
  const evidence = signal.evidence || {};
  const first = pick(pick(evidence, 'first', 'First'), 'transactionId', 'TransactionId', 'id', 'Id');
  const second = pick(pick(evidence, 'second', 'Second'), 'transactionId', 'TransactionId', 'id', 'Id');
  const ids = [first, second].filter(Boolean).map(String);
  if (new Set(ids).size === 2) return ids;
  return [...new Set(String(signal.subjectId || '').split(':').map(value => value.trim()).filter(Boolean))].slice(0, 2);
}

function transferPreviewHtml(ctx, proposal) {
  const preview = proposal?.preview || {};
  const first = preview.first || {};
  const second = preview.second || {};
  const executed = proposal?.state === 'executed';

  const leg = item => '<div class="action-proposal-leg">' +
    '<span><strong>' + ctx.esc(item.account || '—') + '</strong><small>' +
    ctx.esc(item.date ? ctx.date(item.date) : '') + '</small></span>' +
    '<strong>' + ctx.esc(money(ctx, item.amount, item.currency)) + '</strong></div>';

  return '<div class="action-proposal-card">' +
    '<div class="action-proposal-head"><strong>' +
    ctx.esc(tr(ctx, 'insights.transferAction.title', 'Transfer suggestion')) + '</strong><span>' +
    ctx.esc(executed
      ? tr(ctx, 'insights.transferAction.done', 'These bookings are already linked as a transfer.')
      : tr(ctx, 'insights.transferAction.preview', 'Both bookings would be linked as one internal transfer.')) +
    '</span></div>' +
    '<div class="action-proposal-legs">' + leg(first) + leg(second) + '</div>' +
    (executed ? '' : '<button type="button" class="btn btn-primary action-proposal-execute" data-action-proposal-execute>' +
      ctx.esc(tr(ctx, 'insights.transferAction.execute', 'Link as transfer')) + '</button>') +
    '</div>';
}

function staleHtml(ctx) {
  return '<div class="action-proposal-stale"><strong>' +
    ctx.esc(tr(ctx, 'insights.actionProposal.staleTitle', 'Preview is outdated')) +
    '</strong><span>' +
    ctx.esc(tr(ctx, 'insights.actionProposal.stale', 'The underlying finance data changed. Review the refreshed preview.')) +
    '</span><button type="button" class="ghost" data-action-proposal-review>' +
    ctx.esc(tr(ctx, 'insights.actionProposal.review', 'Review updated preview')) +
    '</button></div>';
}

export async function mountTransferActionProposal(ctx, signal, target, detailDialog, refreshInsight) {
  const ids = transferIds(signal);
  if (!target || ids.length !== 2) return;

  target.hidden = false;
  target.innerHTML = '<div class="row-sub">' +
    ctx.esc(tr(ctx, 'insights.actionProposal.loading', 'Preparing action preview…')) + '</div>';

  let response;
  try {
    response = await ctx.api('api/action-proposals', ctx.jsonBody({
      handler: 'transfer-link',
      payload: {
        firstTransactionId: ids[0],
        secondTransactionId: ids[1]
      },
      source: 'insight',
      sourceReference: String(signal.id)
    }));
  } catch (error) {
    if (!target.isConnected) return;
    if (error?.status === 404 || error?.status === 403) {
      target.hidden = true;
      target.innerHTML = '';
      return;
    }
    target.innerHTML = '<div class="row-sub">' +
      ctx.esc(tr(ctx, 'insights.actionProposal.error', 'Action preview could not be loaded.')) + '</div>';
    return;
  }

  if (!target.isConnected) return;
  let proposal = response?.proposal;
  if (!proposal) {
    target.hidden = true;
    target.innerHTML = '';
    return;
  }

  const render = () => {
    target.innerHTML = transferPreviewHtml(ctx, proposal);
    const execute = target.querySelector('[data-action-proposal-execute]');
    if (!execute || proposal.state === 'executed') return;

    execute.addEventListener('click', async () => {
      const first = proposal.preview?.first || {};
      const second = proposal.preview?.second || {};
      const message = tr(
        ctx,
        'insights.transferAction.confirmMessage',
        'Link the bookings on {first} and {second} as one internal transfer?'
      ).replace('{first}', first.account || '—').replace('{second}', second.account || '—');

      const confirmed = await confirmDialog(ctx, message, {
        title: tr(ctx, 'insights.transferAction.confirmTitle', 'Link as transfer?'),
        confirmLabel: tr(ctx, 'insights.transferAction.confirm', 'Link transfer')
      });
      if (!confirmed || !target.isConnected) return;

      execute.disabled = true;
      try {
        const executed = await ctx.api(
          'api/action-proposals/' + encodeURIComponent(proposal.id) + '/execute',
          ctx.jsonBody({ previewToken: proposal.previewToken })
        );
        proposal = executed?.proposal || proposal;
        await ctx.api('api/insights/' + encodeURIComponent(signal.id) + '/dismiss', { method: 'POST' }).catch(() => {});
        ctx.toast(tr(ctx, 'insights.transferAction.success', 'Bookings linked as transfer.'));
        if (detailDialog?.open) detailDialog.close();
        await refreshInsight();
      } catch (error) {
        if (!target.isConnected) return;
        if (error?.status === 409) {
          const bodyProposal = error?.body?.proposal || error?.data?.proposal;
          if (bodyProposal) proposal = bodyProposal;
          target.innerHTML = staleHtml(ctx);
          target.querySelector('[data-action-proposal-review]')?.addEventListener('click', async () => {
            try {
              const updated = await ctx.api(
                'api/action-proposals/' + encodeURIComponent(proposal.id) + '/preview',
                { method: 'POST' }
              );
              proposal = updated?.proposal || proposal;
              render();
            } catch (previewError) {
              ctx.toast(previewError?.message || tr(ctx, 'common.error', 'Could not load data'));
            }
          });
          return;
        }
        execute.disabled = false;
        ctx.toast(error?.message || tr(ctx, 'common.error', 'Could not save changes'));
      }
    });
  };

  render();
}
