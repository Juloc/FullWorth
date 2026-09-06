import { bindGptReceiptTest } from './purchases-gpt-test.js';
import { tryGptReceiptScan } from './purchases-gpt-normal.js';
import { identityIcon, ensureOfficialBrandCatalog } from '../ui/ux-kit.js';

// Purchases & receipts (UI_UX_SPEC §16). Amazon orders use the same Purchase/PurchaseItem model as
// scanned receipts. The Amazon connector only supplies source data; review, categories and bank
// reconciliation remain one shared FullWorth flow.

let ctx = null;

export function bindPurchases(context) {
  ctx = context;
  ctx.$('#purchase-source').addEventListener('change', () => renderPurchases(ctx));
  ctx.$('#scan-receipt').addEventListener('click', () => ctx.$('#receipt-file').click());
  ctx.$('#receipt-file').addEventListener('change', scanReceipt);
  ctx.$('#amazon-import').addEventListener('click', openAmazonConnection);
  refreshAmazonButton().catch(() => {});
  bindGptReceiptTest(ctx, () => renderPurchases(ctx));
}

export async function renderPurchases(context) {
  ctx = context;
  await ensureOfficialBrandCatalog(ctx.api);
  const source = ctx.$('#purchase-source').value;
  const q = source ? `?source=${encodeURIComponent(source)}` : '';
  const rows = (await ctx.api(`api/purchases${q}`)) || [];
  const el = ctx.$('#purchases-list');
  el.innerHTML = '';
  await refreshAmazonButton().catch(() => {});
  if (!rows.length) { el.innerHTML = emptyRow(); return; }

  const needsReview = rows.filter(r => r.status !== 'confirmed' || (r.source !== 'amazon' && !r.transactionId));
  const reviewIds = new Set(needsReview.map(r => r.id));
  const recent = rows.filter(r => !reviewIds.has(r.id));

  const frag = document.createDocumentFragment();
  frag.appendChild(summaryHeader(rows, needsReview.length));
  if (needsReview.length) section(frag, ctx.get('purchases.needsReview'), needsReview);
  if (recent.length) section(frag, ctx.get('purchases.recent'), recent);
  frag.appendChild(merchantSummary(rows));
  el.appendChild(frag);
}

// Concise summary strip (Finanzguru-style header): total spend, purchase count and — only when
// something is pending — an attention-tinted "needs review" tile. Amounts use tabular numerals via
// the shared .amount class. Naive cross-currency sum matches the merchant summary below.
function summaryHeader(rows, needsReviewCount) {
  const total = rows.reduce((sum, r) => sum + Number(r.totalAmount || 0), 0);
  const cur = rows[0]?.currency || 'EUR';
  const wrap = document.createElement('div');
  wrap.className = 'purchase-summary';
  wrap.innerHTML =
    `<div class="purchase-stat"><span class="purchase-stat-k">${ctx.esc(ctx.get('purchases.total'))}</span><span class="purchase-stat-v amount">${ctx.money(total, cur)}</span></div>` +
    `<div class="purchase-stat"><span class="purchase-stat-k">${ctx.esc(ctx.get('purchases.title'))}</span><span class="purchase-stat-v">${rows.length}</span></div>` +
    (needsReviewCount ? `<div class="purchase-stat purchase-stat--review"><span class="purchase-stat-k">${ctx.esc(ctx.get('purchases.needsReview'))}</span><span class="purchase-stat-v">${needsReviewCount}</span></div>` : '');
  return wrap;
}

// A word-label pill hung on a purchase name (Design System §10, matching the transactions markers):
// amber "Prüfen" while a purchase is unconfirmed, neutral "Nicht verknüpft" while a confirmed receipt
// still has no bank booking. Confirmed & linked purchases carry no pill, keeping the recent list calm.
function attentionTag(x) {
  if (x.status !== 'confirmed')
    return `<span class="purchase-tag purchase-tag--review">${ctx.esc(ctx.get('purchases.review'))}</span>`;
  const linked = x.source === 'amazon' ? true : !!x.transactionId;
  if (!linked)
    return `<span class="purchase-tag purchase-tag--unlinked">${ctx.esc(ctx.get('purchases.unlinked'))}</span>`;
  return '';
}

