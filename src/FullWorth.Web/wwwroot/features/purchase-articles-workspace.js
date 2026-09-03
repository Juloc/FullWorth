// Advanced purchases/articles UI. It is loaded as a side effect by purchases-gpt-normal.js so the
// existing compact receipt/Amazon flow can stay untouched. The module only augments #view-purchases:
// Receipts remains the default, while Articles, Products and Analytics use the new API families.

const strings = {
  de: {
    receipts: 'Belege', articles: 'Artikel', products: 'Produkte', analytics: 'Analysen',
    search: 'Suchen…', filter: 'Filter', all: 'Alle', review: 'Zu prüfen', confirmed: 'Bestätigt',
    linked: 'Verknüpft', unlinked: 'Nicht verknüpft', bookmarked: 'Merkliste', refresh: 'Aktualisieren',
    merchant: 'Händler', date: 'Datum', time: 'Uhrzeit', total: 'Gesamt', currency: 'Währung', notes: 'Notizen',
    receiptNo: 'Bonnummer', invoiceNo: 'Rechnungsnummer', paymentMethod: 'Zahlungsart', visibility: 'Sichtbarkeit',
    shared: 'FullWorth Space', private: 'Privat', save: 'Speichern', close: 'Schließen', add: 'Hinzufügen', remove: 'Entfernen',
    lineItems: 'Artikel / Positionen', reconciliation: 'Abgleich', itemTotal: 'Artikelsumme', paymentTotal: 'Zahlungen',
    difference: 'Differenz', confirm: 'Kauf bestätigen', acceptDifference: 'Differenz bewusst akzeptieren',
    payments: 'Zahlungen', addPayment: 'Zahlung verknüpfen', documents: 'Dokumente', uploadDocument: 'Dokument hinzufügen',
    product: 'Produkt', category: 'Kategorie', name: 'Name', rawName: 'OCR-Name', brand: 'Marke', barcode: 'Barcode',
    quantity: 'Menge', unit: 'Einheit', unitPrice: 'Einzelpreis', basePrice: 'Grundpreis', lineTotal: 'Positionssumme',
    lineType: 'Typ', warranty: 'Garantie bis', returnUntil: 'Rückgabe bis', serial: 'Seriennummer',
    chooseProduct: 'Produkt zuordnen', newProduct: 'Neues Produkt', purchaseCount: 'Käufe', lastPrice: 'Letzter Preis',
    history: 'Preisverlauf', noData: 'Keine Daten.', overview: 'Übersicht', spend: 'Ausgaben', needsReview: 'Zu prüfen',
    topCategories: 'Kategorien', topProducts: 'Produkte', topBrands: 'Marken', priceChanges: 'Preisänderungen',
    shrinkflation: 'Mögliche Shrinkflation', error: 'Fehler', saved: 'Gespeichert.', processing: 'Lade…',
    manualPurchase: 'Manuellen Kauf anlegen', create: 'Anlegen', source: 'Quelle', deletePurchase: 'Kauf löschen',
    deleteConfirm: 'Kauf wirklich löschen? Die Bankbuchung bleibt bestehen.', readonly: 'Nur Lesen',
    matchProduct: 'Zuordnen', unlinkProduct: 'Produkt lösen', documentType: 'Dokumenttyp', receipt: 'Kassenbon',
    invoice: 'Rechnung', warrantyDoc: 'Garantiebeleg', other: 'Sonstiges', paymentCandidates: 'Passende Buchungen',
    noCandidates: 'Keine passende Buchung gefunden.', subtotal: 'Zwischensumme', discount: 'Rabatte', deposit: 'Pfand', tax: 'Steuer',
    tip: 'Trinkgeld', shipping: 'Versand', fee: 'Gebühren', details: 'Details', bookmark: 'Merken',
    status: 'Status', itemDifference: 'Beleg ↔ Artikel', paymentDifference: 'Beleg ↔ Zahlung'
  },
  en: {
    receipts: 'Receipts', articles: 'Items', products: 'Products', analytics: 'Analytics',
    search: 'Search…', filter: 'Filter', all: 'All', review: 'Needs review', confirmed: 'Confirmed',
    linked: 'Linked', unlinked: 'Unlinked', bookmarked: 'Bookmarked', refresh: 'Refresh',
    merchant: 'Merchant', date: 'Date', time: 'Time', total: 'Total', currency: 'Currency', notes: 'Notes',
    receiptNo: 'Receipt no.', invoiceNo: 'Invoice no.', paymentMethod: 'Payment method', visibility: 'Visibility',
    shared: 'FullWorth Space', private: 'Private', save: 'Save', close: 'Close', add: 'Add', remove: 'Remove',
    lineItems: 'Items / lines', reconciliation: 'Reconciliation', itemTotal: 'Item total', paymentTotal: 'Payments',
    difference: 'Difference', confirm: 'Confirm purchase', acceptDifference: 'Explicitly accept difference',
    payments: 'Payments', addPayment: 'Link payment', documents: 'Documents', uploadDocument: 'Add document',
    product: 'Product', category: 'Category', name: 'Name', rawName: 'OCR name', brand: 'Brand', barcode: 'Barcode',
    quantity: 'Quantity', unit: 'Unit', unitPrice: 'Unit price', basePrice: 'Base price', lineTotal: 'Line total',
    lineType: 'Type', warranty: 'Warranty until', returnUntil: 'Return until', serial: 'Serial number',
    chooseProduct: 'Assign product', newProduct: 'New product', purchaseCount: 'Purchases', lastPrice: 'Last price',
    history: 'Price history', noData: 'No data.', overview: 'Overview', spend: 'Spend', needsReview: 'Needs review',
    topCategories: 'Categories', topProducts: 'Products', topBrands: 'Brands', priceChanges: 'Price changes',
    shrinkflation: 'Possible shrinkflation', error: 'Error', saved: 'Saved.', processing: 'Loading…',
    manualPurchase: 'Create manual purchase', create: 'Create', source: 'Source', deletePurchase: 'Delete purchase',
    deleteConfirm: 'Delete this purchase? The bank transaction remains.', readonly: 'Read only',
    matchProduct: 'Assign', unlinkProduct: 'Unlink product', documentType: 'Document type', receipt: 'Receipt',
    invoice: 'Invoice', warrantyDoc: 'Warranty proof', other: 'Other', paymentCandidates: 'Matching transactions',
    noCandidates: 'No matching transaction found.', subtotal: 'Subtotal', discount: 'Discounts', deposit: 'Deposit', tax: 'Tax',
    tip: 'Tip', shipping: 'Shipping', fee: 'Fees', details: 'Details', bookmark: 'Bookmark',
    status: 'Status', itemDifference: 'Receipt ↔ items', paymentDifference: 'Receipt ↔ payment'
  }
};

