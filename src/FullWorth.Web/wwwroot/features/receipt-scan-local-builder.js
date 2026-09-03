import { runReceiptScanExperience } from './receipt-scan-ai.js';

// Collect all physical files locally before FullWorth creates a Purchase/ReceiptScanJob. This keeps
// mobile long-receipt capture reversible: cancel creates no server data; Start uploads the ordered set
// exactly once and then hands ownership to the durable queue.
const MAX_FILES = 20;
let active = null;
let captureInstalled = false;

export function runReceiptScanSet(ctx, firstFile) {
  installCapture();
  const input = document.getElementById('receipt-file');
  const initial = [...(input?.files || [])];
  if (!initial.length && firstFile) initial.push(firstFile);

  if (active && !active.finished) {
    appendFiles(active, initial);
    return active.promise;
  }

  active = createDraft(ctx, initial);
  return active.promise;
}

function installCapture() {
  if (captureInstalled) return;
  captureInstalled = true;
  document.addEventListener('change', event => {
    const input = event.target;
    if (!(input instanceof HTMLInputElement) || input.id !== 'receipt-file' || !active || active.finished) return;
    // A collection dialog owns later camera/file selections. Prevent purchases.js from starting a
    // second scan handler for the newly selected page.
    event.preventDefault();
    event.stopImmediatePropagation();
    const files = [...(input.files || [])];
    input.value = '';
    appendFiles(active, files);
  }, true);
}

function createDraft(ctx, files) {
  const draft = {
    ctx,
    files: [],
    dialog: document.createElement('dialog'),
    finished: false,
    resolve: null,
    reject: null,
    promise: null
  };
  draft.promise = new Promise((resolve, reject) => { draft.resolve = resolve; draft.reject = reject; });
  draft.dialog.className = 'receipt-local-set-dialog';
  draft.dialog.addEventListener('cancel', event => { event.preventDefault(); cancel(draft); });
  document.body.appendChild(draft.dialog);
  appendFiles(draft, files);
  if (!draft.dialog.open) draft.dialog.showModal();
  return draft;
}

function appendFiles(draft, files) {
  for (const file of files || []) {
    if (!file || draft.files.some(existing => sameFile(existing, file))) continue;
    if (draft.files.length >= MAX_FILES) {
      draft.ctx.toast?.(t(`Maximal ${MAX_FILES} Dateien pro Beleg.`, `Maximum ${MAX_FILES} files per receipt.`));
      break;
    }
    draft.files.push(file);
  }
  render(draft);
}

function render(draft) {
  if (!draft.dialog?.isConnected || draft.finished) return;
  const rows = draft.files.map((file, index) => `<li class="receipt-local-source">
    <span class="receipt-local-order">${index + 1}</span>
    <div class="receipt-local-thumb">${isImage(file) ? `<img data-preview="${index}" alt="">` : '<strong>PDF</strong>'}</div>
    <div class="receipt-local-main"><strong>${esc(file.name || t('Foto', 'Photo'))}</strong><small>${humanBytes(file.size)}${isPdf(file) ? ` · ${t('alle Seiten', 'all pages')}` : ''}</small></div>
    <div class="receipt-local-actions">
      <button type="button" class="ghost" data-up="${index}" ${index === 0 ? 'disabled' : ''} aria-label="${t('Nach oben', 'Move up')}">↑</button>
      <button type="button" class="ghost" data-down="${index}" ${index === draft.files.length - 1 ? 'disabled' : ''} aria-label="${t('Nach unten', 'Move down')}">↓</button>
      <button type="button" class="ghost" data-remove="${index}" aria-label="${t('Entfernen', 'Remove')}">×</button>
    </div>
  </li>`).join('');

  draft.dialog.innerHTML = `<div class="dialog-card receipt-local-card">
    <div class="panel-head"><div><span class="row-sub">FullWorth Scan-Set</span><h2>${t('Ein Beleg · mehrere Seiten', 'One receipt · multiple pages')}</h2></div><button type="button" class="ghost" data-cancel aria-label="${t('Abbrechen', 'Cancel')}">×</button></div>
    <p>${t('Fotografiere einen langen Bon abschnittsweise. Erst beim Start werden alle Bilder/PDFs gemeinsam als genau ein Kauf gespeichert.', 'Photograph a long receipt section by section. Only Start stores all images/PDFs together as exactly one purchase.')}</p>
    <ol class="receipt-local-list">${rows || `<li class="state-empty">${t('Noch keine Seite ausgewählt.', 'No page selected yet.')}</li>`}</ol>
    <div class="receipt-local-add"><button type="button" class="ghost" data-add>${t('+ Weitere Seite / Foto', '+ Add page / photo')}</button><span class="row-sub">${draft.files.length}/${MAX_FILES}</span></div>
    <div class="dialog-actions"><button type="button" class="ghost" data-cancel>${t('Abbrechen', 'Cancel')}</button><button type="button" data-start ${draft.files.length ? '' : 'disabled'}>${draft.files.length <= 1 ? t('Beleg analysieren', 'Analyze receipt') : t(`${draft.files.length} Dateien als einen Beleg analysieren`, `Analyze ${draft.files.length} files as one receipt`)}</button></div>
  </div>`;

  draft.dialog.querySelectorAll('[data-cancel]').forEach(button => button.onclick = () => cancel(draft));
  draft.dialog.querySelector('[data-add]')?.addEventListener('click', () => {
    const input = document.getElementById('receipt-file');
    if (!input) return;
    input.multiple = true;
    input.click();
  });
  draft.dialog.querySelector('[data-start]')?.addEventListener('click', () => submit(draft));
  draft.dialog.querySelectorAll('[data-remove]').forEach(button => button.onclick = () => { draft.files.splice(Number(button.dataset.remove), 1); render(draft); });
  draft.dialog.querySelectorAll('[data-up]').forEach(button => button.onclick = () => move(draft, Number(button.dataset.up), -1));
  draft.dialog.querySelectorAll('[data-down]').forEach(button => button.onclick = () => move(draft, Number(button.dataset.down), 1));
  hydratePreviews(draft);
  ensureStyle();
}