function section(parent, title, rows) {
  const head = document.createElement('div');
  head.className = 'row-group';
  head.textContent = `${title} (${rows.length})`;
  parent.appendChild(head);
  for (const x of rows) {
    const itemCount = (x.items || []).length;
    const source = x.source === 'amazon' ? 'Amazon' : x.source;
    const order = x.source === 'amazon' && x.externalOrderId ? ` · ${x.externalOrderId}` : '';
    const name = x.merchant || x.externalOrderId || ctx.get('purchases.receipt');
    const row = document.createElement('button');
    row.type = 'button';
    row.className = 'purchase-row';
    // Finanzguru-style identity row: merchant monogram / category icon, name with an attention pill,
    // a calm sub-line (date · source · positions), and the total right-aligned with tabular numerals.
    row.innerHTML =
      `<span class="purchase-ident">${identityIcon(name, { categoryIconKey: x.categoryIconKey })}</span>` +
      `<span class="row-main"><span class="purchase-title"><span class="purchase-name">${ctx.esc(name)}</span>${attentionTag(x)}</span>` +
      `<span class="row-sub">${ctx.esc(ctx.date(x.purchaseDate))} · ${ctx.esc(source)}${ctx.esc(order)} · ${itemCount} ${ctx.esc(ctx.get('purchases.items'))}</span></span>` +
      `<span class="amount">${ctx.money(x.totalAmount, x.currency)}</span>`;
    row.addEventListener('click', () => openDetail(x.id));
    parent.appendChild(row);
  }
}

function merchantSummary(rows) {
  const wrap = document.createElement('div');
  wrap.className = 'purchase-merchants';
  const byMerchant = new Map();
  for (const r of rows) {
    const key = r.merchant || ctx.get('purchases.receipt');
    byMerchant.set(key, (byMerchant.get(key) || 0) + Number(r.totalAmount || 0));
  }
  const top = [...byMerchant.entries()].sort((a, b) => b[1] - a[1]).slice(0, 6);
  const cur = rows[0]?.currency || 'EUR';
  const max = top.length ? top[0][1] : 0;
  const head = document.createElement('div');
  head.className = 'row-group';
  head.textContent = ctx.get('purchases.byMerchant');
  wrap.appendChild(head);
  // Ranked merchants with an identity monogram and a monochrome share bar (proportion of the top
  // spender) so the biggest merchants read at a glance without introducing any non-neutral hue.
  wrap.insertAdjacentHTML('beforeend', top.map(([m, total]) => {
    const pct = max > 0 ? Math.max(4, Math.round((total / max) * 100)) : 0;
    return `<div class="purchase-mrow"><span class="purchase-ident">${identityIcon(m, {})}</span>` +
      `<span class="purchase-mrow-main"><span class="purchase-mrow-top"><span class="purchase-name">${ctx.esc(m)}</span><span class="amount">${ctx.money(total, cur)}</span></span>` +
      `<span class="purchase-bar"><span style="width:${pct}%"></span></span></span></div>`;
  }).join(''));
  return wrap;
}

async function scanReceipt() {
  const input = ctx.$('#receipt-file');
  const file = input.files && input.files[0];
  if (!file) return;
  try {
    // GPT is now the normal preferred extractor whenever the current user/FullWorth Space has an
    // active Codex login. If it is unavailable or returns no usable result, preserve the existing
    // OCR/manual fallback exactly as before. The explicit GPT test console remains for debugging.
    const gptPurchase = await tryGptReceiptScan(ctx, file);
    if (gptPurchase) {
      await renderPurchases(ctx);
      ctx.toast(ctx.get('purchases.scanned'));
      await openDetail(gptPurchase.id);
      return;
    }

    const form = new FormData();
    form.append('receipt', file);
    form.append('currency', 'EUR');
    const p = await ctx.api('api/purchases/receipt-scan', { method: 'POST', body: form });
    await renderPurchases(ctx);
    if (p && p.status === 'review') { ctx.toast(ctx.get('purchases.scanned')); await openDetail(p.id); }
    else ctx.toast(ctx.get('purchases.uploaded'));
  }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  finally { input.value = ''; }
}