let installed = false;
let activeTab = 'receipts';
let host = null;
let originalToolbar = null;
let originalPanel = null;
let advancedPanel = null;

const t = key => (strings[(document.documentElement.lang || 'de').startsWith('de') ? 'de' : 'en'][key] || key);
const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));
const spaceId = () => localStorage.getItem('finance.space') || '';

function withSpace(path) {
  const [base, query = ''] = String(path).replace(/^\//, '').split('?');
  const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', spaceId());
  return `${base}?${params}`;
}

async function api(path, options) {
  const response = await fetch(`/bff/backend/${withSpace(path)}`, options);
  if (!response.ok) {
    let message = `${response.status}`;
    try {
      const body = await response.json();
      message = body.error || body.message || body.title || message;
      if (body.detail?.conflict) message += ` (${body.detail.conflict})`;
    } catch { /* keep status */ }
    const error = new Error(message);
    error.status = response.status;
    throw error;
  }
  if (response.status === 204) return null;
  return response.json();
}

function json(method, body) {
  return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) };
}

function money(value, currency = 'EUR') {
  const amount = Number(value || 0);
  try { return new Intl.NumberFormat(document.documentElement.lang || 'de', { style: 'currency', currency }).format(amount); }
  catch { return `${amount.toFixed(2)} ${currency}`; }
}

function fmtDate(value) {
  if (!value) return '—';
  try { return new Intl.DateTimeFormat(document.documentElement.lang || 'de').format(new Date(`${String(value).slice(0, 10)}T12:00:00`)); }
  catch { return value; }
}

function ensureStyle() {
  if (document.querySelector('#purchase-articles-style')) return;
  const link = document.createElement('link');
  link.id = 'purchase-articles-style';
  link.rel = 'stylesheet';
  link.href = '/features/purchase-articles-workspace.css';
  document.head.appendChild(link);
}

function install() {
  if (installed) return;
  host = document.querySelector('#view-purchases');
  originalToolbar = host?.querySelector('.toolbar');
  originalPanel = document.querySelector('#purchases-list')?.closest('.panel');
  if (!host || !originalToolbar || !originalPanel) return;
  installed = true;
  ensureStyle();

  const nav = document.createElement('div');
  nav.className = 'purchase-subnav';
  nav.innerHTML = ['receipts', 'articles', 'products', 'analytics']
    .map(key => `<button type="button" data-pa-tab="${key}" class="${key === 'receipts' ? 'active' : ''}">${esc(t(key))}</button>`).join('');
  originalToolbar.insertAdjacentElement('beforebegin', nav);

  advancedPanel = document.createElement('article');
  advancedPanel.className = 'panel purchase-advanced-panel';
  advancedPanel.hidden = true;
  originalPanel.insertAdjacentElement('afterend', advancedPanel);

  nav.addEventListener('click', event => {
    const button = event.target.closest('[data-pa-tab]');
    if (!button) return;
    switchTab(button.dataset.paTab);
  });
}

async function switchTab(tab) {
  activeTab = tab;
  host.querySelectorAll('[data-pa-tab]').forEach(button => button.classList.toggle('active', button.dataset.paTab === tab));
  const normal = tab === 'receipts';
  originalToolbar.hidden = !normal;
  originalPanel.hidden = !normal;
  advancedPanel.hidden = normal;
  if (normal) return;
  advancedPanel.innerHTML = `<div class="pa-loading">${esc(t('processing'))}</div>`;
  try {
    if (tab === 'articles') await renderArticles();
    else if (tab === 'products') await renderProducts();
    else await renderAnalytics();
  } catch (error) {
    advancedPanel.innerHTML = `<div class="pa-error"><strong>${esc(t('error'))}</strong><div>${esc(error.message)}</div></div>`;
  }
}

async function renderArticles() {
  advancedPanel.innerHTML = `<div class="panel-head"><div><h2>${esc(t('articles'))}</h2><div class="row-sub">Belege mit stabilen Artikeln, Produkten, Zahlungen und Dokumenten</div></div><button type="button" data-new-purchase>${esc(t('manualPurchase'))}</button></div>
    <div class="pa-toolbar">
      <input type="search" data-pa-query placeholder="${esc(t('search'))}">
      <select data-pa-review><option value="">${esc(t('all'))}</option><option value="needs_review">${esc(t('review'))}</option><option value="confirmed">${esc(t('confirmed'))}</option></select>
      <select data-pa-linked><option value="">${esc(t('all'))}</option><option value="true">${esc(t('linked'))}</option><option value="false">${esc(t('unlinked'))}</option></select>
      <label class="check inline"><input type="checkbox" data-pa-bookmark> ${esc(t('bookmarked'))}</label>
      <button type="button" class="ghost" data-pa-refresh>${esc(t('refresh'))}</button>
    </div><div data-pa-article-list class="pa-list"></div>`;

  const load = async () => {
    const params = new URLSearchParams({ limit: '150' });
    const query = advancedPanel.querySelector('[data-pa-query]').value.trim();
    const review = advancedPanel.querySelector('[data-pa-review]').value;
    const linked = advancedPanel.querySelector('[data-pa-linked]').value;
    if (query) params.set('query', query);
    if (review) params.set('reviewState', review);
    if (linked) params.set('linked', linked);
    if (advancedPanel.querySelector('[data-pa-bookmark]').checked) params.set('bookmarked', 'true');
    const box = advancedPanel.querySelector('[data-pa-article-list]');
    box.innerHTML = `<div class="pa-loading">${esc(t('processing'))}</div>`;
    const data = await api(`api/purchases/paged?${params}`);
    const rows = data.items || [];
    box.innerHTML = rows.length ? rows.map(row => `<button type="button" class="pa-purchase-row" data-purchase-id="${row.id}">
      <div class="pa-row-main"><strong>${esc(row.merchant || '—')}</strong><span>${esc(fmtDate(row.purchaseDate))} · ${row.itemCount} ${esc(t('articles'))} · ${row.documentCount} ${esc(t('documents'))}</span></div>
      <div class="pa-row-flags"><span class="pa-chip ${row.reviewState === 'confirmed' ? 'ok' : ''}">${esc(row.reviewState === 'confirmed' ? t('confirmed') : t('review'))}</span>${row.isBookmarked ? `<span class="pa-star">★</span>` : ''}<strong>${esc(money(row.totalAmount, row.currency))}</strong></div>
    </button>`).join('') : `<div class="state-empty">${esc(t('noData'))}</div>`;
    box.querySelectorAll('[data-purchase-id]').forEach(button => button.addEventListener('click', () => openPurchaseWorkspace(button.dataset.purchaseId)));
  };

  advancedPanel.querySelector('[data-pa-refresh]').onclick = load;
  advancedPanel.querySelector('[data-pa-query]').addEventListener('keydown', event => { if (event.key === 'Enter') load(); });
  advancedPanel.querySelector('[data-pa-review]').onchange = load;
  advancedPanel.querySelector('[data-pa-linked]').onchange = load;
  advancedPanel.querySelector('[data-pa-bookmark]').onchange = load;
  advancedPanel.querySelector('[data-new-purchase]').onclick = openManualPurchase;
  await load();
}

