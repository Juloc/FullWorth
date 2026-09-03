const SUPPORTED = new Set(['vehicle', 'precious_metal']);
let enhancing = false;
let scheduled = null;

const TEXT = {
  de: {
    details: 'Details', valuation: 'Bewertung', financing: 'Finanzierung', history: 'Historie', close: 'Schließen', save: 'Speichern',
    manualValue: 'Manueller Wert', internalEstimate: 'Interne Schätzung', calculate: 'Berechnen', accept: 'Wert übernehmen',
    amount: 'Betrag', currency: 'Währung', date: 'Datum', current: 'Aktuell', noHistory: 'Noch keine Bewertungen vorhanden.',
    debtEmpty: 'Keine Finanzierung verknüpft.', debtAdd: 'Finanzierung verknüpfen', debtSource: 'Kredit / Verbindlichkeit', allocation: 'Zuordnung %', relation: 'Beziehung', add: 'Hinzufügen', remove: 'Entfernen',
    vehicle: 'Fahrzeug', precious_metal: 'Edelmetall', privacy: 'Sensible Kennzeichen-/Lagerdaten werden im Privacy-Modus verdeckt.',
    vehicleType: 'Fahrzeugtyp', manufacturer: 'Hersteller', model: 'Modell', variant: 'Variante', vin: 'VIN', licensePlate: 'Kennzeichen', firstRegistration: 'Erstzulassung', modelYear: 'Modelljahr', mileage: 'Kilometerstand', powertrain: 'Antrieb', powerKw: 'Leistung kW', purchaseDate: 'Kaufdatum', purchasePrice: 'Kaufpreis', condition: 'Zustand', annualMileage: 'Jahresfahrleistung', notes: 'Notizen',
    depreciation: 'Abschreibung/Jahr %', mileageAdj: 'Kilometer-Anpassung %', conditionAdj: 'Zustands-Anpassung %', range: 'Spanne %',
    metalType: 'Metall', form: 'Form', quantity: 'Anzahl', grossWeight: 'Bruttogewicht je Einheit (g)', purity: 'Feinheit (0–1)', fineWeight: 'Feingewicht gesamt', storage: 'Lagerort', referencePrice: 'Referenzpreis je Feingramm', premiumAdj: 'Auf-/Abschlag %',
    estimateHintVehicle: 'FullWorth verwendet nur Kaufdaten und deine expliziten Abschreibungs-/Anpassungswerte. Es wird kein Marktpreis erfunden.',
    estimateHintMetal: 'FullWorth verwendet Feingewicht und deinen expliziten Referenzpreis. Es wird kein Wertpapier-/Marktdatenfeed dupliziert.',
    saved: 'Gespeichert.', accepted: 'Bewertung übernommen.', invalid: 'Aktion fehlgeschlagen.'
  },
  en: {
    details: 'Details', valuation: 'Valuation', financing: 'Financing', history: 'History', close: 'Close', save: 'Save',
    manualValue: 'Manual value', internalEstimate: 'Internal estimate', calculate: 'Calculate', accept: 'Accept value',
    amount: 'Amount', currency: 'Currency', date: 'Date', current: 'Current', noHistory: 'No valuations yet.',
    debtEmpty: 'No financing linked.', debtAdd: 'Link financing', debtSource: 'Loan / liability', allocation: 'Allocation %', relation: 'Relation', add: 'Add', remove: 'Remove',
    vehicle: 'Vehicle', precious_metal: 'Precious metal', privacy: 'Sensitive registration/storage identifiers are masked in privacy mode.',
    vehicleType: 'Vehicle type', manufacturer: 'Manufacturer', model: 'Model', variant: 'Variant', vin: 'VIN', licensePlate: 'License plate', firstRegistration: 'First registration', modelYear: 'Model year', mileage: 'Mileage km', powertrain: 'Powertrain', powerKw: 'Power kW', purchaseDate: 'Purchase date', purchasePrice: 'Purchase price', condition: 'Condition', annualMileage: 'Annual mileage', notes: 'Notes',
    depreciation: 'Depreciation/year %', mileageAdj: 'Mileage adjustment %', conditionAdj: 'Condition adjustment %', range: 'Range %',
    metalType: 'Metal', form: 'Form', quantity: 'Quantity', grossWeight: 'Gross weight per unit (g)', purity: 'Purity (0–1)', fineWeight: 'Total fine weight', storage: 'Storage label', referencePrice: 'Reference price per fine gram', premiumAdj: 'Premium/discount %',
    estimateHintVehicle: 'FullWorth uses only purchase data and your explicit depreciation/adjustment inputs. It does not invent a market price.',
    estimateHintMetal: 'FullWorth uses fine weight and your explicit reference price. It does not duplicate securities/market-data feeds.',
    saved: 'Saved.', accepted: 'Valuation accepted.', invalid: 'Action failed.'
  }
};