async function openDetail(id) {
  let purchase, options, reconciliation, amazon = null;
  try {
    purchase = await ctx.api(`api/purchases/${id}`);
    options = await ctx.categoryOptions();
    reconciliation = await ctx.api(`api/purchases/${id}/reconciliation`).catch(() => null);
    if (purchase.source === 'amazon') amazon = await ctx.api(`api/purchases/${id}/amazon-details`).catch(() => null);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }

  if (amazon) reconciliation = amazonReconciliation(purchase, reconciliation, amazon);
  const items = (purchase.items || []).map((i, index) => {
    const asin = i.asin ? `<span class="row-sub">ASIN ${ctx.esc(i.asin)}</span>` : '';
    return `<div class="purchase-item" data-index="${index}"><div><input class="item-name" value="${ctx.esc(i.name)}">${asin}</div><input class="item-qty" type="number" step="0.001" value="${i.quantity}"><input class="item-total" type="number" step="0.01" value="${i.totalPrice}"><select class="item-category"><option value="">${ctx.esc(ctx.get('common.uncategorized'))}</option>${options}</select></div>`;
  }).join('');

  const amazonBlock = amazon ? amazonDetailsHtml(purchase, amazon) : '';
  const dlg = ctx.dialog(`<form class="dialog-card purchase-detail">
    <div class="panel-head"><h2>${ctx.esc(purchase.merchant || ctx.get('purchases.title'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <div class="row-sub">${ctx.esc(ctx.date(purchase.purchaseDate))} · ${ctx.money(purchase.totalAmount, purchase.currency)}${purchase.externalOrderId ? ` · ${ctx.esc(purchase.externalOrderId)}` : ''}</div>
    ${purchase.hasReceipt ? `<div class="reconcile-link"><button type="button" class="ghost" data-view-receipt>${ctx.esc(ctx.get('purchases.viewReceipt'))}</button></div>` : ''}
    <div class="reconcile" data-reconcile></div>
    ${amazonBlock}
    <h3 class="notif-h">${ctx.esc(ctx.get('purchases.lineItems'))}</h3>
    <div class="purchase-items">${items || `<div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div>`}</div>
    <div class="dialog-actions"><button type="button" data-save>${ctx.esc(ctx.get('common.apply'))}</button></div>
  </form>`);
  (purchase.items || []).forEach((i, index) => { const s = dlg.querySelector(`.purchase-item[data-index="${index}"] .item-category`); if (s && i.categoryId) s.value = i.categoryId; });

  renderReconcile(dlg.querySelector('[data-reconcile]'), purchase, reconciliation, dlg);
  if (amazon) bindAmazonDetails(dlg, purchase, amazon);

  dlg.querySelector('[data-view-receipt]')?.addEventListener('click', () => window.open(ctx.bffUrl(`api/purchases/${id}/receipt`), '_blank', 'noopener'));
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-save]').onclick = async () => {
    const rows = [...dlg.querySelectorAll('.purchase-item')];
    const payload = rows.map(r => {
      const original = (purchase.items || [])[Number(r.dataset.index)] || {};
      return {
        categoryId: r.querySelector('.item-category').value || null,
        name: r.querySelector('.item-name').value,
        brand: original.brand ?? null,
        sku: original.sku ?? null,
        asin: original.asin ?? null,
        quantity: Number(r.querySelector('.item-qty').value || 1),
        unitPrice: original.unitPrice ?? null,
        totalPrice: Number(r.querySelector('.item-total').value || 0),
        currency: original.currency || purchase.currency,
        notes: original.notes ?? null
      };
    });
    try { await ctx.api(`api/purchases/${id}/items`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) }); dlg.close(); await renderPurchases(ctx); }
    catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

function amazonReconciliation(purchase, rec, amazon) {
  const payments = amazon.payments || [];
  const bankAllocated = payments.reduce((sum, x) => sum + Number(x.allocatedAmount || 0), 0);
  const nonBank = Number(amazon.nonBankPaymentAmount || 0);
  const itemTotal = rec?.itemTotal ?? (purchase.items || []).reduce((sum, x) => sum + Number(x.totalPrice || 0), 0);
  return {
    ...(rec || {}),
    purchaseTotal: Number(purchase.totalAmount || 0),
    itemTotal,
    itemDifference: Number(purchase.totalAmount || 0) - itemTotal,
    transactionAmount: payments.length ? -bankAllocated : null,
    transactionDifference: bankAllocated + nonBank - Number(purchase.totalAmount || 0)
  };
}