async function openManualPurchase() {
  const dlg = makeDialog(`<form class="pa-dialog-card pa-small-form">
    <div class="panel-head"><h2>${esc(t('manualPurchase'))}</h2><button type="button" data-close>×</button></div>
    <label>${esc(t('merchant'))}<input name="merchant" required></label>
    <div class="pa-form-grid"><label>${esc(t('date'))}<input name="date" type="date"></label><label>${esc(t('total'))}<input name="total" type="number" step="0.01" min="0" required></label><label>${esc(t('currency'))}<input name="currency" maxlength="3" value="EUR" required></label><label>${esc(t('visibility'))}<select name="visibility"><option value="space">${esc(t('shared'))}</option><option value="private">${esc(t('private'))}</option></select></label></div>
    <label>${esc(t('notes'))}<textarea name="notes" rows="3"></textarea></label>
    <div class="dialog-actions"><button type="button" data-close>${esc(t('close'))}</button><button type="submit">${esc(t('create'))}</button></div>
  </form>`);
  dlg.querySelectorAll('[data-close]').forEach(x => x.onclick = () => dlg.close());
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const fd = new FormData(event.currentTarget);
    try {
      const created = await api('api/purchases/manual', json('POST', {
        merchant: fd.get('merchant'), purchaseDate: fd.get('date') || null, purchaseTime: null,
        totalAmount: Number(fd.get('total') || 0), currency: String(fd.get('currency') || 'EUR').toUpperCase(),
        source: 'manual', notes: fd.get('notes') || null, visibility: fd.get('visibility') || 'space',
        paidByUserId: null, forWhomUserId: null, receiptNumber: null, invoiceNumber: null, paymentMethodText: null
      }));
      dlg.close();
      await switchTab('articles');
      if (created?.id) await openPurchaseWorkspace(created.id);
    } catch (error) { showDialogError(dlg, error.message); }
  };
  dlg.showModal();
}

