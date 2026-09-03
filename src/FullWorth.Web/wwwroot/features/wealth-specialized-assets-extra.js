const SUPPORTED = new Set(['collectible', 'receivable', 'business_interest', 'insurance_pension']);
let enhancing = false;
let scheduled = null;

const COPY = {
  de: {
    details: 'Details', valuation: 'Bewertung', history: 'Historie', payments: 'Zahlungen', distributions: 'Ausschüttungen', close: 'Schließen', save: 'Speichern', add: 'Hinzufügen', accept: 'Wert übernehmen',
    collectible: 'Sammlerstück / Wertgegenstand', receivable: 'Forderung / privates Darlehen', business_interest: 'Unternehmensbeteiligung', insurance_pension: 'Versicherung / Vorsorge',
    manualValue: 'Aktueller Wert', amount: 'Betrag', currency: 'Währung', date: 'Datum', current: 'Aktuell', noHistory: 'Noch keine Bewertungen vorhanden.', notes: 'Notizen', privacy: 'Sensible Identifikatoren werden im Privacy-Modus verdeckt.',
    referenceOnly: 'Referenzwerte ändern den Vermögenswert nicht automatisch.',
    category: 'Kategorie', maker: 'Hersteller / Künstler', model: 'Modell / Bezeichnung', serial: 'Seriennummer', condition: 'Zustand', purchaseDate: 'Kaufdatum', purchasePrice: 'Kaufpreis', insuredValue: 'Versicherungswert', appraisedValue: 'Gutachtenwert', appraisedAt: 'Gutachten vom', provenance: 'Provenienz / Herkunft', acceptAppraisal: 'Gutachtenwert übernehmen',
    counterparty: 'Gegenpartei', originalPrincipal: 'Ursprünglicher Kapitalbetrag', outstandingPrincipal: 'Offener Kapitalbetrag', interestRate: 'Zinssatz %', startDate: 'Startdatum', dueDate: 'Fällig am', paymentCycle: 'Zahlungsrhythmus', expectedPayment: 'Erwartete Rate', status: 'Status',
    principal: 'Tilgung', interest: 'Zins', transaction: 'Banktransaktion (optional)', manualPayment: 'Manuell / keine Verknüpfung', noPayments: 'Noch keine Zahlungen erfasst.', recordPayment: 'Zahlung erfassen', writeDown: 'Abschreiben / Wert berichtigen', recoverable: 'Noch werthaltiger Betrag', confirmWriteDown: 'Ich bestätige die Abschreibung ausdrücklich', writeDownButton: 'Abschreibung übernehmen',
    company: 'Unternehmen', legalForm: 'Rechtsform', ownership: 'Anteil %', acquisitionDate: 'Erwerbsdatum', investedCapital: 'Investiertes Kapital', valuationMethod: 'Bewertungsmethode', lastDistribution: 'Letzte Ausschüttung', noDistributions: 'Noch keine Ausschüttungen erfasst.', addDistribution: 'Ausschüttung erfassen',
    provider: 'Anbieter', product: 'Produkt', productType: 'Produkttyp', policy: 'Policen-/Vertragsnummer', maturityDate: 'Laufzeitende', contribution: 'Regelmäßiger Beitrag', contributionCycle: 'Beitragsrhythmus', guaranteedValue: 'Garantiewert', guaranteedDate: 'Garantiewert zum',
    saved: 'Gespeichert.', accepted: 'Bewertung übernommen.', invalid: 'Aktion fehlgeschlagen.'
  },
  en: {
    details: 'Details', valuation: 'Valuation', history: 'History', payments: 'Payments', distributions: 'Distributions', close: 'Close', save: 'Save', add: 'Add', accept: 'Accept value',
    collectible: 'Collectible / valuable', receivable: 'Receivable / private loan', business_interest: 'Business interest', insurance_pension: 'Insurance / pension',
    manualValue: 'Current value', amount: 'Amount', currency: 'Currency', date: 'Date', current: 'Current', noHistory: 'No valuations yet.', notes: 'Notes', privacy: 'Sensitive identifiers are masked in privacy mode.',
    referenceOnly: 'Reference values do not change net worth automatically.',
    category: 'Category', maker: 'Maker / artist', model: 'Model / description', serial: 'Serial number', condition: 'Condition', purchaseDate: 'Purchase date', purchasePrice: 'Purchase price', insuredValue: 'Insured value', appraisedValue: 'Appraised value', appraisedAt: 'Appraised at', provenance: 'Provenance', acceptAppraisal: 'Accept appraisal value',
    counterparty: 'Counterparty', originalPrincipal: 'Original principal', outstandingPrincipal: 'Outstanding principal', interestRate: 'Interest rate %', startDate: 'Start date', dueDate: 'Due date', paymentCycle: 'Payment cycle', expectedPayment: 'Expected payment', status: 'Status',
    principal: 'Principal', interest: 'Interest', transaction: 'Bank transaction (optional)', manualPayment: 'Manual / unlinked', noPayments: 'No payments recorded.', recordPayment: 'Record payment', writeDown: 'Write down / impair value', recoverable: 'Recoverable amount', confirmWriteDown: 'I explicitly confirm this write-down', writeDownButton: 'Apply write-down',
    company: 'Company', legalForm: 'Legal form', ownership: 'Ownership %', acquisitionDate: 'Acquisition date', investedCapital: 'Invested capital', valuationMethod: 'Valuation method', lastDistribution: 'Last distribution', noDistributions: 'No distributions recorded.', addDistribution: 'Record distribution',
    provider: 'Provider', product: 'Product', productType: 'Product type', policy: 'Policy reference', maturityDate: 'Maturity date', contribution: 'Regular contribution', contributionCycle: 'Contribution cycle', guaranteedValue: 'Guaranteed value', guaranteedDate: 'Guaranteed value date',
    saved: 'Saved.', accepted: 'Valuation accepted.', invalid: 'Action failed.'
  }
};