function lang() { return (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? 'de' : 'en'; }
function t(key) { return TEXT[lang()][key] || key; }
function esc(value) { return String(value ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c])); }
function privacy() { return document.querySelector('#privacy-toggle')?.getAttribute('aria-pressed') === 'true'; }
function money(value, currency) {
  if (privacy()) return '••••••';
  if (value == null || Number.isNaN(Number(value))) return '—';
  try { return new Intl.NumberFormat(lang() === 'de' ? 'de-DE' : 'en-US', { style: 'currency', currency: currency || 'EUR' }).format(Number(value)); }
  catch { return `${Number(value).toFixed(2)} ${currency || ''}`.trim(); }
}
function date(value) { if (!value) return '—'; try { return new Intl.DateTimeFormat(lang() === 'de' ? 'de-DE' : 'en-US').format(new Date(`${String(value).slice(0, 10)}T12:00:00`)); } catch { return String(value); } }
function today() { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; }
function num(value) { return value === '' || value == null ? null : Number(value); }
function toast(message) { const el = document.querySelector('#toast'); if (!el) return; el.textContent = message; el.classList.add('show'); setTimeout(() => el.classList.remove('show'), 2600); }

function ensureCss() {
  if (document.querySelector('link[data-specialized-wealth-css]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/features/wealth-specialized-assets.css';
  link.dataset.specializedWealthCss = '1';
  document.head.appendChild(link);
}

function withSpace(path) {
  const [base, query = ''] = path.split('?');
  const params = new URLSearchParams(query);
  const space = localStorage.getItem('finance.space');
  if (space && !params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', space);
  return `/bff/backend/${base.replace(/^\//, '')}${params.toString() ? `?${params}` : ''}`;
}

async function api(path, options) {
  const response = await fetch(withSpace(path), options);
  if (!response.ok) {
    let message = `${response.status}`;
    try { const body = await response.json(); message = body.error || body.message || body.title || message; } catch {}
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return response.json();
}

function json(method, body) { return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }; }

function orderedAssets(assets) {
  return [
    ...assets.filter(x => x.kind === 'real_estate'),
    ...assets.filter(x => x.kind === 'vehicle'),
    ...assets.filter(x => !['real_estate', 'vehicle'].includes(x.kind))
  ];
}

function scheduleEnhance() {
  clearTimeout(scheduled);
  scheduled = setTimeout(enhanceRows, 30);
}

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
      if (!asset || !SUPPORTED.has(asset.kind) || row.querySelector('[data-specialized-detail]')) return;
      row.dataset.specializedAssetId = asset.id;
      const side = row.querySelector('.row-side');
      if (!side) return;
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'icon-button specialized-detail-button';
      button.dataset.specializedDetail = '1';
      button.title = t('details');
      button.setAttribute('aria-label', t('details'));
      button.textContent = '›';
      const history = side.querySelector('[data-history]');
      if (history) history.replaceWith(button);
      else side.insertBefore(button, side.querySelector('[data-toggle]') || null);
      button.addEventListener('click', () => openSpecializedAsset(asset));
    });
  } catch (error) {
    console.debug('Specialized asset enhancer unavailable', error);
  } finally {
    enhancing = false;
  }
}

