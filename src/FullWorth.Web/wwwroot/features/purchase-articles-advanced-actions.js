// Advanced actions kept separate from the main purchase workspace renderer. This module deliberately
// owns secondary workflows (tags, returns, document OCR, barcode/product maintenance and export) so the
// primary receipt review screen stays readable and every destructive/long-running action remains explicit.

const lang = () => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? 'de' : 'en';
const text = (de, en) => lang() === 'de' ? de : en;
const spaceId = () => localStorage.getItem('finance.space') || '';
const bff = path => `/bff/backend/${String(path).replace(/^\//, '')}${String(path).includes('?') ? '&' : '?'}fullWorthSpaceId=${encodeURIComponent(spaceId())}`;
const json = (method, body) => ({ method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

export function mountExportAndWarrantyActions(panel, { api, esc, makeDialog, money, fmtDate, showError }) {
  const head = panel?.querySelector('.panel-head');
  if (!head || head.querySelector('[data-pa-secondary-actions]')) return;
  const actions = document.createElement('div');
  actions.className = 'panel-head-actions pa-secondary-actions';
  actions.dataset.paSecondaryActions = '';
  actions.innerHTML = `<button type="button" class="ghost" data-pa-warranty>${esc(text('Fristen', 'Deadlines'))}</button>
    <button type="button" class="ghost" data-pa-export>${esc(text('Export', 'Export'))}</button>`;
  head.appendChild(actions);

  actions.querySelector('[data-pa-warranty]').onclick = async () => {
    const dlg = makeDialog(`<div class="pa-dialog-card pa-picker"><div class="panel-head"><h2>${esc(text('Garantie- & Rückgabefristen', 'Warranty & return deadlines'))}</h2><button type="button" data-close>×</button></div><div class="pa-toolbar"><select data-days><option value="30">30 ${esc(text('Tage', 'days'))}</option><option value="90" selected>90 ${esc(text('Tage', 'days'))}</option><option value="365">365 ${esc(text('Tage', 'days'))}</option></select><button type="button" data-load>${esc(text('Aktualisieren', 'Refresh'))}</button></div><div data-results class="pa-list"></div><div class="pa-dialog-error" data-error hidden></div></div>`);
    dlg.querySelector('[data-close]').onclick = () => dlg.close();
    const load = async () => {
      try {
        const data = await api(`api/purchases/warranty/upcoming?days=${encodeURIComponent(dlg.querySelector('[data-days]').value)}`);
        const rows = data.items || [];
        dlg.querySelector('[data-results]').innerHTML = rows.length ? rows.map(row => `<div class="pa-history-row"><div><strong>${esc(row.name)}</strong><span>${esc(row.merchant || '')} · ${esc(fmtDate(row.purchaseDate))}</span></div><div>${row.returnDeadline ? `<span>${esc(text('Rückgabe', 'Return'))}: ${esc(fmtDate(row.returnDeadline))}</span>` : ''}${row.warrantyEnd ? `<span>${esc(text('Garantie', 'Warranty'))}: ${esc(fmtDate(row.warrantyEnd))}</span>` : ''}<strong>${esc(money(row.totalPrice, row.currency))}</strong></div></div>`).join('') : `<div class="state-empty">${esc(text('Keine anstehenden Fristen.', 'No upcoming deadlines.'))}</div>`;
      } catch (error) { showError(dlg, error.message); }
    };
    dlg.querySelector('[data-load]').onclick = load;
    await load();
    dlg.showModal();
  };

  actions.querySelector('[data-pa-export]').onclick = () => {
    const dlg = makeDialog(`<div class="pa-dialog-card pa-picker"><div class="panel-head"><h2>${esc(text('Käufe exportieren', 'Export purchases'))}</h2><button type="button" data-close>×</button></div><p class="row-sub">${esc(text('Der Export respektiert private Käufe und deine FullWorth-Space-Berechtigungen.', 'The export respects private purchases and your FullWorth Space permissions.'))}</p><div class="pa-export-actions"><a class="button ghost" data-format="json">JSON</a><a class="button ghost" data-format="csv">CSV</a><a class="button ghost" data-format="xlsx">XLSX</a><a class="button" data-format="zip">ZIP + ${esc(text('Dokumente', 'documents'))}</a></div></div>`);
    dlg.querySelector('[data-close]').onclick = () => dlg.close();
    dlg.querySelectorAll('[data-format]').forEach(link => {
      const format = link.dataset.format;
      link.href = bff(`api/purchases/export?format=${encodeURIComponent(format)}&includeDocuments=${format === 'zip' ? 'true' : 'false'}`);
      link.target = '_blank';
      link.rel = 'noopener';
    });
    dlg.showModal();
  };
}

export async function mountPurchaseAdvancedActions({ dlg, purchase, writable, api, esc, makeDialog, money, fmtDate, showError, refresh }) {
  if (!dlg || dlg.dataset.paAdvancedMounted === 'true') return;
  dlg.dataset.paAdvancedMounted = 'true';

  await mountTags({ dlg, purchase, writable, api, esc, makeDialog, showError, refresh });
  mountReturns({ dlg, purchase, writable, api, esc, makeDialog, money, fmtDate, showError, refresh });
  mountDocuments({ dlg, purchase, writable, api, esc, makeDialog, showError, refresh });
}

async function mountTags({ dlg, purchase, writable, api, esc, showError, refresh }) {
  const side = dlg.querySelector('.pa-work-side');
  if (!side) return;
  const card = document.createElement('div');
  card.className = 'pa-card pa-tags-card';
  const confirmCard = side.querySelector('.pa-confirm-card');
  side.insertBefore(card, confirmCard || null);

  const render = async () => {
    try {
      const [attached, available] = await Promise.all([
        api(`api/purchases/${purchase.id}/tags`),
        api('api/tags')
      ]);
      const attachedIds = new Set((attached || []).map(x => x.id));
      card.innerHTML = `<div class="pa-card-head"><h3>${esc(text('Tags', 'Tags'))}</h3></div>
        <div class="pa-tag-list">${(attached || []).map(tag => `<span class="pa-chip">${esc(tag.name)}${writable ? `<button type="button" class="pa-chip-remove" data-tag-remove="${tag.id}" aria-label="${esc(text('Tag entfernen', 'Remove tag'))}">×</button>` : ''}</span>`).join('') || `<span class="row-sub">${esc(text('Keine Tags', 'No tags'))}</span>`}</div>
        ${writable ? `<div class="pa-inline-actions"><select data-tag-add><option value="">${esc(text('Tag wählen…', 'Choose tag…'))}</option>${(available || []).filter(x => !attachedIds.has(x.id)).map(tag => `<option value="${tag.id}">${esc(tag.name)}</option>`).join('')}</select><button type="button" class="ghost" data-tag-attach>${esc(text('Hinzufügen', 'Add'))}</button><button type="button" class="ghost" data-tag-create>${esc(text('Neuer Tag', 'New tag'))}</button></div>` : ''}`;
      card.querySelectorAll('[data-tag-remove]').forEach(button => button.onclick = async () => {
        try { await api(`api/purchases/${purchase.id}/tags/${button.dataset.tagRemove}`, { method: 'DELETE' }); await render(); }
        catch (error) { showError(dlg, error.message); }
      });
      card.querySelector('[data-tag-attach]')?.addEventListener('click', async () => {
        const tagId = card.querySelector('[data-tag-add]').value;
        if (!tagId) return;
        try { await api(`api/purchases/${purchase.id}/tags`, json('POST', { tagId })); await render(); }
        catch (error) { showError(dlg, error.message); }
      });
      card.querySelector('[data-tag-create]')?.addEventListener('click', async () => {
        const name = window.prompt(text('Name des neuen Tags', 'New tag name'))?.trim();
        if (!name) return;
        try {
          const created = await api('api/tags', json('POST', { name }));
          if (created?.id) await api(`api/purchases/${purchase.id}/tags`, json('POST', { tagId: created.id }));
          await render();
        } catch (error) { showError(dlg, error.message); }
      });
    } catch (error) { card.innerHTML = `<div class="pa-error">${esc(error.message)}</div>`; }
  };
  await render();
}

function mountReturns({ dlg, purchase, writable, api, esc, makeDialog, money, fmtDate, showError, refresh }) {
  if (!writable) return;
  for (const item of purchase.items || []) {
    const row = dlg.querySelector(`[data-item-id="${item.id}"]`);
    const actionHost = row?.querySelector('.pa-item-product > div');
    if (!actionHost || actionHost.querySelector('[data-item-returns]')) continue;
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'ghost';
    button.dataset.itemReturns = item.id;
    button.textContent = text('Retoure', 'Return');
    actionHost.prepend(button);
    button.onclick = () => openReturnsDialog({ parent: dlg, purchase, item, api, esc, makeDialog, money, fmtDate, showError, refresh });
  }
}

async function openReturnsDialog({ parent, purchase, item, api, esc, makeDialog, money, fmtDate, showError, refresh }) {
  const [returns, txData] = await Promise.all([
    api(`api/purchases/${purchase.id}/items/${item.id}/returns`),
    api('api/transactions?direction=income&limit=200').catch(() => ({ items: [] }))
  ]);
  const income = (txData.items || []).filter(x => String(x.currency || '').toUpperCase() === String(item.currency || purchase.currency).toUpperCase());
  const returnedQuantity = (returns || []).reduce((sum, row) => sum + Number(row.quantity || 0), 0);
  const remaining = Math.max(0, Number(item.quantity || 0) - returnedQuantity);
  const dlg = makeDialog(`<form class="pa-dialog-card pa-picker"><div class="panel-head"><div><h2>${esc(text('Retoure / Erstattung', 'Return / refund'))}</h2><div class="row-sub">${esc(item.name)} · ${esc(text('Noch verfügbar', 'Remaining'))}: ${remaining}</div></div><button type="button" data-close>×</button></div>
    <div class="pa-list" data-return-list>${(returns || []).map(row => `<div class="pa-history-row"><div><strong>${row.quantity}× · ${esc(money(row.amount, row.currency))}</strong><span>${esc(fmtDate(row.createdAt))}${row.note ? ` · ${esc(row.note)}` : ''}</span></div><button type="button" class="ghost danger" data-delete-return="${row.id}">${esc(text('Entfernen', 'Remove'))}</button></div>`).join('') || `<div class="state-empty">${esc(text('Noch keine Retoure.', 'No return recorded yet.'))}</div>`}</div>
    <div class="pa-form-grid"><label>${esc(text('Menge', 'Quantity'))}<input name="quantity" type="number" step="0.001" min="0.001" max="${remaining}" value="${remaining > 0 ? Math.min(1, remaining) : 0}" required></label><label>${esc(text('Erstattungsbetrag', 'Refund amount'))}<input name="amount" type="number" step="0.01" min="0" value="0" required></label><label>${esc(text('Währung', 'Currency'))}<input name="currency" maxlength="3" value="${esc(item.currency || purchase.currency)}" required></label><label>${esc(text('Erstattungsbuchung', 'Refund transaction'))}<select name="refundTransaction"><option value="">${esc(text('Keine / später', 'None / later'))}</option>${income.map(tx => `<option value="${tx.id}">${esc(fmtDate(tx.bookingDate))} · ${esc(tx.counterparty || '—')} · ${esc(money(tx.amount, tx.currency))}</option>`).join('')}</select></label></div>
    <label>${esc(text('Notiz', 'Note'))}<input name="note"></label><div class="dialog-actions"><button type="button" data-close>${esc(text('Schließen', 'Close'))}</button><button type="submit" ${remaining <= 0 ? 'disabled' : ''}>${esc(text('Retoure speichern', 'Save return'))}</button></div><div class="pa-dialog-error" data-error hidden></div></form>`);
  dlg.querySelectorAll('[data-close]').forEach(x => x.onclick = () => dlg.close());
  dlg.querySelectorAll('[data-delete-return]').forEach(button => button.onclick = async () => {
    if (!window.confirm(text('Retoure entfernen? Eine verknüpfte Refund-Zuordnung wird ebenfalls gelöst.', 'Remove return? A linked refund mapping will also be cleared.'))) return;
    try {
      await api(`api/purchases/${purchase.id}/items/${item.id}/returns/${button.dataset.deleteReturn}`, { method: 'DELETE' });
      dlg.close();
      await refresh();
    } catch (error) { showError(dlg, error.message); }
  });
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const fd = new FormData(event.currentTarget);
    try {
      await api(`api/purchases/${purchase.id}/items/${item.id}/returns`, json('POST', {
        quantity: Number(fd.get('quantity')),
        amount: Number(fd.get('amount')),
        currency: String(fd.get('currency') || '').toUpperCase(),
        refundTransactionId: fd.get('refundTransaction') || null,
        note: fd.get('note') || null
      }));
      dlg.close();
      await refresh();
    } catch (error) { showError(dlg, error.message); }
  };
  dlg.showModal();
}

function mountDocuments({ dlg, purchase, writable, api, esc, makeDialog, showError, refresh }) {
  const documentsHost = dlg.querySelector('[data-documents]');
  if (!documentsHost) return;
  const card = documentsHost.closest('.pa-card');
  const uploadButton = card?.querySelector('[data-upload-document]');
  const fileInput = card?.querySelector('[data-document-file]');

  if (writable && uploadButton && fileInput) {
    let typeSelect = card.querySelector('[data-document-type]');
    if (!typeSelect) {
      typeSelect = document.createElement('select');
      typeSelect.dataset.documentType = '';
      typeSelect.innerHTML = `<option value="receipt">${esc(text('Kassenbon', 'Receipt'))}</option><option value="invoice">${esc(text('Rechnung', 'Invoice'))}</option><option value="warranty">${esc(text('Garantiebeleg', 'Warranty proof'))}</option><option value="credit_note">${esc(text('Gutschrift', 'Credit note'))}</option><option value="other">${esc(text('Sonstiges', 'Other'))}</option>`;
      uploadButton.before(typeSelect);
    }
    uploadButton.onclick = () => fileInput.click();
    fileInput.onchange = async event => {
      const file = event.target.files?.[0];
      if (!file) return;
      const form = new FormData();
      form.append('document', file);
      form.append('documentType', typeSelect.value);
      try { await api(`api/purchases/${purchase.id}/documents`, { method: 'POST', body: form }); await refresh(); }
      catch (error) { showError(dlg, error.message); }
      finally { fileInput.value = ''; }
    };
  }

  for (const doc of purchase.documents || []) {
    const viewButton = documentsHost.querySelector(`[data-view-document="${doc.id}"]`);
    const row = viewButton?.closest('.pa-document-row');
    if (!row || row.querySelector('[data-document-actions]')) continue;
    const actions = document.createElement('div');
    actions.className = 'pa-inline-actions';
    actions.dataset.documentActions = '';
    actions.innerHTML = `<button type="button" class="ghost" data-document-runs>${esc(text('OCR-Verlauf', 'OCR history'))}</button>${writable ? `<button type="button" class="ghost" data-document-extract>${esc(text('OCR neu', 'Run OCR'))}</button><button type="button" class="ghost danger" data-document-delete>${esc(text('Löschen', 'Delete'))}</button>` : ''}`;
    row.appendChild(actions);
    actions.querySelector('[data-document-runs]').onclick = () => openExtractionRuns({ parent: dlg, purchase, doc, writable, api, esc, makeDialog, showError, refresh });
    actions.querySelector('[data-document-extract]')?.addEventListener('click', async () => {
      try {
        await api(`api/purchases/${purchase.id}/documents/${doc.id}/extract`, { method: 'POST' });
        await openExtractionRuns({ parent: dlg, purchase, doc, writable, api, esc, makeDialog, showError, refresh });
      } catch (error) { showError(dlg, error.message); }
    });
    actions.querySelector('[data-document-delete]')?.addEventListener('click', async () => {
      if (!window.confirm(text('Dokument wirklich löschen? Der Kauf und die Bankbuchung bleiben bestehen.', 'Delete this document? The purchase and bank transaction remain.'))) return;
      try { await api(`api/purchases/${purchase.id}/documents/${doc.id}`, { method: 'DELETE' }); await refresh(); }
      catch (error) { showError(dlg, error.message); }
    });
  }
}

async function openExtractionRuns({ parent, purchase, doc, writable, api, esc, makeDialog, showError, refresh }) {
  let runs;
  try { runs = await api(`api/purchases/${purchase.id}/documents/${doc.id}/extractions`); }
  catch (error) { showError(parent, error.message); return; }
  const dlg = makeDialog(`<div class="pa-dialog-card pa-picker"><div class="panel-head"><div><h2>${esc(text('OCR-Verlauf', 'OCR history'))}</h2><div class="row-sub">${esc(doc.originalFileName)}</div></div><button type="button" data-close>×</button></div><div class="pa-list">${(runs || []).map(run => `<div class="pa-history-row"><div><strong>${esc(run.provider || 'OCR')} · ${esc(run.status)}</strong><span>${esc(run.errorMessageSafe || '')}</span></div>${writable && run.status === 'completed' ? `<button type="button" class="ghost" data-apply-run="${run.id}">${esc(text('Übernehmen…', 'Apply…'))}</button>` : ''}</div>`).join('') || `<div class="state-empty">${esc(text('Noch keine OCR-Läufe.', 'No OCR runs yet.'))}</div>`}</div><div class="pa-dialog-error" data-error hidden></div></div>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelectorAll('[data-apply-run]').forEach(button => button.onclick = () => openApplyExtraction({ runsDialog: dlg, parent, purchase, runId: button.dataset.applyRun, api, esc, makeDialog, showError, refresh }));
  dlg.showModal();
}

function openApplyExtraction({ runsDialog, parent, purchase, runId, api, esc, makeDialog, showError, refresh }) {
  const dlg = makeDialog(`<form class="pa-dialog-card pa-picker"><div class="panel-head"><h2>${esc(text('OCR-Ergebnis übernehmen', 'Apply OCR result'))}</h2><button type="button" data-close>×</button></div><p class="row-sub">${esc(text('Manuell korrigierte Artikeldaten werden nur ersetzt, wenn du „Artikel ersetzen“ ausdrücklich aktivierst. Finanzielle Allocations verlieren dabei nie ihren Betrag; Artikelbezüge werden vor dem Ersetzen gelöst.', 'Manually corrected items are only replaced when you explicitly enable “Replace items”. Financial allocations keep their amount; item links are detached before replacement.'))}</p><label class="check"><input type="checkbox" name="merchant" checked> ${esc(text('Händler', 'Merchant'))}</label><label class="check"><input type="checkbox" name="date" checked> ${esc(text('Datum', 'Date'))}</label><label class="check"><input type="checkbox" name="total" checked> ${esc(text('Gesamtsumme', 'Total'))}</label><label class="check"><input type="checkbox" name="currency"> ${esc(text('Währung', 'Currency'))}</label><label class="check"><input type="checkbox" name="items"> ${esc(text('Artikel ersetzen', 'Replace items'))}</label><div class="dialog-actions"><button type="button" data-close>${esc(text('Abbrechen', 'Cancel'))}</button><button type="submit">${esc(text('Übernehmen', 'Apply'))}</button></div><div class="pa-dialog-error" data-error hidden></div></form>`);
  dlg.querySelectorAll('[data-close]').forEach(x => x.onclick = () => dlg.close());
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    try {
      await api(`api/purchases/${purchase.id}/apply-extraction/${runId}`, json('POST', {
        applyMerchant: event.currentTarget.elements.merchant.checked,
        applyDate: event.currentTarget.elements.date.checked,
        applyTotal: event.currentTarget.elements.total.checked,
        applyCurrency: event.currentTarget.elements.currency.checked,
        replaceItems: event.currentTarget.elements.items.checked
      }));
      dlg.close(); runsDialog.close(); parent.close();
      await refresh(false);
    } catch (error) { showError(dlg, error.message); }
  };
  dlg.showModal();
}

export async function mountProductAdvancedActions({ dlg, product, api, esc, makeDialog, showError, reload }) {
  if (!dlg || dlg.dataset.paProductAdvancedMounted === 'true') return;
  dlg.dataset.paProductAdvancedMounted = 'true';
  const root = dlg.querySelector('.pa-product-detail');
  if (!root) return;
  const categories = await api('api/categories').catch(() => []);
  const categoryOptions = `<option value="">—</option>${categories.filter(x => !x.isArchived).map(x => `<option value="${x.id}"${x.id === product.defaultCategoryId ? ' selected' : ''}>${esc(x.name)}</option>`).join('')}`;
  const card = document.createElement('div');
  card.className = 'pa-card pa-product-admin';
  card.innerHTML = `<div class="pa-card-head"><h3>${esc(text('Produkt bearbeiten', 'Edit product'))}</h3></div><div class="pa-form-grid"><label>${esc(text('Name', 'Name'))}<input data-pf="name" value="${esc(product.canonicalName)}"></label><label>${esc(text('Marke', 'Brand'))}<input data-pf="brand" value="${esc(product.brand || '')}"></label><label>${esc(text('Kategorie', 'Category'))}<select data-pf="category">${categoryOptions}</select></label><label>${esc(text('Standardeinheit', 'Default unit'))}<input data-pf="unit" value="${esc(product.defaultQuantityUnit || '')}" placeholder="piece / kg / l"></label><label>${esc(text('Packungsmenge', 'Package quantity'))}<input data-pf="packageQuantity" type="number" step="0.001" value="${esc(product.defaultPackageQuantity ?? '')}"></label><label>${esc(text('Packungseinheit', 'Package unit'))}<input data-pf="packageUnit" value="${esc(product.defaultPackageUnit || '')}" placeholder="g / ml / piece"></label></div><label>${esc(text('Notizen', 'Notes'))}<textarea data-pf="notes">${esc(product.notes || '')}</textarea></label><div class="pa-inline-actions"><button type="button" data-product-save>${esc(text('Speichern', 'Save'))}</button><button type="button" class="ghost" data-product-merge>${esc(text('Zusammenführen', 'Merge'))}</button><button type="button" class="ghost danger" data-product-archive>${esc(product.isArchived ? text('Wiederherstellen', 'Restore') : text('Archivieren', 'Archive'))}</button></div>`;
  root.appendChild(card);

  const aliases = document.createElement('div');
  aliases.className = 'pa-card';
  aliases.innerHTML = `<div class="pa-card-head"><h3>${esc(text('Aliase', 'Aliases'))}</h3></div><div class="pa-tag-list">${(product.aliases || []).map(alias => `<span class="pa-chip">${esc(alias.alias)}<button type="button" class="pa-chip-remove" data-alias-remove="${alias.id}">×</button></span>`).join('') || `<span class="row-sub">—</span>`}</div><div class="pa-inline-actions"><input data-alias-new placeholder="${esc(text('z. B. Bon-Produktname', 'e.g. receipt product name'))}"><button type="button" class="ghost" data-alias-add>${esc(text('Alias hinzufügen', 'Add alias'))}</button></div>`;
  root.appendChild(aliases);

  const barcodes = document.createElement('div');
  barcodes.className = 'pa-card';
  barcodes.innerHTML = `<div class="pa-card-head"><h3>${esc(text('Barcodes / EAN', 'Barcodes / EAN'))}</h3></div><div class="pa-tag-list">${(product.barcodes || []).map(code => `<span class="pa-chip">${esc(code.code)} · ${esc(code.standard)}<button type="button" class="pa-chip-remove" data-barcode-remove="${code.id}">×</button></span>`).join('') || `<span class="row-sub">—</span>`}</div><div class="pa-inline-actions"><input data-barcode-new inputmode="numeric" placeholder="EAN / GTIN"><button type="button" class="ghost" data-barcode-scan>${esc(text('Scannen', 'Scan'))}</button><button type="button" class="ghost" data-barcode-add>${esc(text('Hinzufügen', 'Add'))}</button></div>`;
  root.appendChild(barcodes);

  const reloadSelf = async () => { dlg.close(); await reload(product.id); };
  card.querySelector('[data-product-save]').onclick = async () => {
    const v = key => card.querySelector(`[data-pf="${key}"]`).value;
    try {
      await api(`api/products/${product.id}`, json('PATCH', {
        canonicalName: v('name'), brand: v('brand') || null, defaultCategoryId: v('category') || null,
        defaultQuantityUnit: v('unit') || null, defaultPackageQuantity: v('packageQuantity') ? Number(v('packageQuantity')) : null,
        defaultPackageUnit: v('packageUnit') || null, imageReference: product.imageReference || null, notes: v('notes') || null
      }));
      await reloadSelf();
    } catch (error) { showError(dlg, error.message); }
  };
  card.querySelector('[data-product-archive]').onclick = async () => {
    if (!window.confirm(product.isArchived ? text('Produkt wiederherstellen?', 'Restore product?') : text('Produkt archivieren? Historische Käufe bleiben unverändert.', 'Archive product? Historical purchases remain unchanged.'))) return;
    try { await api(`api/products/${product.id}${product.isArchived ? '/restore' : ''}`, { method: product.isArchived ? 'POST' : 'DELETE' }); await reloadSelf(); }
    catch (error) { showError(dlg, error.message); }
  };
  card.querySelector('[data-product-merge]').onclick = () => openProductMerge({ parent: dlg, product, api, esc, makeDialog, showError, reload });

  aliases.querySelector('[data-alias-add]').onclick = async () => {
    const input = aliases.querySelector('[data-alias-new]'); const alias = input.value.trim(); if (!alias) return;
    try { await api(`api/products/${product.id}/aliases`, json('POST', { alias, merchantId: null, aliasType: 'manual' })); await reloadSelf(); }
    catch (error) { showError(dlg, error.message); }
  };
  aliases.querySelectorAll('[data-alias-remove]').forEach(button => button.onclick = async () => {
    try { await api(`api/products/${product.id}/aliases/${button.dataset.aliasRemove}`, { method: 'DELETE' }); await reloadSelf(); }
    catch (error) { showError(dlg, error.message); }
  });

  barcodes.querySelector('[data-barcode-add]').onclick = async () => {
    const input = barcodes.querySelector('[data-barcode-new]'); const code = input.value.trim(); if (!code) return;
    try { await api(`api/products/${product.id}/barcodes`, json('POST', { code, standard: 'unknown' })); await reloadSelf(); }
    catch (error) { showError(dlg, error.message); }
  };
  barcodes.querySelector('[data-barcode-scan]').onclick = async () => {
    const code = await scanBarcode({ makeDialog, esc, showError }).catch(() => null);
    if (code) barcodes.querySelector('[data-barcode-new]').value = code;
  };
  barcodes.querySelectorAll('[data-barcode-remove]').forEach(button => button.onclick = async () => {
    try { await api(`api/products/${product.id}/barcodes/${button.dataset.barcodeRemove}`, { method: 'DELETE' }); await reloadSelf(); }
    catch (error) { showError(dlg, error.message); }
  });
}

async function openProductMerge({ parent, product, api, esc, makeDialog, showError, reload }) {
  const dlg = makeDialog(`<div class="pa-dialog-card pa-picker"><div class="panel-head"><h2>${esc(text('Produkt zusammenführen', 'Merge product'))}</h2><button type="button" data-close>×</button></div><p class="row-sub">${esc(text('Der aktuelle Datensatz wird Quelle und danach archiviert. Historische Artikel, Preise, Barcodes und Aliase werden auf das Ziel umgehängt.', 'The current record is the source and is archived afterwards. Historical items, prices, barcodes and aliases are moved to the target.'))}</p><div class="pa-toolbar"><input type="search" data-query placeholder="${esc(text('Zielprodukt suchen…', 'Search target product…'))}"><button type="button" data-search>${esc(text('Suchen', 'Search'))}</button></div><div data-results class="pa-list"></div><div class="pa-dialog-error" data-error hidden></div></div>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  const load = async () => {
    try {
      const q = dlg.querySelector('[data-query]').value.trim();
      const data = await api(`api/products?limit=100${q ? `&query=${encodeURIComponent(q)}` : ''}`);
      const rows = (data.items || []).filter(x => x.id !== product.id);
      dlg.querySelector('[data-results]').innerHTML = rows.map(row => `<button type="button" class="pa-picker-row" data-target="${row.id}"><div><strong>${esc(row.canonicalName)}</strong><span>${esc(row.brand || '')}</span></div></button>`).join('') || `<div class="state-empty">${esc(text('Kein Zielprodukt gefunden.', 'No target product found.'))}</div>`;
      dlg.querySelectorAll('[data-target]').forEach(button => button.onclick = async () => {
        if (!window.confirm(text('Produkte endgültig zusammenführen? Historische Käufe bleiben erhalten, die Produktidentität wird aber vereinheitlicht.', 'Merge products? Historical purchases remain, but product identity is unified.'))) return;
        try {
          await api('api/products/merge', json('POST', { sourceProductId: product.id, targetProductId: button.dataset.target, preferSourceName: false, preferSourceBrand: false, preferSourceCategory: false }));
          dlg.close(); parent.close(); await reload(button.dataset.target);
        } catch (error) { showError(dlg, error.message); }
      });
    } catch (error) { showError(dlg, error.message); }
  };
  dlg.querySelector('[data-search]').onclick = load;
  dlg.querySelector('[data-query]').addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); load(); } });
  await load();
  dlg.showModal();
}

