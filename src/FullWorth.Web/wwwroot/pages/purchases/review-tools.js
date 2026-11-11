// Was ein Beleg in der Pruefansicht ausser seinen Positionen noch kann: Amazon-Zahlungen und Retouren,
// eine Bankbuchung verknuepfen, den Beleg in Paperless ablegen, die eigene Preishistorie neben die
// Positionen stellen. Aus page.js hierher gezogen, damit die Pruefansicht (review.js) und die Liste
// (page.js) einander nicht importieren muessen. Jede Aenderung meldet sich mit onChanged() - die Ansicht
// laedt den Beleg dann neu.
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { confirmDialog } from '../../components/confirm.js';

let ctx = null;

export function bindReviewTools(context) { ctx = context; }

const t = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en;

// Der Knopf steht von Anfang an im Dialog, nur unsichtbar: ob Paperless verbunden ist und der Beleg
// abgelegt werden kann, kommt erst mit einer Antwort, und die darf nichts verschieben.
// Abgelegt wird nie von selbst - der Beleg verlaesst FullWorth erst nach der Bestaetigung hier.
export async function bindPaperlessUpload(dlg, purchaseId) {
  const button = dlg.querySelector('[data-paperless-upload]');
  const path = `api/purchases/receipt-imports/paperless/uploads/${purchaseId}`;
  const status = await ctx.api(path);
  if (!button?.isConnected || !status?.paperlessConfigured) return;
  if (status.uploadedAt) {
    button.textContent = t('In Paperless abgelegt', 'Stored in Paperless');
    button.disabled = true;
    button.classList.add('is-ready');
    return;
  }
  if (!status.canUpload) return;
  button.classList.add('is-ready');
  button.addEventListener('click', async () => {
    const confirmed = await confirmDialog(ctx,
      t('Der Beleg wird als ein PDF an deine Paperless-Instanz gesendet - mit Händler, Datum und Betrag im Titel. Das lässt sich in FullWorth nicht zurücknehmen.',
        'The receipt is sent to your Paperless instance as one PDF, with merchant, date and amount in the title. FullWorth cannot undo this.'),
      { title: t('In Paperless ablegen', 'Store in Paperless'), confirmLabel: t('Ablegen', 'Store') });
    if (!confirmed) return;
    button.disabled = true;
    try {
      await ctx.api(path, { method: 'POST' });
      button.textContent = t('In Paperless abgelegt', 'Stored in Paperless');
      ctx.toast(t('Beleg in Paperless abgelegt.', 'Receipt stored in Paperless.'));
    } catch (err) {
      button.disabled = false;
      ctx.toast(err.message || ctx.get('common.error'));
    }
  });
}

export async function loadPriceHistory(dlg, purchase) {
  const items = (purchase.items || [])
    .map((item, index) => ({ item, index }))
    .filter(x => x.item?.id && x.item?.barcode)
    .slice(0, 20);

  await Promise.all(items.map(async ({ item, index }) => {
    const target = dlg.querySelector(`[data-price-history-index="${index}"]`);
    if (!target) return;

    try {
      const result = await ctx.api(
        `api/intelligence/prices/purchase-items/${encodeURIComponent(item.id)}`);
      if (!result) return;

      const local = result.local || {};
      if (Number(local.count || 0) > 0 && local.median != null) {
        target.innerHTML = `<span>${ctx.esc(
          `${t('Eigene Historie', 'Your history')}: ` +
          `${t('Median', 'median')} ${ctx.money(Number(local.median), item.currency || purchase.currency)} ` +
          `· ${Number(local.count)} ${t('Käufe', 'purchases')}`)}</span>`;
      }
    } catch {
      // Der Preisverlauf ist Beiwerk und darf die Kaufdetails nie scheitern lassen.
    }
  }));
}

export function amazonReconciliation(purchase, rec, amazon) {
  const payments = amazon.payments || [];
  const bankAllocated = payments.reduce((sum, x) => sum + Number(x.allocatedAmount || 0), 0);
  const nonBank = Number(amazon.nonBankPaymentAmount || 0);
  const itemTotal = rec?.itemTotal ?? (purchase.items || []).reduce((sum, x) => sum + Number(x.totalPrice || 0), 0);
  return {
    ...(rec || {}),
    purchaseTotal: Number(purchase.totalAmount || 0),
    itemTotal,
    itemDifference: Number(purchase.totalAmount || 0) - itemTotal,
    linkedPaymentTotal: bankAllocated,
    paymentDifference: bankAllocated + nonBank - Number(purchase.totalAmount || 0)
  };
}