function dialog(html) {
  const dlg = document.createElement('dialog');
  dlg.innerHTML = html;
  document.body.appendChild(dlg);
  dlg.addEventListener('close', () => dlg.remove());
  return dlg;
}

function tabs(dlg) {
  const buttons = [...dlg.querySelectorAll('[data-tab]')];
  const panels = [...dlg.querySelectorAll('[data-panel]')];
  const show = id => {
    buttons.forEach(b => b.setAttribute('aria-selected', String(b.dataset.tab === id)));
    panels.forEach(p => { p.hidden = p.dataset.panel !== id; });
  };
  buttons.forEach(b => b.addEventListener('click', () => show(b.dataset.tab)));
  show('details');
}

async function openSpecializedAsset(asset) {
  ensureCss();
  const endpoint = asset.kind === 'vehicle' ? `api/assets/${asset.id}/vehicle` : `api/assets/${asset.id}/precious-metal`;
  let detail = null, valuations = [], debts = [], loans = [], liabilities = [];
  try {
    [detail, valuations, debts, loans, liabilities] = await Promise.all([
      api(endpoint).catch(() => null),
      api(`api/assets/${asset.id}/valuations`).catch(() => []),
      api(`api/assets/${asset.id}/debts`).catch(() => []),
      api('api/loans').catch(() => []),
      api('api/liabilities').catch(() => [])
    ]);
  } catch (error) { toast(error.message || t('invalid')); return; }

  const dlg = dialog(`<div class="dialog-card specialized-asset-dialog">
    <div class="panel-head"><div><h2>${esc(asset.name)}</h2><div class="row-sub">${esc(t(asset.kind))} · ${money(asset.currentValue, asset.currency)}</div></div><button type="button" data-close aria-label="${esc(t('close'))}">×</button></div>
    <p class="specialized-privacy-note">${esc(t('privacy'))}</p>
    <div class="specialized-asset-tabs" role="tablist">
      <button type="button" data-tab="details" role="tab">${esc(t('details'))}</button>
      <button type="button" data-tab="valuation" role="tab">${esc(t('valuation'))}</button>
      <button type="button" data-tab="financing" role="tab">${esc(t('financing'))}</button>
      <button type="button" data-tab="history" role="tab">${esc(t('history'))}</button>
    </div>
    <section data-panel="details">${asset.kind === 'vehicle' ? vehicleForm(detail) : metalForm(detail)}</section>
    <section data-panel="valuation" hidden>${valuationPanel(asset, detail)}</section>
    <section data-panel="financing" hidden>${financingPanel(asset, debts, loans, liabilities)}</section>
    <section data-panel="history" hidden>${historyPanel(valuations)}</section>
  </div>`);

  dlg.querySelector('[data-close]').addEventListener('click', () => dlg.close());
  tabs(dlg);
  bindDetailForm(dlg, asset, endpoint);
  bindValuation(dlg, asset, endpoint);
  bindFinancing(dlg, asset);
  dlg.showModal();
}