async function openPurchaseWorkspace(id) {
  const [workspace, categories] = await Promise.all([
    api(`api/purchases/${id}/workspace`),
    api('api/categories').catch(() => [])
  ]);
  const purchase = workspace.purchase;
  const rec = workspace.reconciliation || {};
  const writable = workspace.access === 'write';
  const categoryOptions = `<option value="">—</option>${categoryOptionsHtml(categories)}`;
  const itemRows = (purchase.items || []).map(item => itemEditor(item, categoryOptions, writable)).join('');
  const paymentRows = (purchase.payments || []).map(payment => `<div class="pa-payment-row"><div><strong>${esc(money(payment.amount, payment.currency))}</strong><span>${esc(payment.linkSource)}${payment.confidence != null ? ` · ${Math.round(Number(payment.confidence) * 100)}%` : ''}</span></div>${writable ? `<button type="button" class="ghost" data-remove-payment="${payment.id}">${esc(t('remove'))}</button>` : ''}</div>`).join('');
  const documentRows = (purchase.documents || []).map(doc => `<div class="pa-document-row"><div><strong>${esc(doc.originalFileName)}</strong><span>${esc(doc.documentType)} · ${esc(doc.status)}</span></div><button type="button" class="ghost" data-view-document="${doc.id}">${esc(t('details'))}</button></div>`).join('');

  const dlg = makeDialog(`<div class="pa-dialog-card pa-workspace">
    <div class="panel-head"><div><h2>${esc(purchase.merchant || t('articles'))}</h2><div class="row-sub">${esc(fmtDate(purchase.purchaseDate))} · ${esc(purchase.source)} · ${esc(purchase.reviewState)}</div></div><div class="panel-head-actions">${workspace.access !== 'write' ? `<span class="pa-chip">${esc(t('readonly'))}</span>` : ''}<button type="button" data-close>×</button></div></div>
    <div class="pa-workspace-grid">
      <section class="pa-work-main">
        <div class="pa-card"><div class="pa-card-head"><h3>${esc(t('details'))}</h3></div>
          <div class="pa-form-grid">
            <label>${esc(t('merchant'))}<input data-summary="merchant" value="${esc(purchase.merchant)}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('date'))}<input data-summary="purchaseDate" type="date" value="${esc(purchase.purchaseDate || '')}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('time'))}<input data-summary="purchaseTime" type="time" step="1" value="${esc((purchase.purchaseTime || '').slice(0, 8))}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('total'))}<input data-summary="totalAmount" type="number" step="0.01" value="${esc(purchase.totalAmount)}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('currency'))}<input data-summary="currency" maxlength="3" value="${esc(purchase.currency)}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('receiptNo'))}<input data-summary="receiptNumber" value="${esc(purchase.receiptNumber || '')}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('invoiceNo'))}<input data-summary="invoiceNumber" value="${esc(purchase.invoiceNumber || '')}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('paymentMethod'))}<input data-summary="paymentMethodText" value="${esc(purchase.paymentMethodText || '')}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('subtotal'))}<input data-summary="subtotalAmount" type="number" step="0.01" value="${esc(purchase.subtotalAmount ?? '')}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('discount'))}<input data-summary="discountAmount" type="number" step="0.01" value="${esc(purchase.discountAmount ?? '')}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('deposit'))}<input data-summary="depositAmount" type="number" step="0.01" value="${esc(purchase.depositAmount ?? '')}" ${writable ? '' : 'disabled'}></label>
            <label>${esc(t('tax'))}<input data-summary="taxAmount" type="number" step="0.01" value="${esc(purchase.taxAmount ?? '')}" ${writable ? '' : 'disabled'}></label>
          </div>
          <label>${esc(t('notes'))}<textarea data-summary="notes" rows="3" ${writable ? '' : 'disabled'}>${esc(purchase.notes || '')}</textarea></label>
          ${writable ? `<div class="pa-inline-actions"><label>${esc(t('visibility'))}<select data-visibility><option value="space"${purchase.visibility === 'space' ? ' selected' : ''}>${esc(t('shared'))}</option><option value="private"${purchase.visibility === 'private' ? ' selected' : ''}>${esc(t('private'))}</option></select></label><label class="check inline"><input type="checkbox" data-bookmark ${purchase.isBookmarked ? 'checked' : ''}> ${esc(t('bookmark'))}</label><button type="button" data-save-summary>${esc(t('save'))}</button></div>` : ''}
        </div>

        <div class="pa-card"><div class="pa-card-head"><h3>${esc(t('lineItems'))}</h3>${writable ? `<button type="button" class="ghost" data-add-item>${esc(t('add'))}</button>` : ''}</div><div class="pa-items" data-items>${itemRows || `<div class="state-empty">${esc(t('noData'))}</div>`}</div></div>
      </section>
      <aside class="pa-work-side">
        ${reconcileHtml(rec, purchase.currency, writable)}
        <div class="pa-card"><div class="pa-card-head"><h3>${esc(t('payments'))}</h3>${writable ? `<button type="button" class="ghost" data-add-payment>${esc(t('addPayment'))}</button>` : ''}</div><div data-payments>${paymentRows || `<div class="row-sub">${esc(t('noData'))}</div>`}</div></div>
        <div class="pa-card"><div class="pa-card-head"><h3>${esc(t('documents'))}</h3>${writable ? `<button type="button" class="ghost" data-upload-document>${esc(t('uploadDocument'))}</button>` : ''}</div><div data-documents>${documentRows || `<div class="row-sub">${esc(t('noData'))}</div>`}</div><input type="file" data-document-file accept="image/*,.pdf" hidden></div>
        ${writable ? `<div class="pa-card pa-confirm-card"><button type="button" data-confirm class="primary-action">${esc(t('confirm'))}</button><button type="button" class="ghost danger" data-delete-purchase>${esc(t('deletePurchase'))}</button></div>` : ''}
      </aside>
    </div>
    <div class="pa-dialog-error" data-error hidden></div>
  </div>`);

  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  bindWorkspaceItemActions(dlg, purchase, categoryOptions, writable);
  if (writable) {
    dlg.querySelector('[data-save-summary]').onclick = () => savePurchaseSummary(dlg, purchase).then(() => refreshWorkspace(dlg, id)).catch(error => showDialogError(dlg, error.message));
    dlg.querySelector('[data-add-item]').onclick = () => addBlankItem(dlg, id, purchase.currency, categoryOptions);
    dlg.querySelector('[data-add-payment]').onclick = () => choosePayment(dlg, id);
    dlg.querySelector('[data-upload-document]').onclick = () => dlg.querySelector('[data-document-file]').click();
    dlg.querySelector('[data-document-file]').onchange = event => uploadDocument(dlg, id, event.target.files?.[0]);
    dlg.querySelector('[data-confirm]').onclick = () => confirmPurchase(dlg, id);
    dlg.querySelector('[data-delete-purchase]').onclick = () => deletePurchase(dlg, id);
    dlg.querySelector('[data-accept-items]')?.addEventListener('click', () => acceptDifference(dlg, id, 'items'));
    dlg.querySelector('[data-accept-payments]')?.addEventListener('click', () => acceptDifference(dlg, id, 'payments'));
    dlg.querySelectorAll('[data-remove-payment]').forEach(button => button.onclick = async () => {
      try { await api(`api/purchases/${id}/payments/${button.dataset.removePayment}`, { method: 'DELETE' }); await refreshWorkspace(dlg, id); }
      catch (error) { showDialogError(dlg, error.message); }
    });
  }
  dlg.querySelectorAll('[data-view-document]').forEach(button => button.onclick = () => window.open(`/bff/backend/${withSpace(`api/purchases/${id}/documents/${button.dataset.viewDocument}/content`)}`, '_blank', 'noopener'));
  dlg.showModal();
}