function amazonDetailsHtml(purchase, amazon) {
  const payments = amazon.payments || [];
  const refunds = amazon.refunds || [];
  const status = amazon.externalStatus ? amazonStatusLabel(amazon.externalStatus) : t('Status unbekannt', 'Status unknown');
  const paymentRows = payments.length ? payments.map(p => {
    const allocation = Number(p.allocatedAmount || 0);
    const bankAmount = Math.abs(Number(p.amount || 0));
    const shared = Math.abs(allocation - bankAmount) > 0.01 ? ` · ${t('von', 'of')} ${ctx.money(bankAmount, purchase.currency)}` : '';
    const confidence = p.matchConfidence == null ? t('manuell', 'manual') : Math.round(Number(p.matchConfidence) * 100) + '%';
    return `<div class="row" data-amazon-payment="${p.transactionId}"><div class="row-main"><div class="row-title">${ctx.esc(p.counterparty || 'Amazon')}</div><div class="row-sub">${ctx.esc(ctx.date(p.bookingDate))} · ${confidence}${shared}</div></div><div class="row-side"><span class="amount">${ctx.money(allocation, purchase.currency)}</span><button type="button" class="ghost" data-unlink-amazon-payment>${t('Lösen', 'Unlink')}</button></div></div>`;
  }).join('') : `<div class="row-sub">${t('Noch keine Bankbuchung zugeordnet.', 'No bank transaction linked yet.')}</div>`;
  const nonBank = Number(amazon.nonBankPaymentAmount || 0);
  const nonBankSource = amazon.nonBankPaymentSource === 'manual' ? t('manuell', 'manual') : t('Amazon erkannt', 'detected by Amazon');
  const refundRows = refunds.length ? refunds.map(r => {
    const confidence = r.matchConfidence == null ? '' : ` · ${Math.round(Number(r.matchConfidence) * 100)}%`;
    const action = r.transactionId
      ? `<button type="button" class="ghost" data-unlink-amazon-refund>${t('Lösen', 'Unlink')}</button>`
      : `<button type="button" class="ghost" data-link-amazon-refund>${t('Buchung verknüpfen', 'Link transaction')}</button>`;
    return `<div data-amazon-refund="${r.id}"><div class="row"><div class="row-main"><div class="row-title">${ctx.esc(r.description || t('Amazon-Erstattung', 'Amazon refund'))}</div><div class="row-sub">${ctx.esc(ctx.date(r.refundDate))} · ${r.transactionId ? t('Bankbuchung verknüpft', 'Bank transaction linked') : t('Noch nicht verknüpft', 'Not linked yet')}${confidence}</div></div><div class="row-side"><span class="amount">${ctx.money(Number(r.amount || 0), r.currency || purchase.currency)}</span>${action}</div></div><div data-amazon-refund-candidates></div></div>`;
  }).join('') : `<div class="row-sub">${t('Keine Retouren oder Erstattungen erkannt.', 'No returns or refunds detected.')}</div>`;
  return `<section class="purchase-amazon" data-amazon-details>
    <h3 class="notif-h">Amazon</h3>
    <div class="row"><div class="row-main"><div class="row-title">${ctx.esc(status)}</div><div class="row-sub">${ctx.esc(purchase.externalOrderId || '')}</div></div>${purchase.sourceReference ? `<a class="ghost" href="${ctx.esc(purchase.sourceReference)}" target="_blank" rel="noopener noreferrer">Amazon</a>` : ''}</div>
    <div class="row-group">${t('Zahlungen', 'Payments')}</div>${paymentRows}
    <div class="reconcile-link"><button type="button" class="ghost" data-add-amazon-payment>${t('Weitere Buchung verknüpfen', 'Link another transaction')}</button><div data-amazon-candidates></div></div>
    <div class="row"><div class="row-main"><div class="row-title">${t('Guthaben / Geschenkgutschein', 'Balance / gift card')}</div><div class="row-sub">${nonBankSource}</div></div><div class="row-side"><input data-amazon-nonbank type="number" min="0" max="${Number(purchase.totalAmount || 0)}" step="0.01" value="${nonBank.toFixed(2)}"><button type="button" class="ghost" data-save-amazon-nonbank>${t('Speichern', 'Save')}</button></div></div>
    <div class="row-group">${t('Retouren / Erstattungen', 'Returns / refunds')}</div>${refundRows}
  </section>`;
}