function vehicleForm(d) {
  const secure = privacy() ? 'password' : 'text';
  return `<form data-detail-form class="specialized-asset-section"><h3>${esc(t('vehicle'))}</h3><div class="specialized-asset-grid">
    <label>${esc(t('vehicleType'))}<select name="vehicleType">${options(['car','motorcycle','camper','boat','other'], d?.vehicleType || 'car')}</select></label>
    <label>${esc(t('manufacturer'))}<input name="manufacturer" maxlength="120" value="${esc(d?.manufacturer || '')}"></label>
    <label>${esc(t('model'))}<input name="model" maxlength="120" value="${esc(d?.model || '')}"></label>
    <label>${esc(t('variant'))}<input name="variant" maxlength="120" value="${esc(d?.variant || '')}"></label>
    <label>${esc(t('vin'))}<input type="${secure}" name="vin" maxlength="80" value="${esc(d?.vin || '')}"></label>
    <label>${esc(t('licensePlate'))}<input type="${secure}" name="licensePlate" maxlength="40" value="${esc(d?.licensePlate || '')}"></label>
    <label>${esc(t('firstRegistration'))}<input type="date" name="firstRegistrationDate" value="${esc(d?.firstRegistrationDate || '')}"></label>
    <label>${esc(t('modelYear'))}<input type="number" min="1886" max="2200" name="modelYear" value="${esc(d?.modelYear ?? '')}"></label>
    <label>${esc(t('mileage'))}<input type="number" min="0" name="mileageKm" value="${esc(d?.mileageKm ?? '')}"></label>
    <label>${esc(t('powertrain'))}<select name="powertrain"><option value=""></option>${options(['petrol','diesel','hybrid','phev','electric','other'], d?.powertrain || '')}</select></label>
    <label>${esc(t('powerKw'))}<input type="number" min="0" step="0.1" name="powerKw" value="${esc(d?.powerKw ?? '')}"></label>
    <label>${esc(t('condition'))}<input name="condition" maxlength="32" value="${esc(d?.condition || '')}"></label>
    <label>${esc(t('purchaseDate'))}<input type="date" name="purchaseDate" value="${esc(d?.purchaseDate || '')}"></label>
    <label>${esc(t('purchasePrice'))}<input type="number" min="0" step="0.01" name="purchasePrice" value="${esc(d?.purchasePrice ?? '')}"></label>
    <label>${esc(t('currency'))}<input name="purchaseCurrency" maxlength="3" value="${esc(d?.purchaseCurrency || 'EUR')}"></label>
    <label>${esc(t('annualMileage'))}<input type="number" min="0" name="annualMileageEstimate" value="${esc(d?.annualMileageEstimate ?? '')}"></label>
    <label class="span-2">${esc(t('notes'))}<textarea name="notes" maxlength="2000">${esc(d?.notes || '')}</textarea></label>
  </div><div class="specialized-form-actions"><button type="submit">${esc(t('save'))}</button></div></form>`;
}

function metalForm(d) {
  const secure = privacy() ? 'password' : 'text';
  return `<form data-detail-form class="specialized-asset-section"><h3>${esc(t('precious_metal'))}</h3><div class="specialized-asset-grid">
    <label>${esc(t('metalType'))}<select name="metalType">${options(['gold','silver','platinum','palladium','other'], d?.metalType || 'gold')}</select></label>
    <label>${esc(t('form'))}<select name="form">${options(['bar','coin','jewelry','other'], d?.form || 'bar')}</select></label>
    <label>${esc(t('quantity'))}<input type="number" min="0.00000001" step="0.00000001" name="quantity" value="${esc(d?.quantity ?? 1)}"></label>
    <label>${esc(t('grossWeight'))}<input type="number" min="0" step="0.00000001" name="grossWeightGrams" value="${esc(d?.grossWeightGrams ?? '')}"></label>
    <label>${esc(t('purity'))}<input type="number" min="0" max="1" step="0.00000001" name="purity" value="${esc(d?.purity ?? '')}"></label>
    <label>${esc(t('fineWeight'))}<input disabled value="${esc(d?.fineWeightGrams != null ? `${d.fineWeightGrams} g` : '—')}"></label>
    <label>${esc(t('storage'))}<input type="${secure}" name="storageLabel" maxlength="200" value="${esc(d?.storageLabel || '')}"></label>
    <label>${esc(t('purchaseDate'))}<input type="date" name="purchaseDate" value="${esc(d?.purchaseDate || '')}"></label>
    <label>${esc(t('purchasePrice'))}<input type="number" min="0" step="0.01" name="purchasePrice" value="${esc(d?.purchasePrice ?? '')}"></label>
    <label>${esc(t('currency'))}<input name="purchaseCurrency" maxlength="3" value="${esc(d?.purchaseCurrency || 'EUR')}"></label>
    <label class="span-2">${esc(t('notes'))}<textarea name="notes" maxlength="2000">${esc(d?.notes || '')}</textarea></label>
  </div><div class="specialized-form-actions"><button type="submit">${esc(t('save'))}</button></div></form>`;
}

