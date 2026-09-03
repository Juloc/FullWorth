// Detail explorer for bulk receipt import batches. The core importer owns polling and batch actions;
// this module only enriches rendered cards and loads details after an explicit user action.

let installed = false;
let scheduled = false;

install();

function install() {
  if (installed) return;
  installed = true;

  const begin = () => {
    if (!document.body) return;
    const observer = new MutationObserver(scheduleDecorate);
    observer.observe(document.body, { childList: true, subtree: true });
    decorate();
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', begin, { once: true });
  else begin();
}

function scheduleDecorate() {
  if (scheduled) return;
  scheduled = true;
  queueMicrotask(() => {
    scheduled = false;
    decorate();
  });
}

function decorate() {
  const importDialog = document.querySelector('dialog.receipt-import-dialog');
  if (!importDialog) return;

  importDialog.querySelectorAll('.receipt-import-batch').forEach(card => {
    const actions = card.querySelector('.receipt-import-actions');
    if (!actions || actions.querySelector('[data-import-batch-details]')) return;

    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'ghost';
    button.dataset.importBatchDetails = 'true';
    button.textContent = t('Details', 'Details');
    button.addEventListener('click', () => openDetails(card, button));
    actions.appendChild(button);
  });
}

async function openDetails(card, button) {
  button.disabled = true;
  try {
    const importDialog = card.closest('dialog.receipt-import-dialog');
    const cards = [...(importDialog?.querySelectorAll('.receipt-import-batch') || [])];
    const index = cards.indexOf(card);
    if (index < 0) throw new Error(t('Import-Batch nicht gefunden.', 'Import batch not found.'));

    const sourceText = card.querySelector('.receipt-import-batch-main strong')?.textContent?.trim() || '';
    const dateText = card.querySelector('.receipt-import-batch-main span')?.textContent?.trim() || '';
    const batches = await api('api/purchases/receipt-imports/batches?limit=10');
    const batch = batches?.find(candidate =>
      sourceLabel(candidate?.batch?.sourceType) === sourceText && formatDate(candidate?.batch?.createdAt) === dateText)
      || batches?.[index];
    const id = batch?.batch?.id;
    if (!id) throw new Error(t('Import-Batch nicht gefunden.', 'Import batch not found.'));

    const detail = await api(`api/purchases/receipt-imports/batches/${encodeURIComponent(id)}`);
    showDetailDialog(detail);
  } catch (error) {
    alert(error?.message || t('Details konnten nicht geladen werden.', 'Could not load details.'));
  } finally {
    button.disabled = false;
  }
}

function showDetailDialog(batch) {
  document.querySelector('dialog.receipt-import-batch-dialog')?.close();

  const dlg = document.createElement('dialog');
  dlg.className = 'receipt-import-batch-dialog';
  dlg.innerHTML = `<div class="receipt-import-batch-dialog-shell">
    <div class="panel-head receipt-import-batch-dialog-head">
      <div><h2>${esc(t('Importdetails', 'Import details'))}</h2><div class="row-sub">${esc(batchSubtitle(batch))}</div></div>
      <button type="button" class="icon-button" data-close aria-label="${esc(t('Schließen', 'Close'))}">×</button>
    </div>
    <div class="receipt-import-batch-dialog-body" data-import-batch-detail-panel></div>
  </div>`;
  document.body.appendChild(dlg);
  dlg.addEventListener('close', () => dlg.remove(), { once: true });
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-import-batch-detail-panel]').replaceWith(renderPanel(batch));
  dlg.showModal();
}

function renderPanel(batch) {
  const panel = document.createElement('div');
  panel.className = 'receipt-import-batch-detail';
  panel.dataset.importBatchDetailPanel = 'true';

  const items = batch?.items || [];
  const statuses = [...new Set(items.map(item => item.status).filter(Boolean))].sort();
  const sources = [...new Set(items.map(item => item.sourceType).filter(Boolean))].sort();

  panel.innerHTML = `<div class="receipt-import-batch-filters">
      <label><span>${esc(t('Status', 'Status'))}</span><select data-import-item-status-filter><option value="">${esc(t('Alle', 'All'))}</option>${statuses.map(status => `<option value="${esc(status)}">${esc(statusLabel(status))}</option>`).join('')}</select></label>
      <label><span>${esc(t('Quelle', 'Source'))}</span><select data-import-item-source-filter><option value="">${esc(t('Alle', 'All'))}</option>${sources.map(source => `<option value="${esc(source)}">${esc(sourceLabel(source))}</option>`).join('')}</select></label>
      <span class="row-sub" data-import-item-count></span>
    </div>
    <div class="receipt-import-batch-items" data-import-batch-items>
      ${items.length ? items.map(renderItem).join('') : `<div class="row-sub receipt-import-empty-items">${esc(t('Keine Belege in diesem Batch.', 'No receipts in this batch.'))}</div>`}
    </div>`;

  const statusFilter = panel.querySelector('[data-import-item-status-filter]');
  const sourceFilter = panel.querySelector('[data-import-item-source-filter]');
  const apply = () => applyFilters(panel, statusFilter?.value || '', sourceFilter?.value || '');
  statusFilter?.addEventListener('change', apply);
  sourceFilter?.addEventListener('change', apply);
  apply();
  return panel;
}

