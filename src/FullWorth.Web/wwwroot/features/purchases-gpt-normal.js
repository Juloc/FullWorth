import './purchase-articles-workspace.js';
import './purchase-articles-advanced-installer.js';
import './purchase-price-insights.js';
import './purchase-advanced-insights.js';
import './receipt-imports.js';
import './receipt-import-batch-details.js';
import { addReceiptScanFiles } from './receipt-scan-set.js';

let latestContext = null;
let scanSetCaptureInstalled = false;
let importReviewNavigationInstalled = false;

function configureReceiptInput() {
  const input = document.getElementById('receipt-file');
  if (input) input.multiple = true;
}

function installScanSetCapture() {
  if (scanSetCaptureInstalled) return;
  scanSetCaptureInstalled = true;

  // After the first selection established the purchases context, every later selection (including a
  // second camera capture from the open scan-set dialog) is claimed here before purchases.js can start
  // another Purchase. This is what makes "photo top -> add page -> photo bottom" one logical receipt.
  document.addEventListener('change', event => {
    const input = event.target;
    if (!(input instanceof HTMLInputElement) || input.id !== 'receipt-file' || !latestContext) return;
    const selected = [...(input.files || [])];
    if (!selected.length) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    input.value = '';
    addReceiptScanFiles(latestContext, selected)
      .catch(error => latestContext.toast?.(error?.message || String(error)));
  }, true);
}

function installImportReviewNavigation() {
  if (importReviewNavigationInstalled) return;
  importReviewNavigationInstalled = true;

  const decorate = () => {
    document.querySelectorAll('.receipt-import-batch').forEach(batch => {
      const stats = batch.querySelectorAll('.receipt-import-stats span');
      const reviewCount = Number.parseInt(stats[3]?.textContent || '0', 10) || 0;
      const actions = batch.querySelector('.receipt-import-actions');
      if (!actions || reviewCount <= 0 || actions.querySelector('[data-review-import]')) return;
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'ghost';
      button.dataset.reviewImport = 'true';
      button.textContent = t('Prüfen', 'Review');
      actions.prepend(button);
    });
  };

  const observer = new MutationObserver(decorate);
  const begin = () => {
    if (!document.body) return;
    observer.observe(document.body, { childList: true, subtree: true });
    decorate();
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', begin, { once: true });
  else begin();

  document.addEventListener('click', event => {
    const button = event.target instanceof Element ? event.target.closest('[data-review-import]') : null;
    if (!button) return;
    document.querySelector('dialog.receipt-import-dialog')?.close();
    const source = document.getElementById('purchase-source');
    if (source instanceof HTMLSelectElement) {
      source.value = '';
      source.dispatchEvent(new Event('change', { bubbles: true }));
    } else {
      document.getElementById('refresh')?.click();
    }
    document.getElementById('purchases-list')?.scrollIntoView({ block: 'start', behavior: 'smooth' });
  });
}

configureReceiptInput();
installScanSetCapture();
installImportReviewNavigation();
if (document.readyState === 'loading')
  document.addEventListener('DOMContentLoaded', configureReceiptInput, { once: true });

export function tryGptReceiptScan(ctx, file) {
  latestContext = ctx;
  configureReceiptInput();
  const input = document.getElementById('receipt-file');
  const selected = [...(input?.files || [])];
  const files = selected.length ? selected : (file ? [file] : []);
  if (input) input.value = '';
  return addReceiptScanFiles(ctx, files).then(result => {
    if (result) return result;
    throw new Error(t('Belegscan abgebrochen.', 'Receipt scan cancelled.'));
  });
}

function t(de, en) {
  return document.documentElement.lang?.toLowerCase().startsWith('en') ? en : de;
}