function valuationPanel(asset, detail) {
  const internal = asset.kind === 'vehicle'
    ? `<p class="row-sub">${esc(t('estimateHintVehicle'))}</p><form data-estimate-form class="specialized-asset-grid">
        <label>${esc(t('depreciation'))}<input type="number" name="annualDepreciationPercent" min="0" max="80" step="0.1" value="12"></label>
        <label>${esc(t('mileageAdj'))}<input type="number" name="mileageAdjustmentPercent" min="-50" max="50" step="0.1" value="0"></label>
        <label>${esc(t('conditionAdj'))}<input type="number" name="conditionAdjustmentPercent" min="-50" max="50" step="0.1" value="0"></label>
        <label>${esc(t('range'))}<input type="number" name="rangePercent" min="0" max="50" step="0.1" value="10"></label>
        <div class="span-2 specialized-form-actions"><button type="submit">${esc(t('calculate'))}</button></div></form>`
    : `<p class="row-sub">${esc(t('estimateHintMetal'))}</p><div class="row-sub">${esc(t('fineWeight'))}: ${esc(detail?.fineWeightGrams != null ? `${detail.fineWeightGrams} g` : '—')}</div><form data-estimate-form class="specialized-asset-grid">
        <label>${esc(t('referencePrice'))}<input type="number" name="referencePricePerFineGram" min="0.00000001" step="0.00000001" required></label>
        <label>${esc(t('currency'))}<input name="currency" maxlength="3" value="${esc(asset.currency || 'EUR')}" required></label>
        <label>${esc(t('premiumAdj'))}<input type="number" name="premiumAdjustmentPercent" min="-50" max="100" step="0.1" value="0"></label>
        <label>${esc(t('range'))}<input type="number" name="rangePercent" min="0" max="50" step="0.1" value="5"></label>
        <div class="span-2 specialized-form-actions"><button type="submit">${esc(t('calculate'))}</button></div></form>`;

  return `<div class="specialized-asset-section"><h3>${esc(t('manualValue'))}</h3><form data-manual-form class="specialized-asset-grid">
      <label>${esc(t('amount'))}<input type="number" name="amount" min="0" step="0.01" value="${esc(asset.currentValue ?? '')}" required></label>
      <label>${esc(t('currency'))}<input name="currency" maxlength="3" value="${esc(asset.currency || 'EUR')}" required></label>
      <label>${esc(t('date'))}<input type="date" name="valuedAt" value="${today()}" required></label>
      <div class="span-2 specialized-form-actions"><button type="submit">${esc(t('accept'))}</button></div></form></div>
    <div class="specialized-asset-section"><h3>${esc(t('internalEstimate'))}</h3>${internal}<div data-estimate-result></div></div>`;
}

