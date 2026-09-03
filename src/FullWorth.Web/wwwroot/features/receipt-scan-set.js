// FullWorth receipt scan-set builder.
//
// One logical receipt may contain several independently captured photos or one/more PDFs. Files are
// collected locally first so mobile users can photograph a long receipt section-by-section, review the
// order, remove mistakes and only then create ONE durable ReceiptScanJob/Purchase on the server.

const MAX_FILES = 20;
const POLL_MS = 1200;
let activeDraft = null;

export function runReceiptScanSet(ctx, files) {
  return addReceiptScanFiles(ctx, files);
}

export function addReceiptScanFiles(ctx, files) {
  const incoming = [...(files || [])].filter(Boolean);
  if (!incoming.length) return activeDraft?.promise || Promise.resolve(null);

  if (!activeDraft || activeDraft.finished) activeDraft = createDraft(ctx);
  else activeDraft.ctx = ctx;

  for (const file of incoming) {
    if (activeDraft.files.length >= MAX_FILES) {
      activeDraft.ctx.toast?.(t(`Maximal ${MAX_FILES} Dateien pro Beleg.`, `Maximum ${MAX_FILES} files per receipt.`));
      break;
    }
    activeDraft.files.push(file);
  }
  renderDraft(activeDraft);
  return activeDraft.promise;
}

function createDraft(ctx) {
  const draft = {
    ctx,
    files: [],
    dialog: null,
    state: 'collecting',
    row: null,
    finished: false,
    resolve: null,
    reject: null,
    promise: null
  };
  draft.promise = new Promise((resolve, reject) => { draft.resolve = resolve; draft.reject = reject; });
  draft.dialog = document.createElement('dialog');
  draft.dialog.className = 'receipt-set-dialog';
  draft.dialog.addEventListener('cancel', event => {
    event.preventDefault();
    if (draft.state === 'collecting') cancelDraft(draft);
    else backgroundDraft(draft);
  });
  document.body.appendChild(draft.dialog);
  ensureCss();
  draft.dialog.showModal();
  return draft;
}

function renderDraft(draft) {
  const { dialog, files } = draft;
  if (!dialog?.isConnected) return;

  if (draft.state !== 'collecting') {
    renderProgress(draft);
    return;
  }

  const rows = files.map((file, index) => {
    const image = isImage(file)
      ? `<img data-file-preview="${index}" alt="">`
      : `<div class="receipt-set-pdf">PDF</div>`;
    return `<li class="receipt-set-source" data-index="${index}">
      <span class="receipt-set-order">${index + 1}</span>
      <div class="receipt-set-thumb">${image}</div>
      <div class="receipt-set-source-main"><strong>${esc(file.name || t('Foto', 'Photo'))}</strong><small>${humanBytes(file.size)}${isPdf(file) ? ` · ${t('alle PDF-Seiten', 'all PDF pages')}` : ''}</small></div>
      <div class="receipt-set-source-actions">
        <button type="button" class="ghost" data-up="${index}" ${index === 0 ? 'disabled' : ''} aria-label="${t('Nach oben', 'Move up')}">↑</button>
        <button type="button" class="ghost" data-down="${index}" ${index === files.length - 1 ? 'disabled' : ''} aria-label="${t('Nach unten', 'Move down')}">↓</button>
        <button type="button" class="ghost" data-remove="${index}" aria-label="${t('Entfernen', 'Remove')}">×</button>
      </div>
    </li>`;
  }).join('');

  dialog.innerHTML = `<div class="dialog-card receipt-set-card">
    <div class="panel-head"><div><span class="row-sub">FullWorth Scan-Set</span><h2>${t('Ein Beleg · mehrere Seiten', 'One receipt · multiple pages')}</h2></div><button type="button" class="ghost" data-cancel aria-label="${t('Abbrechen', 'Cancel')}">×</button></div>
    <p>${t('Fotografiere einen langen Bon abschnittsweise. Alle Bilder und alle PDF-Seiten werden gemeinsam als ein Einkauf analysiert.', 'Photograph a long receipt section by section. All images and every PDF page are analyzed together as one purchase.')}</p>
    <ol class="receipt-set-sources">${rows || `<li class="receipt-set-empty">${t('Noch keine Seite ausgewählt.', 'No page selected yet.')}</li>`}</ol>
    <div class="receipt-set-add-row">
      <button type="button" class="ghost" data-add>${t('+ Weitere Seite / Foto', '+ Add page / photo')}</button>
      <span class="row-sub">${files.length}/${MAX_FILES} ${t('Dateien', 'files')}</span>
    </div>
    <div class="dialog-actions receipt-set-actions">
      <button type="button" class="ghost" data-cancel>${t('Abbrechen', 'Cancel')}</button>
      <button type="button" data-start ${files.length ? '' : 'disabled'}>${files.length === 1 ? t('Beleg analysieren', 'Analyze receipt') : t(`${files.length} Dateien als einen Beleg analysieren`, `Analyze ${files.length} files as one receipt`)}</button>
    </div>
  </div>`;

  dialog.querySelectorAll('[data-cancel]').forEach(button => button.addEventListener('click', () => cancelDraft(draft)));
  dialog.querySelector('[data-add]')?.addEventListener('click', () => {
    const input = document.getElementById('receipt-file');
    if (!input) return;
    input.multiple = true;
    input.click();
  });
  dialog.querySelector('[data-start]')?.addEventListener('click', () => submitDraft(draft));
  dialog.querySelectorAll('[data-remove]').forEach(button => button.addEventListener('click', () => {
    draft.files.splice(Number(button.dataset.remove), 1);
    renderDraft(draft);
  }));
  dialog.querySelectorAll('[data-up]').forEach(button => button.addEventListener('click', () => move(draft, Number(button.dataset.up), -1)));
  dialog.querySelectorAll('[data-down]').forEach(button => button.addEventListener('click', () => move(draft, Number(button.dataset.down), 1)));
  hydratePreviews(dialog, files);
}

