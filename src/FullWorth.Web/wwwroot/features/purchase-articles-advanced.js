import {
  mountExportAndWarrantyActions,
  mountPurchaseAdvancedActions,
  mountProductAdvancedActions
} from './purchase-articles-advanced-actions.js';
import { mountPurchaseDiscountActions } from './purchase-discount-actions.js';
import { mountReceiptSourceReview } from './purchase-receipt-source-review.js';
import { api as sharedApi, apiClient } from '../core/services.js';
import { createDialog } from '../ui/dialog.js';
import { confirmMessage } from '../ui/confirm.js';
import { openPurchaseWorkspace, openProduct } from './purchase-articles-workspace.js';

// Adapter between the existing purchase workspace and the secondary advanced-actions module. It avoids
// coupling the large renderer to these workflows: IDs are captured from the existing list interactions
// (with Resource Timing as a fallback for immediately-created manual purchases), while API calls continue
// through the same BFF and FullWorth-Space query contract as the rest of the UI.

const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));
const text = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en;

const api=(path,options)=>sharedApi(path,options);

function makeDialog(html) {
  const normalized = html.replace(/class="pa-dialog-card\b/, 'class="dialog-card pa-dialog-card');
  return createDialog(normalized,{className:'pa-dialog',closeLabel:text('Schließen','Close')});
}

const bffUrl = path => apiClient.backendUrl(path);
const confirmAction = (message, options = {}) => confirmMessage({
  message,
  title: options.title || text('Bestätigen', 'Confirm'),
  confirmLabel: options.confirmLabel || text('Bestätigen', 'Confirm'),
  cancelLabel: options.cancelLabel || text('Abbrechen', 'Cancel'),
  destructive: options.destructive !== false,
  create: html => createDialog(html, { className: 'pa-dialog', closeLabel: text('Schließen', 'Close') })
});

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

async function reopenPurchase(id, currentDialog) {
  if (currentDialog?.open) currentDialog.close();
  await openPurchaseWorkspace(id);
}

async function reopenProduct(id, currentDialog) {
  if (currentDialog?.open) currentDialog.close();
  await openProduct(id);
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
  const id = dialog.dataset.purchaseId;
  if (!id) { dialog.dataset.paAdvancedInstaller = ''; return; }
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
      refresh,
      confirmAction
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
      refresh,
      confirmAction
    });
    await mountReceiptSourceReview({ dlg: dialog, purchase, api, esc, showError, bffUrl });
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
  const id = dialog.dataset.productId;
  if (!id) { dialog.dataset.paProductAdvancedInstaller = ''; return; }
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
      confirmAction,
      reload: async targetId => reopenProduct(targetId, dialog)
    });
  } catch (error) {
    showError(dialog, error.message);
  } finally {
    dialog.dataset.paProductAdvancedInstaller = '';
  }
}

export async function refreshPurchaseAdvancedInstaller(detail = {}) {
  const advancedPanel = document.querySelector('.purchase-advanced-panel:not([hidden])');
  const activeTab = detail.tab || document.querySelector('[data-pa-tab].active')?.dataset.paTab;
  if (advancedPanel && activeTab === 'articles') {
    mountExportAndWarrantyActions(advancedPanel, { api, esc, makeDialog, money, fmtDate, showError, bffUrl });
  }

  const dialog = detail.dialog;
  if (dialog?.querySelector('.pa-workspace')) await mountPurchaseDialog(dialog);
  else if (dialog?.querySelector('.pa-product-detail')) await mountProductDialog(dialog);
}