function financingPanel(asset, debts, loans, liabilities) {
  const rows = (debts || []).map(d => `<div class="specialized-debt-row"><div class="specialized-debt-main"><div class="specialized-debt-title">${esc(d.name)}</div><div class="specialized-debt-sub">${esc(d.relationType)} · ${esc(d.allocationPercent)}% · ${money(d.currentBalance, d.currency)}</div></div><button type="button" data-remove-debt="${esc(d.id)}">${esc(t('remove'))}</button></div>`).join('') || `<div class="specialized-empty">${esc(t('debtEmpty'))}</div>`;
  const choices = [
    ...(loans || []).map(x => ({ value: `loan:${x.id}`, name: x.name || x.lender || 'Loan' })),
    ...(liabilities || []).map(x => ({ value: `liability:${x.id}`, name: x.name || 'Liability' }))
  ];
  return `<div class="specialized-asset-section"><h3>${esc(t('financing'))}</h3><div data-debt-list>${rows}</div></div>
    <div class="specialized-asset-section"><h3>${esc(t('debtAdd'))}</h3><form data-debt-form class="specialized-asset-grid">
      <label>${esc(t('debtSource'))}<select name="source" required><option value=""></option>${choices.map(x => `<option value="${esc(x.value)}">${esc(x.name)}</option>`).join('')}</select></label>
      <label>${esc(t('allocation'))}<input type="number" name="allocationPercent" min="0.01" max="100" step="0.01" value="100" required></label>
      <label>${esc(t('relation'))}<select name="relationType">${options(['vehicle_finance','secured_loan','other'], asset.kind === 'vehicle' ? 'vehicle_finance' : 'secured_loan')}</select></label>
      <div class="span-2 specialized-form-actions"><button type="submit">${esc(t('add'))}</button></div></form></div>`;
}

function historyPanel(valuations) {
  if (!(valuations || []).length) return `<div class="specialized-empty">${esc(t('noHistory'))}</div>`;
  return (valuations || []).map(v => `<div class="specialized-history-row"><div class="specialized-history-main"><div class="specialized-history-title">${money(v.amount, v.currency)}${v.isCurrent ? ` · ${esc(t('current'))}` : ''}</div><div class="specialized-history-sub">${esc(v.method)} · ${esc(date(v.valuedAt))}${v.lowEstimate != null && v.highEstimate != null ? ` · ${money(v.lowEstimate, v.currency)}–${money(v.highEstimate, v.currency)}` : ''}</div></div></div>`).join('');
}

function options(values, selected) { return values.map(value => `<option value="${esc(value)}"${value === selected ? ' selected' : ''}>${esc(value)}</option>`).join(''); }

function bindDetailForm(dlg, asset, endpoint) {
  const form = dlg.querySelector('[data-detail-form]');
  form?.addEventListener('submit', async event => {
    event.preventDefault();
    const fd = new FormData(form);
    let body;
    if (asset.kind === 'vehicle') {
      body = {
        vehicleType: fd.get('vehicleType'), manufacturer: fd.get('manufacturer') || null, model: fd.get('model') || null,
        variant: fd.get('variant') || null, vin: fd.get('vin') || null, licensePlate: fd.get('licensePlate') || null,
        firstRegistrationDate: fd.get('firstRegistrationDate') || null, modelYear: num(fd.get('modelYear')), mileageKm: num(fd.get('mileageKm')),
        powertrain: fd.get('powertrain') || null, powerKw: num(fd.get('powerKw')), purchaseDate: fd.get('purchaseDate') || null,
        purchasePrice: num(fd.get('purchasePrice')), purchaseCurrency: fd.get('purchaseCurrency') || null, condition: fd.get('condition') || null,
        annualMileageEstimate: num(fd.get('annualMileageEstimate')), notes: fd.get('notes') || null
      };
    } else {
      body = {
        metalType: fd.get('metalType'), form: fd.get('form'), quantity: Number(fd.get('quantity')), grossWeightGrams: num(fd.get('grossWeightGrams')),
        purity: num(fd.get('purity')), storageLabel: fd.get('storageLabel') || null, purchaseDate: fd.get('purchaseDate') || null,
        purchasePrice: num(fd.get('purchasePrice')), purchaseCurrency: fd.get('purchaseCurrency') || null, notes: fd.get('notes') || null
      };
    }
    try { await api(endpoint, json('PUT', body)); toast(t('saved')); dlg.close(); document.querySelector('#refresh')?.click(); }
    catch (error) { toast(error.message || t('invalid')); }
  });
}