function itemEditor(item, categoryOptions, writable) {
  const disabled = writable ? '' : 'disabled';
  return `<div class="pa-item" data-item-id="${item.id}" data-product-id="${item.productId || ''}">
    <div class="pa-item-top"><input data-f="name" value="${esc(item.name)}" ${disabled}><span class="pa-chip">${esc(item.lineType || 'product')}</span><strong>${esc(money(item.totalPrice, item.currency))}</strong></div>
    <div class="pa-item-grid">
      <label>${esc(t('rawName'))}<input data-f="rawName" value="${esc(item.rawName || '')}" ${disabled}></label>
      <label>${esc(t('brand'))}<input data-f="brand" value="${esc(item.brand || '')}" ${disabled}></label>
      <label>${esc(t('barcode'))}<input data-f="barcode" value="${esc(item.barcode || '')}" ${disabled}></label>
      <label>${esc(t('category'))}<select data-f="categoryId" ${disabled}>${categoryOptions}</select></label>
      <label>${esc(t('quantity'))}<input data-f="quantity" type="number" step="0.001" value="${esc(item.quantity)}" ${disabled}></label>
      <label>${esc(t('unit'))}<input data-f="quantityUnit" value="${esc(item.quantityUnit || 'piece')}" ${disabled}></label>
      <label>${esc(t('unitPrice'))}<input data-f="unitPrice" type="number" step="0.0001" value="${esc(item.unitPrice ?? '')}" ${disabled}></label>
      <label>${esc(t('lineTotal'))}<input data-f="totalPrice" type="number" step="0.01" value="${esc(item.totalPrice)}" ${disabled}></label>
      <label>${esc(t('deposit'))}<input data-f="depositAmount" type="number" step="0.01" value="${esc(item.depositAmount ?? '')}" ${disabled}></label>
      <label>${esc(t('lineType'))}<select data-f="lineType" ${disabled}>${['product','deposit','discount','coupon','fee','tip','shipping','tax','unknown'].map(v => `<option value="${v}"${v === (item.lineType || 'product') ? ' selected' : ''}>${v}</option>`).join('')}</select></label>
      <label>${esc(t('warranty'))}<input data-f="warrantyEnd" type="date" value="${esc(item.warrantyEnd || '')}" ${disabled}></label>
      <label>${esc(t('returnUntil'))}<input data-f="returnDeadline" type="date" value="${esc(item.returnDeadline || '')}" ${disabled}></label>
      <label>${esc(t('serial'))}<input data-f="serialNumber" value="${esc(item.serialNumber || '')}" ${disabled}></label>
    </div>
    <div class="pa-item-product">${item.productId ? `<span>${esc(t('product'))}: ${esc(item.productId)}</span>` : `<span class="row-sub">${esc(t('product'))}: —</span>`}${writable ? `<div><button type="button" class="ghost" data-choose-product>${esc(t('chooseProduct'))}</button>${item.productId ? `<button type="button" class="ghost" data-unlink-product>${esc(t('unlinkProduct'))}</button>` : ''}<button type="button" data-save-item>${esc(t('save'))}</button><button type="button" class="ghost danger" data-delete-item>${esc(t('remove'))}</button></div>` : ''}</div>
  </div>`;
}

function bindWorkspaceItemActions(dlg, purchase, categoryOptions, writable) {
  (purchase.items || []).forEach(item => {
    const row = dlg.querySelector(`[data-item-id="${item.id}"]`);
    if (!row) return;
    const category = row.querySelector('[data-f="categoryId"]');
    if (category) category.value = item.categoryId || '';
    if (!writable) return;
    row.querySelector('[data-save-item]').onclick = async () => {
      try { await api(`api/purchases/${purchase.id}/items/${item.id}`, json('PATCH', itemPayload(row, item, purchase.currency))); await refreshWorkspace(dlg, purchase.id); }
      catch (error) { showDialogError(dlg, error.message); }
    };
    row.querySelector('[data-delete-item]').onclick = async () => {
      try { await api(`api/purchases/${purchase.id}/items/${item.id}`, { method: 'DELETE' }); await refreshWorkspace(dlg, purchase.id); }
      catch (error) { showDialogError(dlg, error.message); }
    };
    row.querySelector('[data-choose-product]').onclick = () => chooseProduct(dlg, purchase.id, item.id, row);
    row.querySelector('[data-unlink-product]')?.addEventListener('click', async () => {
      try { await api(`api/purchases/${purchase.id}/items/${item.id}/unlink-product`, { method: 'POST' }); await refreshWorkspace(dlg, purchase.id); }
      catch (error) { showDialogError(dlg, error.message); }
    });
  });
}

function itemPayload(row, original, currency) {
  const value = name => row.querySelector(`[data-f="${name}"]`)?.value ?? '';
  const number = name => value(name) === '' ? null : Number(value(name));
  return {
    productId: original.productId || null,
    categoryId: value('categoryId') || null,
    name: value('name').trim(),
    rawName: value('rawName').trim() || value('name').trim(),
    brand: value('brand').trim() || null,
    sku: original.sku || null,
    barcode: value('barcode').trim() || null,
    asin: original.asin || null,
    quantity: Number(value('quantity') || 1),
    quantityUnit: value('quantityUnit').trim() || 'piece',
    packageQuantity: original.packageQuantity ?? null,
    packageUnit: original.packageUnit ?? null,
    packageCount: original.packageCount ?? null,
    unitPrice: number('unitPrice'),
    totalPrice: Number(value('totalPrice') || 0),
    baseUnitPrice: original.baseUnitPrice ?? null,
    discountAmount: original.discountAmount ?? null,
    depositAmount: number('depositAmount'),
    taxRate: original.taxRate ?? null,
    taxAmount: original.taxAmount ?? null,
    currency: original.currency || currency,
    lineType: value('lineType') || 'product',
    notes: original.notes || null,
    sortOrder: original.sortOrder ?? 0,
    returnDeadline: value('returnDeadline') || null,
    warrantyEnd: value('warrantyEnd') || null,
    serialNumber: value('serialNumber').trim() || null,
    totalPriceOverridden: true
  };
}

async function addBlankItem(dlg, purchaseId, currency) {
  try {
    await api(`api/purchases/${purchaseId}/items`, json('POST', {
      productId: null, categoryId: null, name: 'Neue Position', rawName: 'Neue Position', brand: null, sku: null, barcode: null, asin: null,
      quantity: 1, quantityUnit: 'piece', packageQuantity: null, packageUnit: null, packageCount: null, unitPrice: null, totalPrice: 0,
      baseUnitPrice: null, discountAmount: null, depositAmount: null, taxRate: null, taxAmount: null, currency,
      lineType: 'product', notes: null, sortOrder: null, returnDeadline: null, warrantyEnd: null, serialNumber: null, totalPriceOverridden: true
    }));
    await refreshWorkspace(dlg, purchaseId);
  } catch (error) { showDialogError(dlg, error.message); }
}

