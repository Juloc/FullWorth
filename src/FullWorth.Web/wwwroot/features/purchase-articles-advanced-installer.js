import {
  mountExportAndWarrantyActions,
  mountPurchaseAdvancedActions,
  mountProductAdvancedActions
} from './purchase-articles-advanced-actions.js';
import { mountPurchaseDiscountActions } from './purchase-discount-actions.js';
import { mountReceiptSourceReview } from './purchase-receipt-source-review.js';

// Adapter between the existing purchase workspace and the secondary advanced-actions module. It avoids
// coupling the large renderer to these workflows: IDs are captured from the existing list interactions
// (with Resource Timing as a fallback for immediately-created manual purchases), while API calls continue
// through the same BFF and FullWorth-Space query contract as the rest of the UI.

let lastPurchaseId = null;
let lastProductId = null;
let scanScheduled = false;

const spaceId = () => localStorage.getItem('finance.space') || '';
const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));
const text = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en;

function withSpace(path) {
  const [base, query = ''] = String(path).replace(/^\//, '').split('?');
  const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', spaceId());
  return `${base}?${params}`;
}

async function api(path, options) {
  const response = await fetch(`/bff/backend/${withSpace(path)}`, options);
  if (!response.ok) {
    let message = `${response.status}`;
    try {
      const body = await response.json();
      message = body.error || body.message || body.title || message;
      if (body.detail?.conflict) message += ` (${body.detail.conflict})`;
    } catch { /* keep HTTP status */ }
    const error = new Error(message);
    error.status = response.status;
    throw error;
  }
  if (response.status === 204) return null;
  return response.json();
}

function makeDialog(html) {
  const dlg = document.createElement('dialog');
  dlg.className = 'pa-dialog';
  dlg.innerHTML = html;
  document.body.appendChild(dlg);
  dlg.addEventListener('close', () => dlg.remove(), { once: true });
  return dlg;
}

function showError(dlg, message) {
  let box = dlg.querySelector('[data-error]');
  if (!box) {
    box = document.createElement('div');
    box.className = 'pa-dialog-error';
    box.dataset.error = '';
    dlg.firstElementChild?.appendChild(box);
  }
  box.hidden = false;
  box.textContent = message || 'Error';
}

function money(value, currency = 'EUR') {
  const amount = Number(value || 0);
  try { return new Intl.NumberFormat(document.documentElement.lang || 'de', { style: 'currency', currency }).format(amount); }
  catch { return `${amount.toFixed(2)} ${currency}`; }
}

function fmtDate(value) {
  if (!value) return '—';
  try { return new Intl.DateTimeFormat(document.documentElement.lang || 'de').format(new Date(`${String(value).slice(0, 10)}T12:00:00`)); }
  catch { return String(value); }
}

function latestResourceId(kind) {
  const pattern = kind === 'purchase'
    ? /\/api\/purchases\/([0-9a-f-]{36})\/workspace(?:\?|$)/i
    : /\/api\/products\/([0-9a-f-]{36})(?:\?|$)/i;
  const entries = performance.getEntriesByType?.('resource') || [];
  for (let i = entries.length - 1; i >= 0; i--) {
    const match = String(entries[i].name || '').match(pattern);
    if (match) return match[1];
  }
  return null;
}

function clickExisting(selector) {
  const row = document.querySelector(selector);
  if (!row) return false;
  row.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
  return true;
}

async function reopenPurchase(id, currentDialog) {
  if (currentDialog?.open) currentDialog.close();
  await new Promise(resolve => setTimeout(resolve, 0));
  if (!clickExisting(`[data-purchase-id="${id}"]`)) {
    document.querySelector('[data-pa-tab="articles"]')?.click();
  }
}

async function reopenProduct(id, currentDialog) {
  if (currentDialog?.open) currentDialog.close();
  await new Promise(resolve => setTimeout(resolve, 0));
  if (!clickExisting(`[data-product-id="${id}"]`)) document.querySelector('[data-pa-tab="products"]')?.click();
}

function mountCurrencySafePaymentPicker(dialog, purchase, writable) {
  if (!writable || dialog.dataset.paCurrencyPaymentMounted === 'true') return;
  const addButton = dialog.querySelector('[data-add-payment]');
  if (!addButton) return;
  dialog.dataset.paCurrencyPaymentMounted = 'true';

  addButton.onclick = async () => {
    let data;
    try { data = await api(`api/purchases/${purchase.id}/payment-candidates`); }
    catch (error) { showError(dialog, error.message); return; }

    const purchaseCurrency = String(data.purchaseCurrency || purchase.currency || '').toUpperCase();
    const candidates = data.candidates || [];
    const sameCurrency = candidates.filter(row => String(row.currency || '').toUpperCase() === purchaseCurrency);
    const foreignCurrency = candidates.filter(row => String(row.currency || '').toUpperCase() !== purchaseCurrency);
    const remaining = Math.max(0, Number(data.remaining || 0));
    const fullyLinked = remaining <= 0.005;

    const sameRows = fullyLinked ? '' : sameCurrency.map(row => {
      const suggested = Math.min(Math.abs(Number(row.amount || 0)), remaining);
      if (!(suggested > 0)) return '';
      return `<button type="button" class="pa-picker-row" data-safe-payment="${row.id}" data-amount="${suggested}" data-currency="${esc(String(row.currency || '').toUpperCase())}" data-confidence="${Number(row.confidence || 0)}"><div><strong>${esc(row.counterparty || '—')}</strong><span>${esc(fmtDate(row.bookingDate))} · ${Math.round(Number(row.confidence || 0) * 100)}%</span></div><strong>${esc(money(row.amount, row.currency))}</strong></button>`;
    }).join('');

    const foreignRows = foreignCurrency.map(row => `<div class="pa-picker-row pa-fx-blocked" aria-disabled="true"><div><strong>${esc(row.counterparty || '—')}</strong><span>${esc(fmtDate(row.bookingDate))} · ${esc(text('FX-Konvertierung erforderlich', 'FX conversion required'))}</span></div><strong>${esc(money(row.amount, row.currency))}</strong></div>`).join('');
    const sameState = fullyLinked
      ? `<div class="state-empty">${esc(text('Der Kauf ist bereits vollständig mit Zahlungen verknüpft.', 'This purchase is already fully linked to payments.'))}</div>`
      : (sameRows || `<div class="state-empty">${esc(text('Keine passende Buchung in gleicher Währung.', 'No matching transaction in the same currency.'))}</div>`);

    const dlg = makeDialog(`<div class="pa-dialog-card pa-picker"><div class="panel-head"><div><h2>${esc(text('Passende Buchungen', 'Matching transactions'))}</h2><div class="row-sub">${esc(text(`Kaufwährung: ${purchaseCurrency}. Fremdwährungen werden erst nach einer expliziten FX-Konvertierung verknüpft.`, `Purchase currency: ${purchaseCurrency}. Foreign currencies require an explicit FX conversion before linking.`))}</div></div><button type="button" data-close>×</button></div><div class="pa-list">${sameState}${foreignRows ? `<div class="pa-section-label">${esc(text('Andere Währungen', 'Other currencies'))}</div>${foreignRows}` : ''}</div><div class="pa-dialog-error" data-error hidden></div></div>`);
    dlg.querySelector('[data-close]').onclick = () => dlg.close();
    dlg.querySelectorAll('[data-safe-payment]').forEach(button => button.onclick = async () => {
      try {
        await api(`api/purchases/${purchase.id}/payments`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            transactionId: button.dataset.safePayment,
            amount: Number(button.dataset.amount),
            currency: button.dataset.currency,
            linkSource: 'manual',
            confidence: Number(button.dataset.confidence)
          })
        });
        dlg.close();
        await reopenPurchase(purchase.id, dialog);
      } catch (error) { showError(dlg, error.message); }
    });
    dlg.showModal();
  };
}