export function amazonDetailsHtml(purchase, amazon) {
  const payments = amazon.payments || [];
  const refunds = amazon.refunds || [];
  const status = amazon.externalStatus ? amazonStatusLabel(amazon.externalStatus) : t('Status unbekannt', 'Status unknown');
  const paymentRows = payments.length ? payments.map(p => {
    const allocation = Number(p.allocatedAmount || 0);
    const bankAmount = Math.abs(Number(p.amount || 0));
    const shared = Math.abs(allocation - bankAmount) > 0.01 ? ` · ${t('von', 'of')} ${ctx.money(bankAmount, purchase.currency)}` : '';
    const confidence = p.matchConfidence == null ? t('manuell', 'manual') : Math.round(Number(p.matchConfidence) * 100) + '%';
    return `<div class="row" data-amazon-payment="${p.transactionId}"><div class="row-main"><div class="row-title">${ctx.esc(p.counterparty || 'Amazon')}</div><div class="row-sub">${ctx.esc(ctx.date(p.bookingDate))} · ${confidence}${shared}</div></div><div class="row-side"><span class="amount">${ctx.money(allocation, purchase.currency)}</span><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-unlink-amazon-payment>${t('Lösen', 'Unlink')}</button></div></div>`;
  }).join('') : `<div class="row-sub">${t('Noch keine Bankbuchung zugeordnet.', 'No bank transaction linked yet.')}</div>`;
  const nonBank = Number(amazon.nonBankPaymentAmount || 0);
  const nonBankSource = amazon.nonBankPaymentSource === 'manual' ? t('manuell', 'manual') : t('Amazon erkannt', 'detected by Amazon');
  const refundRows = refunds.length ? refunds.map(r => {
    const confidence = r.matchConfidence == null ? '' : ` · ${Math.round(Number(r.matchConfidence) * 100)}%`;
    const action = r.transactionId
      ? `<button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-unlink-amazon-refund>${t('Lösen', 'Unlink')}</button>`
      : `<button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-link-amazon-refund>${t('Buchung verknüpfen', 'Link transaction')}</button>`;
    return `<div data-amazon-refund="${r.id}"><div class="row"><div class="row-main"><div class="row-title">${ctx.esc(r.description || t('Amazon-Erstattung', 'Amazon refund'))}</div><div class="row-sub">${ctx.esc(ctx.date(r.refundDate))} · ${r.transactionId ? t('Bankbuchung verknüpft', 'Bank transaction linked') : t('Noch nicht verknüpft', 'Not linked yet')}${confidence}</div></div><div class="row-side"><span class="amount">${ctx.money(Number(r.amount || 0), r.currency || purchase.currency)}</span>${action}</div></div><div data-amazon-refund-candidates></div></div>`;
  }).join('') : `<div class="row-sub">${t('Keine Retouren oder Erstattungen erkannt.', 'No returns or refunds detected.')}</div>`;
  return `<section class="purchase-amazon" data-amazon-details>
    <h3 class="notif-h">Amazon</h3>
    <div class="row"><div class="row-main"><div class="row-title">${ctx.esc(status)}</div><div class="row-sub">${ctx.esc(purchase.externalOrderId || '')}</div></div>${purchase.sourceReference ? `<a class="${buttonClass(ButtonRole.Secondary)}" href="${ctx.esc(purchase.sourceReference)}" target="_blank" rel="noopener noreferrer">Amazon</a>` : ''}</div>
    <div class="row-group">${t('Zahlungen', 'Payments')}</div>${paymentRows}
    <div class="reconcile-link"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-add-amazon-payment>${t('Weitere Buchung verknüpfen', 'Link another transaction')}</button><div data-amazon-candidates></div></div>
    <div class="row"><div class="row-main"><div class="row-title">${t('Guthaben / Geschenkgutschein', 'Balance / gift card')}</div><div class="row-sub">${nonBankSource}</div></div><div class="row-side"><input data-amazon-nonbank type="number" min="0" max="${Number(purchase.totalAmount || 0)}" step="0.01" value="${nonBank.toFixed(2)}"><button type="button" class="${buttonClass(ButtonRole.Primary)}" data-save-amazon-nonbank>${t('Speichern', 'Save')}</button></div></div>
    <div class="row-group">${t('Retouren / Erstattungen', 'Returns / refunds')}</div>${refundRows}
  </section>`;
}