async function savePurchaseSummary(dlg, purchase) {
  const v = key => dlg.querySelector(`[data-summary="${key}"]`)?.value ?? '';
  const n = key => v(key) === '' ? null : Number(v(key));
  await api(`api/purchases/${purchase.id}/summary`, json('PATCH', {
    merchant: v('merchant'), purchaseDate: v('purchaseDate') || null, purchaseTime: v('purchaseTime') || null,
    totalAmount: Number(v('totalAmount') || 0), currency: v('currency').toUpperCase(), notes: v('notes') || null,
    receiptNumber: v('receiptNumber') || null, invoiceNumber: v('invoiceNumber') || null, paymentMethodText: v('paymentMethodText') || null,
    subtotalAmount: n('subtotalAmount'), discountAmount: n('discountAmount'), depositAmount: n('depositAmount'), taxAmount: n('taxAmount'),
    tipAmount: purchase.tipAmount ?? null, shippingAmount: purchase.shippingAmount ?? null, feeAmount: purchase.feeAmount ?? null
  }));
  await api(`api/purchases/${purchase.id}/visibility`, json('PUT', { visibility: dlg.querySelector('[data-visibility]').value }));
  await api(`api/purchases/${purchase.id}/bookmark`, json('PUT', { bookmarked: dlg.querySelector('[data-bookmark]').checked }));
}

function reconcileHtml(rec, currency, writable) {
  const itemDiff = Number(rec.itemDifference || 0);
  const paymentDiff = Number(rec.paymentDifference || 0);
  const paymentLinked = Number(rec.linkedPaymentTotal || 0) !== 0;
  return `<div class="pa-card"><div class="pa-card-head"><h3>${esc(t('reconciliation'))}</h3><span class="pa-chip ${rec.fullyReconciled ? 'ok' : 'warn'}">${rec.fullyReconciled ? '✓' : '!'}</span></div>
    <div class="pa-reconcile-grid">
      <span>${esc(t('total'))}<strong>${esc(money(rec.purchaseTotal, currency))}</strong></span>
      <span>${esc(t('itemTotal'))}<strong>${esc(money(rec.itemTotal, currency))}</strong></span>
      <span>${esc(t('paymentTotal'))}<strong>${esc(money(rec.linkedPaymentTotal, currency))}</strong></span>
      <span>${esc(t('itemDifference'))}<strong class="${Math.abs(itemDiff) > Number(rec.tolerance || .01) ? 'warn-text' : ''}">${esc(money(itemDiff, currency))}</strong></span>
      <span>${esc(t('paymentDifference'))}<strong class="${paymentLinked && Math.abs(paymentDiff) > Number(rec.tolerance || .01) ? 'warn-text' : ''}">${esc(money(paymentDiff, currency))}</strong></span>
    </div>${writable && !rec.itemsReconciled ? `<button type="button" class="ghost" data-accept-items>${esc(t('acceptDifference'))}</button>` : ''}${writable && paymentLinked && !rec.paymentsReconciled ? `<button type="button" class="ghost" data-accept-payments>${esc(t('acceptDifference'))}</button>` : ''}</div>`;
}

async function acceptDifference(dlg, id, kind) {
  try {
    await api(`api/purchases/${id}/reconciliation/accept-difference`, json('POST', { kind, reason: 'other', note: 'Explicitly accepted in purchase workspace' }));
    await refreshWorkspace(dlg, id);
  } catch (error) { showDialogError(dlg, error.message); }
}

async function choosePayment(parent, purchaseId) {
  try {
    const data = await api(`api/purchases/${purchaseId}/payment-candidates`);
    const rows = data.candidates || [];
    const dlg = makeDialog(`<div class="pa-dialog-card pa-picker"><div class="panel-head"><h2>${esc(t('paymentCandidates'))}</h2><button type="button" data-close>×</button></div><div class="pa-list">${rows.length ? rows.map(row => {
      const suggested = Math.min(Math.abs(Number(row.amount || 0)), Number(data.remaining || 0));
      return `<button type="button" class="pa-picker-row" data-tx="${row.id}" data-amount="${suggested}" data-confidence="${row.confidence}"><div><strong>${esc(row.counterparty || '—')}</strong><span>${esc(fmtDate(row.bookingDate))} · ${Math.round(Number(row.confidence) * 100)}%</span></div><strong>${esc(money(row.amount, row.currency))}</strong></button>`;
    }).join('') : `<div class="state-empty">${esc(t('noCandidates'))}</div>`}</div></div>`);
    dlg.querySelector('[data-close]').onclick = () => dlg.close();
    dlg.querySelectorAll('[data-tx]').forEach(button => button.onclick = async () => {
      try {
        await api(`api/purchases/${purchaseId}/payments`, json('POST', {
          transactionId: button.dataset.tx, amount: Number(button.dataset.amount), currency: data.purchaseCurrency,
          linkSource: 'manual', confidence: Number(button.dataset.confidence)
        }));
        dlg.close();
        await refreshWorkspace(parent, purchaseId);
      } catch (error) { showDialogError(dlg, error.message); }
    });
    dlg.showModal();
  } catch (error) { showDialogError(parent, error.message); }
}

async function uploadDocument(dlg, purchaseId, file) {
  if (!file) return;
  const form = new FormData();
  form.append('document', file);
  form.append('documentType', 'receipt');
  try { await api(`api/purchases/${purchaseId}/documents`, { method: 'POST', body: form }); await refreshWorkspace(dlg, purchaseId); }
  catch (error) { showDialogError(dlg, error.message); }
}

async function confirmPurchase(dlg, purchaseId) {
  try {
    await api(`api/purchases/${purchaseId}/confirm`, json('POST', { createSafeAllocations: true, allowUnlinked: true }));
    await refreshWorkspace(dlg, purchaseId);
    if (activeTab === 'articles') await renderArticles();
  } catch (error) { showDialogError(dlg, error.message); }
}

async function deletePurchase(dlg, id) {
  if (!window.confirm(t('deleteConfirm'))) return;
  try { await api(`api/purchases/${id}`, { method: 'DELETE' }); dlg.close(); if (activeTab === 'articles') await renderArticles(); }
  catch (error) { showDialogError(dlg, error.message); }
}