function bindAmazonDetails(dlg, purchase) {
  dlg.querySelector('[data-save-amazon-nonbank]')?.addEventListener('click', async () => {
    const input = dlg.querySelector('[data-amazon-nonbank]');
    const amount = Number(input?.value || 0);
    if (!Number.isFinite(amount) || amount < 0 || amount > Number(purchase.totalAmount || 0) + 0.01) return;
    try {
      await ctx.api(`api/purchases/${purchase.id}/amazon-nonbank-payment`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ amount }) });
      dlg.close(); await renderPurchases(ctx); await openDetail(purchase.id);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });

  dlg.querySelector('[data-add-amazon-payment]')?.addEventListener('click', async () => {
    const box = dlg.querySelector('[data-amazon-candidates]');
    box.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('common.loading'))}</div>`;
    let candidates;
    try { candidates = (await ctx.api(`api/purchases/${purchase.id}/amazon-payment-candidates`)) || []; }
    catch (err) { box.innerHTML = `<div class="row-sub">${ctx.esc(err.message || ctx.get('common.error'))}</div>`; return; }
    if (!candidates.length) { box.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('purchases.noCandidates'))}</div>`; return; }
    box.innerHTML = candidates.map((c, i) => `<div class="row candidate-row" data-i="${i}"><div class="row-main"><div class="row-title">${ctx.esc(c.counterparty || c.description || '—')}</div><div class="row-sub">${ctx.esc(ctx.date(c.bookingDate))} · ${Math.round(Number(c.confidence || 0) * 100)}% · ${t('verfügbar', 'available')} ${ctx.money(c.availableAmount, purchase.currency)}</div></div><div class="row-side"><input data-allocation type="number" min="0.01" max="${Number(c.availableAmount || 0)}" step="0.01" value="${Number(c.suggestedAllocation || 0).toFixed(2)}"><button type="button" class="ghost" data-link-amazon-payment>${ctx.esc(ctx.get('purchases.link'))}</button></div></div>`).join('');
    box.querySelectorAll('.candidate-row').forEach(row => row.querySelector('[data-link-amazon-payment]').addEventListener('click', async () => {
      const candidate = candidates[Number(row.dataset.i)];
      const allocatedAmount = Number(row.querySelector('[data-allocation]')?.value || 0);
      if (!Number.isFinite(allocatedAmount) || allocatedAmount <= 0) return;
      try {
        await ctx.api(`api/purchases/${purchase.id}/amazon-payment-links`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ transactionId: candidate.transactionId, confidence: candidate.confidence ?? null, allocatedAmount }) });
        dlg.close(); await renderPurchases(ctx); await openDetail(purchase.id);
      } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
    }));
  });

  dlg.querySelectorAll('[data-amazon-payment]').forEach(row => row.querySelector('[data-unlink-amazon-payment]')?.addEventListener('click', async () => {
    try {
      await ctx.api(`api/purchases/${purchase.id}/amazon-payment-links/${row.dataset.amazonPayment}`, { method: 'DELETE' });
      dlg.close(); await renderPurchases(ctx); await openDetail(purchase.id);
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
      box.innerHTML = candidates.map((c, i) => `<div class="row candidate-row" data-i="${i}"><div class="row-main"><div class="row-title">${ctx.esc(c.counterparty || c.description || '—')}</div><div class="row-sub">${ctx.esc(ctx.date(c.bookingDate))} · ${Math.round(Number(c.confidence || 0) * 100)}%</div></div><div class="row-side"><span class="amount">${ctx.money(c.amount, purchase.currency)}</span><button type="button" class="ghost" data-confirm-amazon-refund>${ctx.esc(ctx.get('purchases.link'))}</button></div></div>`).join('');
      box.querySelectorAll('.candidate-row').forEach(row => row.querySelector('[data-confirm-amazon-refund]').addEventListener('click', async () => {
        const candidate = candidates[Number(row.dataset.i)];
        try {
          await ctx.api(`api/purchases/${purchase.id}/amazon-refunds/${refundId}/link`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ transactionId: candidate.transactionId, confidence: candidate.confidence ?? null }) });
          dlg.close(); await renderPurchases(ctx); await openDetail(purchase.id);
        } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
      }));
    });

    refundBlock.querySelector('[data-unlink-amazon-refund]')?.addEventListener('click', async () => {
      try {
        await ctx.api(`api/purchases/${purchase.id}/amazon-refunds/${refundId}/link`, { method: 'DELETE' });
        dlg.close(); await renderPurchases(ctx); await openDetail(purchase.id);
      } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
    });
  });
}