export function bindAmazonDetails(dlg, purchase, onChanged) {
  dlg.querySelector('[data-save-amazon-nonbank]')?.addEventListener('click', async () => {
    const input = dlg.querySelector('[data-amazon-nonbank]');
    const amount = Number(input?.value || 0);
    if (!Number.isFinite(amount) || amount < 0 || amount > Number(purchase.totalAmount || 0) + 0.01) return;
    try {
      await ctx.api(`api/purchases/${purchase.id}/amazon-nonbank-payment`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ amount }) });
      await onChanged();
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });

  dlg.querySelector('[data-add-amazon-payment]')?.addEventListener('click', async () => {
    const box = dlg.querySelector('[data-amazon-candidates]');
    box.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('common.loading'))}</div>`;
    let candidates;
    try { candidates = (await ctx.api(`api/purchases/${purchase.id}/amazon-payment-candidates`)) || []; }
    catch (err) { box.innerHTML = `<div class="row-sub">${ctx.esc(err.message || ctx.get('common.error'))}</div>`; return; }
    if (!candidates.length) { box.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('purchases.noCandidates'))}</div>`; return; }
    box.innerHTML = candidates.map((c, i) => `<div class="row candidate-row" data-i="${i}"><div class="row-main"><div class="row-title">${ctx.esc(c.counterparty || c.description || '—')}</div><div class="row-sub">${ctx.esc(ctx.date(c.bookingDate))} · ${Math.round(Number(c.confidence || 0) * 100)}% · ${t('verfügbar', 'available')} ${ctx.money(c.availableAmount, purchase.currency)}</div></div><div class="row-side"><input data-allocation type="number" min="0.01" max="${Number(c.availableAmount || 0)}" step="0.01" value="${Number(c.suggestedAllocation || 0).toFixed(2)}"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-link-amazon-payment>${ctx.esc(ctx.get('purchases.link'))}</button></div></div>`).join('');
    box.querySelectorAll('.candidate-row').forEach(row => row.querySelector('[data-link-amazon-payment]').addEventListener('click', async () => {
      const candidate = candidates[Number(row.dataset.i)];
      const allocatedAmount = Number(row.querySelector('[data-allocation]')?.value || 0);
      if (!Number.isFinite(allocatedAmount) || allocatedAmount <= 0) return;
      try {
        await ctx.api(`api/purchases/${purchase.id}/amazon-payment-links`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ transactionId: candidate.transactionId, confidence: candidate.confidence ?? null, allocatedAmount }) });
        await onChanged();
      } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
    }));
  });

  dlg.querySelectorAll('[data-amazon-payment]').forEach(row => row.querySelector('[data-unlink-amazon-payment]')?.addEventListener('click', async () => {
    try {
      await ctx.api(`api/purchases/${purchase.id}/amazon-payment-links/${row.dataset.amazonPayment}`, { method: 'DELETE' });
      await onChanged();
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  }));

  dlg.querySelectorAll('[data-amazon-refund]').forEach(refundBlock => {
    const refundId = refundBlock.dataset.amazonRefund;
    refundBlock.querySelector('[data-link-amazon-refund]')?.addEventListener('click', async () => {
      const box = refundBlock.querySelector('[data-amazon-refund-candidates]');
      box.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('common.loading'))}</div>`;
      let candidates;
      try { candidates = (await ctx.api(`api/purchases/${purchase.id}/amazon-refunds/${refundId}/candidates`)) || []; }
      catch (err) { box.innerHTML = `<div class="row-sub">${ctx.esc(err.message || ctx.get('common.error'))}</div>`; return; }
      if (!candidates.length) { box.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('purchases.noCandidates'))}</div>`; return; }
      box.innerHTML = candidates.map((c, i) => `<div class="row candidate-row" data-i="${i}"><div class="row-main"><div class="row-title">${ctx.esc(c.counterparty || c.description || '—')}</div><div class="row-sub">${ctx.esc(ctx.date(c.bookingDate))} · ${Math.round(Number(c.confidence || 0) * 100)}%</div></div><div class="row-side"><span class="amount">${ctx.money(c.amount, purchase.currency)}</span><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-confirm-amazon-refund>${ctx.esc(ctx.get('purchases.link'))}</button></div></div>`).join('');
      box.querySelectorAll('.candidate-row').forEach(row => row.querySelector('[data-confirm-amazon-refund]').addEventListener('click', async () => {
        const candidate = candidates[Number(row.dataset.i)];
        try {
          await ctx.api(`api/purchases/${purchase.id}/amazon-refunds/${refundId}/link`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ transactionId: candidate.transactionId, confidence: candidate.confidence ?? null }) });
          await onChanged();
        } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
      }));
    });

    refundBlock.querySelector('[data-unlink-amazon-refund]')?.addEventListener('click', async () => {
      try {
        await ctx.api(`api/purchases/${purchase.id}/amazon-refunds/${refundId}/link`, { method: 'DELETE' });
        await onChanged();
      } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
    });
  });
}