function move(draft, index, delta) {
  const target = index + delta;
  if (index < 0 || target < 0 || index >= draft.files.length || target >= draft.files.length) return;
  [draft.files[index], draft.files[target]] = [draft.files[target], draft.files[index]];
  render(draft);
}

async function submit(draft) {
  if (draft.finished || !draft.files.length) return;
  draft.finished = true;
  const files = [...draft.files];
  close(draft);
  if (active === draft) active = null;

  const input = document.getElementById('receipt-file');
  try {
    // The durable scanner already supports an ordered multi-file input. Populate its normal input so
    // it receives the complete local set rather than only the original first change event.
    if (input && typeof DataTransfer === 'function') {
      const transfer = new DataTransfer();
      files.forEach(file => transfer.items.add(file));
      input.files = transfer.files;
    }
    const completion = runReceiptScanExperience(draft.ctx, files[0]);
    // "Analyze" is one user action. The durable dialog first waits until every file is committed; once
    // the server draft is ready, trigger its real Start button. No fake client-side processing occurs.
    autoStartDurableDraft();
    draft.resolve?.(await completion);
  } catch (error) {
    draft.reject?.(error);
  } finally {
    if (input) input.value = '';
    draft.resolve = null;
    draft.reject = null;
  }
}

function autoStartDurableDraft() {
  const deadline = Date.now() + 30000;
  const tryStart = () => {
    const button = document.querySelector('.receipt-ai-dialog[open] [data-start]');
    if (button instanceof HTMLButtonElement && !button.disabled) { button.click(); return true; }
    return false;
  };
  if (tryStart()) return;
  const observer = new MutationObserver(() => {
    if (tryStart() || Date.now() > deadline) observer.disconnect();
  });
  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['disabled', 'open'] });
  setTimeout(() => observer.disconnect(), 31000);
}

function cancel(draft) {
  if (draft.finished) return;
  draft.finished = true;
  close(draft);
  if (active === draft) active = null;
  const input = document.getElementById('receipt-file');
  if (input) input.value = '';
  // Reject rather than resolve null: purchases.js must not enter its historical single-file fallback
  // after an intentional cancellation. The existing handler shows a compact cancellation toast only.
  draft.reject?.(new Error(t('Belegscan abgebrochen.', 'Receipt scan cancelled.')));
  draft.resolve = null;
  draft.reject = null;
}

async function hydratePreviews(draft) {
  for (let index = 0; index < draft.files.length; index++) {
    const image = draft.dialog.querySelector(`[data-preview="${index}"]`);
    const file = draft.files[index];
    if (!(image instanceof HTMLImageElement) || !isImage(file)) continue;
    const url = URL.createObjectURL(file);
    image.onload = image.onerror = () => URL.revokeObjectURL(url);
    image.src = url;
  }
}

function close(draft) { if (draft.dialog?.open) draft.dialog.close(); draft.dialog?.remove(); }
function isImage(file) { return file?.type?.startsWith('image/') || /\.(jpe?g|png|webp|heic)$/i.test(file?.name || ''); }
function isPdf(file) { return file?.type === 'application/pdf' || /\.pdf$/i.test(file?.name || ''); }
function sameFile(a, b) { return a === b || (!!a && !!b && a.name === b.name && a.size === b.size && a.lastModified === b.lastModified); }
function humanBytes(value) { const bytes = Number(value || 0); if (bytes < 1024) return `${bytes} B`; if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`; return `${(bytes / 1024 / 1024).toFixed(1)} MB`; }
function esc(value) { return String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]); }
function t(de, en) { return (document.documentElement.lang || localStorage.getItem('finance.language') || 'de').toLowerCase().startsWith('de') ? de : en; }

function ensureStyle() {
  // Production CSP forbids JS-created inline <style> blocks (style-src 'self'); load the module's
  // CSS as a same-origin linked stylesheet instead, exactly once.
  if (document.querySelector('link[data-feature-css="receipt-scan-local-builder"]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/features/receipt-scan-local-builder.css';
  link.dataset.featureCss = 'receipt-scan-local-builder';
  document.head.appendChild(link);
}