function move(draft, index, delta) {
  const target = index + delta;
  if (index < 0 || target < 0 || index >= draft.files.length || target >= draft.files.length) return;
  [draft.files[index], draft.files[target]] = [draft.files[target], draft.files[index]];
  renderDraft(draft);
}

async function hydratePreviews(dialog, files) {
  for (let index = 0; index < files.length; index++) {
    const image = dialog.querySelector(`[data-file-preview="${index}"]`);
    if (!(image instanceof HTMLImageElement) || !isImage(files[index])) continue;
    try {
      const url = URL.createObjectURL(files[index]);
      image.onload = () => URL.revokeObjectURL(url);
      image.onerror = () => URL.revokeObjectURL(url);
      image.src = url;
    } catch { /* filename remains enough */ }
  }
}

async function submitDraft(draft) {
  if (draft.state !== 'collecting' || !draft.files.length) return;
  draft.state = 'uploading';
  renderProgress(draft);

  const clientJobId = crypto.randomUUID();
  const form = new FormData();
  for (const file of draft.files) form.append('receipt', file, file.name);
  form.append('currency', 'EUR');
  form.append('clientJobId', clientJobId);

  try {
    setStatus(draft, t('Scan-Set wird sicher gespeichert …', 'Securely storing scan set …'));
    try {
      draft.row = await draft.ctx.api('api/purchases/receipt-scan/jobs', { method: 'POST', body: form });
    } catch (error) {
      try { draft.row = await draft.ctx.api(`api/purchases/receipt-scan/jobs/${clientJobId}`); }
      catch { throw error; }
    }

    const sourceCount = Number(draft.row?.sourceCount || draft.files.length);
    setMeta(draft, t(`${sourceCount} Seiten/Bilder · ein Beleg`, `${sourceCount} pages/images · one receipt`));
    draft.state = 'processing';

    while (draft.row && draft.row.status !== 'done' && draft.row.status !== 'error') {
      setStatus(draft, stageLabel(draft.row.stage, draft.row.engine));
      await sleep(POLL_MS);
      draft.row = await draft.ctx.api(`api/purchases/receipt-scan/jobs/${draft.row.id || clientJobId}`);
    }

    if (!draft.row?.purchaseId) throw new Error(t('Scan-Job enthält keinen Kauf.', 'Scan job has no purchase.'));
    setStatus(draft, draft.row.status === 'done'
      ? t('Analyse abgeschlossen.', 'Analysis complete.')
      : t('Beleg gespeichert – manuelle Prüfung nötig.', 'Receipt saved — manual review required.'));

    const result = { id: draft.row.purchaseId, status: draft.row.status === 'done' ? 'review' : 'captured' };
    draft.finished = true;
    draft.resolve?.(result);
    draft.resolve = null;
    draft.reject = null;
    notifyPurchaseRefresh(draft.ctx);
    await sleep(220);
    closeDialog(draft);
    if (activeDraft === draft) activeDraft = null;
  } catch (error) {
    draft.state = 'error';
    setStatus(draft, error?.message || String(error), true);
    draft.reject?.(error);
    draft.resolve = null;
    draft.reject = null;
    draft.finished = true;
    if (activeDraft === draft) activeDraft = null;
  }
}