function renderItem(item) {
  const status = item.status || '';
  const source = item.sourceType || '';
  const reference = item.sourceReference ? ` · ${item.sourceReference}` : '';
  const error = item.error ? `<div class="receipt-import-item-error">${esc(item.error)}</div>` : '';
  const receipt = item.purchaseId
    ? `<a class="ghost receipt-import-item-open" href="${esc(receiptUrl(item.purchaseId))}" target="_blank" rel="noopener noreferrer">${esc(t('Beleg öffnen', 'Open receipt'))}</a>`
    : '';

  return `<div class="receipt-import-batch-item" data-import-batch-item data-status="${esc(status)}" data-source="${esc(source)}">
    <div class="receipt-import-batch-item-main"><strong>${esc(item.displayName || t('Beleg', 'Receipt'))}</strong><span>${esc(statusLabel(status))} · ${esc(sourceLabel(source))}${esc(reference)}</span>${error}</div>
    <div class="receipt-import-batch-item-actions">${receipt}</div>
  </div>`;
}

function applyFilters(panel, status, source) {
  const rows = [...panel.querySelectorAll('[data-import-batch-item]')];
  let visible = 0;
  rows.forEach(row => {
    const show = (!status || row.dataset.status === status) && (!source || row.dataset.source === source);
    row.hidden = !show;
    if (show) visible += 1;
  });
  const count = panel.querySelector('[data-import-item-count]');
  if (count) count.textContent = `${visible}/${rows.length} ${t('Belege', 'receipts')}`;
}

function batchSubtitle(batch) {
  const meta = batch?.batch || {};
  return `${sourceLabel(meta.sourceType)} · ${formatDate(meta.createdAt)} · ${meta.currency || ''}`.replace(/ · $/, '');
}

function receiptUrl(purchaseId) {
  const id = spaceId();
  const query = new URLSearchParams({ fullWorthSpaceId: id || '' });
  return `/bff/backend/api/purchases/${encodeURIComponent(purchaseId)}/receipt?${query}`;
}

async function api(path) {
  const id = spaceId();
  if (!id) throw new Error(t('Kein FullWorth Space ausgewählt.', 'No FullWorth Space selected.'));
  const [base, query = ''] = path.split('?');
  const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', id);
  const response = await fetch(`/bff/backend/${base.replace(/^\//, '')}?${params}`);
  if (!response.ok) {
    let message = `${response.status}`;
    try {
      const body = await response.json();
      message = body.error || body.message || body.title || message;
    } catch {}
    throw new Error(message);
  }
  return response.status === 204 ? null : response.json();
}

function sourceLabel(source) {
  if (source === 'paperless') return 'Paperless-ngx';
  if (source === 'folder') return t('Importordner', 'Import folder');
  if (source === 'upload') return t('Dateien', 'Files');
  return source || t('Unbekannt', 'Unknown');
}

function statusLabel(status) {
  const labels = {
    pending: t('Ausstehend', 'Pending'),
    queued: t('Wartend', 'Queued'),
    processing: t('Läuft', 'Processing'),
    done: t('Fertig', 'Done'),
    needs_review: t('Prüfen', 'Needs review'),
    skipped_duplicate: t('Duplikat', 'Duplicate'),
    failed: t('Fehler', 'Failed')
  };
  return labels[status] || status || t('Unbekannt', 'Unknown');
}

function formatDate(value) {
  if (!value) return '';
  try {
    return new Intl.DateTimeFormat(document.documentElement.lang || 'de', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value));
  } catch {
    return value;
  }
}

function spaceId() { return localStorage.getItem('finance.space'); }
function t(de, en) { return document.documentElement.lang?.toLowerCase().startsWith('en') ? en : de; }
function esc(value) { return String(value ?? '').replace(/[&<>'"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[char])); }