function renderReconcile(box, purchase, rec, dlg) {
  const cur = purchase.currency;
  const amount = v => v == null ? '—' : ctx.money(v, cur);
  const diff = rec ? Number(rec.itemDifference || 0) : 0;
  const diffClass = Math.abs(diff) <= 0.01 ? 'positive' : 'negative';
  const txAmount = rec && rec.transactionAmount != null ? ctx.money(Math.abs(rec.transactionAmount), cur) : ctx.get('purchases.notLinked');

  box.innerHTML = `<div class="reconcile-grid">
      <div class="detail-item"><span class="detail-k">${ctx.esc(ctx.get('purchases.bankAmount'))}</span><span class="detail-v">${ctx.esc(txAmount)}</span></div>
      <div class="detail-item"><span class="detail-k">${ctx.esc(ctx.get('purchases.receiptTotal'))}</span><span class="detail-v">${amount(rec ? rec.purchaseTotal : purchase.totalAmount)}</span></div>
      <div class="detail-item"><span class="detail-k">${ctx.esc(ctx.get('purchases.itemTotal'))}</span><span class="detail-v">${amount(rec ? rec.itemTotal : null)}</span></div>
      <div class="detail-item"><span class="detail-k">${ctx.esc(ctx.get('purchases.unallocated'))}</span><span class="detail-v ${diffClass}">${amount(rec ? rec.itemDifference : null)}</span></div>
    </div>
    ${!purchase.transactionId && purchase.source !== 'amazon' ? `<div class="reconcile-link" data-link></div>` : ''}`;

  if (!purchase.transactionId && purchase.source !== 'amazon') {
    const linkBox = box.querySelector('[data-link]');
    linkBox.innerHTML = `<button type="button" class="ghost" data-auto-link>${ctx.esc(ctx.get('purchases.autoLink'))}</button> <button type="button" class="ghost" data-load-candidates>${ctx.esc(ctx.get('purchases.linkTransaction'))}</button>`;
    linkBox.querySelector('[data-auto-link]').addEventListener('click', async () => {
      try {
        const res = await ctx.api(`api/purchases/${purchase.id}/auto-link`, { method: 'POST' });
        if (res && res.linked) { ctx.toast(ctx.get('common.saved')); dlg.close(); await renderPurchases(ctx); await openDetail(purchase.id); }
        else ctx.toast(ctx.get('purchases.noAutoMatch'));
      } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
    });
    linkBox.querySelector('[data-load-candidates]').addEventListener('click', async () => {
      linkBox.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('common.loading'))}</div>`;
      let candidates;
      try { candidates = (await ctx.api(`api/purchases/${purchase.id}/match-candidates`)) || []; }
      catch (err) { linkBox.innerHTML = `<div class="row-sub">${ctx.esc(err.message || ctx.get('common.error'))}</div>`; return; }
      if (!candidates.length) { linkBox.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('purchases.noCandidates'))}</div>`; return; }
      linkBox.innerHTML = `<div class="row-group">${ctx.esc(ctx.get('purchases.candidates'))}</div>` + candidates.map((c, i) =>
        `<div class="row candidate-row" data-i="${i}"><div class="row-main"><div class="row-title">${ctx.esc(c.counterparty || c.description || '—')}</div><div class="row-sub">${ctx.esc(ctx.date(c.bookingDate))} · ${Math.round((c.confidence || 0) * 100)}%</div></div><div class="row-side"><span class="amount">${ctx.money(c.amount, cur)}</span><button type="button" class="ghost" data-link-btn>${ctx.esc(ctx.get('purchases.link'))}</button></div></div>`).join('');
      linkBox.querySelectorAll('.candidate-row').forEach(row => row.querySelector('[data-link-btn]').addEventListener('click', async () => {
        const c = candidates[Number(row.dataset.i)];
        try {
          await ctx.api(`api/purchases/${purchase.id}/link`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ transactionId: c.id, confidence: c.confidence ?? null }) });
          dlg.close(); await renderPurchases(ctx); await openDetail(purchase.id);
        } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
      }));
    });
  }
}

