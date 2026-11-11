// Die Pruefansicht eines Belegs: der Bon, wie er auf Papier aussieht, neben dem Original, und dazu die
// Warteschlange - wer 367 Belege zu pruefen hat, geht sie hintereinander durch, ohne die Ansicht zu
// verlassen. Am Rechner stehen Original und Bon nebeneinander; am Telefon wechselt ein Schalter zwischen
// Bon, Original und beidem uebereinander ("Geteilt"). Eine Position wird dort bearbeitet, wo sie steht
// (am Telefon als Blatt von unten).
//
// Alles, was ein Beleg sonst noch kann (Bankbuchung, Amazon, Paperless, Preisverlauf), liegt in
// review-tools.js; hier steht nur die Ansicht.
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { confirmMessage } from '../../components/confirm.js';
import { createDialog } from '../../components/dialog.js';
import { createDocumentViewer } from '../../components/document-viewer.js';
import { attachCombobox } from '../../components/combobox.js';
import { categoryComboboxItems } from '../../components/category-combobox.js';
import { spriteHref } from '../../components/sprite.js';
import { bindReviewTools, bindPaperlessUpload, loadPriceHistory, amazonReconciliation, amazonDetailsHtml, bindAmazonDetails, renderLink } from './review-tools.js';
import { openPurchaseWorkspace } from './articles-workspace.js';

const t = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en;
const JSON_HEADERS = { 'Content-Type': 'application/json' };
const MODE_KEY = 'fullworth.reviewMode';
const MODES = ['bon', 'original', 'split'];
const icon = (id, cls = '') => `<svg class="rv-icon ${cls}" viewBox="0 0 24 24" aria-hidden="true"><use href="${spriteHref(id)}"></use></svg>`;
const round2 = value => Math.round(value * 100) / 100;

// "1.234,50", "12,5" und "12.5" lesen; alles andere ist keine Zahl.
function parseAmount(text) {
  const raw = String(text ?? '').trim().replace(/\s/g, '');
  if (!raw) return NaN;
  const normalized = raw.includes(',') ? raw.replace(/\./g, '').replace(',', '.') : raw;
  return /^-?\d+(\.\d+)?$/.test(normalized) ? Number(normalized) : NaN;
}

function parseWarnings(json) {
  try { const list = json ? JSON.parse(json) : []; return Array.isArray(list) ? list : []; } catch { return []; }
}

/**
 * Oeffnet die Pruefansicht fuer einen Kauf. `source` ist der Quellfilter der Liste (Belege/Amazon), damit die
 * Warteschlange dieselben Kaeufe durchlaeuft; `onClose(changed)` meldet, ob etwas gespeichert wurde.
 */