function renderProgress(draft) {
  const dialog = draft.dialog;
  if (!dialog?.isConnected) return;
  const sourceNames = draft.files.map((file, index) => `<li><span>${index + 1}</span>${esc(file.name)}</li>`).join('');
  dialog.innerHTML = `<div class="dialog-card receipt-set-card">
    <div class="panel-head"><div><span class="row-sub">FullWorth Scan-Set</span><h2>${t('Ein Beleg wird verarbeitet', 'Processing one receipt')}</h2></div><button type="button" class="ghost" data-background>${t('Im Hintergrund', 'Background')}</button></div>
    <p data-meta>${t(`${draft.files.length} Dateien werden gemeinsam verarbeitet.`, `${draft.files.length} files are processed together.`)}</p>
    <ol class="receipt-set-sources compact">${sourceNames}</ol>
    <div class="receipt-set-progress"><span class="receipt-set-spinner" aria-hidden="true"></span><strong data-status>${t('Vorbereitung …', 'Preparing …')}</strong></div>
  </div>`;
  dialog.querySelector('[data-background]')?.addEventListener('click', () => backgroundDraft(draft));
}

function backgroundDraft(draft) {
  if (draft.dialog?.open) draft.dialog.close();
  draft.ctx.toast?.(t('Beleg wird im Hintergrund weiterverarbeitet.', 'Receipt continues processing in the background.'));
}

function cancelDraft(draft) {
  if (draft.state !== 'collecting') return backgroundDraft(draft);
  draft.finished = true;
  draft.resolve?.(null);
  draft.resolve = null;
  draft.reject = null;
  closeDialog(draft);
  if (activeDraft === draft) activeDraft = null;
}

function closeDialog(draft) {
  if (!draft.dialog) return;
  if (draft.dialog.open) draft.dialog.close();
  draft.dialog.remove();
}

function setStatus(draft, text, error = false) {
  const node = draft.dialog?.querySelector('[data-status]');
  if (!node) return;
  node.textContent = text;
  node.classList.toggle('is-error', error);
}

function setMeta(draft, text) {
  const node = draft.dialog?.querySelector('[data-meta]');
  if (node) node.textContent = text;
}

function notifyPurchaseRefresh(ctx) {
  document.getElementById('purchase-source')?.dispatchEvent(new Event('change'));
  ctx.reload?.();
}

function stageLabel(stage, engine) {
  const labels = {
    queued: t('Wartet auf Server …', 'Waiting on server …'),
    preparing: t('Seiten werden vorbereitet …', 'Preparing pages …'),
    connecting: t('GPT-Verbindung wird geprüft …', 'Checking GPT connection …'),
    analyzing: t('GPT analysiert alle Seiten gemeinsam …', 'GPT is analyzing all pages together …'),
    structuring: t('Artikel und Überlappungen werden zusammengeführt …', 'Merging items and overlaps …'),
    ocr: t('Lokales OCR verarbeitet alle Seiten …', 'Local OCR is processing all pages …'),
    saving: t('Ergebnis wird gespeichert …', 'Saving result …')
  };
  const base = labels[stage] || t('Beleg wird verarbeitet …', 'Processing receipt …');
  return engine && stage === 'ocr' ? `${base} (${engine})` : base;
}

function ensureCss() {
  // Production CSP forbids JS-created inline <style> blocks (style-src 'self'); load the module's
  // CSS as a same-origin linked stylesheet instead, exactly once.
  if (document.querySelector('link[data-feature-css="receipt-scan-set"]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/features/receipt-scan-set.css';
  link.dataset.featureCss = 'receipt-scan-set';
  document.head.appendChild(link);
}

function isImage(file) { return String(file?.type || '').startsWith('image/') || /\.(jpe?g|png|webp|heic)$/i.test(file?.name || ''); }
function isPdf(file) { return file?.type === 'application/pdf' || /\.pdf$/i.test(file?.name || ''); }
function humanBytes(bytes) { const n = Number(bytes || 0); if (n < 1024) return `${n} B`; if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`; return `${(n / 1024 / 1024).toFixed(1)} MB`; }
function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }
function t(de, en) { return document.documentElement.lang?.toLowerCase().startsWith('en') ? en : de; }
function esc(value) { const div = document.createElement('div'); div.textContent = String(value ?? ''); return div.innerHTML; }