async function refreshAmazonButton() {
  const button = ctx?.$('#amazon-import');
  if (!button) return;
  try {
    const status = await ctx.api('api/purchases/amazon/status');
    button.textContent = status?.connected ? t('Amazon synchronisieren', 'Sync Amazon') : t('Amazon verbinden', 'Connect Amazon');
    if (status?.status === 'requires_reauth') button.textContent = t('Amazon neu verbinden', 'Reconnect Amazon');
  } catch { button.textContent = t('Amazon verbinden', 'Connect Amazon'); }
}

async function openAmazonConnection() {
  let status;
  try { status = await ctx.api('api/purchases/amazon/status'); }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }

  const dlg = ctx.dialog(`<form class="dialog-card" data-amazon-form>
    <div class="panel-head"><h2>Amazon</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <div data-amazon-body></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  renderAmazonConnectionBody(dlg, status);
  dlg.showModal();
}

function renderAmazonConnectionBody(dlg, status) {
  const body = dlg.querySelector('[data-amazon-body]');
  if (status?.connected) {
    const last = status.lastSuccessfulSyncAt ? new Date(status.lastSuccessfulSyncAt).toLocaleString() : t('Noch nie', 'Never');
    body.innerHTML = `<div class="row-sub">${t('Verbunden. Letzte erfolgreiche Synchronisierung:', 'Connected. Last successful sync:')} ${ctx.esc(last)}</div>
      ${status.lastError ? `<div class="row-sub negative">${ctx.esc(status.lastError)}</div>` : ''}
      <div class="dialog-actions"><button type="button" class="ghost" data-disconnect>${t('Trennen', 'Disconnect')}</button><button type="button" class="ghost" data-sync-days="365">${t('1 Jahr', '1 year')}</button><button type="button" class="ghost" data-sync-days="36500">${t('Alle', 'All')}</button><button type="button" data-sync-days="90">${t('90 Tage synchronisieren', 'Sync 90 days')}</button></div>`;
    body.querySelectorAll('[data-sync-days]').forEach(button => button.addEventListener('click', () => runAmazonSync(dlg, Number(button.dataset.syncDays))));
    body.querySelector('[data-disconnect]').addEventListener('click', async () => {
      try { await ctx.api('api/purchases/amazon/connection', { method: 'DELETE' }); dlg.close(); await refreshAmazonButton(); }
      catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
    });
    return;
  }

  body.innerHTML = `${status?.lastError ? `<div class="row-sub negative">${ctx.esc(status.lastError)}</div>` : ''}
    <div class="row-sub">${t('FullWorth speichert nur die verschlüsselte Amazon-Sitzung. Passwort und Bestätigungscode werden nicht gespeichert.', 'FullWorth stores only the encrypted Amazon session. Password and verification code are not stored.')}</div>
    <label>${t('Amazon E-Mail', 'Amazon email')}<input name="amazonEmail" type="email" autocomplete="username" required></label>
    <label>${t('Amazon Passwort', 'Amazon password')}<input name="amazonPassword" type="password" autocomplete="current-password" required></label>
    <div class="dialog-actions"><button type="button" data-connect>${t('Verbinden', 'Connect')}</button></div>`;
  body.querySelector('[data-connect]').addEventListener('click', () => startAmazonLogin(dlg));
}

async function startAmazonLogin(dlg) {
  const email = dlg.querySelector('[name="amazonEmail"]')?.value || '';
  const passwordBox = dlg.querySelector('[name="amazonPassword"]');
  const password = passwordBox?.value || '';
  if (!email || !password) return;
  if (passwordBox) passwordBox.value = '';
  setAmazonBusy(dlg, t('Amazon-Anmeldung läuft…', 'Signing in to Amazon…'));
  try {
    const result = await ctx.api('api/purchases/amazon/connect/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) });
    await handleAmazonLoginResult(dlg, result);
  } catch (err) { renderAmazonConnectionBody(dlg, { connected: false, lastError: err.message || ctx.get('common.error') }); }
}

async function completeAmazonLogin(dlg, challengeId, otp = null) {
  setAmazonBusy(dlg, t('Amazon-Bestätigung wird geprüft…', 'Checking Amazon verification…'));
  try {
    const result = await ctx.api(`api/purchases/amazon/connect/${challengeId}/complete`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ otp }) });
    await handleAmazonLoginResult(dlg, result);
  } catch (err) { renderAmazonConnectionBody(dlg, { connected: false, lastError: err.message || ctx.get('common.error') }); }
}

async function handleAmazonLoginResult(dlg, result) {
  if (result?.status === 'connected') {
    await refreshAmazonButton();
    await runAmazonSync(dlg, 90);
    return;
  }
  const body = dlg.querySelector('[data-amazon-body]');
  if (result?.status === 'otp' && result.challengeId) {
    body.innerHTML = `<div class="row-sub">${t('Amazon-Bestätigungscode eingeben.', 'Enter the Amazon verification code.')}</div><label>${t('Code', 'Code')}<input name="amazonOtp" inputmode="numeric" autocomplete="one-time-code" required></label><div class="dialog-actions"><button type="button" data-otp>${t('Bestätigen', 'Verify')}</button></div>`;
    body.querySelector('[data-otp]').addEventListener('click', () => { const otp = body.querySelector('[name="amazonOtp"]')?.value || ''; body.querySelector('[name="amazonOtp"]').value = ''; completeAmazonLogin(dlg, result.challengeId, otp); });
    return;
  }
  if (result?.status === 'approval' && result.challengeId) {
    body.innerHTML = `<div class="row-sub">${t('Amazon-Anmeldung auf dem anderen Gerät bestätigen. Danach hier fortfahren.', 'Approve the Amazon sign-in on the other device, then continue here.')}</div><div class="dialog-actions"><button type="button" data-approved>${t('Ich habe bestätigt', 'I approved it')}</button></div>`;
    body.querySelector('[data-approved]').addEventListener('click', () => completeAmazonLogin(dlg, result.challengeId, null));
    return;
  }
  renderAmazonConnectionBody(dlg, { connected: false, lastError: result?.message || t('Amazon-Anmeldung fehlgeschlagen.', 'Amazon sign-in failed.') });
}

async function runAmazonSync(dlg, historyDays) {
  setAmazonBusy(dlg, t('Amazon-Bestellungen werden synchronisiert…', 'Syncing Amazon orders…'));
  try {
    const result = await ctx.api('api/purchases/amazon/sync', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ historyDays }) });
    ctx.toast(t(`${result.ordersImported ?? 0} Amazon-Bestellungen synchronisiert`, `${result.ordersImported ?? 0} Amazon orders synced`));
    dlg.close();
    await refreshAmazonButton();
    await renderPurchases(ctx);
  } catch (err) {
    let status = null;
    try { status = await ctx.api('api/purchases/amazon/status'); } catch { }
    renderAmazonConnectionBody(dlg, status || { connected: false, lastError: err.message || ctx.get('common.error') });
  }
}

function setAmazonBusy(dlg, text) {
  const body = dlg.querySelector('[data-amazon-body]');
  if (body) body.innerHTML = `<div class="row state-loading"><div class="row-sub">${ctx.esc(text)}</div></div>`;
}

function amazonStatusLabel(status) {
  const labels = {
    cancelled: t('Storniert', 'Cancelled'), return: t('Retoure', 'Return'), delivered: t('Zugestellt', 'Delivered'),
    shipped: t('Versandt', 'Shipped'), ordered: t('Bestellt', 'Ordered')
  };
  return labels[status] || status;
}

function t(de, en) { return (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en; }
function emptyRow() { return `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`; }