export async function scanBarcode({ makeDialog, esc, showError }) {
  if (!('BarcodeDetector' in window) || !navigator.mediaDevices?.getUserMedia) {
    window.alert(text('Barcode-Scan wird von diesem Browser nicht unterstützt. Du kannst den Code weiterhin manuell eingeben.', 'Barcode scanning is not supported by this browser. You can still enter the code manually.'));
    return null;
  }
  let formats = [];
  try { formats = await BarcodeDetector.getSupportedFormats(); } catch { /* detector will choose defaults */ }
  const preferred = ['ean_13', 'ean_8', 'upc_a', 'upc_e', 'itf', 'code_128'].filter(x => formats.length === 0 || formats.includes(x));
  const detector = new BarcodeDetector(preferred.length ? { formats: preferred } : undefined);
  const dlg = makeDialog(`<div class="pa-dialog-card pa-barcode-dialog"><div class="panel-head"><h2>${esc(text('Barcode scannen', 'Scan barcode'))}</h2><button type="button" data-close>×</button></div><video data-video autoplay playsinline muted></video><div class="row-sub" data-status>${esc(text('Kamera wird gestartet…', 'Starting camera…'))}</div><div class="pa-dialog-error" data-error hidden></div></div>`);
  const video = dlg.querySelector('[data-video]');
  const status = dlg.querySelector('[data-status]');
  let stream = null; let stopped = false; let resolveResult;
  const result = new Promise(resolve => { resolveResult = resolve; });
  const stop = value => {
    if (stopped) return; stopped = true;
    stream?.getTracks().forEach(track => track.stop());
    resolveResult(value || null);
    if (dlg.open) dlg.close();
  };
  dlg.querySelector('[data-close]').onclick = () => stop(null);
  dlg.addEventListener('close', () => { if (!stopped) stop(null); }, { once: true });
  dlg.showModal();
  try {
    stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } }, audio: false });
    video.srcObject = stream;
    status.textContent = text('Barcode vor die Kamera halten.', 'Hold the barcode in front of the camera.');
    const detect = async () => {
      if (stopped) return;
      try {
        const codes = video.readyState >= 2 ? await detector.detect(video) : [];
        const value = codes?.[0]?.rawValue?.trim();
        if (value) { stop(value); return; }
      } catch { /* transient frame errors are normal */ }
      setTimeout(detect, 350);
    };
    detect();
  } catch (error) {
    showError(dlg, error.message || text('Kamera konnte nicht geöffnet werden.', 'Could not open camera.'));
  }
  return result;
}