async function mountPurchaseDialog(dialog) {
  if (!dialog?.querySelector('.pa-workspace') || dialog.dataset.paAdvancedInstaller === 'loading' || dialog.dataset.paAdvancedMounted === 'true') return;
  dialog.dataset.paAdvancedInstaller = 'loading';
  const id = lastPurchaseId || latestResourceId('purchase');
  if (!id) { dialog.dataset.paAdvancedInstaller = ''; return; }
  lastPurchaseId = id;
  try {
    const workspace = await api(`api/purchases/${id}/workspace`);
    const purchase = workspace.purchase;
    const writable = workspace.access === 'write';
    const refresh = async () => reopenPurchase(id, dialog);
    await mountPurchaseAdvancedActions({
      dlg: dialog,
      purchase,
      writable,
      api,
      esc,
      makeDialog,
      money,
      fmtDate,
      showError,
      refresh
    });
    await mountPurchaseDiscountActions({
      dlg: dialog,
      purchase,
      writable,
      api,
      esc,
      makeDialog,
      money,
      showError,
      refresh
    });
    await mountReceiptSourceReview({ dlg: dialog, purchase, api, esc, showError });
    mountCurrencySafePaymentPicker(dialog, purchase, writable);
  } catch (error) {
    showError(dialog, error.message);
  } finally {
    dialog.dataset.paAdvancedInstaller = '';
  }
}

async function mountProductDialog(dialog) {
  if (!dialog?.querySelector('.pa-product-detail') || dialog.dataset.paProductAdvancedInstaller === 'loading' || dialog.dataset.paProductAdvancedMounted === 'true') return;
  dialog.dataset.paProductAdvancedInstaller = 'loading';
  const id = lastProductId || latestResourceId('product');
  if (!id) { dialog.dataset.paProductAdvancedInstaller = ''; return; }
  lastProductId = id;
  try {
    const data = await api(`api/products/${id}`);
    if (!data?.product) return;
    await mountProductAdvancedActions({
      dlg: dialog,
      product: data.product,
      api,
      esc,
      makeDialog,
      showError,
      reload: async targetId => reopenProduct(targetId, dialog)
    });
  } catch (error) {
    showError(dialog, error.message);
  } finally {
    dialog.dataset.paProductAdvancedInstaller = '';
  }
}

function scan() {
  scanScheduled = false;
  const advancedPanel = document.querySelector('.purchase-advanced-panel:not([hidden])');
  const activeTab = document.querySelector('[data-pa-tab].active')?.dataset.paTab;
  if (advancedPanel && activeTab === 'articles') {
    mountExportAndWarrantyActions(advancedPanel, { api, esc, makeDialog, money, fmtDate, showError });
  }
  document.querySelectorAll('dialog.pa-dialog').forEach(dialog => {
    if (dialog.querySelector('.pa-workspace')) void mountPurchaseDialog(dialog);
    else if (dialog.querySelector('.pa-product-detail')) void mountProductDialog(dialog);
  });
}

function scheduleScan() {
  if (scanScheduled) return;
  scanScheduled = true;
  queueMicrotask(scan);
}

document.addEventListener('click', event => {
  const purchase = event.target.closest?.('[data-purchase-id]');
  if (purchase?.dataset.purchaseId) lastPurchaseId = purchase.dataset.purchaseId;
  const product = event.target.closest?.('[data-product-id]');
  if (product?.dataset.productId) lastProductId = product.dataset.productId;
  scheduleScan();
}, true);

function install() {
  if (!document.body) return;
  const observer = new MutationObserver(scheduleScan);
  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['hidden', 'class'] });
  scheduleScan();
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, { once: true });
else install();