async function refreshWorkspace(oldDialog, id) {
  oldDialog.close();
  await openPurchaseWorkspace(id);
}

async function chooseProduct(parent, purchaseId, itemId, itemRow) {
  const initial = itemRow.querySelector('[data-f="name"]')?.value || '';
  const dlg = makeDialog(`<div class="pa-dialog-card pa-picker"><div class="panel-head"><h2>${esc(t('chooseProduct'))}</h2><button type="button" data-close>×</button></div><div class="pa-toolbar"><input type="search" data-product-query value="${esc(initial)}" placeholder="${esc(t('search'))}"><button type="button" data-product-search>${esc(t('search'))}</button><button type="button" class="ghost" data-product-create>${esc(t('newProduct'))}</button></div><div data-product-results class="pa-list"></div></div>`);
  const load = async () => {
    const q = dlg.querySelector('[data-product-query]').value.trim();
    const data = await api(`api/products?limit=80${q ? `&query=${encodeURIComponent(q)}` : ''}`);
    dlg.querySelector('[data-product-results]').innerHTML = (data.items || []).map(product => `<button type="button" class="pa-picker-row" data-product-id="${product.id}"><div><strong>${esc(product.canonicalName)}</strong><span>${esc(product.brand || '')}</span></div><span>${product.lastPrice == null ? '' : esc(money(product.lastPrice, product.lastCurrency || 'EUR'))}</span></button>`).join('') || `<div class="state-empty">${esc(t('noData'))}</div>`;
    dlg.querySelectorAll('[data-product-id]').forEach(button => button.onclick = async () => {
      try { await api(`api/purchases/${purchaseId}/items/${itemId}/match-product?productId=${encodeURIComponent(button.dataset.productId)}`, { method: 'POST' }); dlg.close(); await refreshWorkspace(parent, purchaseId); }
      catch (error) { showDialogError(dlg, error.message); }
    });
  };
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-product-search]').onclick = load;
  dlg.querySelector('[data-product-query]').addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); load(); } });
  dlg.querySelector('[data-product-create]').onclick = async () => {
    const name = dlg.querySelector('[data-product-query]').value.trim();
    if (!name) return;
    try {
      const created = await api('api/products', json('POST', { canonicalName: name, brand: null, defaultCategoryId: null, defaultQuantityUnit: null, defaultPackageQuantity: null, defaultPackageUnit: null, imageReference: null, notes: null }));
      const id = created?.product?.id || created?.id;
      if (id) { await api(`api/purchases/${purchaseId}/items/${itemId}/match-product?productId=${encodeURIComponent(id)}`, { method: 'POST' }); dlg.close(); await refreshWorkspace(parent, purchaseId); }
      else await load();
    } catch (error) { showDialogError(dlg, error.message); }
  };
  await load();
  dlg.showModal();
}

async function renderProducts() {
  advancedPanel.innerHTML = `<div class="panel-head"><div><h2>${esc(t('products'))}</h2><div class="row-sub">FullWorth-Space-Produktkatalog mit Preisverlauf und Grundpreisen</div></div><button type="button" data-new-product>${esc(t('newProduct'))}</button></div><div class="pa-toolbar"><input type="search" data-product-query placeholder="${esc(t('search'))}"><button type="button" data-product-refresh>${esc(t('search'))}</button></div><div data-products class="pa-list"></div>`;
  const load = async () => {
    const q = advancedPanel.querySelector('[data-product-query]').value.trim();
    const data = await api(`api/products?limit=200${q ? `&query=${encodeURIComponent(q)}` : ''}`);
    const box = advancedPanel.querySelector('[data-products]');
    box.innerHTML = (data.items || []).map(product => `<button type="button" class="pa-product-row" data-product-id="${product.id}"><div class="pa-row-main"><strong>${esc(product.canonicalName)}</strong><span>${esc(product.brand || '')} · ${product.purchaseCount} ${esc(t('purchaseCount'))}</span></div><div>${product.lastPrice == null ? '—' : `<strong>${esc(money(product.lastPrice, product.lastCurrency || 'EUR'))}</strong>${product.lastBasePrice == null ? '' : `<span>${esc(money(product.lastBasePrice, product.lastCurrency || 'EUR'))}/${esc(product.defaultPackageUnit || product.defaultQuantityUnit || 'unit')}</span>`}`}</div></button>`).join('') || `<div class="state-empty">${esc(t('noData'))}</div>`;
    box.querySelectorAll('[data-product-id]').forEach(button => button.onclick = () => openProduct(button.dataset.productId));
  };
  advancedPanel.querySelector('[data-product-refresh]').onclick = load;
  advancedPanel.querySelector('[data-product-query]').addEventListener('keydown', event => { if (event.key === 'Enter') load(); });
  advancedPanel.querySelector('[data-new-product]').onclick = () => openProductCreate(load);
  await load();
}

async function openProductCreate(onSaved) {
  const categories = await api('api/categories').catch(() => []);
  const dlg = makeDialog(`<form class="pa-dialog-card pa-small-form"><div class="panel-head"><h2>${esc(t('newProduct'))}</h2><button type="button" data-close>×</button></div><label>${esc(t('name'))}<input name="name" required></label><label>${esc(t('brand'))}<input name="brand"></label><label>${esc(t('category'))}<select name="category"><option value="">—</option>${categoryOptionsHtml(categories)}</select></label><div class="pa-form-grid"><label>${esc(t('unit'))}<input name="unit" placeholder="piece / kg / l"></label><label>Packungsmenge<input name="packageQuantity" type="number" step="0.001"></label><label>Packungseinheit<input name="packageUnit" placeholder="g / ml / piece"></label></div><label>${esc(t('notes'))}<textarea name="notes"></textarea></label><div class="dialog-actions"><button type="button" data-close>${esc(t('close'))}</button><button type="submit">${esc(t('create'))}</button></div></form>`);
  dlg.querySelectorAll('[data-close]').forEach(x => x.onclick = () => dlg.close());
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    try {
      await api('api/products', json('POST', { canonicalName: fd.get('name'), brand: fd.get('brand') || null, defaultCategoryId: fd.get('category') || null, defaultQuantityUnit: fd.get('unit') || null, defaultPackageQuantity: fd.get('packageQuantity') ? Number(fd.get('packageQuantity')) : null, defaultPackageUnit: fd.get('packageUnit') || null, imageReference: null, notes: fd.get('notes') || null }));
      dlg.close(); await onSaved?.();
    } catch (error) { showDialogError(dlg, error.message); }
  };
  dlg.showModal();
}