export async function openReview(ctx, startId, { source = '', onClose = () => {} } = {}) {
  bindReviewTools(ctx);
  const api = ctx.api;
  const num = new Intl.NumberFormat(document.documentElement.lang || 'de', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const signed = value => (value > 0 ? '+' : value < 0 ? '−' : '') + num.format(Math.abs(value));

  const viewer = createDocumentViewer();
  let purchase = null;
  let rec = null;
  let sources = null;
  let amazon = null;
  let lines = [];
  let savedSum = 0;
  let editing = -1;
  let changed = false;
  let busy = false;
  let loadToken = 0;
  let flagged = new Set();
  let categories = [];
  let categoryById = new Map();
  let categorySelect = '';
  const warnings = new Map();
  let warningsDone = false;
  const queue = { ids: [], total: 0, done: true };

  const dlg = createDialog(`<div class="rv" data-mode="split">
    <header class="rv-head">
      <button type="button" class="${buttonClass(ButtonRole.Icon, 'rv-close')}" data-act="close" aria-label="${t('Schließen', 'Close')}">${icon('ui-close')}</button>
      <div class="rv-title"><strong data-title></strong><small data-sub></small></div>
      <div class="rv-menu-wrap">
        <button type="button" class="${buttonClass(ButtonRole.Icon, 'rv-menu-btn')}" data-act="menu" aria-haspopup="menu" aria-expanded="false" aria-label="${t('Weitere Aktionen', 'More actions')}">${icon('nav-more')}</button>
        <div class="rv-menu" role="menu" hidden>
          <button type="button" role="menuitem" data-act="workspace">${icon('ui-edit')}<span>${t('Alles bearbeiten …', 'Edit everything …')}</span></button>
          <button type="button" role="menuitem" class="paperless-upload" data-paperless-upload>${icon('ui-upload')}<span>${t('In Paperless ablegen', 'Store in Paperless')}</span></button>
          <button type="button" role="menuitem" class="rv-danger" data-act="delete">${icon('ui-trash')}<span>${t('Beleg löschen', 'Delete receipt')}</span></button>
        </div>
      </div>
    </header>
    <div class="rv-modes" role="tablist" aria-label="${t('Ansicht', 'View')}">
      <button type="button" role="tab" data-mode="bon">${t('Bon', 'Receipt')}</button>
      <button type="button" role="tab" data-mode="original">${t('Original', 'Original')}</button>
      <button type="button" role="tab" data-mode="split">${t('Geteilt', 'Split')}</button>
    </div>
    <div class="rv-body">
      <section class="rv-original" aria-label="${t('Original', 'Original')}"></section>
      <section class="rv-bon-wrap" aria-label="${t('Bon', 'Receipt')}">
        <article class="rv-paper">
          <div class="rv-paper-head"><h2 data-merchant></h2><p data-when></p></div>
          <div class="rv-hints" data-hints><span class="dt-sk-line shimmer"></span></div>
          <ol class="rv-lines" data-lines>${'<li class="rv-line"><span class="dt-sk-line shimmer"></span></li>'.repeat(4)}</ol>
          <div class="rv-sum" data-sum></div>
          <div class="rv-bank" data-bank></div>
          <div class="rv-edge" aria-hidden="true"></div>
        </article>
        <div class="rv-extra" data-extra></div>
      </section>
    </div>
    <footer class="rv-bar">
      <div class="rv-bar-info"><span data-bar-sum></span><span class="rv-chip" data-chip></span></div>
      <div class="rv-bar-nav">
        <button type="button" class="${buttonClass(ButtonRole.Secondary, 'rv-nav-btn')}" data-act="prev" aria-label="${t('Zurück', 'Back')}">${icon('nav-chevron', 'rv-flip')}</button>
        <span class="rv-pos" data-pos aria-live="polite"></span>
        <button type="button" class="${buttonClass(ButtonRole.Secondary, 'rv-nav-btn rv-skip')}" data-act="next" aria-label="${t('Überspringen', 'Skip')}"><span>${t('Überspringen', 'Skip')}</span>${icon('nav-chevron')}</button>
      </div>
      <div class="rv-bar-main">
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-act="save" hidden>${t('Speichern', 'Save')}</button>
        <button type="button" class="${buttonClass(ButtonRole.Primary, 'rv-confirm')}" data-act="confirm"></button>
      </div>
    </footer>
    <div class="rv-scrim" data-act="editor-close"></div>
  </div>`, { className: 'rv-dialog', closeLabel: t('Schließen', 'Close') });

  const root = dlg.querySelector('.rv');
  const $ = selector => root.querySelector(selector);
  dlg.setAttribute('aria-label', t('Beleg prüfen', 'Review receipt'));
  $('.rv-original').append(viewer.element);

  let mode = MODES.includes(localStorage.getItem(MODE_KEY)) ? localStorage.getItem(MODE_KEY) : 'split';
  function setMode(next) {
    mode = next;
    root.dataset.mode = mode;
    localStorage.setItem(MODE_KEY, mode);
    root.querySelectorAll('.rv-modes [data-mode]').forEach(button => button.setAttribute('aria-selected', String(button.dataset.mode === mode)));
  }
  setMode(mode);

  // --- Warteschlange ---------------------------------------------------------------------------------

  const pageQuery = offset => `api/purchases?scope=review&limit=100&offset=${offset}${source ? `&source=${encodeURIComponent(source)}` : ''}`;
  async function moreIds() {
    if (queue.done) return;
    const rows = (await api(pageQuery(queue.ids.length))) || [];
    queue.ids.push(...rows.map(row => row.id));
    queue.done = rows.length < 100;
  }
  async function buildQueue(id) {
    queue.ids = []; queue.done = false;
    const counts = await api(`api/purchases/counts${source ? `?source=${encodeURIComponent(source)}` : ''}`).catch(() => null);
    queue.total = counts?.review ?? 0;
    while (!queue.ids.includes(id) && !queue.done) await moreIds();
    if (!queue.ids.includes(id)) { queue.ids = []; queue.total = 0; }
  }
  const index = () => queue.ids.indexOf(purchase?.id);
  const hasQueue = () => queue.ids.length > 0 && index() >= 0;

  // Die Hinweise der Auslese stehen an den Lese-Auftraegen, nicht am Kauf: im Hintergrund seitenweise holen.
  async function loadWarnings() {
    try {
      for (let offset = 0; offset < 1000; offset += 100) {
        const jobs = (await api(`api/purchases/receipt-scan/jobs?scope=review&limit=100&offset=${offset}`)) || [];
        for (const job of jobs) warnings.set(job.purchaseId, { list: parseWarnings(job.warningsJson), engine: job.engine || '' });
        if (!dlg.isConnected) return;
        paintHints();
        paintHead();
        if (jobs.length < 100) return;
      }
    } finally {
      warningsDone = true;
      if (dlg.isConnected) { paintHints(); paintHead(); }
    }
  }

  // --- Zeichnen --------------------------------------------------------------------------------------

  const sumNow = () => round2(lines.reduce((sum, line) => sum + (Number.isFinite(line.total) ? line.total : 0), 0));
  // Die Summe der Positionen, wie der Server sie rechnet (Rabatt, Pfand, Trinkgeld zaehlen anders als eine blosse Summe),
  // verschoben um das, was hier seit dem Speichern geaendert wurde.
  const itemTotalNow = () => round2((rec ? Number(rec.itemTotal) : savedSum) + sumNow() - savedSum);
  // Die Packung ("500 g", "10 Stück") ist optional und gehoert zur Position, nicht in den Namen: erst dann lassen sich
  // "Eier aus Bodenhaltung" ueber verschiedene Packungen vergleichen.
  const lineOf = (item, index) => ({ index, item, name: item.name || '', quantity: Number(item.quantity), total: Number(item.totalPrice), categoryId: item.categoryId || '',
    packQuantity: Number(item.packageQuantity) || null, packUnit: item.packageUnit || 'piece' });
  const packChanged = line => line.packQuantity !== (Number(line.item.packageQuantity) || null) || (line.packQuantity && line.packUnit !== (line.item.packageUnit || 'piece'));
  const isDirty = () => lines.some(line => line.name !== line.item.name || line.quantity !== Number(line.item.quantity)
    || line.total !== Number(line.item.totalPrice) || line.categoryId !== (line.item.categoryId || '') || packChanged(line));
  function difference() {
    const tolerance = Number(rec?.tolerance || 0.01);
    const base = rec ? Number(rec.itemDifference || 0) : round2(Number(purchase.totalAmount) - savedSum);
    const diff = round2(base - (sumNow() - savedSum));
    return { diff, ok: Math.abs(diff) <= tolerance };
  }

  function lineReasons(line) {
    const reasons = [];
    if (flagged.has(line.index)) reasons.push(t('Von der Auslese markiert', 'Flagged by the reader'));
    const unit = Number(line.item.unitPrice);
    const expected = round2(line.quantity * unit);
    if (line.item.unitPrice != null && Number.isFinite(unit) && unit > 0 && Math.abs(expected - line.total) > 0.02 && !line.item.discountAmount)
      reasons.push(t(`Menge × Einzelpreis ergibt ${num.format(expected)}`, `Quantity × unit price is ${num.format(expected)}`));
    if (!(line.total > 0) && (line.item.lineType || 'product') === 'product') reasons.push(t('Kein Preis', 'No price'));
    return reasons;
  }

  const PACK_UNITS = { piece: 'Stück', g: 'g', kg: 'kg', ml: 'ml', l: 'l' };
  const packLabel = line => `${line.item.packageCount && !packChanged(line) ? `${line.item.packageCount} × ` : ''}${String(line.packQuantity).replace('.', ',')} ${PACK_UNITS[line.packUnit] || line.packUnit}`;

  function lineHtml(line) {
    const reasons = lineReasons(line);
    const unit = Number(line.item.unitPrice);
    const calc = [
      line.packQuantity ? packLabel(line) : '',
      line.quantity !== 1 ? `${String(line.quantity).replace('.', ',')} × ${num.format(Number.isFinite(unit) && line.item.unitPrice != null ? unit : round2(line.total / (line.quantity || 1)))}` : '',
      line.item.asin ? `ASIN ${line.item.asin}` : ''
    ].filter(Boolean).join(' · ');
    const category = categoryById.get(line.categoryId);
    const history = line.item.id && line.item.barcode ? `<span class="rv-line-hist" data-price-history-index="${line.index}"></span>` : '';
    return `<button type="button" class="rv-line-main${reasons.length ? ' is-flagged' : ''}" data-act="line" data-index="${line.index}" aria-label="${ctx.esc(t('Position bearbeiten', 'Edit item'))}: ${ctx.esc(line.name)}">
      <span class="rv-line-name">${ctx.esc(line.name || t('(ohne Namen)', '(no name)'))}</span>
      <span class="rv-line-sum">${Number.isFinite(line.total) ? num.format(line.total) : '—'}</span>
      ${calc ? `<span class="rv-line-calc">${ctx.esc(calc)}</span>` : ''}
      <span class="rv-line-cat${category ? '' : ' is-missing'}">${ctx.esc(category ? category.name : t('Keine Kategorie', 'No category'))}</span>
      ${reasons.length ? `<span class="rv-line-why">${ctx.esc(reasons.join(' · '))}</span>` : ''}${history}
    </button>`;
  }

  function paintLines() {
    $('[data-lines]').innerHTML = lines.length
      ? lines.map(line => `<li class="rv-line" data-line="${line.index}">${lineHtml(line)}</li>`).join('')
      : `<li class="rv-line rv-empty">${ctx.esc(t('Keine Positionen erkannt.', 'No items recognised.'))}</li>`;
    editing = -1;
    syncEditorState();
    loadPriceHistory(root, purchase).catch(() => {});
  }

  function paintSums() {
    const { diff, ok } = difference();
    const cur = purchase.currency;
    $('[data-sum]').innerHTML = `
      <div class="rv-sum-row"><span>${ctx.esc(t(`Positionen (${lines.length})`, `Items (${lines.length})`))}</span><b>${num.format(itemTotalNow())}</b></div>
      <div class="rv-sum-row rv-total"><span>${ctx.esc(t('Summe laut Beleg', 'Receipt total'))}</span><b>${ctx.esc(ctx.money(purchase.totalAmount, cur))}</b></div>
      <div class="rv-sum-row rv-diff ${ok ? 'is-ok' : 'is-off'}"><span>${ctx.esc(t('Differenz', 'Difference'))}</span><b>${ok ? num.format(0) : signed(diff)}</b></div>`;
    $('[data-bar-sum]').textContent = ctx.money(purchase.totalAmount, cur);
    const chip = $('[data-chip]');
    chip.className = `rv-chip ${ok ? 'is-ok' : 'is-off'}`;
    chip.textContent = ok ? t('Summe stimmt', 'Adds up') : t(`Differenz ${signed(diff)}`, `Off by ${signed(diff)}`);
    const dirty = isDirty();
    const save = $('[data-act="save"]');
    save.hidden = !dirty;
    const confirmed = purchase.status === 'confirmed';
    const confirm = $('[data-act="confirm"]');
    confirm.hidden = confirmed;
    confirm.textContent = hasQueue() ? t('Bestätigen & weiter', 'Confirm & next') : t('Bestätigen', 'Confirm');
  }

  // Der Platz fuer die Hinweise ist immer da (Kopf des Bons), damit nichts springt, wenn sie nachkommen.
  function paintHints() {
    if (!purchase) return;
    const found = warnings.get(purchase.id);
    const duplicates = (sources?.duplicateWarnings || []).map(text => `${t('Mögliches Duplikat', 'Possible duplicate')}: ${text}`);
    const list = [...(found?.list || []), ...duplicates];
    const box = $('[data-hints]');
    const pending = purchase.status !== 'confirmed' && !found && !warningsDone;
    box.classList.toggle('is-warn', list.length > 0);
    box.classList.remove('is-open');
    const head = (glyph, label, more = '') => `<div class="rv-hints-head"><span class="rv-hints-label">${icon(glyph)}${ctx.esc(label)}</span>${more}</div>`;
    if (pending) box.innerHTML = `${head('ui-info', t('Hinweise der Auslese', 'Reader notes'))}<span class="dt-sk-line shimmer"></span>`;
    else if (list.length) box.innerHTML = head('ui-alert', t('Bitte prüfen', 'Please check'), list.length > 1 || list[0].length > 100 ? `<button type="button" class="${buttonClass(ButtonRole.Quiet, 'rv-hints-more')}" data-act="hints-open">${ctx.esc(list.length > 1 ? t(`${list.length - 1} weitere`, `${list.length - 1} more`) : t('Mehr', 'More'))}</button>` : '')
      + `<ul>${list.map(text => `<li>${ctx.esc(text)}</li>`).join('')}</ul>`;
    else if (purchase.status === 'confirmed') box.innerHTML = `${head('ui-check', t('Bestätigt', 'Confirmed'))}<p>${ctx.esc(t('Dieser Beleg ist geprüft.', 'This receipt has been reviewed.'))}</p>`;
    else box.innerHTML = `${head('ui-check', t('Hinweise der Auslese', 'Reader notes'))}<p>${ctx.esc(t('Keine Auffälligkeiten gemeldet.', 'Nothing flagged.'))}</p>`;
    const before = [...flagged].join();
    flagged = new Set();
    for (const text of found?.list || []) for (const hit of String(text).matchAll(/(?:position|pos\.?|zeile|item|line)\s*(\d+)/gi)) flagged.add(Number(hit[1]) - 1);
    if ([...flagged].join() !== before && lines.length && editing < 0) paintLines();
  }

  function paintPosition() {
    const pos = $('[data-pos]');
    const known = hasQueue();
    pos.textContent = known ? t(`${index() + 1} von ${queue.total}`, `${index() + 1} of ${queue.total}`) : '';
    $('[data-act="prev"]').disabled = !known || index() === 0;
    $('[data-act="next"]').disabled = !known || (index() >= queue.ids.length - 1 && queue.done);
    root.classList.toggle('has-queue', known);
  }

  function paintHead() {
    $('[data-title]').textContent = purchase.merchant || ctx.get('purchases.title');
    const readerLabel = { text: t('Digitales PDF', 'Digital PDF'), gpt: t('KI (GPT)', 'AI (GPT)'), tesseract: t('Texterkennung', 'Text recognition') }[warnings.get(purchase.id)?.engine] || '';
    $('[data-sub]').textContent = [purchase.purchaseDate ? ctx.date(purchase.purchaseDate) : '', purchase.externalOrderId, readerLabel].filter(Boolean).join(' · ');
    $('[data-merchant]').textContent = purchase.merchant || '—';
    $('[data-when]').textContent = [purchase.purchaseDate ? ctx.date(purchase.purchaseDate) : '', purchase.source === 'amazon' ? 'Amazon' : ''].filter(Boolean).join(' · ');
    $('[data-paperless-upload]').hidden = !purchase.hasReceipt;
  }

  function viewerSources() {
    if (sources?.sources?.length) {
      return sources.sources.filter(item => item.contentUrl).map(item => {
        const pdf = String(item.mimeType || '').toLowerCase() === 'application/pdf';
        const content = ctx.bffUrl(String(item.contentUrl).replace(/^\//, ''));
        return {
          label: `${t('Seite', 'Page')} ${item.displayNumber}`,
          imageUrl: pdf ? content.replace('/content?', `/pages/${item.pageNumber || 1}?`) : content,
          openUrl: content
        };
      });
    }
    // Ohne Aufnahmen (aelterer Beleg): die eine abgelegte Datei, wie sie der Server hat.
    return purchase.hasReceipt ? [{ label: t('Beleg', 'Receipt'), imageUrl: ctx.bffUrl(`api/purchases/${purchase.id}/receipt`), openUrl: ctx.bffUrl(`api/purchases/${purchase.id}/receipt`) }] : [];
  }

  function paintAll() {
    paintHead();
    paintLines();
    paintSums();
    paintHints();
    paintPosition();
    renderLink($('[data-bank]'), purchase, rec, reload);
    $('[data-extra]').innerHTML = amazon ? amazonDetailsHtml(purchase, amazon) : '';
    if (amazon) bindAmazonDetails(root, purchase, reload);
    if (purchase.hasReceipt) bindPaperlessUpload(root, purchase.id).catch(() => {});
    viewer.setSources(viewerSources());
    $('.rv-bon-wrap').scrollTo(0, 0);
  }

  // --- Laden und Bewegen -----------------------------------------------------------------------------

  async function go(id, known = null) {
    const token = ++loadToken;
    root.classList.add('is-loading');
    try {
      const [loaded, reconciliation, receiptSources] = await Promise.all([
        known ?? api(`api/purchases/${id}`),
        api(`api/purchases/${id}/reconciliation`).catch(() => null),
        api(`api/purchases/${id}/receipt-sources`).catch(() => null)
      ]);
      if (token !== loadToken) return;
      purchase = loaded;
      sources = receiptSources;
      amazon = loaded.source === 'amazon' ? await api(`api/purchases/${id}/amazon-details`).catch(() => null) : null;
      rec = amazon ? amazonReconciliation(loaded, reconciliation, amazon) : reconciliation;
      lines = (purchase.items || []).map(lineOf);
      savedSum = sumNow();
      $('[data-problem]')?.remove();
      paintAll();
    } catch (err) {
      if (token === loadToken) ctx.toast(err.message || ctx.get('common.error'));
    } finally {
      if (token === loadToken) root.classList.remove('is-loading');
    }
  }
  const reload = async () => { changed = true; await go(purchase.id); };

  function itemPayload(line) {
    const original = line.item;
    return {
      categoryId: line.categoryId || null, name: line.name, brand: original.brand ?? null, sku: original.sku ?? null, asin: original.asin ?? null,
      quantity: line.quantity || 1, unitPrice: original.unitPrice ?? null, totalPrice: line.total, currency: original.currency || purchase.currency,
      notes: original.notes ?? null, productId: original.productId ?? null, rawName: original.rawName ?? original.name ?? null, barcode: original.barcode ?? null,
      quantityUnit: original.quantityUnit ?? null, packageQuantity: line.packQuantity, packageUnit: line.packQuantity ? line.packUnit : null,
      packageCount: packChanged(line) ? null : original.packageCount ?? null, baseUnitPrice: packChanged(line) ? null : original.baseUnitPrice ?? null, discountAmount: original.discountAmount ?? null,
      depositAmount: original.depositAmount ?? null, taxRate: original.taxRate ?? null, taxAmount: original.taxAmount ?? null, lineType: original.lineType ?? null,
      extractionConfidence: original.extractionConfidence ?? null, isManuallyCorrected: original.isManuallyCorrected ?? false,
      totalPriceOverridden: original.totalPriceOverridden ?? false, sortOrder: original.sortOrder ?? line.index, returnDeadline: original.returnDeadline ?? null,
      warrantyEnd: original.warrantyEnd ?? null, serialNumber: original.serialNumber ?? null, originalUnitPrice: original.originalUnitPrice ?? null,
      discountLabel: original.discountLabel ?? null
    };
  }

  async function saveLines() {
    if (!isDirty()) return;
    await api(`api/purchases/${purchase.id}/items`, { method: 'PUT', headers: JSON_HEADERS, body: JSON.stringify(lines.map(itemPayload)) });
    changed = true;
    const fresh = await api(`api/purchases/${purchase.id}`);
    purchase = fresh;
    lines = (fresh.items || []).map(lineOf);
    rec = amazon ? amazonReconciliation(fresh, await api(`api/purchases/${fresh.id}/reconciliation`).catch(() => null), amazon) : await api(`api/purchases/${fresh.id}/reconciliation`).catch(() => null);
    savedSum = sumNow();
    paintLines();
    paintSums();
  }

  function showProblem(message) {
    $('[data-problem]')?.remove();
    const box = document.createElement('div');
    box.className = 'rv-problem';
    box.dataset.problem = '';
    box.setAttribute('role', 'alert');
    box.textContent = message;
    $('.rv-paper-head').after(box);
    box.scrollIntoView({ block: 'nearest' });
  }

  async function move(delta) {
    if (busy || !hasQueue()) return;
    const target = index() + delta;
    if (target < 0) return;
    if (target >= queue.ids.length && !queue.done) await moreIds();
    if (target >= queue.ids.length) return;
    busy = true;
    try { await saveLines(); await go(queue.ids[target]); }
    catch (err) { showProblem(err.message || ctx.get('common.error')); }
    finally { busy = false; }
  }

  // Aus der Warteschlange nehmen (bestaetigt oder geloescht) und zum naechsten - oder zum vorigen, wenn es der letzte war.
  async function leaveCurrent() {
    const at = index();
    if (at >= 0) { queue.ids.splice(at, 1); queue.total = Math.max(0, queue.total - 1); }
    changed = true;
    if (!queue.ids.length && !queue.done) await moreIds();
    const next = queue.ids[Math.min(at, queue.ids.length - 1)];
    if (at >= 0 && next) { await go(next); return; }
    ctx.toast(t('Alles geprüft.', 'Everything is reviewed.'));
    dlg.close();
  }

  async function confirmCurrent() {
    if (busy || purchase.status === 'confirmed') return;
    busy = true;
    root.classList.add('is-busy');
    try {
      await saveLines();
      await api(`api/purchases/${purchase.id}/confirm`, { method: 'POST', headers: JSON_HEADERS, body: JSON.stringify({ createSafeAllocations: true, allowUnlinked: true }) });
      if (hasQueue()) await leaveCurrent();
      else { changed = true; ctx.toast(t('Bestätigt.', 'Confirmed.')); dlg.close(); }
    } catch (err) {
      const { diff, ok } = difference();
      showProblem(ok ? (err.message || ctx.get('common.error')) : `${t(`Die Positionen ergeben ${num.format(itemTotalNow())}, der Beleg ${num.format(Number(purchase.totalAmount))} (Differenz ${signed(diff)}).`, `The items add up to ${num.format(itemTotalNow())}, the receipt says ${num.format(Number(purchase.totalAmount))} (off by ${signed(diff)}).`)} ${err.message || ''}`.trim());
    } finally {
      busy = false;
      root.classList.remove('is-busy');
    }
  }

  async function deleteCurrent() {
    const confirmed = await confirmMessage({
      title: t('Beleg löschen', 'Delete receipt'),
      message: t('Kauf wirklich löschen? Die Bankbuchung bleibt bestehen.', 'Delete this purchase? The bank transaction remains.'),
      confirmLabel: t('Löschen', 'Delete'), cancelLabel: t('Abbrechen', 'Cancel'), destructive: true,
      create: html => createDialog(html, { closeLabel: t('Schließen', 'Close') })
    });
    if (!confirmed) return;
    try {
      await api(`api/purchases/${purchase.id}`, { method: 'DELETE' });
      if (hasQueue()) await leaveCurrent(); else { changed = true; dlg.close(); }
    } catch (err) { showProblem(err.message || ctx.get('common.error')); }
  }

  // Schliessen mit ungespeicherten Aenderungen fragt nach - Esc auf der Frage heisst "verwerfen", wie das Kreuz.
  async function requestClose() {
    if (isDirty() && !busy) {
      const save = await confirmMessage({
        title: t('Änderungen speichern?', 'Save changes?'),
        message: t('Du hast Positionen geändert.', 'You changed some items.'),
        confirmLabel: t('Speichern', 'Save'), cancelLabel: t('Verwerfen', 'Discard'),
        create: html => createDialog(html, { closeLabel: t('Schließen', 'Close') })
      });
      if (save) { try { await saveLines(); } catch (err) { showProblem(err.message || ctx.get('common.error')); return; } }
    }
    dlg.close();
  }

  // --- Eine Position bearbeiten ----------------------------------------------------------------------

  function syncEditorState() {
    root.classList.toggle('is-editing', editing >= 0);
  }

  function closeEditor() {
    if (editing < 0) return;
    const line = lines[editing];
    root.querySelector(`.rv-line[data-line="${editing}"] .rv-editor`)?.remove();
    root.querySelector(`.rv-line[data-line="${editing}"] .rv-line-main`).outerHTML = lineHtml(line);
    editing = -1;
    syncEditorState();
    paintSums();
    loadPriceHistory(root, purchase).catch(() => {});
  }

  function openEditor(at) {
    if (editing === at) return;
    closeEditor();
    const line = lines[at];
    editing = at;
    const li = root.querySelector(`.rv-line[data-line="${at}"]`);
    const editor = document.createElement('div');
    editor.className = 'rv-editor';
    editor.setAttribute('role', 'group');
    editor.setAttribute('aria-label', t('Position bearbeiten', 'Edit item'));
    editor.innerHTML = `
      <label class="rv-field"><span>${ctx.esc(t('Bezeichnung', 'Name'))}</span><input data-f="name" value="${ctx.esc(line.name)}" autocomplete="off"></label>
      <div class="rv-field-row">
        <div class="rv-field"><span>${ctx.esc(t('Menge', 'Quantity'))}</span>
          <div class="rv-stepper">
            <button type="button" class="${buttonClass(ButtonRole.Secondary, 'rv-step')}" data-act="step" data-by="-1" aria-label="${t('Weniger', 'Less')}">${icon('ui-minus')}</button>
            <input data-f="quantity" inputmode="decimal" value="${ctx.esc(String(line.quantity).replace('.', ','))}" aria-label="${ctx.esc(t('Menge', 'Quantity'))}">
            <button type="button" class="${buttonClass(ButtonRole.Secondary, 'rv-step')}" data-act="step" data-by="1" aria-label="${t('Mehr', 'More')}">${icon('ui-plus')}</button>
          </div></div>
        <label class="rv-field"><span>${ctx.esc(t('Preis der Position', 'Line price'))}</span><input data-f="total" inputmode="decimal" value="${ctx.esc(Number.isFinite(line.total) ? num.format(line.total) : '')}"></label>
      </div>
      <div class="rv-field-row">
        <label class="rv-field"><span>${ctx.esc(t('Packung (optional)', 'Pack size (optional)'))}</span><input data-f="packQuantity" inputmode="decimal" placeholder="${ctx.esc(t('z. B. 500 oder 10', 'e.g. 500 or 10'))}" value="${ctx.esc(line.packQuantity ? String(line.packQuantity).replace('.', ',') : '')}"></label>
        <label class="rv-field"><span>${ctx.esc(t('Einheit', 'Unit'))}</span><select data-f="packUnit">${Object.entries(PACK_UNITS).map(([key, label]) => `<option value="${key}"${key === line.packUnit ? ' selected' : ''}>${label}</option>`).join('')}</select></label>
      </div>
      <label class="rv-field"><span>${ctx.esc(t('Kategorie', 'Category'))}</span><select data-f="category"><option value="">${ctx.esc(ctx.get('common.uncategorized'))}</option>${categorySelect}</select></label>
      <div class="rv-editor-actions">
        <button type="button" class="${buttonClass(ButtonRole.Quiet)}" data-act="editor-reset">${t('Zurücksetzen', 'Reset')}</button>
        <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-act="editor-close">${t('Fertig', 'Done')}</button>
      </div>`;
    li.append(editor);
    const select = editor.querySelector('[data-f="category"]');
    select.value = line.categoryId;
    attachCombobox(ctx, select, { title: ctx.get('transactions.category'), anchored: true, items: () => categoryComboboxItems(categories, { allLabel: ctx.get('common.uncategorized') }) });
    syncEditorState();
    if (!matchMedia('(pointer:coarse)').matches) editor.querySelector('[data-f="name"]').focus();
    editor.scrollIntoView({ block: 'nearest' });
  }

  function onEdit(event) {
    const field = event.target.closest('[data-f]');
    if (!field || editing < 0) return;
    const line = lines[editing];
    if (field.dataset.f === 'name') line.name = field.value;
    else if (field.dataset.f === 'category') line.categoryId = field.value;
    else if (field.dataset.f === 'packUnit') line.packUnit = field.value;
    else if (field.dataset.f === 'packQuantity') {
      const value = parseAmount(field.value);
      field.classList.toggle('is-invalid', !Number.isFinite(value) && field.value.trim() !== '');
      if (field.value.trim() === '') line.packQuantity = null; else if (Number.isFinite(value) && value > 0) line.packQuantity = value; else return;
    } else {
      const value = parseAmount(field.value);
      field.classList.toggle('is-invalid', !Number.isFinite(value) && field.value.trim() !== '');
      if (!Number.isFinite(value)) return;
      if (field.dataset.f === 'quantity') line.quantity = value; else line.total = round2(value);
    }
    root.querySelector(`.rv-line[data-line="${editing}"] .rv-line-main`).outerHTML = lineHtml(line);
    paintSums();
  }

  function resetEditor() {
    const line = lines[editing];
    Object.assign(line, lineOf(line.item, line.index));
    const at = editing;
    closeEditor();
    openEditor(at);
  }

  // --- Eingaben --------------------------------------------------------------------------------------

  const actions = {
    close: requestClose,
    prev: () => move(-1),
    next: () => move(1),
    confirm: confirmCurrent,
    save: async () => { try { await saveLines(); ctx.toast(t('Gespeichert.', 'Saved.')); } catch (err) { showProblem(err.message || ctx.get('common.error')); } },
    delete: deleteCurrent,
    workspace: async () => {
      try { await openPurchaseWorkspace(purchase.id); dlg.close(); }
      catch (err) { showProblem(err.message || ctx.get('common.error')); }
    },
    'editor-close': closeEditor,
    'editor-reset': resetEditor,
    menu: () => toggleMenu()
  };

  function toggleMenu(open) {
    const menu = $('.rv-menu');
    const next = open ?? menu.hidden;
    menu.hidden = !next;
    $('[data-act="menu"]').setAttribute('aria-expanded', String(next));
  }

  root.addEventListener('click', event => {
    const mode = event.target.closest('.rv-modes [data-mode]');
    if (mode) return setMode(mode.dataset.mode);
    const target = event.target.closest('[data-act]');
    if (!target) { if (!event.target.closest('.rv-menu-wrap') || event.target.closest('[role=menuitem]')) toggleMenu(false); return; }
    const act = target.dataset.act;
    if (act !== 'menu') toggleMenu(false);
    if (act === 'line') return openEditor(Number(target.dataset.index));
    if (act === 'hints-open') return target.closest('.rv-hints').classList.add('is-open');
    if (act === 'step') {
      const input = root.querySelector('.rv-editor [data-f="quantity"]');
      const next = Math.max(0, round2((parseAmount(input.value) || 0) + Number(target.dataset.by)));
      input.value = String(next).replace('.', ',');
      return onEdit({ target: input });
    }
    actions[act]?.();
  });
  root.addEventListener('input', onEdit);
  root.addEventListener('change', onEdit);
  root.addEventListener('keydown', event => {
    const inField = event.target.matches('input,textarea,select');
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); confirmCurrent(); }
    else if (event.key === 'Enter' && editing >= 0 && event.target.matches('.rv-editor input')) { event.preventDefault(); closeEditor(); }
    else if (!inField && !event.target.closest('.dv-stage') && event.key === 'ArrowRight') move(1);
    else if (!inField && !event.target.closest('.dv-stage') && event.key === 'ArrowLeft') move(-1);
  });

  // Esc schliesst zuerst, was oben liegt: Menue, dann Positionsblatt, dann die Ansicht.
  dlg.addEventListener('cancel', event => {
    event.preventDefault();
    if (!$('.rv-menu').hidden) toggleMenu(false);
    else if (editing >= 0) closeEditor();
    else requestClose();
  });

  // Wischen auf dem Bon blaettert durch die Warteschlange (nur waagerecht, nur wenn nichts bearbeitet wird).
  let swipe = null;
  const bonWrap = $('.rv-bon-wrap');
  bonWrap.addEventListener('touchstart', event => { swipe = editing < 0 && event.touches.length === 1 ? { x: event.touches[0].clientX, y: event.touches[0].clientY } : null; }, { passive: true });
  bonWrap.addEventListener('touchend', event => {
    if (!swipe) return;
    const dx = event.changedTouches[0].clientX - swipe.x;
    const dy = event.changedTouches[0].clientY - swipe.y;
    swipe = null;
    if (Math.abs(dx) > 90 && Math.abs(dy) < 40) move(dx < 0 ? 1 : -1);
  }, { passive: true });

  dlg.addEventListener('close', () => onClose(changed), { once: true });
  dlg.showModal();

  // Erst jetzt laden: der Rahmen steht, nichts springt, wenn die Daten kommen.
  try {
    [categories, categorySelect] = await Promise.all([api('api/categories'), ctx.categoryOptions()]);
    categoryById = new Map(categories.map(category => [category.id, category]));
    const first = await api(`api/purchases/${startId}`);
    if (first.status !== 'confirmed') await buildQueue(startId);
    await go(startId, first);
    if (first.status !== 'confirmed') loadWarnings().catch(() => {});
  } catch (err) {
    ctx.toast(err.message || ctx.get('common.error'));
    dlg.close();
  }
}