function lang() { return (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? 'de' : 'en'; }
function t(key) { return COPY[lang()][key] || key; }
function esc(value) { return String(value ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c])); }
function privacy() { return document.querySelector('#privacy-toggle')?.getAttribute('aria-pressed') === 'true'; }
function money(value, currency) {
  if (privacy()) return '••••••';
  if (value == null || Number.isNaN(Number(value))) return '—';
  try { return new Intl.NumberFormat(lang() === 'de' ? 'de-DE' : 'en-US', { style: 'currency', currency: currency || 'EUR' }).format(Number(value)); }
  catch { return `${Number(value).toFixed(2)} ${currency || ''}`.trim(); }
}
function fmtDate(value) { if (!value) return '—'; try { return new Intl.DateTimeFormat(lang() === 'de' ? 'de-DE' : 'en-US').format(new Date(`${String(value).slice(0, 10)}T12:00:00`)); } catch { return String(value); } }
function today() { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; }
function num(value) { return value === '' || value == null ? null : Number(value); }
function toast(message) { const el = document.querySelector('#toast'); if (!el) return; el.textContent = message; el.classList.add('show'); setTimeout(() => el.classList.remove('show'), 2600); }
function json(method, body) { return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }; }
function opts(values, selected) { return values.map(value => `<option value="${esc(value)}"${value === selected ? ' selected' : ''}>${esc(value)}</option>`).join(''); }

