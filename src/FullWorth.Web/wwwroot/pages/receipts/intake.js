// Belege annehmen: Foto-Scan-Dialog, Kamera und Import. Das Dateifeld #receipt-file steht auf der Seite; was die
// Auswahl damit macht, entscheidet dieses Modul: mehrere Fotos EINES Belegs werden zu einem Scan-Set.

import { ensureReceiptImportsLauncher } from './receipt-imports.js';
import { refreshReceiptImportBatchDetails } from './receipt-import-batch-details.js';
import { addReceiptScanFiles } from './receipt-scan-set.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';

let latestContext = null;
let installed = false;

function installScanSetCapture() {
  // Jede Auswahl im Dateifeld - auch eine weitere Aufnahme aus dem offenen Scan-Dialog - wird hier abgefangen,
  // bevor eine zweite Buchung entsteht. So wird aus "Foto oben -> Seite hinzufuegen -> Foto unten" ein Beleg.
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

function refreshImportReviewNavigation() {
  document.querySelectorAll('.receipt-import-batch').forEach(batch => {
    const stats = batch.querySelectorAll('.receipt-import-stats span');
    const reviewCount = Number.parseInt(stats[3]?.textContent || '0', 10) || 0;
    const actions = batch.querySelector('.receipt-import-actions');
    if (!actions || reviewCount <= 0 || actions.querySelector('[data-review-import]')) return;
    const button = document.createElement('button');
    button.type = 'button';
    button.className = buttonClass(ButtonRole.Secondary);
    button.dataset.reviewImport = 'true';
    button.textContent = t('Prüfen', 'Review');
    actions.prepend(button);
  });
}

function installImportReviewNavigation() {
  document.addEventListener('click', event => {
    const button = event.target instanceof Element ? event.target.closest('[data-review-import]') : null;
    if (!button) return;
    document.querySelector('dialog.receipt-import-dialog')?.close();
    document.dispatchEvent(new CustomEvent('fullworth:receipts-show-review'));
  });
  document.addEventListener('fullworth:receipt-imports-rendered', () => {
    refreshImportReviewNavigation();
    refreshReceiptImportBatchDetails();
  });
}

export function initializeReceiptIntake(ctx) {
  latestContext = ctx;
  const input = document.getElementById('receipt-file');
  if (input) input.multiple = true;
  if (!installed) {
    installed = true;
    installScanSetCapture();
    installImportReviewNavigation();
  }
  ensureReceiptImportsLauncher();
  refreshImportReviewNavigation();
  refreshReceiptImportBatchDetails();
}

function t(de, en) {
  return document.documentElement.lang?.toLowerCase().startsWith('en') ? en : de;
}