function bindValuation(dlg, asset, endpoint) {
  dlg.querySelector('[data-manual-form]')?.addEventListener('submit', async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    try {
      await api(`api/assets/${asset.id}/valuations`, json('POST', { amount: Number(fd.get('amount')), currency: String(fd.get('currency')).toUpperCase(), valuedAt: fd.get('valuedAt'), method: 'manual', isAccepted: true }));
      toast(t('accepted')); dlg.close(); document.querySelector('#refresh')?.click();
    } catch (error) { toast(error.message || t('invalid')); }
  });

  dlg.querySelector('[data-estimate-form]')?.addEventListener('submit', async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    const body = asset.kind === 'vehicle'
      ? { annualDepreciationPercent: Number(fd.get('annualDepreciationPercent')), mileageAdjustmentPercent: Number(fd.get('mileageAdjustmentPercent')), conditionAdjustmentPercent: Number(fd.get('conditionAdjustmentPercent')), rangePercent: Number(fd.get('rangePercent')) }
      : { referencePricePerFineGram: Number(fd.get('referencePricePerFineGram')), currency: String(fd.get('currency')).toUpperCase(), premiumAdjustmentPercent: Number(fd.get('premiumAdjustmentPercent')), rangePercent: Number(fd.get('rangePercent')) };
    try {
      const estimate = await api(`${endpoint}/estimate`, json('POST', body));
      const result = dlg.querySelector('[data-estimate-result]');
      result.innerHTML = `<div class="specialized-estimate"><div><span>${esc(t('amount'))}</span><strong>${money(estimate.amount, estimate.currency)}</strong></div><div><span>Low</span><strong>${money(estimate.lowEstimate, estimate.currency)}</strong></div><div><span>High</span><strong>${money(estimate.highEstimate, estimate.currency)}</strong></div></div><div class="specialized-form-actions"><button type="button" data-accept-estimate>${esc(t('accept'))}</button></div>`;
      result.querySelector('[data-accept-estimate]').addEventListener('click', async () => {
        try {
          await api(`api/assets/${asset.id}/valuations`, json('POST', { amount: estimate.amount, currency: estimate.currency, valuedAt: estimate.valuedAt, method: 'internal_estimate', lowEstimate: estimate.lowEstimate, highEstimate: estimate.highEstimate, isAccepted: true }));
          toast(t('accepted')); dlg.close(); document.querySelector('#refresh')?.click();
        } catch (error) { toast(error.message || t('invalid')); }
      });
    } catch (error) { toast(error.message || t('invalid')); }
  });
}

function bindFinancing(dlg, asset) {
  dlg.querySelectorAll('[data-remove-debt]').forEach(button => button.addEventListener('click', async () => {
    try { await api(`api/assets/${asset.id}/debts/${button.dataset.removeDebt}`, { method: 'DELETE' }); button.closest('.specialized-debt-row')?.remove(); toast(t('saved')); }
    catch (error) { toast(error.message || t('invalid')); }
  }));

  dlg.querySelector('[data-debt-form]')?.addEventListener('submit', async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget); const source = String(fd.get('source') || '');
    const [type, id] = source.split(':'); if (!id) return;
    const body = { loanId: type === 'loan' ? id : null, liabilityId: type === 'liability' ? id : null, relationType: fd.get('relationType'), allocationPercent: Number(fd.get('allocationPercent')) };
    try { await api(`api/assets/${asset.id}/debts`, json('POST', body)); toast(t('saved')); dlg.close(); document.querySelector('#refresh')?.click(); }
    catch (error) { toast(error.message || t('invalid')); }
  });
}

function init() {
  ensureCss();
  const root = document.querySelector('#assets-list');
  if (!root) { setTimeout(init, 100); return; }
  new MutationObserver(scheduleEnhance).observe(root, { childList: true, subtree: true });
  document.querySelector('#privacy-toggle')?.addEventListener('click', scheduleEnhance);
  scheduleEnhance();
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init, { once: true });
else init();