function withSpace(path) {
  const [base, query = ''] = path.split('?');
  const params = new URLSearchParams(query);
  const space = localStorage.getItem('finance.space');
  if (space && !params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', space);
  return `/bff/backend/${base.replace(/^\//, '')}${params.toString() ? `?${params}` : ''}`;
}
async function api(path, options) {
  const response = await fetch(withSpace(path), options);
  if (!response.ok) { let message = `${response.status}`; try { const body = await response.json(); message = body.error || body.message || body.title || message; } catch {} throw new Error(message); }
  if (response.status === 204) return null;
  return response.json();
}
function dialog(html) { const dlg = document.createElement('dialog'); dlg.innerHTML = html; document.body.appendChild(dlg); dlg.addEventListener('close', () => dlg.remove()); return dlg; }
function orderedAssets(assets) { return [...assets.filter(x => x.kind === 'real_estate'), ...assets.filter(x => x.kind === 'vehicle'), ...assets.filter(x => !['real_estate', 'vehicle'].includes(x.kind))]; }

function scheduleEnhance() { clearTimeout(scheduled); scheduled = setTimeout(enhanceRows, 35); }
async function enhanceRows() {
  if (enhancing) return;
  const root = document.querySelector('#assets-list');
  if (!root || !localStorage.getItem('finance.space')) return;
  enhancing = true;
  try {
    const assets = orderedAssets(await api('api/assets'));
    const rows = [...root.querySelectorAll('.nw-item')];
    rows.forEach((row, index) => {
      const asset = assets[index];
      if (!asset || !SUPPORTED.has(asset.kind) || row.querySelector('[data-extra-specialized-detail]')) return;
      const side = row.querySelector('.row-side'); if (!side) return;
      const button = document.createElement('button');
      button.type = 'button'; button.className = 'icon-button specialized-detail-button'; button.dataset.extraSpecializedDetail = '1';
      button.title = t('details'); button.setAttribute('aria-label', t('details')); button.textContent = '›';
      const history = side.querySelector('[data-history]'); if (history) history.replaceWith(button); else side.insertBefore(button, side.querySelector('[data-toggle]') || null);
      button.addEventListener('click', () => openAsset(asset));
    });
  } catch (error) { console.debug('Remaining specialized asset enhancer unavailable', error); }
  finally { enhancing = false; }
}

function endpoint(asset) {
  return `api/assets/${asset.id}/${({ collectible: 'collectible', receivable: 'receivable', business_interest: 'business-interest', insurance_pension: 'insurance-pension' })[asset.kind]}`;
}

async function openAsset(asset) {
  const base = endpoint(asset);
  const extra = asset.kind === 'receivable'
    ? Promise.all([api(`${base}/payments`).catch(() => []), api('api/transactions?direction=income&limit=100').catch(() => ({ items: [] }))])
    : asset.kind === 'business_interest'
      ? api(`api/assets/${asset.id}/cashflows`).catch(() => [])
      : Promise.resolve(null);
  let detail, valuations, activity;
  try { [detail, valuations, activity] = await Promise.all([api(base).catch(() => null), api(`api/assets/${asset.id}/valuations`).catch(() => []), extra]); }
  catch (error) { toast(error.message || t('invalid')); return; }

  const activityTab = asset.kind === 'receivable' ? `<button type="button" data-tab="activity">${esc(t('payments'))}</button>`
    : asset.kind === 'business_interest' ? `<button type="button" data-tab="activity">${esc(t('distributions'))}</button>` : '';
  const dlg = dialog(`<div class="dialog-card specialized-asset-dialog">
    <div class="panel-head"><div><h2>${esc(asset.name)}</h2><div class="row-sub">${esc(t(asset.kind))} · ${money(asset.currentValue, asset.currency)}</div></div><button type="button" data-close aria-label="${esc(t('close'))}">×</button></div>
    <p class="specialized-privacy-note">${esc(t('privacy'))}</p>
    <div class="specialized-asset-tabs"><button type="button" data-tab="details">${esc(t('details'))}</button><button type="button" data-tab="valuation">${esc(t('valuation'))}</button>${activityTab}<button type="button" data-tab="history">${esc(t('history'))}</button></div>
    <section data-panel="details">${detailForm(asset, detail)}</section>
    <section data-panel="valuation" hidden>${valuationPanel(asset, detail)}</section>
    ${activityTab ? `<section data-panel="activity" hidden>${activityPanel(asset, activity)}</section>` : ''}
    <section data-panel="history" hidden>${historyPanel(valuations)}</section>
  </div>`);
  dlg.querySelector('[data-close]').addEventListener('click', () => dlg.close());
  bindTabs(dlg); bindDetails(dlg, asset, base); bindValuation(dlg, asset, detail);
  if (asset.kind === 'receivable') bindReceivableActivity(dlg, asset, base);
  if (asset.kind === 'business_interest') bindDistributionActivity(dlg, asset);
  dlg.showModal();
}

function bindTabs(dlg) {
  const buttons = [...dlg.querySelectorAll('[data-tab]')]; const panels = [...dlg.querySelectorAll('[data-panel]')];
  const show = id => { buttons.forEach(b => b.setAttribute('aria-selected', String(b.dataset.tab === id))); panels.forEach(p => p.hidden = p.dataset.panel !== id); };
  buttons.forEach(b => b.addEventListener('click', () => show(b.dataset.tab))); show('details');
}

function detailForm(asset, d) {
  if (asset.kind === 'collectible') return `<form data-detail-form class="specialized-asset-section"><div class="specialized-asset-grid">
    <label>${esc(t('category'))}<select name="category">${opts(['watch','jewelry','art','trading_card','wine','instrument','electronics','other'], d?.category || 'other')}</select></label>
    <label>${esc(t('maker'))}<input name="maker" maxlength="160" value="${esc(d?.maker || '')}"></label><label>${esc(t('model'))}<input name="model" maxlength="160" value="${esc(d?.model || '')}"></label>
    <label>${esc(t('serial'))}<input type="${privacy() ? 'password' : 'text'}" name="serialNumber" maxlength="160" value="${esc(d?.serialNumber || '')}"></label><label>${esc(t('condition'))}<input name="condition" maxlength="64" value="${esc(d?.condition || '')}"></label>
    <label>${esc(t('purchaseDate'))}<input type="date" name="purchaseDate" value="${esc(d?.purchaseDate || '')}"></label><label>${esc(t('purchasePrice'))}<input type="number" min="0" step="0.01" name="purchasePrice" value="${esc(d?.purchasePrice ?? '')}"></label>
    <label>${esc(t('currency'))}<input name="purchaseCurrency" maxlength="3" value="${esc(d?.purchaseCurrency || asset.currency || 'EUR')}"></label><label>${esc(t('insuredValue'))}<input type="number" min="0" step="0.01" name="insuredValue" value="${esc(d?.insuredValue ?? '')}"></label>
    <label>${esc(t('appraisedValue'))}<input type="number" min="0" step="0.01" name="appraisedValue" value="${esc(d?.appraisedValue ?? '')}"></label><label>${esc(t('appraisedAt'))}<input type="date" name="appraisedAt" value="${esc(d?.appraisedAt || '')}"></label>
    <label class="span-2">${esc(t('provenance'))}<textarea name="provenanceNotes" maxlength="4000">${esc(d?.provenanceNotes || '')}</textarea></label></div><p class="row-sub">${esc(t('referenceOnly'))}</p><div class="specialized-form-actions"><button type="submit">${esc(t('save'))}</button></div></form>`;

  if (asset.kind === 'receivable') return `<form data-detail-form class="specialized-asset-section"><div class="specialized-asset-grid">
    <label>${esc(t('counterparty'))}<input type="${privacy() ? 'password' : 'text'}" name="counterpartyDisplayLabel" maxlength="200" value="${esc(d?.counterpartyDisplayLabel || '')}" required></label>
    <label>${esc(t('currency'))}<input name="currency" maxlength="3" value="${esc(d?.currency || asset.currency || 'EUR')}" required></label><label>${esc(t('originalPrincipal'))}<input type="number" min="0" step="0.01" name="originalPrincipal" value="${esc(d?.originalPrincipal ?? asset.currentValue ?? '')}" required></label>
    <label>${esc(t('outstandingPrincipal'))}<input type="number" min="0" step="0.01" name="outstandingPrincipal" value="${esc(d?.outstandingPrincipal ?? asset.currentValue ?? '')}" required></label><label>${esc(t('interestRate'))}<input type="number" min="0" step="0.0001" name="interestRate" value="${esc(d?.interestRate ?? '')}"></label>
    <label>${esc(t('startDate'))}<input type="date" name="startDate" value="${esc(d?.startDate || '')}"></label><label>${esc(t('dueDate'))}<input type="date" name="dueDate" value="${esc(d?.dueDate || '')}"></label>
    <label>${esc(t('paymentCycle'))}<select name="paymentCycle"><option value=""></option>${opts(['weekly','monthly','quarterly','yearly','one_time','other'], d?.paymentCycle || '')}</select></label><label>${esc(t('expectedPayment'))}<input type="number" min="0" step="0.01" name="expectedPayment" value="${esc(d?.expectedPayment ?? '')}"></label>
    <label>${esc(t('status'))}<select name="status">${opts(['active','overdue','settled','written_off'], d?.status || 'active')}</select></label><label class="span-2">${esc(t('notes'))}<textarea name="notes" maxlength="2000">${esc(d?.notes || '')}</textarea></label>
    </div><p class="row-sub">${esc(t('referenceOnly'))}</p><div class="specialized-form-actions"><button type="submit">${esc(t('save'))}</button></div></form>`;

  if (asset.kind === 'business_interest') return `<form data-detail-form class="specialized-asset-section"><div class="specialized-asset-grid">
    <label>${esc(t('company'))}<input name="companyDisplayName" maxlength="240" value="${esc(d?.companyDisplayName || '')}" required></label><label>${esc(t('legalForm'))}<input name="legalForm" maxlength="80" value="${esc(d?.legalForm || '')}"></label>
    <label>${esc(t('ownership'))}<input type="number" min="0" max="100" step="0.0001" name="ownershipPercent" value="${esc(d?.ownershipPercent ?? '')}"></label><label>${esc(t('acquisitionDate'))}<input type="date" name="acquisitionDate" value="${esc(d?.acquisitionDate || '')}"></label>
    <label>${esc(t('investedCapital'))}<input type="number" min="0" step="0.01" name="investedCapital" value="${esc(d?.investedCapital ?? '')}"></label><label>${esc(t('currency'))}<input name="investedCurrency" maxlength="3" value="${esc(d?.investedCurrency || asset.currency || 'EUR')}"></label>
    <label>${esc(t('valuationMethod'))}<select name="valuationMethod"><option value=""></option>${opts(['manual','last_financing','earnings_multiple','book_value','external_appraisal','other'], d?.valuationMethod || '')}</select></label><label>${esc(t('lastDistribution'))}<input type="date" name="lastDistributionDate" value="${esc(d?.lastDistributionDate || '')}"></label>
    <label class="span-2">${esc(t('notes'))}<textarea name="notes" maxlength="3000">${esc(d?.notes || '')}</textarea></label></div><p class="row-sub">${esc(t('referenceOnly'))}</p><div class="specialized-form-actions"><button type="submit">${esc(t('save'))}</button></div></form>`;

  return `<form data-detail-form class="specialized-asset-section"><div class="specialized-asset-grid">
    <label>${esc(t('provider'))}<input name="providerName" maxlength="200" value="${esc(d?.providerName || '')}"></label><label>${esc(t('product'))}<input name="productName" maxlength="200" value="${esc(d?.productName || '')}"></label>
    <label>${esc(t('productType'))}<select name="productType">${opts(['pension','life_insurance','endowment','other'], d?.productType || 'pension')}</select></label><label>${esc(t('policy'))}<input type="${privacy() ? 'password' : 'text'}" name="policyReference" maxlength="200" value="${esc(d?.policyReference || '')}"></label>
    <label>${esc(t('startDate'))}<input type="date" name="startDate" value="${esc(d?.startDate || '')}"></label><label>${esc(t('maturityDate'))}<input type="date" name="maturityDate" value="${esc(d?.maturityDate || '')}"></label>
    <label>${esc(t('contribution'))}<input type="number" min="0" step="0.01" name="regularContribution" value="${esc(d?.regularContribution ?? '')}"></label><label>${esc(t('contributionCycle'))}<select name="contributionCycle"><option value=""></option>${opts(['weekly','monthly','quarterly','yearly','other'], d?.contributionCycle || '')}</select></label>
    <label>${esc(t('guaranteedValue'))}<input type="number" min="0" step="0.01" name="guaranteedValue" value="${esc(d?.guaranteedValue ?? '')}"></label><label>${esc(t('guaranteedDate'))}<input type="date" name="guaranteedValueDate" value="${esc(d?.guaranteedValueDate || '')}"></label>
    <label class="span-2">${esc(t('notes'))}<textarea name="notes" maxlength="3000">${esc(d?.notes || '')}</textarea></label></div><p class="row-sub">${esc(t('referenceOnly'))}</p><div class="specialized-form-actions"><button type="submit">${esc(t('save'))}</button></div></form>`;
}

function valuationPanel(asset, d) {
  const appraisal = asset.kind === 'collectible' && d?.appraisedValue != null
    ? `<div class="specialized-asset-section"><div class="specialized-debt-row"><div class="specialized-debt-main"><div class="specialized-debt-title">${esc(t('appraisedValue'))}</div><div class="specialized-debt-sub">${money(d.appraisedValue, d.purchaseCurrency || asset.currency)} · ${fmtDate(d.appraisedAt)}</div></div><button type="button" data-accept-appraisal>${esc(t('acceptAppraisal'))}</button></div></div>` : '';
  return `<div class="specialized-asset-section"><h3>${esc(t('manualValue'))}</h3><form data-manual-valuation class="specialized-asset-grid">
    <label>${esc(t('amount'))}<input type="number" min="0" step="0.01" name="amount" value="${esc(asset.currentValue ?? '')}" required></label><label>${esc(t('currency'))}<input name="currency" maxlength="3" value="${esc(asset.currency || 'EUR')}" required></label>
    <label>${esc(t('date'))}<input type="date" name="valuedAt" value="${today()}" required></label><div class="span-2 specialized-form-actions"><button type="submit">${esc(t('accept'))}</button></div></form></div>${appraisal}<p class="row-sub">${esc(t('referenceOnly'))}</p>`;
}

function historyPanel(valuations) {
  if (!(valuations || []).length) return `<div class="specialized-empty">${esc(t('noHistory'))}</div>`;
  return (valuations || []).map(v => `<div class="specialized-history-row"><div class="specialized-history-main"><div class="specialized-history-title">${money(v.amount, v.currency)}${v.isCurrent ? ` · ${esc(t('current'))}` : ''}</div><div class="specialized-history-sub">${esc(v.method)} · ${esc(fmtDate(v.valuedAt))}</div></div></div>`).join('');
}

function activityPanel(asset, activity) {
  if (asset.kind === 'receivable') {
    const [payments, transactions] = activity || [[], { items: [] }];
    const rows = (payments || []).map(p => `<div class="specialized-history-row"><div class="specialized-history-main"><div class="specialized-history-title">${esc(fmtDate(p.date))} · ${esc(t('principal'))}: ${money(p.principalAmount, p.currency)}</div><div class="specialized-history-sub">${esc(t('interest'))}: ${money(p.interestAmount, p.currency)}${p.transactionId ? ' · ✓ Transaction' : ''}</div></div></div>`).join('') || `<div class="specialized-empty">${esc(t('noPayments'))}</div>`;
    const txOptions = (transactions?.items || []).map(tx => `<option value="${esc(tx.id)}">${esc(fmtDate(tx.bookingDate))} · ${esc(tx.counterparty || tx.account || 'Transaction')} · ${money(tx.amount, tx.currency)}</option>`).join('');
    return `<div class="specialized-asset-section"><div data-payment-list>${rows}</div></div><div class="specialized-asset-section"><h3>${esc(t('recordPayment'))}</h3><form data-payment-form class="specialized-asset-grid">
      <label>${esc(t('transaction'))}<select name="transactionId"><option value="">${esc(t('manualPayment'))}</option>${txOptions}</select></label><label>${esc(t('date'))}<input type="date" name="date" value="${today()}" required></label>
      <label>${esc(t('principal'))}<input type="number" min="0" step="0.01" name="principalAmount" value="0" required></label><label>${esc(t('interest'))}<input type="number" min="0" step="0.01" name="interestAmount" value="0" required></label>
      <label>${esc(t('currency'))}<input name="currency" maxlength="3" value="EUR" required></label><label class="span-2">${esc(t('notes'))}<input name="notes" maxlength="1000"></label><div class="span-2 specialized-form-actions"><button type="submit">${esc(t('add'))}</button></div></form></div>
      <div class="specialized-asset-section"><h3>${esc(t('writeDown'))}</h3><form data-write-down class="specialized-asset-grid"><label>${esc(t('recoverable'))}<input type="number" min="0" step="0.01" name="recoverableAmount" required></label><label class="check"><input type="checkbox" name="confirmed" required><span>${esc(t('confirmWriteDown'))}</span></label><div class="span-2 specialized-form-actions"><button type="submit">${esc(t('writeDownButton'))}</button></div></form></div>`;
  }
  const rows = (activity || []).filter(x => x.type === 'distribution').map(x => `<div class="specialized-history-row"><div class="specialized-history-main"><div class="specialized-history-title">${esc(fmtDate(x.date))} · ${money(x.amount, x.currency)}</div><div class="specialized-history-sub">${esc(x.transactionCounterparty || x.notes || 'distribution')}</div></div></div>`).join('') || `<div class="specialized-empty">${esc(t('noDistributions'))}</div>`;
  return `<div class="specialized-asset-section">${rows}</div><div class="specialized-asset-section"><h3>${esc(t('addDistribution'))}</h3><form data-distribution-form class="specialized-asset-grid"><label>${esc(t('date'))}<input type="date" name="date" value="${today()}" required></label><label>${esc(t('amount'))}<input type="number" min="0.01" step="0.01" name="amount" required></label><label>${esc(t('currency'))}<input name="currency" maxlength="3" value="${esc(asset.currency || 'EUR')}" required></label><label class="span-2">${esc(t('notes'))}<input name="notes" maxlength="1000"></label><div class="span-2 specialized-form-actions"><button type="submit">${esc(t('add'))}</button></div></form></div>`;
}

function bindDetails(dlg, asset, base) {
  const form = dlg.querySelector('[data-detail-form]');
  form?.addEventListener('submit', async event => {
    event.preventDefault(); const fd = new FormData(form); let body;
    if (asset.kind === 'collectible') body = { category: fd.get('category'), maker: fd.get('maker') || null, model: fd.get('model') || null, serialNumber: fd.get('serialNumber') || null, condition: fd.get('condition') || null, purchaseDate: fd.get('purchaseDate') || null, purchasePrice: num(fd.get('purchasePrice')), purchaseCurrency: fd.get('purchaseCurrency') || null, insuredValue: num(fd.get('insuredValue')), appraisedValue: num(fd.get('appraisedValue')), appraisedAt: fd.get('appraisedAt') || null, provenanceNotes: fd.get('provenanceNotes') || null };
    else if (asset.kind === 'receivable') body = { counterpartyDisplayLabel: fd.get('counterpartyDisplayLabel'), originalPrincipal: Number(fd.get('originalPrincipal')), outstandingPrincipal: Number(fd.get('outstandingPrincipal')), currency: String(fd.get('currency')).toUpperCase(), interestRate: num(fd.get('interestRate')), startDate: fd.get('startDate') || null, dueDate: fd.get('dueDate') || null, paymentCycle: fd.get('paymentCycle') || null, expectedPayment: num(fd.get('expectedPayment')), status: fd.get('status'), notes: fd.get('notes') || null };
    else if (asset.kind === 'business_interest') body = { companyDisplayName: fd.get('companyDisplayName'), legalForm: fd.get('legalForm') || null, ownershipPercent: num(fd.get('ownershipPercent')), acquisitionDate: fd.get('acquisitionDate') || null, investedCapital: num(fd.get('investedCapital')), investedCurrency: fd.get('investedCurrency') || null, valuationMethod: fd.get('valuationMethod') || null, lastDistributionDate: fd.get('lastDistributionDate') || null, notes: fd.get('notes') || null };
    else body = { providerName: fd.get('providerName') || null, productName: fd.get('productName') || null, productType: fd.get('productType'), policyReference: fd.get('policyReference') || null, startDate: fd.get('startDate') || null, maturityDate: fd.get('maturityDate') || null, regularContribution: num(fd.get('regularContribution')), contributionCycle: fd.get('contributionCycle') || null, guaranteedValue: num(fd.get('guaranteedValue')), guaranteedValueDate: fd.get('guaranteedValueDate') || null, notes: fd.get('notes') || null };
    try { await api(base, json('PUT', body)); toast(t('saved')); dlg.close(); document.querySelector('#refresh')?.click(); } catch (error) { toast(error.message || t('invalid')); }
  });
}

function bindValuation(dlg, asset, d) {
  dlg.querySelector('[data-manual-valuation]')?.addEventListener('submit', async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    try { await api(`api/assets/${asset.id}/valuations`, json('POST', { amount: Number(fd.get('amount')), currency: String(fd.get('currency')).toUpperCase(), valuedAt: fd.get('valuedAt'), method: 'manual', isAccepted: true })); toast(t('accepted')); dlg.close(); document.querySelector('#refresh')?.click(); } catch (error) { toast(error.message || t('invalid')); }
  });
  dlg.querySelector('[data-accept-appraisal]')?.addEventListener('click', async () => {
    try { await api(`api/assets/${asset.id}/valuations`, json('POST', { amount: Number(d.appraisedValue), currency: d.purchaseCurrency || asset.currency || 'EUR', valuedAt: d.appraisedAt || today(), method: 'appraisal', isAccepted: true })); toast(t('accepted')); dlg.close(); document.querySelector('#refresh')?.click(); } catch (error) { toast(error.message || t('invalid')); }
  });
}