async function openProduct(id) {
  const data = await api(`api/products/${id}`);
  const product = data.product;
  const history = data.history || {};
  const comp = history.latestComparison;
  const dlg = makeDialog(`<div class="pa-dialog-card pa-product-detail"><div class="panel-head"><div><h2>${esc(product.canonicalName)}</h2><div class="row-sub">${esc(product.brand || '')}</div></div><button type="button" data-close>×</button></div>
    ${comp ? `<div class="pa-metrics"><div><span>Packung</span><strong>${comp.packPriceChangePercent == null ? '—' : `${comp.packPriceChangePercent}%`}</strong></div><div><span>${esc(t('basePrice'))}</span><strong>${comp.basePriceChangePercent == null ? '—' : `${comp.basePriceChangePercent}%`}</strong></div><div><span>Packungsgröße</span><strong>${comp.packageSizeChangePercent == null ? '—' : `${comp.packageSizeChangePercent}%`}</strong></div>${comp.possibleShrinkflation ? `<div class="warn"><span>⚠</span><strong>${esc(t('shrinkflation'))}</strong></div>` : ''}</div>` : ''}
    <h3>${esc(t('history'))}</h3><div class="pa-list">${(history.observations || []).slice().reverse().map(row => `<div class="pa-history-row"><div><strong>${esc(row.merchant)}</strong><span>${esc(fmtDate(row.purchaseDate))} · ${esc(row.name)}</span></div><div><strong>${esc(money(row.unitPrice ?? row.totalPrice, row.currency))}</strong>${row.baseUnitPrice == null ? '' : `<span>${esc(money(row.baseUnitPrice, row.currency))}/${esc(row.packageUnit || row.quantityUnit)}</span>`}</div></div>`).join('') || `<div class="state-empty">${esc(t('noData'))}</div>`}</div></div>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.showModal();
}

async function renderAnalytics() {
  const [overview, categories, products, brands, changes] = await Promise.all([
    api('api/purchase-analytics/overview'),
    api('api/purchase-analytics/by-category'),
    api('api/purchase-analytics/by-product'),
    api('api/purchase-analytics/by-brand'),
    api('api/purchase-analytics/price-changes')
  ]);
  advancedPanel.innerHTML = `<div class="panel-head"><div><h2>${esc(t('analytics'))}</h2><div class="row-sub">Artikel-, Produkt- und Händlerdaten aus bestätigten Käufen</div></div><button type="button" class="ghost" data-refresh-analytics>${esc(t('refresh'))}</button></div>
    <div class="pa-metrics"><div><span>${esc(t('spend'))}</span><strong>${esc(money(overview.totalSpend, overview.baseCurrency))}</strong></div><div><span>${esc(t('purchaseCount'))}</span><strong>${overview.purchaseCount}</strong></div><div><span>${esc(t('articles'))}</span><strong>${overview.itemCount}</strong></div><div class="${overview.needsReview ? 'warn' : ''}"><span>${esc(t('needsReview'))}</span><strong>${overview.needsReview}</strong></div></div>
    <div class="pa-analytics-grid">${analyticsCard(t('topCategories'), categories.items, categories.currency)}${analyticsCard(t('topProducts'), products.items, products.currency)}${analyticsCard(t('topBrands'), brands.items, brands.currency)}<div class="pa-card"><div class="pa-card-head"><h3>${esc(t('priceChanges'))}</h3></div>${(changes.items || []).slice(0, 12).map(row => `<div class="pa-analytics-row"><div><strong>${esc(row.productName)}</strong><span>${esc(row.current?.merchant || '')}</span></div><div>${row.comparison?.basePriceChangePercent == null ? '—' : `${row.comparison.basePriceChangePercent}%`}${row.comparison?.possibleShrinkflation ? `<span class="warn-text"> ${esc(t('shrinkflation'))}</span>` : ''}</div></div>`).join('') || `<div class="state-empty">${esc(t('noData'))}</div>`}</div></div>`;
  advancedPanel.querySelector('[data-refresh-analytics]').onclick = renderAnalytics;
}

function analyticsCard(title, items = [], currency = 'EUR') {
  return `<div class="pa-card"><div class="pa-card-head"><h3>${esc(title)}</h3></div>${items.slice(0, 10).map(row => `<div class="pa-analytics-row"><div><strong>${esc(row.label)}</strong><span>${row.count}×</span></div><strong>${esc(money(row.amount, currency))}</strong></div>`).join('') || `<div class="state-empty">${esc(t('noData'))}</div>`}</div>`;
}

function categoryOptionsHtml(categories) {
  const byId = new Map(categories.map(x => [x.id, x]));
  const path = row => {
    const names = []; const seen = new Set(); let current = row;
    while (current && !seen.has(current.id)) { seen.add(current.id); names.unshift(current.name); current = current.parentId ? byId.get(current.parentId) : null; }
    return names.join(' › ');
  };
  return categories.filter(x => !x.isArchived).map(x => `<option value="${x.id}">${esc(path(x))}</option>`).join('');
}

function makeDialog(html) {
  const dlg = document.createElement('dialog');
  dlg.className = 'pa-dialog';
  dlg.innerHTML = html;
  document.body.appendChild(dlg);
  dlg.addEventListener('close', () => dlg.remove(), { once: true });
  return dlg;
}

function showDialogError(dlg, message) {
  let box = dlg.querySelector('[data-error]');
  if (!box) {
    box = document.createElement('div');
    box.className = 'pa-dialog-error';
    box.dataset.error = '';
    dlg.firstElementChild?.appendChild(box);
  }
  box.hidden = false;
  box.textContent = message || t('error');
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, { once: true });
else queueMicrotask(install);