// Die Bankbuchung des Belegs: verknuepft, oder - wenn es noch keine gibt - der Weg zu einer
// (automatisch oder aus den Kandidaten). Die vier Summen, die dieser Block frueher zeigte, stehen jetzt
// auf dem Bon selbst.
export function renderLink(box, purchase, rec, onChanged) {
  const cur = purchase.currency;
  const paid = rec ? Number(rec.linkedPaymentTotal || 0) : 0;
  const offerLink = !purchase.transactionId && !paid && purchase.source !== 'amazon';
  box.innerHTML = `<div class="rv-sum-row"><span>${ctx.esc(ctx.get('purchases.bankAmount'))}</span><b>${ctx.esc(paid ? ctx.money(Math.abs(paid), cur) : ctx.get('purchases.notLinked'))}</b></div>`
    + (offerLink ? `<div class="rv-bank-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-auto-link>${ctx.esc(ctx.get('purchases.autoLink'))}</button><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-load-candidates>${ctx.esc(ctx.get('purchases.linkTransaction'))}</button></div><div data-candidates></div>` : '');
  if (!offerLink) return;

  const candidatesBox = box.querySelector('[data-candidates]');
  box.querySelector('[data-auto-link]').addEventListener('click', async () => {
    try {
      const res = await ctx.api(`api/purchases/${purchase.id}/auto-link`, { method: 'POST' });
      if (res && res.linked) { ctx.toast(ctx.get('common.saved')); await onChanged(); }
      else ctx.toast(ctx.get('purchases.noAutoMatch'));
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });
  box.querySelector('[data-load-candidates]').addEventListener('click', async () => {
    candidatesBox.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('common.loading'))}</div>`;
    let candidates;
    try { candidates = (await ctx.api(`api/purchases/${purchase.id}/match-candidates`)) || []; }
    catch (err) { candidatesBox.innerHTML = `<div class="row-sub">${ctx.esc(err.message || ctx.get('common.error'))}</div>`; return; }
    if (!candidates.length) { candidatesBox.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('purchases.noCandidates'))}</div>`; return; }
    candidatesBox.innerHTML = `<div class="row-group">${ctx.esc(ctx.get('purchases.candidates'))}</div>` + candidates.map((c, i) =>
      `<div class="row candidate-row" data-i="${i}"><div class="row-main"><div class="row-title">${ctx.esc(c.counterparty || c.description || '—')}</div><div class="row-sub">${ctx.esc(ctx.date(c.bookingDate))} · ${Math.round((c.confidence || 0) * 100)}%</div></div><div class="row-side"><span class="amount">${ctx.money(c.amount, cur)}</span><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-link-btn>${ctx.esc(ctx.get('purchases.link'))}</button></div></div>`).join('');
    candidatesBox.querySelectorAll('.candidate-row').forEach(row => row.querySelector('[data-link-btn]').addEventListener('click', async () => {
      const c = candidates[Number(row.dataset.i)];
      try {
        await ctx.api(`api/purchases/${purchase.id}/link`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ transactionId: c.id, confidence: c.confidence ?? null }) });
        await onChanged();
      } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
    }));
  });
}

function amazonStatusLabel(status) {
  const labels = {
    cancelled: t('Storniert', 'Cancelled'), return: t('Retoure', 'Return'), delivered: t('Zugestellt', 'Delivered'),
    shipped: t('Versandt', 'Shipped'), ordered: t('Bestellt', 'Ordered')
  };
  return labels[status] || status;
}

