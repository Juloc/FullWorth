// Multi-page / multi-image receipt review. Source files stay server-side; previews are loaded only
// through the authenticated BFF. The module is deliberately read-only because source editing belongs
// to the durable scan draft before extraction starts.

const text = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en;

function bffContentUrl(contentUrl) {
  if (!contentUrl) return '';
  return `/bff/backend/${String(contentUrl).replace(/^\//, '')}`;
}

export async function mountReceiptSourceReview({ dlg, purchase, api, esc, showError }) {
  if (!dlg || dlg.dataset.paReceiptSourcesMounted === 'true') return;
  dlg.dataset.paReceiptSourcesMounted = 'true';

  let data;
  try { data = await api(`api/purchases/${purchase.id}/receipt-sources`); }
  catch (error) {
    if (error.status !== 404) showError(dlg, error.message);
    return;
  }

  const sources = data?.sources || [];
  const links = data?.itemSources || [];
  const duplicateWarnings = data?.duplicateWarnings || [];
  if (!sources.length && !duplicateWarnings.length) return;

  const host = dlg.querySelector('.pa-workspace') || dlg.querySelector('.pa-dialog-card');
  if (!host) return;
  const section = document.createElement('section');
  section.className = 'pa-card pa-receipt-source-review';
  section.innerHTML = `
    <div class="panel-head"><div><h3>${esc(text('Belegquellen', 'Receipt sources'))}</h3><div class="row-sub">${esc(text('Alle Fotos/PDF-Seiten gehören zu diesem einen Kauf. Artikelquellen zeigen, wo eine Position erkannt wurde.', 'All photos/PDF pages belong to this one purchase. Item source chips show where each line was recognized.'))}</div></div></div>
    ${duplicateWarnings.length ? `<div class="pa-review-warning" role="alert">${duplicateWarnings.map(w => `<div>⚠ ${esc(w)}</div>`).join('')}</div>` : ''}
    ${sources.length ? `<div class="pa-source-tabs" role="tablist" aria-label="${esc(text('Belegseiten', 'Receipt pages'))}">${sources.map((source, index) => `<button type="button" role="tab" data-source-index="${index}" aria-selected="${index === 0}">${esc(text('Seite', 'Page'))} ${source.displayNumber}${source.pageNumber ? ` · PDF ${source.pageNumber}` : ''}</button>`).join('')}</div><div class="pa-source-preview" data-source-preview></div><div class="pa-source-items" data-source-items></div>` : ''}`;
  host.appendChild(section);

  if (!sources.length) return;
  const items = purchase.items || [];
  const byItem = new Map();
  for (const link of links) {
    const list = byItem.get(String(link.purchaseItemId)) || [];
    list.push(String(link.receiptScanSourceId));
    byItem.set(String(link.purchaseItemId), list);
  }

  const renderItems = activeSourceId => {
    const itemHost = section.querySelector('[data-source-items]');
    const rows = items.filter(item => (byItem.get(String(item.id)) || []).includes(String(activeSourceId)));
    itemHost.innerHTML = rows.length
      ? `<div class="pa-section-label">${esc(text('Auf dieser Quelle erkannte Artikel', 'Items recognized on this source'))}</div><div class="pa-source-item-list">${rows.map(item => `<button type="button" class="pa-source-item-chip" data-item-id="${esc(item.id)}">${esc(item.name || item.rawName || 'Artikel')}</button>`).join('')}</div>`
      : `<div class="row-sub">${esc(text('Für diese Quelle ist keine eindeutige Artikelzuordnung gespeichert.', 'No unambiguous item provenance is stored for this source.'))}</div>`;
    itemHost.querySelectorAll('[data-item-id]').forEach(button => button.onclick = () => {
      const target = dlg.querySelector(`[data-item-id="${CSS.escape(button.dataset.itemId)}"]`);
      target?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    });
  };

  const activate = index => {
    const source = sources[index];
    if (!source) return;
    section.querySelectorAll('[data-source-index]').forEach((button, buttonIndex) => button.setAttribute('aria-selected', String(buttonIndex === index)));
    const preview = section.querySelector('[data-source-preview]');
    const url = bffContentUrl(source.contentUrl);
    if (!url) {
      preview.innerHTML = `<div class="state-empty">${esc(text('Quelldatei nicht mehr verfügbar.', 'Source file is no longer available.'))}</div>`;
    } else if (String(source.mimeType || '').toLowerCase() === 'application/pdf') {
      const page = source.pageNumber || 1;
      preview.innerHTML = `<iframe title="${esc(text('Beleg-PDF', 'Receipt PDF'))}" src="${esc(url)}#page=${page}&view=FitH"></iframe>`;
    } else {
      preview.innerHTML = `<img src="${esc(url)}" alt="${esc(text(`Belegseite ${source.displayNumber}`, `Receipt page ${source.displayNumber}`))}" loading="lazy">`;
    }
    renderItems(source.id);
  };

  section.querySelectorAll('[data-source-index]').forEach(button => button.onclick = () => activate(Number(button.dataset.sourceIndex)));
  activate(0);
}