function bindReceivableActivity(dlg, asset, base) {
  dlg.querySelector('[data-payment-form]')?.addEventListener('submit', async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    try { await api(`${base}/payments`, json('POST', { transactionId: fd.get('transactionId') || null, date: fd.get('date'), principalAmount: Number(fd.get('principalAmount')), interestAmount: Number(fd.get('interestAmount')), currency: String(fd.get('currency')).toUpperCase(), notes: fd.get('notes') || null })); toast(t('saved')); dlg.close(); document.querySelector('#refresh')?.click(); } catch (error) { toast(error.message || t('invalid')); }
  });
  dlg.querySelector('[data-write-down]')?.addEventListener('submit', async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    try { await api(`${base}/write-down`, json('POST', { recoverableAmount: Number(fd.get('recoverableAmount')), confirmed: fd.get('confirmed') === 'on' })); toast(t('accepted')); dlg.close(); document.querySelector('#refresh')?.click(); } catch (error) { toast(error.message || t('invalid')); }
  });
}

function bindDistributionActivity(dlg, asset) {
  dlg.querySelector('[data-distribution-form]')?.addEventListener('submit', async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    try { await api(`api/assets/${asset.id}/cashflows`, json('POST', { transactionId: null, date: fd.get('date'), type: 'distribution', amount: Number(fd.get('amount')), direction: 'income', currency: String(fd.get('currency')).toUpperCase(), isPlanned: false, notes: fd.get('notes') || null })); toast(t('saved')); dlg.close(); document.querySelector('#refresh')?.click(); } catch (error) { toast(error.message || t('invalid')); }
  });
}

function init() {
  const root = document.querySelector('#assets-list'); if (!root) { setTimeout(init, 100); return; }
  new MutationObserver(scheduleEnhance).observe(root, { childList: true, subtree: true });
  document.querySelector('#privacy-toggle')?.addEventListener('click', scheduleEnhance); scheduleEnhance();
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init, { once: true }); else init();
