import { createDialog } from '../../components/dialog.js';
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
  draft.dialog = createDialog('<div class="dialog-card receipt-set-card"></div>', {
    className: 'receipt-set-dialog',
    closeLabel: t('Schließen', 'Close')
  });
  draft.dialog.addEventListener('cancel', event => {
    event.preventDefault();
    if (draft.state === 'collecting') cancelDraft(draft);
    else backgroundDraft(draft);
  });
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

  // One page is the normal case, and it used to get the whole multi-page apparatus: a numbered list,
  // disabled ↑/↓ buttons, a remove ×, a file counter, a paragraph explaining section-by-section
  // photography, and a primary button whose label is a sentence. Nothing there is wrong for four
  // pages; for one photo it is furniture between the owner and the answer.
  //
  // The step itself stays. Seeing the photo before it is analysed is the point: a blurry capture
  // found here costs one tap, found after the extraction it costs the whole round trip. So the
  // single-page view keeps the preview and drops everything that only means something with a
  // second page — including the reorder buttons, which cannot do anything to a list of one.
  //
  // "+ Weitere Seite" stays in both views: it is what turns one capture into a multi-page receipt,
  // and without it the shorter view would quietly cost a capability.
  const single = files.length === 1;
  const heading = single
    ? t('Beleg prüfen', 'Check the receipt')
    : t('Ein Beleg · mehrere Seiten', 'One receipt · multiple pages');
  const lead = single
    ? ''
    : `<p>${t('Fotografiere einen langen Bon abschnittsweise. Alle Bilder und alle PDF-Seiten werden gemeinsam als ein Einkauf analysiert.', 'Photograph a long receipt section by section. All images and every PDF page are analyzed together as one purchase.')}</p>`;
  const sources = single
    ? `<div class="receipt-set-single">${singleRow(files[0])}</div>`
    : `<ol class="receipt-set-sources">${rows || `<li class="receipt-set-empty">${t('Noch keine Seite ausgewählt.', 'No page selected yet.')}</li>`}</ol>`;
  const counter = single ? '' : `<span class="row-sub">${files.length}/${MAX_FILES} ${t('Dateien', 'files')}</span>`;
  const startLabel = files.length === 1
    ? t('Analysieren', 'Analyze')
    : t(`${files.length} Dateien als einen Beleg analysieren`, `Analyze ${files.length} files as one receipt`);

  dialog.innerHTML = `<div class="dialog-card receipt-set-card">
    <div class="panel-head"><div><span class="row-sub">FullWorth Scan-Set</span><h2>${heading}</h2></div><button type="button" class="ghost" data-cancel aria-label="${t('Abbrechen', 'Cancel')}">×</button></div>
    ${lead}
    ${sources}
    <div class="receipt-set-add-row">
      <button type="button" class="ghost" data-add>${t('+ Weitere Seite / Foto', '+ Add page / photo')}</button>
      ${counter}
    </div>
    <div class="dialog-actions receipt-set-actions">
      <button type="button" class="ghost" data-cancel>${t('Abbrechen', 'Cancel')}</button>
      <button type="button" data-start ${files.length ? '' : 'disabled'}>${startLabel}</button>
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

function singleRow(file) {
  const preview = isImage(file)
    ? `<img data-file-preview="0" alt="">`
    : `<div class="receipt-set-pdf">PDF</div>`;
  return `<div class="receipt-set-single-thumb">${preview}</div>
    <div class="receipt-set-single-main">
      <strong>${esc(file.name || t('Foto', 'Photo'))}</strong>
      <small>${humanBytes(file.size)}${isPdf(file) ? ` · ${t('alle PDF-Seiten', 'all PDF pages')}` : ''}</small>
    </div>`;
  // No remove button here on purpose: with one page, removing it and cancelling are the same act, and
  // two × in one dialog makes the reader choose between them for nothing.
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
    await followDraft(draft, clientJobId);
  } catch (error) {
    failDraft(draft, error);
  }
}

/**
 * Den laufenden Auftrag beobachten, bis er fertig ist. Getrennt vom Hochladen, damit ein
 * "Erneut versuchen" nach einem Abbruch WEITER beobachten kann statt dieselben Bilder ein zweites
 * Mal hochzuladen - das ergaebe zwei Belege aus einem Einkauf.
 */
async function followDraft(draft, clientJobId = null) {
  try {
    while (draft.row && draft.row.status !== 'done' && draft.row.status !== 'error') {
      setStatus(draft, stageLabel(draft.row.stage, draft.row.engine));
      setEngine(draft);
      await sleep(POLL_MS);
      draft.row = await draft.ctx.api(`api/purchases/receipt-scan/jobs/${draft.row.id || clientJobId}`);
    }
    setEngine(draft);

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
    failDraft(draft, error);
  }
}

// Der Fehler beendet den Vorgang NICHT mehr. Die Dateien liegen noch hier, der Auftrag oft auch -
// also bleibt der Dialog stehen und bietet beides an: noch einmal, oder aufgeben. Erst das Aufgeben
// meldet den Fehler nach aussen.
function failDraft(draft, error) {
  draft.state = 'error';
  draft.error = error instanceof Error ? error : new Error(String(error));
  renderProgress(draft);
}

// Waehrend der Verarbeitung blieb von dem Beleg, den man gerade fotografiert hat, eine Liste von
// Dateinamen uebrig - das Bild war weg, obwohl es direkt daneben liegt und nichts kostet. Genau
// darum geht es in #129: das Dokument bleibt sichtbar, und die Analyse laeuft darueber.
function renderProgress(draft) {
  const dialog = draft.dialog;
  if (!dialog?.isConnected) return;

  const failed = draft.state === 'error';
  const single = draft.files.length === 1;
  const sources = single
    ? `<div class="receipt-set-single">${singleRow(draft.files[0])}</div>`
    : `<ol class="receipt-set-sources">${draft.files.map((file, index) => `<li class="receipt-set-source">
        <span class="receipt-set-order">${index + 1}</span>
        <div class="receipt-set-thumb">${isImage(file) ? `<img data-file-preview="${index}" alt="">` : `<div class="receipt-set-pdf">PDF</div>`}</div>
        <div class="receipt-set-source-main"><strong>${esc(file.name || t('Foto', 'Photo'))}</strong></div>
      </li>`).join('')}</ol>`;

  const progress = failed
    // Ein Fehler ist keine Sackgasse: dieselben Dateien liegen noch hier, also kann man es noch
    // einmal versuchen, ohne neu zu fotografieren.
    ? `<div class="receipt-set-progress is-error"><strong data-status class="is-error">${esc(draft.error?.message || String(draft.error || ''))}</strong></div>
       <div class="dialog-actions receipt-set-actions">
         <button type="button" class="ghost" data-give-up>${t('Abbrechen', 'Cancel')}</button>
         <button type="button" data-retry>${t('Erneut versuchen', 'Try again')}</button>
       </div>`
    : `<div class="receipt-set-progress"><span class="receipt-set-spinner" aria-hidden="true"></span><strong data-status>${t('Vorbereitung …', 'Preparing …')}</strong></div>`;

  dialog.innerHTML = `<div class="dialog-card receipt-set-card">
    <div class="panel-head"><div><span class="row-sub">FullWorth Scan-Set</span><h2>${failed
      ? t('Verarbeitung fehlgeschlagen', 'Processing failed')
      : t('Ein Beleg wird verarbeitet', 'Processing one receipt')}</h2></div>${failed
      ? ''
      : `<button type="button" class="ghost" data-background>${t('Im Hintergrund', 'Background')}</button>`}</div>
    <p data-meta>${t(`${draft.files.length} Dateien werden gemeinsam verarbeitet.`, `${draft.files.length} files are processed together.`)}</p>
    ${sources}
    <div data-engine class="row-sub receipt-set-engine">${engineLine(draft)}</div>
    ${progress}
  </div>`;

  dialog.querySelector('[data-background]')?.addEventListener('click', () => backgroundDraft(draft));
  dialog.querySelector('[data-retry]')?.addEventListener('click', () => retryDraft(draft));
  dialog.querySelector('[data-give-up]')?.addEventListener('click', () => abandonDraft(draft));
  hydratePreviews(dialog, draft.files);
}

/**
 * Welche Strecke gerade laeuft - und das soll sie sagen, bevor sie fertig ist.
 *
 * Die Forderung aus #129 ist ausdruecklich in beide Richtungen: KI muss sichtbar sein, wenn sie
 * benutzt wird, und OCR muss ebenso sichtbar sein, wenn KEINE KI beteiligt war. Ein Ergebnis, das
 * nach KI aussieht, aber regelbasiert entstand, ist die schlimmere Haelfte davon.
 */
function engineLine(draft) {
  const stage = draft.row?.stage;
  const engine = draft.row?.engine;
  if (!stage) return t('Strecke steht noch nicht fest.', 'Pipeline not decided yet.');
  if (stage === 'ocr')
    return t(`Texterkennung${engine ? ` (${engine})` : ''} – keine KI beteiligt.`,
             `Text recognition${engine ? ` (${engine})` : ''} — no AI involved.`);
  if (stage === 'connecting' || stage === 'analyzing' || stage === 'structuring')
    return t('KI-Analyse (GPT) liest den Beleg.', 'AI analysis (GPT) is reading the receipt.');
  return t('Vorbereitung – Strecke wird gewählt.', 'Preparing — choosing the pipeline.');
}

function retryDraft(draft) {
  draft.error = null;
  // Gab es den Auftrag schon, wird weiter beobachtet statt neu hochgeladen - sonst entstuende ein
  // zweiter Beleg aus denselben Bildern.
  if (draft.row?.id) {
    draft.state = 'processing';
    renderProgress(draft);
    void followDraft(draft);
    return;
  }
  draft.state = 'collecting';
  void submitDraft(draft);
}

function abandonDraft(draft) {
  const error = draft.error || new Error(t('Verarbeitung abgebrochen.', 'Processing cancelled.'));
  draft.finished = true;
  draft.reject?.(error);
  draft.resolve = null;
  draft.reject = null;
  closeDialog(draft);
  if (activeDraft === draft) activeDraft = null;
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
}

function setStatus(draft, text, error = false) {
  const node = draft.dialog?.querySelector('[data-status]');
  if (!node) return;
  node.textContent = text;
  node.classList.toggle('is-error', error);
}

function setEngine(draft) {
  const node = draft.dialog?.querySelector('[data-engine]');
  if (node) node.textContent = engineLine(draft);
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

function isImage(file) { return String(file?.type || '').startsWith('image/') || /\.(jpe?g|png|webp|heic)$/i.test(file?.name || ''); }
function isPdf(file) { return file?.type === 'application/pdf' || /\.pdf$/i.test(file?.name || ''); }
function humanBytes(bytes) { const n = Number(bytes || 0); if (n < 1024) return `${n} B`; if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`; return `${(n / 1024 / 1024).toFixed(1)} MB`; }
function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }
function t(de, en) { return document.documentElement.lang?.toLowerCase().startsWith('en') ? en : de; }
function esc(value) { const div = document.createElement('div'); div.textContent = String(value ?? ''); return div.innerHTML; }
